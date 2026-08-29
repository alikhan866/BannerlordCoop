using Common;
using GameInterface.Services.MapEvents.TroopSupply;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace Missions.Battles;

/// <summary>
/// Plan 2 C1 - one on-demand answer describing both sides of the battle in progress.
/// </summary>
/// <remarks>
/// COMPOSED, NOT REBUILT
/// Every number here already existed. <c>coop.debug.battle.size_state</c> reports the sizing half (battle size,
/// per-side totals and targets, allocation revision) and <c>BattleTeamDiagnostics</c> samples agents on field
/// every five seconds. Neither answers C1 on its own: size_state does not read the RESERVES at all, and the
/// diagnostics sampler only writes a log line, so a scenario wanting the state at a chosen moment had to scrape
/// the log and hope a sample landed near enough. This joins the two and adds the reserve dimension.
///
/// PER SIDE, BECAUSE THE FAULTS ARE PER SIDE
/// A total hides the failure this capability exists to catch. The motivating case was a side sitting at
/// onField=1 against a phase holding 1,123 men - a total across both sides looked merely low rather than
/// stuck. Agents are therefore counted by <c>Team.Side</c>, phases are read per side, and reserves are grouped
/// by <c>CoopTroopSupplier.Side</c>.
///
/// THE ANSWER IS PER PROCESS, AND THAT IS THE POINT
/// A reserve is what THIS process holds, so the server and each client legitimately return different numbers.
/// The command does not try to reconcile them - comparing the two is C6's job, and a command that averaged or
/// preferred one side would destroy the evidence that comparison needs. <c>ownedByThisProcess</c> is reported
/// so a reader can tell a client's own share from the side-wide figure without inferring it.
///
/// Degrades honestly. Outside a battle it says so rather than returning zeros, because "no battle" and "a
/// battle with nothing on the field" are the two states this command must never conflate.
/// </remarks>
public static class BattleSnapshotDebugCommand
{
    [CommandLineArgumentFunction("snapshot", "coop.debug.battle")]
    public static string Snapshot(List<string> args)
    {
        bool asJsonOnly = args.Count == 1 && string.Equals(args[0], "json", System.StringComparison.OrdinalIgnoreCase);
        if (args.Count > 1 || (args.Count == 1 && !asJsonOnly))
            return "Usage: coop.debug.battle.snapshot [json]";

        Mission mission = Mission.Current;
        CoopBattleController controller = mission?.GetMissionBehavior<CoopBattleController>();
        if (mission == null || controller == null)
            return "BATTLE_SNAPSHOT active=false reason=no-coop-battle-mission";

        var spawnLogic = mission.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
        var suppliers = CoopTroopSupplierRegistry.GetSuppliers(controller.Session.InstanceId);

        // The per-side TARGET is the sizing decision - how many this side is allowed to field - and it comes
        // from the coop spawn handler, not from the engine's phase. Reading InitialSpawnNumber for it looks
        // plausible and is wrong: that is how many the opening wave put out, so it drops to 0 the moment the
        // initial spawn is over and every later sample reports a target of zero on a battle that plainly has
        // one. Measured as target=0 against 351 agents on field before this was corrected.
        var spawnHandler = mission.GetMissionBehavior<CoopBattleMissionSpawnHandler>();
        var sizing = spawnHandler?.CaptureBattleSizeState();

        SideView defenders = BuildSide(
            BattleSideEnum.Defender, mission, spawnLogic, suppliers, sizing?.DefenderTarget);
        SideView attackers = BuildSide(
            BattleSideEnum.Attacker, mission, spawnLogic, suppliers, sizing?.AttackerTarget);

        var payload = new
        {
            active = true,
            instanceId = controller.Session.InstanceId,
            isLocalHost = controller.Session.IsLocalHost,
            controllerId = controller.Session.OwnControllerId,
            battleSize = spawnLogic?.BattleSize ?? 0,
            initialSpawnOver = spawnLogic?.IsInitialSpawnOver ?? false,
            deploymentActivated = controller.Deployment.IsActivated,
            deploymentCommitted = controller.Deployment.IsCommitted,
            defender = defenders,
            attacker = attackers,
        };

        string json = "LIVE_TEST_JSON=" + JsonConvert.SerializeObject(payload);
        if (asJsonOnly) return json;

        var report = new StringBuilder();
        report.AppendLine(
            $"BATTLE_SNAPSHOT active=true instance={controller.Session.InstanceId} " +
            $"host={controller.Session.IsLocalHost} battleSize={spawnLogic?.BattleSize ?? 0} " +
            $"initialSpawnOver={spawnLogic?.IsInitialSpawnOver ?? false}");
        report.AppendLine(Describe(defenders));
        report.AppendLine(Describe(attackers));
        report.Append(json);
        return report.ToString();
    }

    private static string Describe(SideView side) =>
        $"  {side.side,-8} onField={side.onField,-5} target={side.target,-5} " +
        $"phase(total={side.phaseTotal},remaining={side.phaseRemaining},initial={side.phaseInitial}) " +
        $"reserve(sideTotal={side.reserveSideTotal},owned={side.reserveOwnedTotal}," +
        $"remaining={side.reserveRemaining},removed={side.reserveRemoved}) " +
        $"suppliers={side.supplierCount} populated={side.populatedSuppliers} rev={side.reserveRevision}";

    private static SideView BuildSide(
        BattleSideEnum side,
        Mission mission,
        DefaultBattleMissionAgentSpawnLogic spawnLogic,
        IReadOnlyList<CoopTroopSupplier> suppliers,
        int? sideTarget)
    {
        int onField = mission.Agents.Count(agent =>
            agent != null && agent.IsActive() && agent.IsHuman && (agent.Team?.Side ?? BattleSideEnum.None) == side);

        var phase = side == BattleSideEnum.Defender
            ? spawnLogic?.DefenderActivePhase
            : spawnLogic?.AttackerActivePhase;

        // A side can legitimately have several suppliers - one per owner holding parties on it - so the reserve
        // figures are summed rather than taken from the first. sideTotal is NOT summed: every supplier on a side
        // reports the same side-wide figure, so adding them would multiply it by the number of owners.
        var onSide = suppliers.Where(supplier => supplier.Side == side).ToArray();
        var ownedParties = onSide
            .SelectMany(supplier => supplier.GetSuppliedByParty())
            .Select(entry => new { entry.partyId, entry.supplied })
            .ToArray();

        return new SideView
        {
            side = side.ToString(),
            onField = onField,
            target = sideTarget ?? -1,
            phaseTotal = phase?.TotalSpawnNumber ?? 0,
            phaseRemaining = phase?.RemainingSpawnNumber ?? 0,
            phaseInitial = phase?.InitialSpawnNumber ?? 0,
            reserveSideTotal = onSide.Length == 0 ? 0 : onSide.Max(supplier => supplier.SideTotalTroops),
            reserveOwnedTotal = onSide.Sum(supplier => supplier.TotalTroops),
            reserveRemaining = onSide.Sum(supplier => supplier.NumTroopsNotSupplied),
            reserveRemoved = onSide.Sum(supplier => supplier.NumRemovedTroops),
            supplierCount = onSide.Length,
            populatedSuppliers = onSide.Count(supplier => supplier.IsPopulated),
            reserveRevision = onSide.Length == 0 ? 0 : onSide.Max(supplier => supplier.ReserveRevision),
            ownedByThisProcess = ownedParties.Select(entry => $"{entry.partyId}:{entry.supplied}").ToArray(),
        };
    }

    private sealed class SideView
    {
        public string side { get; set; }
        public int onField { get; set; }
        public int target { get; set; }
        public int phaseTotal { get; set; }
        public int phaseRemaining { get; set; }
        public int phaseInitial { get; set; }
        public int reserveSideTotal { get; set; }
        public int reserveOwnedTotal { get; set; }
        public int reserveRemaining { get; set; }
        public int reserveRemoved { get; set; }
        public int supplierCount { get; set; }
        public int populatedSuppliers { get; set; }
        public int reserveRevision { get; set; }
        public string[] ownedByThisProcess { get; set; }
    }
}
