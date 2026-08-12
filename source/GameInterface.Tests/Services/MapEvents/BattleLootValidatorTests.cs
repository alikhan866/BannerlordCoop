using GameInterface.Services.MapEvents.Loot;
using System.Collections.Generic;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// The trust boundary for battle spoils: everything a client can say about what it took goes through here.
/// </summary>
public class BattleLootValidatorTests
{
    private const string OfferId = "offer-1";

    private static BattleLootOffer OfferOf(params BattleLootOfferLine[] lines)
        => new BattleLootOffer(OfferId, "MapEvent_1", "Party_1", lines);

    private static BattleLootOfferLine Item(string id, int count, string modifier = null)
        => new BattleLootOfferLine(BattleLootLineKind.Item, id, modifier, count);

    private static BattleLootOfferLine Troops(string id, int count)
        => new BattleLootOfferLine(BattleLootLineKind.Member, id, null, count);

    private static BattleLootOfferLine HeroPrisoner(string id)
        => new BattleLootOfferLine(BattleLootLineKind.Prisoner, id, null, 1, isHero: true);

    private static BattleLootResult ResultOf(params BattleLootClaim[] claims)
        => new BattleLootResult(OfferId, claims);

    [Fact]
    public void TakingPartOfALine_IsAccepted()
    {
        var offer = OfferOf(Item("grain", 600));

        Assert.True(BattleLootValidator.TryValidate(
            offer, ResultOf(new BattleLootClaim(0, 5)), out var resolved, out var rejection));

        Assert.Equal(BattleLootRejection.None, rejection);
        var claim = Assert.Single(resolved);
        Assert.Equal(5, claim.Count);
        Assert.Equal("grain", claim.Line.ObjectId);
    }

    [Fact]
    public void LeavingALineAlone_TakesNothingFromIt()
    {
        // Vanilla discards what the player leaves on the screen. Silence means "not taken", so an empty
        // result is valid and yields nothing rather than being treated as malformed.
        var offer = OfferOf(Item("grain", 600), Troops("recruit", 10));

        Assert.True(BattleLootValidator.TryValidate(
            offer, ResultOf(), out var resolved, out _));

        Assert.Empty(resolved);
    }

    [Fact]
    public void ClaimingMoreThanTheLineHolds_IsRejected()
    {
        var offer = OfferOf(Item("grain", 5));

        Assert.False(BattleLootValidator.TryValidate(
            offer, ResultOf(new BattleLootClaim(0, 6)), out _, out var rejection));

        Assert.Equal(BattleLootRejection.ExceedsOfferedCount, rejection);
    }

    [Fact]
    public void UnknownLineIndex_IsRejected()
    {
        var offer = OfferOf(Item("grain", 5));

        Assert.False(BattleLootValidator.TryValidate(
            offer, ResultOf(new BattleLootClaim(7, 1)), out _, out var rejection));

        Assert.Equal(BattleLootRejection.UnknownLineIndex, rejection);
    }

    [Fact]
    public void NegativeLineIndex_IsRejected()
    {
        var offer = OfferOf(Item("grain", 5));

        Assert.False(BattleLootValidator.TryValidate(
            offer, ResultOf(new BattleLootClaim(-1, 1)), out _, out var rejection));

        Assert.Equal(BattleLootRejection.UnknownLineIndex, rejection);
    }

    [Fact]
    public void TwoClaimsOnOneLine_AreRejected()
    {
        // Each would pass the per-claim bound while together taking twice what was offered.
        var offer = OfferOf(Item("grain", 10));

        Assert.False(BattleLootValidator.TryValidate(
            offer,
            ResultOf(new BattleLootClaim(0, 6), new BattleLootClaim(0, 6)),
            out _,
            out var rejection));

        Assert.Equal(BattleLootRejection.DuplicateLineIndex, rejection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void NonPositiveCount_IsRejected(int count)
    {
        var offer = OfferOf(Item("grain", 10));

        Assert.False(BattleLootValidator.TryValidate(
            offer, ResultOf(new BattleLootClaim(0, count)), out _, out var rejection));

        Assert.Equal(BattleLootRejection.NonPositiveCount, rejection);
    }

    [Fact]
    public void AHeroClaimedMoreThanOnce_IsRejected()
    {
        var offer = OfferOf(HeroPrisoner("lord_1_68"));

        Assert.False(BattleLootValidator.TryValidate(
            offer, ResultOf(new BattleLootClaim(0, 2)), out _, out var rejection));

        Assert.Equal(BattleLootRejection.HeroCountMustBeOne, rejection);
    }

    [Fact]
    public void AHeroGivenTwoDispositions_IsRejected()
    {
        // Keep and release the same person: caught as a duplicate line, which is what it is.
        var offer = OfferOf(HeroPrisoner("lord_1_68"));

        Assert.False(BattleLootValidator.TryValidate(
            offer,
            ResultOf(
                new BattleLootClaim(0, 1, BattleLootDisposition.Keep),
                new BattleLootClaim(0, 1, BattleLootDisposition.Release)),
            out _,
            out var rejection));

        Assert.Equal(BattleLootRejection.DuplicateLineIndex, rejection);
    }

    [Fact]
    public void ReleasingAHeroPrisoner_IsAccepted()
    {
        var offer = OfferOf(HeroPrisoner("lord_1_68"));

        Assert.True(BattleLootValidator.TryValidate(
            offer,
            ResultOf(new BattleLootClaim(0, 1, BattleLootDisposition.Release)),
            out var resolved,
            out _));

        Assert.Equal(BattleLootDisposition.Release, Assert.Single(resolved).Disposition);
    }

    [Fact]
    public void ReleasingSomethingThatIsNotAPrisoner_IsRejected()
    {
        var offer = OfferOf(Item("grain", 10));

        Assert.False(BattleLootValidator.TryValidate(
            offer,
            ResultOf(new BattleLootClaim(0, 1, BattleLootDisposition.Release)),
            out _,
            out var rejection));

        Assert.Equal(BattleLootRejection.DispositionOnNonPrisonerLine, rejection);
    }

    [Fact]
    public void AnAnswerToADifferentOffer_IsRejected()
    {
        // The stale-answer case: a siege runs assault after assault, so the previous battle's answer can
        // genuinely arrive while this offer is outstanding.
        var offer = OfferOf(Item("grain", 10));
        var stale = new BattleLootResult("offer-0", new[] { new BattleLootClaim(0, 1) });

        Assert.False(BattleLootValidator.TryValidate(offer, stale, out _, out var rejection));

        Assert.Equal(BattleLootRejection.OfferIdMismatch, rejection);
    }

    [Fact]
    public void AnEmptyOffer_AcceptsAnEmptyAnswerAndRejectsAnyClaim()
    {
        // Real case: killing every enemy outright produces 0 prisoners, so an offer can be empty.
        var offer = OfferOf();

        Assert.True(BattleLootValidator.TryValidate(offer, ResultOf(), out var resolved, out _));
        Assert.Empty(resolved);

        Assert.False(BattleLootValidator.TryValidate(
            offer, ResultOf(new BattleLootClaim(0, 1)), out _, out var rejection));
        Assert.Equal(BattleLootRejection.UnknownLineIndex, rejection);
    }

    [Fact]
    public void NullLinesAndNullClaims_AreTreatedAsEmptyRatherThanThrowing()
    {
        // These arrive off the wire, where a struct with no lines deserialises to a null array.
        var offer = new BattleLootOffer(OfferId, "MapEvent_1", "Party_1", null);
        var result = new BattleLootResult(OfferId, null);

        Assert.True(BattleLootValidator.TryValidate(offer, result, out var resolved, out var rejection));
        Assert.Empty(resolved);
        Assert.Equal(BattleLootRejection.None, rejection);
    }

    [Fact]
    public void ItemModifierIsPartOfTheLineIdentity()
    {
        // The reason results reference lines by index: two lines can share an item id and differ only by
        // modifier, and a claim must not be able to slide from the plain one to the lordly one.
        var offer = OfferOf(Item("sword", 5), Item("sword", 1, "lordly"));

        Assert.True(BattleLootValidator.TryValidate(
            offer, ResultOf(new BattleLootClaim(1, 1)), out var resolved, out _));

        Assert.Equal("lordly", Assert.Single(resolved).Line.ModifierId);
    }
}
