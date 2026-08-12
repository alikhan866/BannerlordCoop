using GameInterface.Services.MapEvents.Loot;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// The server's memory of what it offered. Everything here is about paying loot out exactly once.
/// </summary>
public class BattleLootOfferRegistryTests
{
    private const string PartyId = "Party_1";
    private const string OtherPartyId = "Party_2";

    private static BattleLootOffer Offer(string offerId = "offer-1", string mapEventId = "MapEvent_1", string partyId = PartyId)
        => new BattleLootOffer(
            offerId,
            mapEventId,
            partyId,
            new[] { new BattleLootOfferLine(BattleLootLineKind.Item, "grain", null, 600) });

    [Fact]
    public void AnOutstandingOffer_CanBeClaimedOnce()
    {
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer(), nowSeconds: 0);

        Assert.True(registry.TryClaim("offer-1", PartyId, 1, out var claimed, out var refusal));
        Assert.Equal(BattleLootClaimRefusal.None, refusal);
        Assert.Equal("grain", claimed.Lines[0].ObjectId);
    }

    [Fact]
    public void AReplayedResult_IsRefusedRatherThanPaidTwice()
    {
        // The transport is reliable-ordered but the client is not; a retry after a hiccup must not pay again.
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer(), nowSeconds: 0);

        Assert.True(registry.TryClaim("offer-1", PartyId, 1, out _, out _));
        Assert.False(registry.TryClaim("offer-1", PartyId, 1, out _, out var refusal));

        Assert.Equal(BattleLootClaimRefusal.AlreadyResolved, refusal);
    }

    [Fact]
    public void AnUnknownOffer_IsRefused()
    {
        var registry = new BattleLootOfferRegistry();

        Assert.False(registry.TryClaim("never-offered", PartyId, 0, out _, out var refusal));

        Assert.Equal(BattleLootClaimRefusal.UnknownOffer, refusal);
    }

    [Fact]
    public void AnotherPlayersOffer_CannotBeAnswered()
    {
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer(partyId: PartyId), nowSeconds: 0);

        Assert.False(registry.TryClaim("offer-1", OtherPartyId, 1, out _, out var refusal));

        Assert.Equal(BattleLootClaimRefusal.WrongParty, refusal);
        // Still open afterwards: refusing someone else's answer must not consume the real owner's offer.
        Assert.True(registry.IsOutstanding("offer-1", 1));
    }

    [Fact]
    public void AnExpiredOffer_IsRefused()
    {
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer(), nowSeconds: 0, lifetimeSeconds: 10);

        Assert.False(registry.TryClaim("offer-1", PartyId, 11, out _, out var refusal));

        Assert.Equal(BattleLootClaimRefusal.Expired, refusal);
    }

    [Fact]
    public void AnAnswerArrivingExactlyAtExpiry_IsRefused()
    {
        // The boundary is deliberate: expiry is inclusive so "expired" and "just in time" cannot both be true.
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer(), nowSeconds: 0, lifetimeSeconds: 10);

        Assert.False(registry.TryClaim("offer-1", PartyId, 10, out _, out var refusal));
        Assert.Equal(BattleLootClaimRefusal.Expired, refusal);
    }

    [Fact]
    public void AReplayAfterExpiry_IsStillReportedAsAReplay()
    {
        // Reporting "expired" for something already paid out would send a reader hunting a timing bug.
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer(), nowSeconds: 0, lifetimeSeconds: 10);

        Assert.True(registry.TryClaim("offer-1", PartyId, 1, out _, out _));
        Assert.False(registry.TryClaim("offer-1", PartyId, 999, out _, out var refusal));

        Assert.Equal(BattleLootClaimRefusal.AlreadyResolved, refusal);
    }

    [Fact]
    public void ForgettingAMapEvent_ClosesItsOffersSoALateAnswerCannotLand()
    {
        // A siege runs assault after assault; wave one's unanswered offer must not survive into wave two.
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer("wave-1", "MapEvent_1"), nowSeconds: 0);
        registry.Register(Offer("elsewhere", "MapEvent_2"), nowSeconds: 0);

        Assert.Equal(1, registry.ForgetMapEvent("MapEvent_1"));

        Assert.False(registry.TryClaim("wave-1", PartyId, 1, out _, out var refusal));
        Assert.Equal(BattleLootClaimRefusal.UnknownOffer, refusal);
        Assert.True(registry.IsOutstanding("elsewhere", 1));
    }

    [Fact]
    public void PurgeExpired_DropsOldEntriesAndLeavesLiveOnes()
    {
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer("old"), nowSeconds: 0, lifetimeSeconds: 10);
        registry.Register(Offer("fresh"), nowSeconds: 0, lifetimeSeconds: 1000);

        Assert.Equal(1, registry.PurgeExpired(50));

        Assert.True(registry.IsOutstanding("fresh", 50));
        Assert.False(registry.IsOutstanding("old", 50));
    }

    [Fact]
    public void OutstandingFor_ListsOnlyOpenUnexpiredOffersOfThatParty()
    {
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer("mine", partyId: PartyId), nowSeconds: 0, lifetimeSeconds: 100);
        registry.Register(Offer("theirs", partyId: OtherPartyId), nowSeconds: 0, lifetimeSeconds: 100);
        registry.Register(Offer("stale", partyId: PartyId), nowSeconds: 0, lifetimeSeconds: 5);

        var open = registry.OutstandingFor(PartyId, 10);

        Assert.Single(open);
        Assert.Equal("mine", open[0].OfferId);
    }

    [Fact]
    public void RegisteringTheSameIdAgain_ReopensItRatherThanLeavingItResolved()
    {
        // Re-offering the same id is how a resend after a reconnect must behave; a stale "resolved" flag
        // would otherwise make the fresh offer unanswerable.
        var registry = new BattleLootOfferRegistry();
        registry.Register(Offer(), nowSeconds: 0);
        Assert.True(registry.TryClaim("offer-1", PartyId, 1, out _, out _));

        registry.Register(Offer(), nowSeconds: 2);

        Assert.True(registry.TryClaim("offer-1", PartyId, 3, out _, out var refusal));
        Assert.Equal(BattleLootClaimRefusal.None, refusal);
    }

    [Fact]
    public void AnOfferWithNoId_IsNotRecorded()
    {
        // Comes off the wire; a missing id must not create an entry that can never be addressed or purged.
        var registry = new BattleLootOfferRegistry();
        registry.Register(new BattleLootOffer(null, "MapEvent_1", PartyId, null), nowSeconds: 0);

        Assert.Equal(0, registry.OpenOfferCount);
        Assert.False(registry.TryClaim(null, PartyId, 0, out _, out var refusal));
        Assert.Equal(BattleLootClaimRefusal.UnknownOffer, refusal);
    }

    [Fact]
    public void AnEmptyOffer_IsStillAnOfferThatMustBeAnswered()
    {
        // Killing every enemy outright yields no prisoners, so an empty offer is real - and it still has to be
        // claimed and closed, or the client would wait forever on a screen with nothing in it.
        var registry = new BattleLootOfferRegistry();
        registry.Register(new BattleLootOffer("empty", "MapEvent_1", PartyId, null), nowSeconds: 0);

        Assert.True(registry.IsOutstanding("empty", 1));
        Assert.True(registry.TryClaim("empty", PartyId, 1, out var claimed, out _));
        Assert.True(claimed.IsEmpty);
    }
}
