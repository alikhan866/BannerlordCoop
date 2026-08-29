using GameInterface;
using Missions.Services.Network;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace Missions.Battles;

/// <summary>
/// Plan 2 C7 - every live agent should have exactly one owner. Reports the ones that do not.
/// </summary>
/// <remarks>
/// WHY UNOWNED AGENTS MATTER
/// An agent nobody claims cannot be adjudicated. A blow aimed at one has no owner to route to, so it is
/// dropped - measured at 483 dropped hits in three minutes, which presents in play as troops that simply
/// cannot be killed. The count is therefore not a curiosity; it is the direct cause of a class of battle
/// that never ends.
///
/// THE ANSWER IS PER PROCESS
/// Ownership is a distributed fact and each process holds its own view, so this reports what THIS process
/// believes and does not attempt to reconcile. Two processes disagreeing about who owns an agent is exactly
/// the fault worth finding, and averaging it away here would destroy the evidence. Run it on both and compare.
///
/// AUTHORITY IS REPORTED SEPARATELY FROM ORIGIN
/// An agent can be spawned by one client and later migrate to another - that is normal and not a fault. So
/// OriginalOwner and CurrentAuthority are counted separately: a mismatch between them is a migration, while a
/// MISSING entry is the defect. Conflating the two would bury the real finding under routine host changes.
/// </remarks>
public static class BattleOwnershipCensusCommand
{
    [CommandLineArgumentFunction("ownership_census", "coop.debug.battle")]
    public static string OwnershipCensus(List<string> args)
    {
        bool asJsonOnly = args.Count == 1 && string.Equals(args[0], "json", System.StringComparison.OrdinalIgnoreCase);
        if (args.Count > 1 || (args.Count == 1 && !asJsonOnly))
            return "Usage: coop.debug.battle.ownership_census [json]";

        Mission mission = Mission.Current;
        if (mission == null) return "OWNERSHIP_CENSUS active=false reason=no-mission";
        if (!ContainerProvider.TryResolve<INetworkAgentRegistry>(out var registry))
            return "OWNERSHIP_CENSUS active=false reason=agent-registry-unavailable";

        var live = mission.Agents
            .Where(agent => agent != null && agent.IsActive() && agent.IsHuman)
            .ToArray();

        var byOwner = new Dictionary<string, int>();
        var byAuthority = new Dictionary<string, int>();
        var unclaimed = new List<string>();
        int migrated = 0;

        foreach (var agent in live)
        {
            if (!registry.TryGetAgentInfo(agent, out var info) || info == null)
            {
                // Capped in the report, not here: a battle where everything is unowned would otherwise
                // produce a line per agent and bury its own headline number.
                unclaimed.Add(
                    $"index={agent.Index} name=\"{Safe(agent)}\" " +
                    $"side={(agent.Team?.Side.ToString() ?? "none")} mount={Lower(agent.HasMount)}");
                continue;
            }

            string owner = string.IsNullOrEmpty(info.OriginalOwner) ? "<none>" : info.OriginalOwner;
            string authority = string.IsNullOrEmpty(info.CurrentAuthority) ? "<none>" : info.CurrentAuthority;
            byOwner[owner] = byOwner.TryGetValue(owner, out var o) ? o + 1 : 1;
            byAuthority[authority] = byAuthority.TryGetValue(authority, out var a) ? a + 1 : 1;
            if (owner != authority) migrated++;
        }

        var payload = new
        {
            active = true,
            liveAgents = live.Length,
            claimed = live.Length - unclaimed.Count,
            unclaimed = unclaimed.Count,
            migrated,
            controllers = registry.GetControllerIds()?.Count ?? 0,
            byOriginalOwner = byOwner,
            byCurrentAuthority = byAuthority,
        };

        string json = "LIVE_TEST_JSON=" + JsonConvert.SerializeObject(payload);
        if (asJsonOnly) return json;

        var report = new StringBuilder();
        report.AppendLine(
            $"OWNERSHIP_CENSUS active=true liveAgents={live.Length} " +
            $"claimed={live.Length - unclaimed.Count} unclaimed={unclaimed.Count} migrated={migrated} " +
            $"verdict={(unclaimed.Count == 0 ? "ok" : "UNOWNED-AGENTS")}");
        foreach (var pair in byOwner.OrderByDescending(pair => pair.Value))
            report.AppendLine($"  originalOwner {pair.Key} -> {pair.Value}");
        foreach (var pair in byAuthority.OrderByDescending(pair => pair.Value))
            report.AppendLine($"  currentAuthority {pair.Key} -> {pair.Value}");
        foreach (var line in unclaimed.Take(15)) report.AppendLine("  UNCLAIMED " + line);
        if (unclaimed.Count > 15) report.AppendLine($"  ... and {unclaimed.Count - 15} more unclaimed");
        report.Append(json);
        return report.ToString();
    }

    private static string Safe(Agent agent)
    {
        try { return agent.Name ?? "<unnamed>"; }
        catch { return "<unreadable>"; }
    }

    private static string Lower(bool value) => value.ToString().ToLowerInvariant();
}
