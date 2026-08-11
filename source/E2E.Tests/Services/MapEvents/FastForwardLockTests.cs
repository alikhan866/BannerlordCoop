using System.Collections.Generic;
using System.Linq;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Handlers;
using GameInterface.Services.Players.Data;
using Xunit;

namespace E2E.Tests.Services.MapEvents;

/// <summary>
/// Holding the campaign at normal speed for as long as a player is actually in a battle.
/// </summary>
/// <remarks>
/// The lock used to answer from <c>MobileParty.MapEvent</c>, which is only a proxy for "this player is
/// fighting" - and the proxy fails in the dangerous direction. A map event can be torn down while the mission
/// it belongs to keeps running: measured live, the server destroyed the event at 13:39:39 while the player was
/// still fighting it at 13:40:49. The lock released, announced "No more players are in map events" to a player
/// mid-battle, and let the campaign run at speed underneath him.
///
/// <c>SiegeBattleFreeze</c> had already been given mission membership for exactly this reason, at the same
/// settlement. These cover the fast-forward lock getting the same treatment.
/// </remarks>
public class FastForwardLockTests
{
    private static Player PlayerWith(string controllerId)
        => new Player(controllerId, heroId: "hero", mobilePartyId: "party", clanId: "clan", characterObjectId: "char");

    private static int CountWith(IEnumerable<Player> players, System.Func<Player, bool> inBlockingMapEvent)
        => BattleHandler.CountConnectedPlayersInMapEvents(players, _ => true, inBlockingMapEvent);

    public FastForwardLockTests() => PlayersInBattleMissions.Clear();

    [Fact]
    public void APlayerInAMissionCountsEvenWhenNoMapEventRemains()
    {
        // The reported failure: the campaign object is gone, the player is not.
        PlayersInBattleMissions.Replace(new[] { "jian" });

        Assert.True(PlayersInBattleMissions.Contains("jian"));
    }

    [Fact]
    public void APlayerWhoLeftIsForgotten()
    {
        PlayersInBattleMissions.Replace(new[] { "jian", "kan" });
        PlayersInBattleMissions.Replace(new[] { "kan" });

        Assert.False(PlayersInBattleMissions.Contains("jian"));
        Assert.True(PlayersInBattleMissions.Contains("kan"));
    }

    [Fact]
    public void ReplacingWholesaleCannotLeakAnEntry()
    {
        // Deliberately a replace rather than add/remove pairs. A leaked entry here does not degrade anything -
        // it pins the campaign at normal speed for the rest of the session, with nothing to clear it.
        PlayersInBattleMissions.Replace(new[] { "a", "b", "c" });
        PlayersInBattleMissions.Replace(System.Array.Empty<string>());

        Assert.False(PlayersInBattleMissions.Contains("a"));
        Assert.False(PlayersInBattleMissions.Contains("b"));
        Assert.False(PlayersInBattleMissions.Contains("c"));
    }

    [Fact]
    public void NullAndEmptyIdsAreIgnoredRatherThanStored()
    {
        // An unresolved controller id must not become a phantom member that holds the lock forever.
        PlayersInBattleMissions.Replace(new[] { null, "", "real" });

        Assert.False(PlayersInBattleMissions.Contains(""));
        Assert.False(PlayersInBattleMissions.Contains(null));
        Assert.True(PlayersInBattleMissions.Contains("real"));
    }

    // ---- the count the lock is actually built on ------------------------------------------------------

    [Fact]
    public void OnlyConnectedPlayersHoldTheLock()
    {
        // A player who has dropped is not fighting, whatever the campaign still says about their party.
        var players = new[] { PlayerWith("gone"), PlayerWith("here") };

        var count = BattleHandler.CountConnectedPlayersInMapEvents(
            players, p => p.ControllerId == "here", _ => true);

        Assert.Equal(1, count);
    }

    [Fact]
    public void NobodyFighting_LeavesTheCampaignFree()
    {
        Assert.Equal(0, CountWith(new[] { PlayerWith("a"), PlayerWith("b") }, _ => false));
    }

    [Fact]
    public void EveryFightingPlayerIsCounted_NotJustTheFirst()
    {
        // The message quotes the number, and it is also what decides the 0 / non-0 transition that announces
        // the lock releasing.
        var players = new[] { PlayerWith("a"), PlayerWith("b"), PlayerWith("c") };

        Assert.Equal(3, CountWith(players, _ => true));
        Assert.Equal(1, CountWith(players, p => p.ControllerId == "b"));
    }
}
