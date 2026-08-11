using GameInterface.Services.Heroes.Commands;
using Xunit;

namespace E2E.Tests.Services.Heroes;

/// <summary>
/// Removing a hero's stale roster entry without detaching him from the party he leads.
/// </summary>
/// <remarks>
/// A companion given his own party should leave the player's roster. When that removal is lost his
/// <c>CharacterObject</c> sits in two member rosters at once - he shows in the player's party screen while also
/// leading a party on the map, and his party never appears in Army Management, so he cannot be summoned.
///
/// The obvious repair is to subtract him from the wrong roster. That is wrong, and it was tried: vanilla treats
/// removing a HERO from a roster as detaching that hero, so <c>Hero.PartyBelongedTo</c> was cleared as a side
/// effect. Oragur the Knowing ended up belonging to no party at all while his party carried on without him -
/// strictly worse than the duplicate it removed. These tests pin the two rules that prevent a repeat.
/// </remarks>
public class StaleRosterEntryRepairTests
{
    [Fact]
    public void AnEntryInAPartyTheHeroDoesNotLeadIsStale()
    {
        // Oragur's case: present in Jian's roster, and Jian's party is not his own.
        Assert.True(HeroStaleRosterEntryCommand.IsStaleEntry(
            partyContainsHero: true, partyIsTheHerosOwnParty: false));
    }

    [Fact]
    public void TheHerosOwnPartyIsNeverStripped()
    {
        // THE safety property. That entry is what makes him the leader of his own party; deleting it is exactly
        // the mistake this command exists to undo.
        Assert.False(HeroStaleRosterEntryCommand.IsStaleEntry(
            partyContainsHero: true, partyIsTheHerosOwnParty: true));
    }

    [Fact]
    public void APartyThatDoesNotContainHimIsLeftAlone()
    {
        Assert.False(HeroStaleRosterEntryCommand.IsStaleEntry(
            partyContainsHero: false, partyIsTheHerosOwnParty: false));
        Assert.False(HeroStaleRosterEntryCommand.IsStaleEntry(
            partyContainsHero: false, partyIsTheHerosOwnParty: true));
    }

    [Fact]
    public void MembershipLostToTheRemovalIsRestored()
    {
        // The regression that orphaned him: he had a party before the removal and none after.
        Assert.True(HeroStaleRosterEntryCommand.NeedsMembershipRestore(
            hadOwnPartyBefore: true, stillHasItAfter: false));
    }

    [Fact]
    public void MembershipThatSurvivedIsNotTouchedAgain()
    {
        // If vanilla did not detach him, re-establishing it would duplicate his entry in his own roster.
        Assert.False(HeroStaleRosterEntryCommand.NeedsMembershipRestore(
            hadOwnPartyBefore: true, stillHasItAfter: true));
    }

    [Fact]
    public void AHeroWhoLedNothingIsNotGivenAParty()
    {
        // A companion with no party of his own must not acquire one as a side effect of tidying a roster.
        Assert.False(HeroStaleRosterEntryCommand.NeedsMembershipRestore(
            hadOwnPartyBefore: false, stillHasItAfter: false));
    }
}
