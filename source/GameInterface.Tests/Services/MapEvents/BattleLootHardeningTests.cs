using GameInterface.Services.MapEvents.Loot;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// The situations that only show up with more than one player, more than one battle, or nothing to loot.
/// </summary>
public class BattleLootHardeningTests
{
    private const string Alice = "Party_Alice";
    private const string Bob = "Party_Bob";

    private static BattleLootOfferLine HeroPrisoner(string id)
        => new BattleLootOfferLine(BattleLootLineKind.Prisoner, id, null, 1, isHero: true);

    private static BattleLootOffer Offer(string offerId, string mapEventId, string partyId, params BattleLootOfferLine[] lines)
        => new BattleLootOffer(offerId, mapEventId, partyId, lines);

    [Fact]
    public void TwoPlayersInOneBattle_EachAnswerOnlyTheirOwnOffer()
    {
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer("alice", "MapEvent_1", Alice), 0);
        registry.Register(Offer("bob", "MapEvent_1", Bob), 0);

        // Neither can answer the other's, even though both belong to the same battle.
        Assert.False(registry.TryClaim("bob", Alice, 1, out _, out var stolen));
        Assert.Equal(BattleLootClaimRefusal.WrongParty, stolen);

        Assert.True(registry.TryClaim("alice", Alice, 1, out _, out _));
        Assert.True(registry.TryClaim("bob", Bob, 1, out _, out _));
    }

    [Fact]
    public void ARefusedAttemptOnAnotherPlayersOffer_DoesNotConsumeIt()
    {
        // The dangerous shape: Alice's fumbled attempt silently burning Bob's spoils.
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer("bob", "MapEvent_1", Bob), 0);

        Assert.False(registry.TryClaim("bob", Alice, 1, out _, out _));

        Assert.True(registry.IsOutstanding("bob", 1));
        Assert.True(registry.TryClaim("bob", Bob, 1, out _, out var refusal));
        Assert.Equal(BattleLootClaimRefusal.None, refusal);
    }

    [Fact]
    public void TheSameHeroIsNeverOfferedToTwoPlayersAtOnce()
    {
        // Each player's offer is built from their OWN MapEventParty results, so a captured lord appears in
        // exactly one of them. If both could claim him, the second apply would find him already imprisoned -
        // which BattleLootApplier skips - but the offer must not create the race in the first place.
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer("alice", "MapEvent_1", Alice, HeroPrisoner("lord_1_68")), 0);
        registry.Register(Offer("bob", "MapEvent_1", Bob), 0);

        Assert.True(registry.TryClaim("alice", Alice, 1, out var aliceOffer, out _));
        Assert.True(registry.TryClaim("bob", Bob, 1, out var bobOffer, out _));

        Assert.Contains(aliceOffer.Lines, l => l.ObjectId == "lord_1_68");
        Assert.DoesNotContain(bobOffer.Lines, l => l.ObjectId == "lord_1_68");
    }

    [Fact]
    public void ASecondSiegeWave_RetiresTheFirstWavesUnansweredOffer()
    {
        // A siege assaults again immediately. A late answer to wave one must not be honoured against wave two.
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer("wave-1", "MapEvent_Siege", Alice), 0);

        registry.ForgetMapEvent("MapEvent_Siege");
        registry.Register(Offer("wave-2", "MapEvent_Siege", Alice), 10);

        Assert.False(registry.TryClaim("wave-1", Alice, 11, out _, out var refusal));
        Assert.Equal(BattleLootClaimRefusal.UnknownOffer, refusal);

        Assert.True(registry.TryClaim("wave-2", Alice, 11, out _, out _));
    }

    [Fact]
    public void RetiringOneBattlesOffers_LeavesAnotherBattleAlone()
    {
        // Two players can be in different battles at once; clearing one must not disarm the other.
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer("siege", "MapEvent_Siege", Alice), 0);
        registry.Register(Offer("field", "MapEvent_Field", Bob), 0);

        registry.ForgetMapEvent("MapEvent_Siege");

        Assert.False(registry.IsOutstanding("siege", 1));
        Assert.True(registry.TryClaim("field", Bob, 1, out _, out _));
    }

    [Fact]
    public void AnEmptyOfferProducesAnAnswerTheServerAccepts()
    {
        // Killing every enemy outright yields zero prisoners. The client answers an empty offer immediately
        // rather than letting it become pending - otherwise the finalize deferral waits on an answer that is
        // sent when the encounter closes, while the encounter waits for loot screens that an empty offer
        // never opens. That is a deadlock, and this is the answer that breaks it.
        var offer = Offer("empty", "MapEvent_1", Alice);
        Assert.True(offer.IsEmpty);

        var answer = BattleLootSelection.FromRemaining(offer, null);

        Assert.Empty(answer.Claims);
        Assert.True(BattleLootValidator.TryValidate(offer, answer, out var resolved, out var rejection));
        Assert.Equal(BattleLootRejection.None, rejection);
        Assert.Empty(resolved);
    }

    [Fact]
    public void AnEmptyOfferStillResolvesOnTheServerSoItCannotLinger()
    {
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer("empty", "MapEvent_1", Alice), 0);

        Assert.True(registry.TryClaim("empty", Alice, 1, out var claimed, out _));
        Assert.True(claimed.IsEmpty);

        // Answered once and gone - a retry finds nothing rather than reopening.
        Assert.False(registry.TryClaim("empty", Alice, 1, out _, out var refusal));
        Assert.Equal(BattleLootClaimRefusal.AlreadyResolved, refusal);
        Assert.False(registry.AnyOutstanding(1));
    }

    [Fact]
    public void OffersBelongToPlayersNotToWhoeverHostedTheMission()
    {
        // The battle host is whichever client entered the mission first. It carries no entitlement: each
        // player answers for their own party and nobody answers on the host's behalf.
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer("alice", "MapEvent_1", Alice), 0);
        registry.Register(Offer("bob", "MapEvent_1", Bob), 0);

        Assert.Equal(2, registry.OutstandingFor(Alice, 1).Count + registry.OutstandingFor(Bob, 1).Count);
        Assert.Single(registry.OutstandingFor(Alice, 1));
        Assert.Single(registry.OutstandingFor(Bob, 1));
    }

    [Fact]
    public void ClearedStagedRostersWouldClaimEverythingTheePlayerRefused()
    {
        // Why the declined troops are snapshotted before the party screen's callback runs, rather than read
        // afterwards like items are. Vanilla's OnPlayerLootMembersAndPrisonerEnd CLEARS both staged rosters
        // as the screen closes, so a later read reports nothing remaining - and nothing remaining is claimed
        // in full. This is that mistake written down: prisoners the player deliberately left, delivered anyway.
        var offer = Offer("o", "MapEvent_1", Alice,
            new BattleLootOfferLine(BattleLootLineKind.Prisoner, "looter", null, 9),
            HeroPrisoner("lord_1_68"));

        var asIfReadAfterTheClear = BattleLootSelection.FromRemaining(offer, new[] { 0, 0 });
        Assert.True(BattleLootValidator.TryValidate(offer, asIfReadAfterTheClear, out var claimedAll, out _));
        Assert.Equal(9, claimedAll.Single(c => c.Line.ObjectId == "looter").Count);

        // Read at the right moment - the player kept none of them - and nothing is claimed.
        var asSnapshottedBeforeTheClear = BattleLootSelection.FromRemaining(offer, new[] { 9, 1 });
        Assert.True(BattleLootValidator.TryValidate(offer, asSnapshottedBeforeTheClear, out var claimedNone, out _));
        Assert.Empty(claimedNone);
    }

    [Fact]
    public void OneAbandonedPlayerDoesNotAffectTheOther()
    {
        // Alice crashes, Bob keeps playing. Alice's take-all must not touch Bob's outstanding offer.
        var registry = new BattleLootOfferRegistry();
        var aliceOffer = Offer("alice", "MapEvent_1", Alice,
            new BattleLootOfferLine(BattleLootLineKind.Item, "grain", null, 10));
        registry.Register(aliceOffer, 0);
        registry.Register(Offer("bob", "MapEvent_1", Bob), 0);

        var onCrash = BattleLootAbandonPolicy.AnswerFor(aliceOffer, BattleLootAbandonReason.Disconnected);
        Assert.True(registry.TryClaim("alice", Alice, 1, out var claimed, out _));
        Assert.True(BattleLootValidator.TryValidate(claimed, onCrash, out var resolved, out _));
        Assert.Equal(10, Assert.Single(resolved).Count);

        Assert.True(registry.IsOutstanding("bob", 1));
    }
}
