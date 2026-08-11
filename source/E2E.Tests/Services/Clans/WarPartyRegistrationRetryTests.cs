using System;
using GameInterface.Services.Clans.Handlers;
using Xunit;

namespace E2E.Tests.Services.Clans;

/// <summary>
/// A war-party registration that arrives before the party it names must be held, not dropped.
/// </summary>
/// <remarks>
/// <c>Handle_AddWarParty</c> resolved the clan and the component and simply returned if either was missing.
/// The order is a race - the server sends this the instant the war party is added, and the client may not have
/// registered the component yet - so a perfectly valid registration could be discarded with nothing retrying it.
///
/// The consequence is not subtle. That cache is what Army Management lists, so the party became uncallable AND
/// invisible there: a player could see his companion's party sitting on the map and had no row for it in the
/// army screen at all. Rejoining fixed it, because a fresh save transfer rebuilds the cache from scratch -
/// which is the signature of a lost one-shot message rather than corrupted data.
/// </remarks>
public class WarPartyRegistrationRetryTests
{
    [Fact]
    public void AnUnresolvedRegistrationIsRetriedRatherThanDropped()
    {
        // The regression: previously the first failed lookup ended it forever.
        Assert.True(ClanCachesHandler.ShouldKeepRetrying(
            waited: TimeSpan.FromSeconds(1), timeout: ClanCachesHandler.PendingWarPartyTimeout));
    }

    [Fact]
    public void RetryingIsBoundedSoADeadRegistrationDoesNotLingerForever()
    {
        // A component that never arrives - its party destroyed in the meantime, say - must stop being retried
        // for the rest of the session.
        Assert.False(ClanCachesHandler.ShouldKeepRetrying(
            waited: ClanCachesHandler.PendingWarPartyTimeout + TimeSpan.FromSeconds(1),
            timeout: ClanCachesHandler.PendingWarPartyTimeout));
    }

    [Fact]
    public void TheBoundaryItselfStopsRetrying()
    {
        Assert.False(ClanCachesHandler.ShouldKeepRetrying(
            waited: ClanCachesHandler.PendingWarPartyTimeout,
            timeout: ClanCachesHandler.PendingWarPartyTimeout));
    }

    [Fact]
    public void TheTimeoutIsLongEnoughToOutlastAnOrderingRace()
    {
        // The race this covers is object registration arriving moments later, not minutes. A timeout of a few
        // hundred milliseconds would reintroduce the bug under load; this pins that it is generous.
        Assert.True(ClanCachesHandler.PendingWarPartyTimeout >= TimeSpan.FromSeconds(30));
    }
}
