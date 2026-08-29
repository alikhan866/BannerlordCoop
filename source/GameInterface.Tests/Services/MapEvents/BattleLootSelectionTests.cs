using GameInterface.Services.MapEvents.Loot;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// Deriving what the player took from what they left on the staged rosters.
/// </summary>
public class BattleLootSelectionTests
{
    private static BattleLootOffer Offer(params BattleLootOfferLine[] lines)
        => new BattleLootOffer("offer-1", "MapEvent_1", "Party_1", lines);

    private static BattleLootOfferLine Item(string id, int count)
        => new BattleLootOfferLine(BattleLootLineKind.Item, id, null, count);

    private static BattleLootOfferLine HeroPrisoner(string id)
        => new BattleLootOfferLine(BattleLootLineKind.Prisoner, id, null, 1, isHero: true);

    [Fact]
    public void TakingSomeOfALine_ClaimsTheDifference()
    {
        var offer = Offer(Item("grain", 10));

        var result = BattleLootSelection.FromRemaining(offer, new[] { 4 });

        var claim = Assert.Single(result.Claims);
        Assert.Equal(0, claim.LineIndex);
        Assert.Equal(6, claim.Count);
    }

    [Fact]
    public void TakingEverything_ClaimsTheWholeLine()
    {
        var offer = Offer(Item("grain", 10));

        var result = BattleLootSelection.FromRemaining(offer, new[] { 0 });

        Assert.Equal(10, Assert.Single(result.Claims).Count);
    }

    [Fact]
    public void LeavingALineUntouched_ProducesNoClaim()
    {
        // Vanilla discards what is left on the screen, so silence is how "declined" is expressed.
        var offer = Offer(Item("grain", 10));

        var result = BattleLootSelection.FromRemaining(offer, new[] { 10 });

        Assert.Empty(result.Claims);
    }

    [Fact]
    public void ReleasingALord_SurvivesIntoTheAnswer()
    {
        // The defect this covers: FromRemaining has always handled a release correctly, but nothing ever built
        // the dispositions to hand it. Every production caller passed null, so the only line that can construct
        // a Release claim was unreachable and a freed lord reached the server marked "keep".
        var offer = Offer(HeroPrisoner("CharacterObject_lord_1_68"), Item("grain", 10));
        var released = new HashSet<string> { "CharacterObject_lord_1_68" };

        var dispositions = BattleLootSelection.DispositionsFor(offer, released);
        var result = BattleLootSelection.FromRemaining(offer, new[] { 1, 0 }, dispositions);

        var heroClaim = Assert.Single(result.Claims, claim => claim.LineIndex == 0);
        Assert.Equal(BattleLootDisposition.Release, heroClaim.Disposition);
    }

    [Fact]
    public void ALordNobodyFreed_IsNotMarkedForRelease()
    {
        var offer = Offer(HeroPrisoner("CharacterObject_lord_1_68"));

        var dispositions = BattleLootSelection.DispositionsFor(offer, new HashSet<string>());

        Assert.Equal(BattleLootDisposition.Keep, dispositions[0]);
    }

    [Fact]
    public void OnlyHeroPrisonerLinesCanBeReleased()
    {
        // Releasing an item or a troop is meaningless, and BattleLootValidator rejects such a claim outright -
        // so it must never be produced in the first place, however the released set was gathered.
        var offer = Offer(Item("grain", 10));

        var dispositions = BattleLootSelection.DispositionsFor(offer, new HashSet<string> { "grain" });

        Assert.Equal(BattleLootDisposition.Keep, dispositions[0]);
    }

    [Fact]
    public void AReleasedHero_IsClaimedEvenThoughNothingWasTaken()
    {
        // The staged roster looks the same whether the lord was freed or ignored, so the release has to be
        // carried explicitly - it is the one choice with consequences the server must run.
        var offer = Offer(HeroPrisoner("lord_1_68"));

        var result = BattleLootSelection.FromRemaining(
            offer,
            new[] { 1 },
            new[] { BattleLootDisposition.Release });

        var claim = Assert.Single(result.Claims);
        Assert.Equal(BattleLootDisposition.Release, claim.Disposition);
        Assert.Equal(1, claim.Count);
    }

    [Fact]
    public void AnIgnoredHero_ProducesNoClaim()
    {
        var offer = Offer(HeroPrisoner("lord_1_68"));

        var result = BattleLootSelection.FromRemaining(
            offer,
            new[] { 1 },
            new[] { BattleLootDisposition.Keep });

        Assert.Empty(result.Claims);
    }

    [Fact]
    public void AKeptHero_IsClaimedOnce()
    {
        var offer = Offer(HeroPrisoner("lord_1_68"));

        var result = BattleLootSelection.FromRemaining(offer, new[] { 0 });

        var claim = Assert.Single(result.Claims);
        Assert.Equal(1, claim.Count);
        Assert.Equal(BattleLootDisposition.Keep, claim.Disposition);
    }

    [Fact]
    public void RemainingAboveWhatWasOffered_CannotProduceANegativeClaim()
    {
        // Defensive: a miscount must never turn into a claim that the validator would then reject, or worse
        // one that reads as a negative take.
        var offer = Offer(Item("grain", 5));

        var result = BattleLootSelection.FromRemaining(offer, new[] { 99 });

        Assert.Empty(result.Claims);
    }

    [Fact]
    public void NegativeRemaining_IsReadAsNothingLeft()
    {
        var offer = Offer(Item("grain", 5));

        var result = BattleLootSelection.FromRemaining(offer, new[] { -3 });

        Assert.Equal(5, Assert.Single(result.Claims).Count);
    }

    [Fact]
    public void AMissingOrShortRemainingArray_ClaimsTheWholeOffer()
    {
        // "Nothing left" is the safe default: it can only ever claim what the offer really held, and the
        // server re-validates anyway.
        var offer = Offer(Item("grain", 5), Item("meat", 3));

        var result = BattleLootSelection.FromRemaining(offer, null);

        Assert.Equal(2, result.Claims.Length);
        Assert.Equal(8, result.Claims.Sum(c => c.Count));
    }

    [Fact]
    public void TheAnswerCarriesTheOfferId()
    {
        var offer = Offer(Item("grain", 5));

        var result = BattleLootSelection.FromRemaining(offer, new[] { 0 });

        Assert.Equal("offer-1", result.OfferId);
    }

    [Fact]
    public void AnEmptyOffer_ProducesAnEmptyAnswerThatIsStillSent()
    {
        // Real case: killing every enemy outright yields no prisoners. The answer still has to go, or the
        // server never closes the offer and the client waits on a screen with nothing in it.
        var offer = Offer();

        var result = BattleLootSelection.FromRemaining(offer, null);

        Assert.Empty(result.Claims);
        Assert.Equal("offer-1", result.OfferId);
    }

    [Fact]
    public void TheAnswerSurvivesValidationAgainstItsOwnOffer()
    {
        // The two halves have to agree: anything this produces must be something the server will accept.
        var offer = Offer(Item("grain", 10), HeroPrisoner("lord_1_68"), Item("meat", 4));

        var result = BattleLootSelection.FromRemaining(
            offer,
            new[] { 3, 1, 0 },
            new[] { BattleLootDisposition.Keep, BattleLootDisposition.Release, BattleLootDisposition.Keep });

        Assert.True(BattleLootValidator.TryValidate(offer, result, out var resolved, out var rejection));
        Assert.Equal(BattleLootRejection.None, rejection);
        Assert.Equal(3, resolved.Count);
    }
}
