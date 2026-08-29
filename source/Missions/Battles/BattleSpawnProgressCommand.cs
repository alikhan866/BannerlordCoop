using GameInterface.Services.MapEvents.TroopSupply;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace Missions.Battles;

/// <summary>
/// Plan 2 C2 - is either side still filling the field, or has it stopped with men still owed to it?
/// </summary>
/// <remarks>
/// WHY A COUNT IS NOT AN ANSWER
/// C1 reports what is on the field right now, and that number alone cannot tell the two states apart that
/// matter. A side showing 251 of 502 is healthy thirty seconds before deployment commits and broken thirty
/// seconds after, and the only difference between them is whether the number is MOVING. Measured directly on
/// the two-owner fixture: attackers sat at 251 across two samples, then went to 502 the moment deployment
/// committed. A scenario sampling once would have called either reading a result.
///
/// So progress is measured between calls, and the process keeps the previous sample to measure against.
/// Nothing here polls on its own - a background sampler would decide the interval for every caller, and the
/// interval IS the sensitivity.
///
/// OWED, NOT TARGET
/// The stall test asks whether anything is still OWED to the field - troops left in the phase, or reserves the
/// side has not supplied - and not whether onField reached the target. Those differ, and the difference is the
/// point: a target is a sizing intention that legitimately goes unmet when a side takes casualties, while
/// troops still sitting in a reserve that nothing is drawing from is the actual fault. Judging against the
/// target would report every battle with losses as stalled.
///
/// A SIDE CAN LEGITIMATELY OWE NOTHING
/// Once the phase and the reserves are empty the side is COMPLETE, and complete is not stalled however long
/// the number stays still. Conflating them is what would make this command cry wolf on every finished battle.
///
/// LOCAL PROGRESS IS NOT THE WHOLE SIDE
/// Owing nothing locally is not the same as the side being at strength, and the first version of this command
/// conflated them and reported COMPLETE on a side fielding half its men. A process only holds ITS OWN reserve,
/// so the other owner's troops are in neither its phase nor its reserve; when they fail to arrive, every
/// per-process check still reads ok because each client is internally consistent. Measured on the two-owner
/// fixture: 251 of 502 attackers on both clients, deploymentCommitted=false on both, every other observable
/// green. So a second dimension is reported - what this process has SEEN of the side against what the side
/// says it holds.
///
/// COVERAGE IS JUDGED ON THE PEAK, BECAUSE CASUALTIES ALSO LOWER onField
/// A side below its total might have lost men rather than never received them, and those two need different
/// answers. The highest count seen since the baseline separates them: attrition can only reduce a number that
/// was once reached, so a peak that never approached the side total is evidence of non-arrival rather than of
/// losses. The peak is only as good as when sampling started - a baseline taken mid-battle has already missed
/// the peak - so reset marks the start explicitly and the sample count is reported alongside.
/// </remarks>
public static class BattleSpawnProgressCommand
{
    /// <summary>Consecutive unmoved samples before an owing side is called stalled rather than slow.</summary>
    private const int StallSamples = 2;

    // Per process, keyed by nothing: one mission at a time. The instance id is stored WITH the sample so a
    // sample from a previous battle can never be differenced against this one - that would report the whole
    // of the last battle's field as this one's progress.
    private static Sample previous;

    [CommandLineArgumentFunction("spawn_progress", "coop.debug.battle")]
    public static string SpawnProgress(List<string> args)
    {
        bool reset = args.Count == 1 && string.Equals(args[0], "reset", StringComparison.OrdinalIgnoreCase);
        bool asJsonOnly = args.Count == 1 && string.Equals(args[0], "json", StringComparison.OrdinalIgnoreCase);
        if (args.Count > 1 || (args.Count == 1 && !reset && !asJsonOnly))
            return "Usage: coop.debug.battle.spawn_progress [json|reset]";

        if (reset)
        {
            previous = null;
            return "SPAWN_PROGRESS baseline=cleared";
        }

        Mission mission = Mission.Current;
        CoopBattleController controller = mission?.GetMissionBehavior<CoopBattleController>();
        if (mission == null || controller == null)
        {
            previous = null;
            return "SPAWN_PROGRESS active=false reason=no-coop-battle-mission";
        }

        var spawnLogic = mission.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
        var suppliers = CoopTroopSupplierRegistry.GetSuppliers(controller.Session.InstanceId);

        var current = new Sample
        {
            InstanceId = controller.Session.InstanceId,
            TakenUtc = DateTime.UtcNow,
            Defender = Measure(BattleSideEnum.Defender, mission, spawnLogic, suppliers),
            Attacker = Measure(BattleSideEnum.Attacker, mission, spawnLogic, suppliers),
        };

        var baseline = previous != null && previous.InstanceId == current.InstanceId ? previous : null;
        double sinceSeconds = baseline == null ? 0 : (current.TakenUtc - baseline.TakenUtc).TotalSeconds;

        var defender = Compare(current.Defender, baseline?.Defender, baseline != null);
        var attacker = Compare(current.Attacker, baseline?.Attacker, baseline != null);

        // Carried forward so the NEXT call measures against this one. The stall counters live on the sample so
        // a run of unmoved samples accumulates instead of resetting on every call.
        current.Defender.StalledSamples = defender.stalledSamples;
        current.Attacker.StalledSamples = attacker.stalledSamples;
        current.Defender.PeakOnField = defender.peakOnField;
        current.Attacker.PeakOnField = attacker.peakOnField;
        previous = current;

        var payload = new
        {
            active = true,
            instanceId = current.InstanceId,
            isLocalHost = controller.Session.IsLocalHost,
            hadBaseline = baseline != null,
            sinceSeconds = Math.Round(sinceSeconds, 2),
            battleSize = spawnLogic?.BattleSize ?? 0,
            defender,
            attacker,
            verdict = Worst(defender.verdict, attacker.verdict),
        };

        string json = "LIVE_TEST_JSON=" + JsonConvert.SerializeObject(payload);
        if (asJsonOnly) return json;

        var report = new StringBuilder();
        report.AppendLine(
            $"SPAWN_PROGRESS active=true verdict={Worst(defender.verdict, attacker.verdict)} " +
            $"baseline={Lower(baseline != null)} sinceSeconds={sinceSeconds:F1} " +
            $"instance={current.InstanceId}");
        report.AppendLine(Describe(defender));
        report.AppendLine(Describe(attacker));
        report.Append(json);
        return report.ToString();
    }

    private static string Describe(SideProgress side) =>
        $"  {side.side,-8} {side.verdict,-12} spawn={side.spawnVerdict} coverage={side.coverage} " +
        $"onField={side.onField} ({Signed(side.onFieldDelta)}) peak={side.peakOnField} " +
        $"sideTotal={side.reserveSideTotal} missing={side.missingFromSide} " +
        $"owed={side.owed} ({Signed(side.owedDelta)}) " +
        $"phaseRemaining={side.phaseRemaining} reserveRemaining={side.reserveRemaining} " +
        $"unmovedSamples={side.stalledSamples}";

    private static SideProgress Compare(SideSample now, SideSample before, bool hasBaseline)
    {
        int onFieldDelta = before == null ? 0 : now.OnField - before.OnField;
        int owedDelta = before == null ? 0 : now.Owed - before.Owed;
        bool moved = onFieldDelta != 0 || owedDelta != 0;

        // A side that owes nothing is done, and stays done. Checked before the movement test so a finished
        // battle never accumulates stall counts for standing still, which is all a finished battle can do.
        int stalledSamples = now.Owed == 0 || !hasBaseline || moved
            ? 0
            : (before?.StalledSamples ?? 0) + 1;

        string spawnVerdict =
            now.Owed == 0 ? "COMPLETE" :
            !hasBaseline ? "FIRST_SAMPLE" :
            moved ? "ADVANCING" :
            stalledSamples >= StallSamples ? "STALLED" : "UNMOVED";

        // The peak carries forward so attrition cannot be mistaken for non-arrival - see the class remarks.
        now.PeakOnField = Math.Max(now.OnField, before?.PeakOnField ?? 0);
        string coverage =
            now.ReserveSideTotal <= 0 ? "UNKNOWN" :
            now.PeakOnField >= now.ReserveSideTotal ? "FULL" :
            onFieldDelta > 0 ? "FILLING" :
            !hasBaseline ? "FIRST_SAMPLE" : "SHORT";

        return new SideProgress
        {
            side = now.Side,
            verdict = Worst(spawnVerdict, coverage),
            spawnVerdict = spawnVerdict,
            coverage = coverage,
            peakOnField = now.PeakOnField,
            missingFromSide = Math.Max(0, now.ReserveSideTotal - now.PeakOnField),
            onField = now.OnField,
            onFieldDelta = onFieldDelta,
            owed = now.Owed,
            owedDelta = owedDelta,
            phaseRemaining = now.PhaseRemaining,
            reserveRemaining = now.ReserveRemaining,
            reserveSideTotal = now.ReserveSideTotal,
            target = now.Target,
            stalledSamples = stalledSamples,
        };
    }

    private static SideSample Measure(
        BattleSideEnum side,
        Mission mission,
        DefaultBattleMissionAgentSpawnLogic spawnLogic,
        IReadOnlyList<CoopTroopSupplier> suppliers)
    {
        var phase = side == BattleSideEnum.Defender
            ? spawnLogic?.DefenderActivePhase
            : spawnLogic?.AttackerActivePhase;

        var onSide = suppliers.Where(supplier => supplier.Side == side).ToArray();
        int phaseRemaining = phase?.RemainingSpawnNumber ?? 0;
        int reserveRemaining = onSide.Sum(supplier => supplier.NumTroopsNotSupplied);

        return new SideSample
        {
            Side = side.ToString(),
            OnField = mission.Agents.Count(agent =>
                agent != null && agent.IsActive() && agent.IsHuman &&
                (agent.Team?.Side ?? BattleSideEnum.None) == side),
            PhaseRemaining = phaseRemaining,
            ReserveRemaining = reserveRemaining,
            ReserveSideTotal = onSide.Length == 0 ? 0 : onSide.Max(supplier => supplier.SideTotalTroops),
            Target = phase?.TotalSpawnNumber ?? 0,
            Owed = phaseRemaining + reserveRemaining,
        };
    }

    // STALLED and SHORT are the findings; the rest are states a healthy battle passes through, so they win
    // over them. SHORT ranks just under STALLED because a side that never received its men is as broken as one
    // that stopped supplying them - it is only less certain, since sampling may have started too late.
    private static string Worst(string first, string second)
    {
        string[] order =
        {
            "STALLED", "SHORT", "UNMOVED", "FILLING", "FIRST_SAMPLE", "ADVANCING", "COMPLETE", "FULL", "UNKNOWN",
        };
        int rank(string value) => Math.Max(0, Array.IndexOf(order, value));
        return rank(first) <= rank(second) ? first : second;
    }

    private static string Signed(int value) => value > 0 ? "+" + value : value.ToString();

    private static string Lower(bool value) => value.ToString().ToLowerInvariant();

    private sealed class Sample
    {
        public string InstanceId;
        public DateTime TakenUtc;
        public SideSample Defender;
        public SideSample Attacker;
    }

    /// <summary>One side's reading plus how it moved since the previous call.</summary>
    private sealed class SideProgress
    {
        public string side { get; set; }
        public string verdict { get; set; }
        public string spawnVerdict { get; set; }
        public string coverage { get; set; }
        public int peakOnField { get; set; }
        public int missingFromSide { get; set; }
        public int onField { get; set; }
        public int onFieldDelta { get; set; }
        public int owed { get; set; }
        public int owedDelta { get; set; }
        public int phaseRemaining { get; set; }
        public int reserveRemaining { get; set; }
        public int reserveSideTotal { get; set; }
        public int target { get; set; }
        public int stalledSamples { get; set; }
    }

    private sealed class SideSample
    {
        public string Side;
        public int OnField;
        public int PhaseRemaining;
        public int ReserveRemaining;
        public int ReserveSideTotal;
        public int Target;
        public int Owed;
        public int PeakOnField;
        public int StalledSamples;
    }
}
