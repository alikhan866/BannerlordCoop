using Autofac;
using Common;
using GameInterface.Services.ObjectManager;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Clans.Commands;

/// <summary>
/// C19 - the one clan and kingdom action that had no command.
/// </summary>
/// <remarks>
/// C19 asks for army join and leave, fief and governor assignment, and diplomacy. Four of those five already
/// existed - army.mobile_party_add and mobile_party_remove, settlements.set_ownerclan, and the kingdom
/// declare_war / end_alliance / join_kingdom / leave_kingdom family. Governor assignment was the gap, so that is
/// all this adds. Rebuilding the other four would have made the capability look larger and the rig no more able.
///
/// Governors belong to a Town, not a Settlement: villages and hideouts have none, and asking for one is a
/// mistake worth naming rather than a null to trip over later.
/// </remarks>
public class GovernorActionDebugCommand
{
    [CommandLineArgumentFunction("set_governor", "coop.debug.settlements")]
    public static string SetGovernor(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 2) return "Usage: coop.debug.settlements.set_governor <settlementId> <heroId>";

        var settlement = Settlement.Find(args[0]);
        if (settlement == null) return $"Settlement with id {args[0]} not found.";
        if (settlement.Town == null)
            return $"{settlement.StringId} is not a town or castle, so it cannot have a governor.";
        if (!TryResolve(args[1], out Hero governor, out var error)) return error;

        var previous = settlement.Town.Governor?.StringId ?? "none";
        ChangeGovernorAction.Apply(settlement.Town, governor);

        // Reported rather than enforced. Vanilla governors come from the owning clan, but a test may want the
        // invalid case on purpose - so the scenario is told which it got instead of being refused or misled.
        var sameClan = governor.Clan != null && governor.Clan == settlement.OwnerClan;

        return $"SET_GOVERNOR settlement={settlement.StringId} was={previous} " +
               $"now={settlement.Town.Governor?.StringId ?? "none"} " +
               $"ownerClan={settlement.OwnerClan?.StringId ?? "none"} " +
               $"governorInOwnerClan={sameClan.ToString().ToLowerInvariant()}";
    }

    [CommandLineArgumentFunction("remove_governor", "coop.debug.settlements")]
    public static string RemoveGovernor(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 1) return "Usage: coop.debug.settlements.remove_governor <settlementId>";

        var settlement = Settlement.Find(args[0]);
        if (settlement == null) return $"Settlement with id {args[0]} not found.";
        if (settlement.Town == null)
            return $"{settlement.StringId} is not a town or castle, so it cannot have a governor.";

        var previous = settlement.Town.Governor?.StringId ?? "none";

        // RemoveGovernorOfIfExists rather than RemoveGovernorOf: the latter wants a governor that is definitely
        // there, and a command whose job is "make sure there is none" should not fail when there already is none.
        ChangeGovernorAction.RemoveGovernorOfIfExists(settlement.Town);

        return $"REMOVE_GOVERNOR settlement={settlement.StringId} was={previous} " +
               $"now={settlement.Town.Governor?.StringId ?? "none"}";
    }

    /// <summary>Where each part of C19 lives, since only one of the five needed new code.</summary>
    [CommandLineArgumentFunction("clan_action_support", "coop.debug.clan")]
    public static string ClanActionSupport(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.clan.clan_action_support";

        return "CLAN_ACTION_SUPPORT " +
               "army_join=coop.debug.army.mobile_party_add army_leave=coop.debug.army.mobile_party_remove " +
               "fief_assignment=coop.debug.settlements.set_ownerclan " +
               "governor_assignment=coop.debug.settlements.set_governor|remove_governor " +
               "diplomacy=coop.debug.kingdom.declare_war|end_alliance,coop.debug.clan.join_kingdom|leave_kingdom";
    }

    private static bool TryResolve<T>(string id, out T resolved, out string error) where T : class
    {
        resolved = null;
        error = null;

        if (!ContainerProvider.TryGetContainer(out var container) || !container.TryResolve(out IObjectManager objectManager))
        {
            error = "Unable to resolve ObjectManager.";
            return false;
        }
        if (!objectManager.TryGetObject(id, out resolved))
        {
            error = $"{typeof(T).Name} with id {id} not found.";
            return false;
        }

        return true;
    }
}
