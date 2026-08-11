using GameInterface.Services.Barters.Handlers;
using Xunit;

namespace E2E.Tests.Services.Barters;

/// <summary>
/// Recruiting a captured lord has to survive the server's availability check.
/// </summary>
/// <remarks>
/// The check refused every prisoner, which also refused the one barter a prisoner is FOR. Taking a captured
/// lord out of the dungeon and offering "Join &lt;clan&gt;" produced "That lord is no longer available for
/// barter" from the server, with no way to complete it - the whole prisoner-recruitment mechanic was dead in
/// coop.
///
/// A prisoner still must not be bartered with in general: a lord held in someone else's dungeon is not the
/// player's to negotiate with. So the question is who holds them, not whether they are held.
/// </remarks>
public class LordBarterPrisonerAvailabilityTests
{
    [Fact]
    public void APrisonerTheRequesterHoldsCanBeRecruited()
    {
        // The reported case: captured lord taken out of the player's own dungeon.
        Assert.True(LordBarterHandler.IsTargetAvailableForBarter(
            targetIsPrisoner: true, targetIsHeldByRequester: true, targetHasClan: true));
    }

    [Fact]
    public void SomeoneElsesPrisonerStillCannotBeBarteredWith()
    {
        // THE safety property. Without it a player could negotiate with a lord rotting in a rival's dungeon.
        Assert.False(LordBarterHandler.IsTargetAvailableForBarter(
            targetIsPrisoner: true, targetIsHeldByRequester: false, targetHasClan: true));
    }

    [Fact]
    public void AFreeLordIsUnaffected()
    {
        // Ordinary map barter has to keep working exactly as before.
        Assert.True(LordBarterHandler.IsTargetAvailableForBarter(
            targetIsPrisoner: false, targetIsHeldByRequester: false, targetHasClan: true));
    }

    [Fact]
    public void AClanlessLordIsStillRefused()
    {
        // Unchanged by this fix, and checked in both captivity states so loosening the prisoner rule cannot
        // smuggle a clanless hero through.
        Assert.False(LordBarterHandler.IsTargetAvailableForBarter(
            targetIsPrisoner: false, targetIsHeldByRequester: false, targetHasClan: false));
        Assert.False(LordBarterHandler.IsTargetAvailableForBarter(
            targetIsPrisoner: true, targetIsHeldByRequester: true, targetHasClan: false));
    }
}
