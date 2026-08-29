using GameInterface.Services.MapEvents.TroopSupply;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace Missions.Battles;

/// <summary>
/// Plan 2 C3 - "this battle should field N per side within T seconds", judged rather than eyeballed.
/// </summary>
/// <remarks>
/// ARM THEN POLL, BECAUSE A COMMAND MUST NOT WAIT
/// The obvious shape - one call that blocks until the field reaches strength or the deadline passes - cannot
/// exist here. Debug commands run on the game thread, so a command that slept for the timeout would stop the
/// mission it is measuring and guarantee the very failure it is testing for. So the expectation is ARMED once
/// and JUDGED on each later call, and the caller does the waiting on its own clock, which it is already doing
/// between samples anyway.
///
/// The consequence is worth stating plainly: the verdict is evaluated at CALL time. A caller that arms an
/// expectation and never polls again never learns it failed. Nothing here can fix that - a background timer
/// would be a poller with an interval nobody chose - so the remaining window is reported on every PENDING
/// answer, which is the caller's cue that another call is owed.
///
/// PEAK, NOT CURRENT
/// Strength is judged on the highest count seen since arming, for the same reason C2 measures coverage that
/// way: casualties legitimately pull the current count back down, and a battle that reached full strength and
/// then took losses has met the expectation. Judging on the instantaneous count would fail every battle that
/// was doing exactly what it should.
///
/// A BATTLE THAT ENDS EARLY IS A FAILURE, NOT A PASS
/// If the mission disappears or the instance changes while an armed expectation is unmet, that is reported as
/// a failure with its own reason rather than being forgotten. An expectation quietly dropped when the battle
/// it referred to ended is how a scenario ends up green for a battle that never happened.
/// </remarks>
public static class BattleStrengthExpectationCommand
{
    private static Expectation armed;

    [CommandLineArgumentFunction("expect_strength", "coop.debug.battle")]
    public static string ExpectStrength(List<string> args)
    {
        if (args.Count > 0 && string.Equals(args[0], "clear", StringComparison.OrdinalIgnoreCase))
        {
            armed = null;
            return "EXPECT_STRENGTH armed=false action=cleared";
        }

        if (args.Count > 0 && string.Equals(args[0], "arm", StringComparison.OrdinalIgnoreCase))
            return Arm(args);

        bool asJsonOnly = args.Count == 1 && string.Equals(args[0], "json", StringComparison.OrdinalIgnoreCase);
        if (args.Count > 1 || (args.Count == 1 && !asJsonOnly))
            return "Usage: coop.debug.battle.expect_strength [json] | " +
                   "arm <defenderMin> <attackerMin> <withinSeconds> | clear";

        return Judge(asJsonOnly);
    }

    private static string Arm(List<string> args)
    {
        if (args.Count != 4 ||
            !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int defenderMin) ||
            !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int attackerMin) ||
            !double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double within) ||
            defenderMin < 0 || attackerMin < 0 || within <= 0)
            return "Usage: coop.debug.battle.expect_strength arm <defenderMin> <attackerMin> <withinSeconds>";

        if (!TryReadBattle(out var mission, out var controller, out string error)) return error;

        armed = new Expectation
        {
            InstanceId = controller.Session.InstanceId,
            DefenderMin = defenderMin,
            AttackerMin = attackerMin,
            WithinSeconds = within,
            ArmedUtc = DateTime.UtcNow,
            DefenderPeak = CountOnField(mission, BattleSideEnum.Defender),
            AttackerPeak = CountOnField(mission, BattleSideEnum.Attacker),
        };

        return $"EXPECT_STRENGTH armed=true instance={armed.InstanceId} " +
               $"defenderMin={defenderMin} attackerMin={attackerMin} withinSeconds={within:F0} " +
               $"startingPeaks(defender={armed.DefenderPeak},attacker={armed.AttackerPeak})";
    }

    private static string Judge(bool asJsonOnly)
    {
        var expectation = armed;
        if (expectation == null) return "EXPECT_STRENGTH armed=false note=nothing to judge; use arm first";

        double elapsed = (DateTime.UtcNow - expectation.ArmedUtc).TotalSeconds;

        if (!TryReadBattle(out var mission, out var controller, out _) ||
            controller.Session.InstanceId != expectation.InstanceId)
        {
            // Already met stays met - the battle ending afterwards does not retract a strength it reached.
            string endedVerdict = expectation.MetUtc.HasValue ? "MET" : "FAILED";
            return $"EXPECT_STRENGTH verdict={endedVerdict} reason=battle-ended " +
                   $"instance={expectation.InstanceId} elapsedSeconds={elapsed:F1} " +
                   $"peaks(defender={expectation.DefenderPeak}/{expectation.DefenderMin}," +
                   $"attacker={expectation.AttackerPeak}/{expectation.AttackerMin})";
        }

        int defenderNow = CountOnField(mission, BattleSideEnum.Defender);
        int attackerNow = CountOnField(mission, BattleSideEnum.Attacker);
        expectation.DefenderPeak = Math.Max(expectation.DefenderPeak, defenderNow);
        expectation.AttackerPeak = Math.Max(expectation.AttackerPeak, attackerNow);

        bool met = expectation.DefenderPeak >= expectation.DefenderMin &&
                   expectation.AttackerPeak >= expectation.AttackerMin;
        if (met && !expectation.MetUtc.HasValue) expectation.MetUtc = DateTime.UtcNow;

        // The window LATCHES. Checking "met" first would let a side that reached strength four minutes into a
        // ninety-second expectation report MET, which is how an assertion about timing quietly stops being one
        // - measured exactly that way: metAfterSeconds=246 against withinSeconds=90, reported as a pass.
        if (!expectation.MetUtc.HasValue && elapsed > expectation.WithinSeconds) expectation.ExpiredUnmet = true;

        string verdict =
            expectation.ExpiredUnmet ? "FAILED" :
            met ? "MET" :
            elapsed > expectation.WithinSeconds ? "FAILED" : "PENDING";
        string reason =
            !expectation.ExpiredUnmet ? null :
            expectation.MetUtc.HasValue ? "reached-strength-late" : "never-reached-strength";
        double metAfter = expectation.MetUtc.HasValue
            ? (expectation.MetUtc.Value - expectation.ArmedUtc).TotalSeconds
            : -1;

        var suppliers = CoopTroopSupplierRegistry.GetSuppliers(controller.Session.InstanceId);
        var payload = new
        {
            verdict,
            reason,
            instanceId = expectation.InstanceId,
            elapsedSeconds = Math.Round(elapsed, 2),
            withinSeconds = expectation.WithinSeconds,
            remainingSeconds = Math.Round(Math.Max(0, expectation.WithinSeconds - elapsed), 2),
            metAfterSeconds = metAfter < 0 ? (double?)null : Math.Round(metAfter, 2),
            defender = new
            {
                required = expectation.DefenderMin,
                peak = expectation.DefenderPeak,
                onField = defenderNow,
                sideTotal = SideTotal(suppliers, BattleSideEnum.Defender),
            },
            attacker = new
            {
                required = expectation.AttackerMin,
                peak = expectation.AttackerPeak,
                onField = attackerNow,
                sideTotal = SideTotal(suppliers, BattleSideEnum.Attacker),
            },
        };

        string json = "LIVE_TEST_JSON=" + JsonConvert.SerializeObject(payload);
        if (asJsonOnly) return json;

        return $"EXPECT_STRENGTH verdict={verdict}{(reason == null ? "" : " reason=" + reason)} " +
               $"elapsedSeconds={elapsed:F1} " +
               $"withinSeconds={expectation.WithinSeconds:F0} " +
               $"remainingSeconds={Math.Max(0, expectation.WithinSeconds - elapsed):F1} " +
               $"metAfterSeconds={(metAfter < 0 ? "n/a" : metAfter.ToString("F1", CultureInfo.InvariantCulture))}\n" +
               $"  Defender peak={expectation.DefenderPeak}/{expectation.DefenderMin} " +
               $"onField={defenderNow} sideTotal={SideTotal(suppliers, BattleSideEnum.Defender)}\n" +
               $"  Attacker peak={expectation.AttackerPeak}/{expectation.AttackerMin} " +
               $"onField={attackerNow} sideTotal={SideTotal(suppliers, BattleSideEnum.Attacker)}\n" +
               json;
    }

    private static bool TryReadBattle(out Mission mission, out CoopBattleController controller, out string error)
    {
        mission = Mission.Current;
        controller = mission?.GetMissionBehavior<CoopBattleController>();
        error = mission == null || controller == null
            ? "EXPECT_STRENGTH active=false reason=no-coop-battle-mission"
            : null;
        return error == null;
    }

    private static int CountOnField(Mission mission, BattleSideEnum side) =>
        mission.Agents.Count(agent =>
            agent != null && agent.IsActive() && agent.IsHuman &&
            (agent.Team?.Side ?? BattleSideEnum.None) == side);

    // Reported for context, never used in the verdict: what a side HOLDS is C2's subject, and folding it in
    // here would make one assertion answer two questions and be unclear about which one it failed.
    private static int SideTotal(IReadOnlyList<CoopTroopSupplier> suppliers, BattleSideEnum side)
    {
        var onSide = suppliers.Where(supplier => supplier.Side == side).ToArray();
        return onSide.Length == 0 ? 0 : onSide.Max(supplier => supplier.SideTotalTroops);
    }

    private sealed class Expectation
    {
        public string InstanceId;
        public int DefenderMin;
        public int AttackerMin;
        public double WithinSeconds;
        public DateTime ArmedUtc;
        public DateTime? MetUtc;
        public bool ExpiredUnmet;
        public int DefenderPeak;
        public int AttackerPeak;
    }
}
