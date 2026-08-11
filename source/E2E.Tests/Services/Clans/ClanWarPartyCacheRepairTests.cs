using GameInterface.Services.Clans.Commands;
using Xunit;

namespace E2E.Tests.Services.Clans;

/// <summary>
/// Rebuilding the clan war-party cache that Army Management reads.
/// </summary>
/// <remarks>
/// <c>Clan.WarPartyComponents</c> is a cache maintained by <c>OnWarPartyAdded</c> as parties are created, not
/// something derived from the parties themselves. A party can therefore exist, belong to the clan, field troops
/// and roam the map while missing from that list - and the only symptom is that it cannot be summoned to an
/// army. No row, no greyed-out entry, no explanation.
///
/// "Oragur the Knowing's Party" was exactly that: present and healthy on the server, absent from the army
/// screen. The registration is a single message sent when the war party is added, and the handler used to DROP
/// it when the component had not been registered yet - a race it loses regularly - with nothing retrying. The
/// cache then stayed short by one entry for the rest of the save's life.
/// </remarks>
public class ClanWarPartyCacheRepairTests
{
    [Fact]
    public void AClanWarPartyAbsentFromTheCacheIsRestored()
    {
        // Oragur's case.
        Assert.True(ClanWarPartyRepairCommand.IsMissingFromCache(
            belongsToClan: true, isWarParty: true, alreadyCached: false));
    }

    [Fact]
    public void AnAlreadyCachedPartyIsNotAddedTwice()
    {
        // A second registration would put a duplicate row in the army screen.
        Assert.False(ClanWarPartyRepairCommand.IsMissingFromCache(
            belongsToClan: true, isWarParty: true, alreadyCached: true));
    }

    [Fact]
    public void AnotherClansPartyIsNeverRegisteredHere()
    {
        // Registering someone else's party into this clan's cache would put a party the player does not own
        // into their army list.
        Assert.False(ClanWarPartyRepairCommand.IsMissingFromCache(
            belongsToClan: false, isWarParty: true, alreadyCached: false));
    }

    [Fact]
    public void NonWarPartiesAreNeverRegistered()
    {
        // Caravans and garrisons belong to the clan but are not war parties; they have no place in the army list.
        Assert.False(ClanWarPartyRepairCommand.IsMissingFromCache(
            belongsToClan: true, isWarParty: false, alreadyCached: false));
    }
}
