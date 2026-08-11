using Common.Util;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Moq;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;
using Xunit;

using KingdomDecisionBase = TaleWorlds.CampaignSystem.Election.KingdomDecision;

namespace GameInterface.Tests.Services.Kingdoms;

/// <summary>
/// Which player clans a kingdom decision waits for before it resolves.
/// </summary>
/// <remarks>
/// A decision resolves once every eligible player clan has voted. Eligibility used to be every REGISTERED
/// player, connected or not — so a friend from an earlier session, or anyone who dropped mid-vote, left the
/// decision permanently one vote short. The remaining players sat on the Done screen unable to continue, and
/// only reloading cleared it (which is what rebuilds the player registry).
///
/// Only a connected player can send a vote, so only a connected player may be waited on — the same rule every
/// other place that waits on players already applies (e.g. <c>BattleHandler.CountConnectedPlayersInMapEvents</c>).
/// </remarks>
public class KingdomDecisionEligibilityTests
{
    [Fact]
    public void AConnectedPlayerInTheKingdom_IsWaitedOn()
    {
        var fixture = new Fixture();
        fixture.AddPlayer("p1", "Clan_1", connected: true);

        Assert.NotEmpty(fixture.VotingClanIds());
    }

    [Fact]
    public void ADisconnectedPlayer_IsNotWaitedOn()
    {
        // The reported hang: their vote can never arrive, so waiting on it strands everyone else.
        var fixture = new Fixture();
        fixture.AddPlayer("dropped", "Clan_dropped", connected: false);

        Assert.Empty(fixture.VotingClanIds());
    }

    [Fact]
    public void AConnectedPlayerAlongsideDisconnectedOnes_IsStillWaitedOn()
    {
        // The player who IS present must still get their say — dropping the absent ones must not skip the vote
        // entirely and decide the kingdom's business for the person sitting there.
        var fixture = new Fixture();
        fixture.AddPlayer("gone-1", "Clan_gone1", connected: false);
        fixture.AddPlayer("here", "Clan_here", connected: true);
        fixture.AddPlayer("gone-2", "Clan_gone2", connected: false);

        Assert.NotEmpty(fixture.VotingClanIds());
    }

    [Fact]
    public void EveryPlayerDisconnected_LeavesNobodyToWaitOn()
    {
        // Reported as "no eligible clan" so the caller leaves the decision pending rather than resolving an
        // outcome no present player chose.
        var fixture = new Fixture();
        fixture.AddPlayer("gone-1", "Clan_gone1", connected: false);
        fixture.AddPlayer("gone-2", "Clan_gone2", connected: false);

        Assert.Empty(fixture.VotingClanIds());
    }

    [Fact]
    public void EveryConnectedPlayerInTheKingdom_IsWaitedOn_NotJustTheFirstTwo()
    {
        // More than two clients: the rule scales with however many are actually present, and one of them
        // dropping removes only that one.
        var fixture = new Fixture();
        fixture.AddPlayer("p1", "Clan_1", connected: true);
        fixture.AddPlayer("p2", "Clan_2", connected: true);
        var third = fixture.AddPlayer("p3", "Clan_3", connected: true);

        Assert.Equal(3, fixture.VotingClanIds().Count);

        fixture.SetConnected(third, false);

        Assert.Equal(2, fixture.VotingClanIds().Count);
    }

    [Fact]
    public void WhetherPlayersVoteAtAll_DoesNotDependOnWhoIsConnected()
    {
        // The two questions must stay separate. "Is this a decision players vote on?" is asked on the SERVER
        // AND on every client to choose between a player vote and an immediate AI election — and clients hold
        // no peers for anyone, so a connection-aware answer there would have a client resolve the decision by
        // AI while the server put it to a vote. Only "is a vote still owed?" is connection-aware.
        var fixture = new Fixture();
        var player = fixture.AddPlayer("friend", "Clan_friend", connected: true);

        Assert.True(fixture.IsPlayerVotedDecision());
        Assert.NotEmpty(fixture.VotingClanIds());

        fixture.SetConnected(player, false);

        Assert.True(fixture.IsPlayerVotedDecision()); // still a player decision — every machine agrees
        Assert.Empty(fixture.VotingClanIds());        // but nobody is left to vote on it
    }

    [Fact]
    public void AConnectedPlayerInAnotherKingdom_IsNotWaitedOn()
    {
        // Connected, but their clan belongs to a different realm — this kingdom's business is not theirs.
        var fixture = new Fixture();
        fixture.AddPlayer("elsewhere", "Clan_elsewhere", connected: true, inOtherKingdom: true);

        Assert.Empty(fixture.VotingClanIds());
    }

    [Fact]
    public void ADisconnectedPlayerNoLongerBlocks_OnceTheyDrop()
    {
        // The transition the fix is really about: the same player, same decision, before and after dropping.
        var fixture = new Fixture();
        var player = fixture.AddPlayer("friend", "Clan_friend", connected: true);
        Assert.NotEmpty(fixture.VotingClanIds());

        fixture.SetConnected(player, false);

        Assert.Empty(fixture.VotingClanIds());
    }

    /// <summary>A kingdom, a decision on it, and a player manager whose connectivity the test controls.</summary>
    private sealed class Fixture
    {
        private readonly Mock<IPlayerManager> playerManager = new Mock<IPlayerManager>();
        private readonly Mock<IObjectManager> objectManager = new Mock<IObjectManager>();
        private readonly List<Player> players = new List<Player>();
        private readonly Kingdom kingdom = ObjectHelper.SkipConstructor<Kingdom>();
        private readonly Kingdom otherKingdom = ObjectHelper.SkipConstructor<Kingdom>();

        public readonly KingdomDecisionBase Decision;
        public readonly IKingdomDecisionVoteManager Manager;

        public Fixture()
        {
            var decision = ObjectHelper.SkipConstructor<DeclareWarDecision>();
            decision._kingdom = kingdom;
            Decision = decision;

            playerManager.Setup(m => m.Players).Returns(players);
            Manager = new KingdomDecisionVoteManager(
                playerManager.Object,
                objectManager.Object,
                messageBroker: null,
                outcomeResolver: null);
        }

        public Player AddPlayer(string controllerId, string clanId, bool connected, bool inOtherKingdom = false)
        {
            var player = new Player(controllerId, $"Hero_{controllerId}", $"Party_{controllerId}", clanId, null);
            players.Add(player);
            SetConnected(player, connected);

            var clan = ObjectHelper.SkipConstructor<Clan>();
            clan._kingdom = inOtherKingdom ? otherKingdom : kingdom;

            var resolved = clan;
            objectManager.Setup(m => m.TryGetObject(clanId, out resolved)).Returns(true);

            var id = clanId;
            objectManager.Setup(m => m.TryGetId(clan, out id)).Returns(true);

            return player;
        }

        public void SetConnected(Player player, bool connected)
            => playerManager.Setup(m => m.IsConnected(player)).Returns(connected);

        /// <summary>The clans whose vote the decision is still waiting for (connection-aware).</summary>
        public HashSet<string> VotingClanIds()
            => ((KingdomDecisionVoteManager)Manager).GetVotingClanIds(Decision);

        /// <summary>Whether this is a decision players vote on at all (deliberately connection-agnostic).</summary>
        public bool IsPlayerVotedDecision() => Manager.HasEligiblePlayerClan(Decision);
    }
}
