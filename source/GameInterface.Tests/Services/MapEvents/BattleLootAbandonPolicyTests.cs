using GameInterface.Services.MapEvents.Loot;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// What happens to spoils nobody answered for. A gameplay judgement, so it is pinned down by tests.
/// </summary>
public class BattleLootAbandonPolicyTests
{
    private static BattleLootOffer Offer(params BattleLootOfferLine[] lines)
        => new BattleLootOffer("offer-1", "MapEvent_1", "Party_1", lines);

    private static BattleLootOfferLine Item(string id, int count)
        => new BattleLootOfferLine(BattleLootLineKind.Item, id, null, count);

    private static BattleLootOfferLine HeroPrisoner(string id)
        => new BattleLootOfferLine(BattleLootLineKind.Prisoner, id, null, 1, isHero: true);

    [Fact]
    public void ADisconnectPaysOutInFull()
    {
        // A crash is not a decision. Taking a battle's spoils away for one punishes an accident, and it is
        // also what the mod did before this feature existed.
        Assert.Equal(
            BattleLootAbandonOutcome.TakeAll,
            BattleLootAbandonPolicy.Decide(BattleLootAbandonReason.Disconnected));
    }

    [Fact]
    public void WalkingAwayDiscards()
    {
        // Vanilla discards loot left on the screen. Paying it out would leave no way to decline anything.
        Assert.Equal(
            BattleLootAbandonOutcome.Discard,
            BattleLootAbandonPolicy.Decide(BattleLootAbandonReason.LeftDeliberately));
    }

    [Fact]
    public void ATimeoutDiscards()
    {
        // Otherwise "walk away and wait" is strictly better than answering, and can be farmed.
        Assert.Equal(
            BattleLootAbandonOutcome.Discard,
            BattleLootAbandonPolicy.Decide(BattleLootAbandonReason.TimedOut));
    }

    [Fact]
    public void TheTakeAllAnswerClaimsEveryLineInFull()
    {
        var offer = Offer(Item("grain", 10), Item("meat", 3));

        var result = BattleLootAbandonPolicy.AnswerFor(offer, BattleLootAbandonReason.Disconnected);

        Assert.Equal(2, result.Claims.Length);
        Assert.Equal(13, result.Claims.Sum(c => c.Count));
    }

    [Fact]
    public void TheTakeAllAnswerKeepsHeroesRatherThanReleasingThem()
    {
        // Releasing on the player's behalf would end a captivity and move relations off the back of a
        // dropped connection - a decision nobody made.
        var offer = Offer(HeroPrisoner("lord_1_68"));

        var result = BattleLootAbandonPolicy.AnswerFor(offer, BattleLootAbandonReason.Disconnected);

        var claim = Assert.Single(result.Claims);
        Assert.Equal(BattleLootDisposition.Keep, claim.Disposition);
        Assert.Equal(1, claim.Count);
    }

    [Fact]
    public void ADiscardAnswerClaimsNothing()
    {
        var offer = Offer(Item("grain", 10), HeroPrisoner("lord_1_68"));

        var result = BattleLootAbandonPolicy.AnswerFor(offer, BattleLootAbandonReason.LeftDeliberately);

        Assert.Empty(result.Claims);
        Assert.Equal("offer-1", result.OfferId);
    }

    [Fact]
    public void AnAbandonedAnswerStillPassesTheSameValidation()
    {
        // The whole point of building it as a normal result: abandonment goes through validation, planning
        // and application like any other answer, so there is no second route that could skip hero handling.
        var offer = Offer(Item("grain", 10), HeroPrisoner("lord_1_68"));

        var takeAll = BattleLootAbandonPolicy.AnswerFor(offer, BattleLootAbandonReason.Disconnected);
        var discard = BattleLootAbandonPolicy.AnswerFor(offer, BattleLootAbandonReason.TimedOut);

        Assert.True(BattleLootValidator.TryValidate(offer, takeAll, out var kept, out _));
        Assert.Equal(2, kept.Count);

        Assert.True(BattleLootValidator.TryValidate(offer, discard, out var none, out _));
        Assert.Empty(none);
    }

    [Fact]
    public void AnEmptyOfferIsAnsweredCleanlyWhicheverWayItIsAbandoned()
    {
        // Killing every enemy outright yields no prisoners, so an empty offer is real and must still close.
        var offer = Offer();

        foreach (var reason in new[]
                 {
                     BattleLootAbandonReason.Disconnected,
                     BattleLootAbandonReason.LeftDeliberately,
                     BattleLootAbandonReason.TimedOut,
                 })
        {
            var result = BattleLootAbandonPolicy.AnswerFor(offer, reason);
            Assert.Empty(result.Claims);
            Assert.True(BattleLootValidator.TryValidate(offer, result, out _, out _));
        }
    }
}
