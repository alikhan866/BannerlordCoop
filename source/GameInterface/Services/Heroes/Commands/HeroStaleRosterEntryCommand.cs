using System.Collections.Generic;
using Common;
using Common.Logging;
using Common.Util;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Heroes.Commands;

/// <summary>
/// Removes a hero's stale entry from a party they do not belong to, without detaching them from the one they do.
/// </summary>
/// <remarks>
/// A companion who is given his own party should leave the player's roster. When that removal is lost, his
/// <c>CharacterObject</c> sits in two member rosters at once: he shows in the player's party screen while also
/// leading a party on the map, and his own party stops qualifying for Army Management - it never appears in the
/// list, so he cannot be summoned at all.
///
/// The obvious repair is to subtract him from the wrong roster, and it is wrong. Vanilla treats removing a HERO
/// from a roster as detaching that hero: <c>Hero.PartyBelongedTo</c> is cleared as a side effect. Doing exactly
/// that left Oragur the Knowing belonging to no party at all while his party carried on existing without him,
/// which is worse than the duplicate it fixed.
///
/// So the membership is snapshotted first and put back afterwards. The removal is the point; the detach is
/// collateral, and this undoes it.
/// </remarks>
public static class HeroStaleRosterEntryCommand
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(HeroStaleRosterEntryCommand));

    /// <summary>
    /// Whether this is a stale entry worth removing, decided over plain values so it can be asserted.
    /// </summary>
    /// <remarks>
    /// Refusing the hero's OWN party is the safety property: that entry is what makes him the leader of it, and
    /// deleting it is precisely the mistake this command exists to undo.
    /// </remarks>
    internal static bool IsStaleEntry(bool partyContainsHero, bool partyIsTheHerosOwnParty)
        => partyContainsHero && !partyIsTheHerosOwnParty;

    /// <summary>Whether party membership was lost by the removal and has to be restored.</summary>
    internal static bool NeedsMembershipRestore(bool hadOwnPartyBefore, bool stillHasItAfter)
        => hadOwnPartyBefore && !stillHasItAfter;

    [CommandLineArgumentFunction("remove_stale_roster_entry", "coop.debug.hero")]
    public static string RemoveStaleRosterEntry(List<string> args)
    {
        if (ModInformation.IsClient) return "Command can only be run on the server.";
        if (args.Count != 2)
            return "Usage: coop.debug.hero.remove_stale_roster_entry <heroId> <partyIdToRemoveFrom>";

        if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) == false)
            return "Unable to resolve the ObjectManager.";
        if (!objectManager.TryGetObject<Hero>(args[0], out var hero) || hero == null)
            return $"No hero with id {args[0]}.";
        if (!objectManager.TryGetObject<MobileParty>(args[1], out var wrongParty) || wrongParty == null)
            return $"No party with id {args[1]}.";

        var character = hero.CharacterObject;
        if (character == null) return $"{hero.Name} has no CharacterObject.";

        var ownParty = hero.PartyBelongedTo;
        int present = wrongParty.MemberRoster.GetTroopCount(character);

        if (!IsStaleEntry(present > 0, ReferenceEquals(ownParty, wrongParty)))
        {
            return present <= 0
                ? $"{hero.Name} is not in {wrongParty.Name}; nothing to remove."
                : $"{wrongParty.Name} IS {hero.Name}'s own party - refusing to remove the entry that makes him its leader.";
        }

        var before = $"in {wrongParty.Name} x{present}, belongs to {ownParty?.Name?.ToString() ?? "<nothing>"}";

        using (new AllowedThread())
        {
            wrongParty.MemberRoster.AddToCounts(character, -present, false, 0, 0, true);

            // Vanilla will have detached him as a side effect of the hero removal. Put it back.
            if (NeedsMembershipRestore(ownParty != null, ReferenceEquals(hero.PartyBelongedTo, ownParty)))
                RestoreMembership(hero, ownParty);
        }

        var after = $"in {wrongParty.Name} x{wrongParty.MemberRoster.GetTroopCount(character)}, " +
                    $"belongs to {hero.PartyBelongedTo?.Name?.ToString() ?? "<nothing>"}";

        Logger.Information("[Repair] Removed {Hero}'s stale roster entry from {Party}: {Before} -> {After}",
            hero.Name, wrongParty.Name, before, after);

        return $"Repaired {hero.Name}.\n  before: {before}\n  after:  {after}";
    }

    /// <summary>
    /// Puts a hero back in the party they lead after the removal detached them.
    /// </summary>
    /// <remarks>
    /// Two cases, because the detach does not always empty the roster it belonged to. If his own party still
    /// lists him, only the back-reference needs restoring and re-adding him would duplicate the entry; if it
    /// does not, the public roster call re-establishes both at once.
    ///
    /// <c>Hero.PartyBelongedTo</c> has a non-public setter, so the back-reference is written through the same
    /// <c>AccessTools</c> reflection this codebase uses for every other engine internal.
    /// </remarks>
    private static void RestoreMembership(Hero hero, MobileParty ownParty)
    {
        if (ownParty == null) return;

        if (ownParty.MemberRoster.GetTroopCount(hero.CharacterObject) > 0)
        {
            AccessTools.Field(typeof(Hero), "_partyBelongedTo")?.SetValue(hero, ownParty);
            return;
        }

        ownParty.AddElementToMemberRoster(hero.CharacterObject, 1, false);
    }
}
