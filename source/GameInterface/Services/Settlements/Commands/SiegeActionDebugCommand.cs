using Autofac;
using Common;
using GameInterface.Services.ObjectManager;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Settlements.Commands;

/// <summary>
/// C16 - drive a siege: besiege, choose how to press it, and abandon.
/// </summary>
/// <remarks>
/// Not blocked either, and the existing surface was already substantial - besiegercamp.add_besiegerparty,
/// set_leader_party, settlements.set_siege_state, capture_by_siege, list_siege_state, mobileparty.siege_buff.
/// What none of them could do is START one or END one, which is what turns a pile of siege knobs into a siege
/// that can be driven from beginning to end.
///
/// SiegeEventManager.StartSiegeEvent and BesiegerCamp.RemoveAllSiegeParties are direct campaign calls - no
/// encounter, no menu, no scene - so they work on a headless client for the same reason
/// EnterSettlementAction does in C17.
///
/// ASSAULT IS THE PART THAT IS NOT HERE
/// PlayerSiege.StartSiegeMission puts the player into a siege SCENE, which needs a renderer. The campaign-level
/// outcome already has a command - settlements.capture_by_siege - so a headless scenario can drive a siege to
/// its conclusion without ever fighting it. What cannot be tested headlessly is the assault mission itself, and
/// that is a renderer limit rather than a missing command.
/// </remarks>
public class SiegeActionDebugCommand
{
    [CommandLineArgumentFunction("besiege", "coop.debug.settlements")]
    public static string Besiege(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 2) return "Usage: coop.debug.settlements.besiege <besiegerPartyId> <settlementId>";
        if (!TryResolve(args[0], out MobileParty party, out var error)) return error;

        var settlement = Settlement.Find(args[1]);
        if (settlement == null) return $"Settlement with id {args[1]} not found.";
        if (!settlement.IsFortification)
            return $"{settlement.StringId} is not a fortification, so it cannot be besieged.";
        if (settlement.SiegeEvent != null)
            return $"{settlement.StringId} is already under siege by " +
                   $"{settlement.SiegeEvent.BesiegerCamp?.LeaderParty?.StringId ?? "unknown"}.";

        var manager = Campaign.Current?.SiegeEventManager;
        if (manager == null) return "SiegeEventManager is unavailable.";

        manager.StartSiegeEvent(settlement, party);

        return $"BESIEGE settlement={settlement.StringId} besieger={party.StringId} " +
               $"siegeStarted={(settlement.SiegeEvent != null).ToString().ToLowerInvariant()} " +
               $"leader={settlement.SiegeEvent?.BesiegerCamp?.LeaderParty?.StringId ?? "none"}";
    }

    [CommandLineArgumentFunction("siege_status", "coop.debug.settlements")]
    public static string SiegeStatus(List<string> args)
    {
        if (args.Count != 1) return "Usage: coop.debug.settlements.siege_status <settlementId>";

        var settlement = Settlement.Find(args[0]);
        if (settlement == null) return $"Settlement with id {args[0]} not found.";

        var siege = settlement.SiegeEvent;
        if (siege == null) return $"SIEGE_STATUS settlement={settlement.StringId} underSiege=false";

        var camp = siege.BesiegerCamp;
        var result = new StringBuilder();
        result.AppendLine(
            $"SIEGE_STATUS settlement={settlement.StringId} underSiege=true " +
            $"leader={camp?.LeaderParty?.StringId ?? "none"} " +
            $"strategy={camp?.SiegeStrategy?.StringId ?? "none"} " +
            $"readyToBesiege={(camp?.IsReadyToBesiege ?? false).ToString().ToLowerInvariant()}");
        result.AppendLine($"strategiesAvailable={string.Join(",", StrategyNames())}");
        return result.ToString();
    }

    /// <summary>
    /// The "build" half of C16 - which engines get raised is decided by the siege strategy.
    /// </summary>
    /// <remarks>
    /// Strategies are looked up by reflection over DefaultSiegeStrategies rather than hardcoded, so the command
    /// offers exactly what this game version defines and cannot drift out of date into a name that no longer
    /// exists. siege_status prints the list, so a caller never has to guess one.
    /// </remarks>
    [CommandLineArgumentFunction("set_siege_strategy", "coop.debug.settlements")]
    public static string SetSiegeStrategy(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 2) return "Usage: coop.debug.settlements.set_siege_strategy <settlementId> <strategyName>";

        var settlement = Settlement.Find(args[0]);
        if (settlement == null) return $"Settlement with id {args[0]} not found.";

        var camp = settlement.SiegeEvent?.BesiegerCamp;
        if (camp == null) return $"{settlement.StringId} is not under siege.";

        var strategy = FindStrategy(args[1]);
        if (strategy == null)
            return $"Unknown siege strategy '{args[1]}'. Available: {string.Join(",", StrategyNames())}";

        var previous = camp.SiegeStrategy?.StringId ?? "none";
        camp.SetSiegeStrategy(strategy);

        return $"SET_SIEGE_STRATEGY settlement={settlement.StringId} was={previous} " +
               $"now={camp.SiegeStrategy?.StringId ?? "none"}";
    }

    [CommandLineArgumentFunction("abandon_siege", "coop.debug.settlements")]
    public static string AbandonSiege(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 1) return "Usage: coop.debug.settlements.abandon_siege <settlementId>";

        var settlement = Settlement.Find(args[0]);
        if (settlement == null) return $"Settlement with id {args[0]} not found.";

        var camp = settlement.SiegeEvent?.BesiegerCamp;
        if (camp == null) return $"ABANDON_SIEGE settlement={settlement.StringId} wasUnderSiege=false";

        var leader = camp.LeaderParty?.StringId ?? "none";

        // RemoveAllSiegeParties is the campaign-level lift: it takes the besiegers off the settlement, and the
        // siege event is finalized as a consequence. Calling FinalizeSiegeEvent directly would tear down the
        // event while parties still believed they were besieging it.
        camp.RemoveAllSiegeParties();

        return $"ABANDON_SIEGE settlement={settlement.StringId} wasUnderSiege=true wasLedBy={leader} " +
               $"stillUnderSiege={(settlement.SiegeEvent != null).ToString().ToLowerInvariant()}";
    }

    private static IEnumerable<PropertyInfo> StrategyProperties() =>
        typeof(DefaultSiegeStrategies)
            .GetProperties(BindingFlags.Static | BindingFlags.Public)
            .Where(property => typeof(SiegeStrategy).IsAssignableFrom(property.PropertyType));

    private static IEnumerable<string> StrategyNames() =>
        StrategyProperties().Select(property => property.Name);

    private static SiegeStrategy FindStrategy(string name)
    {
        var property = StrategyProperties()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, System.StringComparison.OrdinalIgnoreCase));

        return property?.GetValue(null) as SiegeStrategy;
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
