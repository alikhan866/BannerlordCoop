using GameInterface.Services.MobileParties.Commands;
using Xunit;

namespace E2E.Tests.Services.MobileParties;

/// <summary>
/// Restoring the leader a lord party lost, without reinstating one vanilla removed on purpose.
/// </summary>
/// <remarks>
/// <c>LordPartyComponent</c> holds the same hero twice: <c>Owner</c>, written once by the constructor, and
/// <c>_leader</c>, which vanilla clears through <c>RemovePartyLeader</c> whenever the lord stops leading. Vanilla
/// always follows that up - the party is destroyed, disbanded, or handed a new leader. In coop it can stall
/// halfway, leaving a party that still carries its owner's name everywhere while <c>LeaderHero</c> is null.
///
/// Nothing reports that. "Oragur the Knowing's Party" kept 90 men under a size limit of 20 (its siblings were at
/// ~106, the difference being every leader-derived bonus), sat parked on Hold, and was missing from Army
/// Management entirely - no row, no greyed-out entry - so he could not be summoned to an army at all.
/// </remarks>
public class PartyLeaderRepairTests
{
    [Fact]
    public void ALordPartyWithAnOwnerAndNoLeaderIsRepaired()
    {
        // Oragur's case.
        Assert.True(PartyLeaderRepairCommand.NeedsLeaderRestore(
            hasLeader: false, hasOwner: true, ownerIsAlive: true, ownerIsPrisoner: false));
    }

    [Fact]
    public void APartyThatAlreadyHasALeaderIsLeftAlone()
    {
        // Re-running ChangePartyLeader on a healthy party would fire OnPartyLeaderChanged again for nothing.
        Assert.False(PartyLeaderRepairCommand.NeedsLeaderRestore(
            hasLeader: true, hasOwner: true, ownerIsAlive: true, ownerIsPrisoner: false));
    }

    [Fact]
    public void ACaptiveOwnerIsNotPutBackInCharge()
    {
        // THE safety property. Captivity is the single likeliest reason the leader was removed in the first
        // place - TakePrisonerAction calls RemovePartyLeader - so promoting him again would hand a party on the
        // campaign map to a lord who is sitting in somebody's dungeon.
        Assert.False(PartyLeaderRepairCommand.NeedsLeaderRestore(
            hasLeader: false, hasOwner: true, ownerIsAlive: true, ownerIsPrisoner: true));
    }

    [Fact]
    public void ADeadOwnerIsNotResurrectedIntoCommand()
    {
        // KillCharacterAction.MakeDead also calls RemovePartyLeader; that removal is correct and permanent.
        Assert.False(PartyLeaderRepairCommand.NeedsLeaderRestore(
            hasLeader: false, hasOwner: true, ownerIsAlive: false, ownerIsPrisoner: false));
    }

    [Fact]
    public void APartyWithNoOwnerHasNobodyToPromote()
    {
        Assert.False(PartyLeaderRepairCommand.NeedsLeaderRestore(
            hasLeader: false, hasOwner: false, ownerIsAlive: false, ownerIsPrisoner: false));
    }

    [Fact]
    public void AnOwnerMissingFromTheRosterJoinsItFirst()
    {
        // PartyComponent.ChangePartyLeader asserts and returns without changing anything when the new leader is
        // not in that party's member roster, and the assert is silent in a release build. Skipping this would
        // make the repair report success while nothing happened.
        Assert.True(PartyLeaderRepairCommand.MustAddOwnerToRoster(ownerIsInPartyRoster: false));
    }

    [Fact]
    public void AnOwnerAlreadyInTheRosterIsNotAddedTwice()
    {
        // Adding him again would duplicate the hero entry that makes him the leader - the exact corruption
        // this whole line of repairs exists to undo.
        Assert.False(PartyLeaderRepairCommand.MustAddOwnerToRoster(ownerIsInPartyRoster: true));
    }
}
