using Autofac;
using Common;
using GameInterface.Services.ObjectManager;
using System.Collections.Generic;
using System.Globalization;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Party.Commands;

/// <summary>
/// C18 - moving troops and prisoners between parties, and taking or releasing heroes.
/// </summary>
/// <remarks>
/// Most of C18 already existed: addtroops, removetroops, set_troops, addprisoners, removeprisoners. What was
/// missing is a transfer that treats two parties as ONE operation. Doing it as "remove here, add there" from the
/// rig means two round trips with a window in between where the troops exist in neither party or in both, and a
/// scenario that fails in that window leaves a world no restore was told about.
///
/// ORDER AND COMPENSATION
/// Remove-then-add can delete troops if the add fails; add-then-remove can duplicate them if the remove fails.
/// Neither is safe on its own, so this validates first, removes, adds, and then CHECKS the destination actually
/// gained them - putting them back in the source if it did not. Duplication and deletion both silently corrupt a
/// fixture, and a rig that corrupts its own fixture reports faults that are its own.
/// </remarks>
public class PartyRosterActionDebugCommand
{
    [CommandLineArgumentFunction("transfer", "coop.debug.mobileparty")]
    public static string Transfer(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count < 4 || args.Count > 5 ||
            !int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count <= 0)
            return "Usage: coop.debug.mobileparty.transfer <fromPartyId> <toPartyId> <troopId> <count> [members|prisoners]";

        var which = args.Count == 5 ? args[4].ToLowerInvariant() : "members";
        if (which != "members" && which != "prisoners")
            return "The fifth argument must be 'members' or 'prisoners'.";

        if (!TryResolve(args[0], out MobileParty from, out var fromError)) return fromError;
        if (!TryResolve(args[1], out MobileParty to, out var toError)) return toError;
        if (from == to) return "Source and destination are the same party.";
        if (!TryResolve(args[2], out CharacterObject troop, out var troopError)) return troopError;

        TroopRoster source = which == "prisoners" ? from.PrisonRoster : from.MemberRoster;
        TroopRoster destination = which == "prisoners" ? to.PrisonRoster : to.MemberRoster;

        var sourceBefore = source.GetTroopCount(troop);
        var destinationBefore = destination.GetTroopCount(troop);
        if (sourceBefore < count)
            return $"{from.StringId} has {sourceBefore} of {troop.StringId}, cannot transfer {count}.";

        // Wounded status has to be carried across explicitly: AddToCounts defaults woundedCount to 0, so a
        // plain transfer would deliver wounded troops as healthy and quietly change both parties' strength -
        // and any assertion resting on it. Healthy move first, which is what the party screen does, so wounded
        // only travel once the healthy ones are used up.
        var sourceIndex = source.FindIndexOfTroop(troop);
        var woundedBefore = sourceIndex >= 0 ? source.GetElementWoundedNumber(sourceIndex) : 0;
        var healthyBefore = sourceBefore - woundedBefore;
        var woundedToMove = count > healthyBefore ? count - healthyBefore : 0;

        source.RemoveTroop(troop, count);
        destination.AddToCounts(troop, count, false, woundedToMove);

        // Verified rather than assumed. If the destination did not take them, the source gets them back - the
        // one outcome worse than a failed transfer is a transfer that quietly destroyed troops.
        var destinationAfter = destination.GetTroopCount(troop);
        if (destinationAfter != destinationBefore + count)
        {
            source.AddToCounts(troop, count, false, woundedToMove);
            return $"TRANSFER_FAILED troop={troop.StringId} count={count} " +
                   $"destinationExpected={destinationBefore + count} destinationActual={destinationAfter} " +
                   "troopsReturnedToSource=true";
        }

        return $"TRANSFER roster={which} troop={troop.StringId} count={count} wounded={woundedToMove} " +
               $"from={from.StringId} fromBefore={sourceBefore} fromAfter={source.GetTroopCount(troop)} " +
               $"to={to.StringId} toBefore={destinationBefore} toAfter={destinationAfter}";
    }

    [CommandLineArgumentFunction("take_prisoner", "coop.debug.mobileparty")]
    public static string TakePrisoner(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 2) return "Usage: coop.debug.mobileparty.take_prisoner <captorPartyId> <heroId>";
        if (!TryResolve(args[0], out MobileParty captor, out var captorError)) return captorError;
        if (!TryResolve(args[1], out Hero hero, out var heroError)) return heroError;
        if (hero.IsPrisoner) return $"Hero {hero.StringId} is already a prisoner.";
        if (!hero.IsAlive) return $"Hero {hero.StringId} is not alive.";

        TakePrisonerAction.Apply(captor.Party, hero);

        return $"TAKE_PRISONER hero={hero.StringId} captor={captor.StringId} " +
               $"isPrisoner={hero.IsPrisoner.ToString().ToLowerInvariant()} " +
               $"heldBy={hero.PartyBelongedToAsPrisoner?.Id.ToString() ?? "none"}";
    }

    /// <summary>
    /// Releases a captive hero.
    /// </summary>
    /// <remarks>
    /// ApplyByReleasedAfterBattle is the least entangled of the eight release routes - ransom, peace, escape and
    /// choice each drag in gold, relation or notification side effects that a test would then have to model.
    /// It is still not the inverse of being captured, which is why C20 records captivity as an APPROXIMATE undo
    /// rather than claiming the world comes back untouched.
    /// </remarks>
    [CommandLineArgumentFunction("release_prisoner", "coop.debug.mobileparty")]
    public static string ReleasePrisoner(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 1) return "Usage: coop.debug.mobileparty.release_prisoner <heroId>";
        if (!TryResolve(args[0], out Hero hero, out var heroError)) return heroError;
        if (!hero.IsPrisoner) return $"RELEASE_PRISONER hero={hero.StringId} wasPrisoner=false";

        var heldBy = hero.PartyBelongedToAsPrisoner?.Id.ToString() ?? "none";
        EndCaptivityAction.ApplyByReleasedAfterBattle(hero);

        return $"RELEASE_PRISONER hero={hero.StringId} wasHeldBy={heldBy} " +
               $"isPrisoner={hero.IsPrisoner.ToString().ToLowerInvariant()}";
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
