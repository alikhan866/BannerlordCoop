using GameInterface.Services.MapEvents.TroopSupply;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace Missions.Battles;

/// <summary>
/// Plan 2 C4 (supply refusals) and C5 (reserve integrity), read on demand.
/// </summary>
/// <remarks>
/// The refusals and the duplicate-hero findings are recorded by the code that already detects them - see
/// BattleObservationLedger. These commands only read that ledger, plus one check that had no home: whether the
/// per-party reserves on a side actually add up to the side total every client sizes from.
///
/// THE SUM CHECK IS COMPUTED, NOT RECORDED
/// Unlike the other two it has no natural detection site, because no single code path is wrong when it fails -
/// it is a disagreement BETWEEN a side total sent by the server and the parties that arrived. It is therefore
/// evaluated when asked. A mismatch here is the shape that makes a side size itself from the wrong denominator
/// and field the wrong number of men, which is why the plan names it.
/// </remarks>
public static class BattleObservationDebugCommands
{
    [CommandLineArgumentFunction("supply_refusals", "coop.debug.battle")]
    public static string SupplyRefusals(List<string> args)
    {
        if (args.Count > 1) return "Usage: coop.debug.battle.supply_refusals [mapEventId]";
        string filter = args.Count == 1 ? args[0] : null;

        var (entries, dropped) = BattleObservationLedger.GetRefusals(filter);
        if (entries.Count == 0)
            return $"SUPPLY_REFUSALS count=0 dropped={dropped} " +
                   $"note=no wave has been capped{(filter == null ? "" : $" for {filter}")}";

        var report = new StringBuilder();
        int withheld = entries.Sum(entry => entry.Requested - entry.Supplied);
        report.AppendLine($"SUPPLY_REFUSALS count={entries.Count} dropped={dropped} totalWithheld={withheld}");
        foreach (var entry in entries) report.AppendLine("  " + entry);
        return report.ToString().TrimEnd();
    }

    [CommandLineArgumentFunction("reserve_integrity", "coop.debug.battle")]
    public static string ReserveIntegrity(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.battle.reserve_integrity";

        var report = new StringBuilder();
        var (findings, dropped) = BattleObservationLedger.GetFindings();
        report.AppendLine($"RESERVE_INTEGRITY findings={findings.Count} dropped={dropped}");
        foreach (var finding in findings) report.AppendLine("  " + finding);

        var controller = Mission.Current?.GetMissionBehavior<CoopBattleController>();
        if (controller == null)
        {
            report.Append("  sumCheck=skipped reason=no-coop-battle-mission");
            return report.ToString().TrimEnd();
        }

        var suppliers = CoopTroopSupplierRegistry.GetSuppliers(controller.Session.InstanceId);
        foreach (var side in new[] { BattleSideEnum.Defender, BattleSideEnum.Attacker })
        {
            var onSide = suppliers.Where(supplier => supplier.Side == side).ToArray();
            if (onSide.Length == 0)
            {
                report.AppendLine($"  sumCheck side={side} suppliers=0 (nothing held here)");
                continue;
            }

            // sideTotal is the same figure on every supplier of a side, so it is read rather than summed;
            // owned troops ARE per-supplier and are added up. On one client the owned sum is normally SMALLER
            // than the side total - the rest belongs to other owners - so only owned > sideTotal is wrong.
            int sideTotal = onSide.Max(supplier => supplier.SideTotalTroops);
            int owned = onSide.Sum(supplier => supplier.TotalTroops);
            string verdict = owned > sideTotal
                ? "MISMATCH owned-exceeds-sideTotal"
                : sideTotal == 0 && owned > 0
                    ? "MISMATCH sideTotal-is-zero-but-troops-are-held"
                    : "ok";

            report.AppendLine(
                $"  sumCheck side={side} sideTotal={sideTotal} ownedHere={owned} " +
                $"suppliers={onSide.Length} verdict={verdict}");
        }

        return report.ToString().TrimEnd();
    }

    [CommandLineArgumentFunction("observation_clear", "coop.debug.battle")]
    public static string ObservationClear(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.battle.observation_clear";
        BattleObservationLedger.Clear();
        return "BATTLE_OBSERVATION_CLEARED refusals=0 findings=0";
    }
}
