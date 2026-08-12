using GameInterface.Services.MapEvents.Loot;
using GameInterface.Services.TroopRosters.Data;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// Recognising the offer inside the staged rosters, which carry no line numbers of their own.
/// </summary>
public class BattleLootLineMatcherTests
{
    private static BattleLootOffer Offer(params BattleLootOfferLine[] lines)
        => new BattleLootOffer("offer-1", "MapEvent_1", "Party_1", lines);

    private static BattleLootOfferLine Item(string id, int count, string modifier = null)
        => new BattleLootOfferLine(BattleLootLineKind.Item, id, modifier, count);

    private static BattleLootOfferLine Prisoner(string id, int count, bool isHero = false)
        => new BattleLootOfferLine(BattleLootLineKind.Prisoner, id, null, count, isHero: isHero);

    private static BattleLootItemStack Stack(string id, int count, string modifier = null)
        => new BattleLootItemStack(id, modifier, count);

    private static TroopRosterElementData Troop(string id, int count)
        => new TroopRosterElementData(id, count, 0, 0);

    [Fact]
    public void UntouchedStaging_LeavesEveryLineFullyRemaining()
    {
        var offer = Offer(Item("grain", 10));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, new[] { Stack("grain", 10) }, null, null);

        Assert.Equal(new[] { 10 }, remaining);
    }

    [Fact]
    public void EmptyStaging_MeansEverythingWasTaken()
    {
        var offer = Offer(Item("grain", 10));

        var remaining = BattleLootLineMatcher.CountRemaining(offer, null, null, null);

        Assert.Equal(new[] { 0 }, remaining);
    }

    [Fact]
    public void PartiallyEmptiedStaging_IsCountedExactly()
    {
        var offer = Offer(Item("grain", 10));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, new[] { Stack("grain", 4) }, null, null);

        Assert.Equal(new[] { 4 }, remaining);
    }

    [Fact]
    public void TheSameItemWithDifferentModifiers_AreDifferentLines()
    {
        // The attack this prevents: leave the plain swords, take the lordly one, and have the plain ones
        // counted as the lordly line's remainder.
        var offer = Offer(Item("sword", 5), Item("sword", 1, "lordly"));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, new[] { Stack("sword", 5) }, null, null);

        Assert.Equal(5, remaining[0]);   // the plain swords are all still there
        Assert.Equal(0, remaining[1]);   // the lordly one was taken
    }

    [Fact]
    public void NullAndEmptyModifiers_AreTheSameIdentity()
    {
        // One comes off the wire, the other out of a roster with no modifier; splitting them would turn one
        // line into two and mis-count both.
        var offer = Offer(Item("grain", 5, null));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, new[] { Stack("grain", 5, "") }, null, null);

        Assert.Equal(new[] { 5 }, remaining);
    }

    [Fact]
    public void TwoLinesOfTheSameIdentity_ConsumeGreedilyInOrder()
    {
        // Both lines describe the same goods, so the split cannot change what the player ends up with - but
        // the answer must still be deterministic.
        var offer = Offer(Item("grain", 5), Item("grain", 5));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, new[] { Stack("grain", 7) }, null, null);

        Assert.Equal(5, remaining[0]);
        Assert.Equal(2, remaining[1]);
    }

    [Fact]
    public void ItemsAndTroopsOfTheSameId_DoNotBleedIntoEachOther()
    {
        var offer = Offer(Item("shared_id", 3), Prisoner("shared_id", 3));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, new[] { Stack("shared_id", 3) }, null, null);

        Assert.Equal(3, remaining[0]);   // the items are untouched
        Assert.Equal(0, remaining[1]);   // the prisoners are gone
    }

    [Fact]
    public void MembersAndPrisonersAreCountedSeparately()
    {
        var offer = Offer(
            new BattleLootOfferLine(BattleLootLineKind.Member, "recruit", null, 4),
            Prisoner("recruit", 6));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, null, new[] { Troop("recruit", 4) }, new[] { Troop("recruit", 2) });

        Assert.Equal(4, remaining[0]);
        Assert.Equal(2, remaining[1]);
    }

    [Fact]
    public void MoreLeftThanWasOffered_IsClampedToTheLine()
    {
        // Should not happen, but a miscount must not produce a negative "taken" downstream.
        var offer = Offer(Item("grain", 5));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, new[] { Stack("grain", 50) }, null, null);

        Assert.Equal(new[] { 5 }, remaining);
    }

    [Fact]
    public void StagedContentsThatMatchNoLine_AreIgnored()
    {
        var offer = Offer(Item("grain", 5));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, new[] { Stack("grain", 5), Stack("mystery", 99) }, null, null);

        Assert.Equal(new[] { 5 }, remaining);
    }

    [Fact]
    public void AHeroStillStaged_CountsAsNotTaken()
    {
        var offer = Offer(Prisoner("lord_1_68", 1, isHero: true));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, null, null, new[] { Troop("lord_1_68", 1) });

        Assert.Equal(new[] { 1 }, remaining);
    }

    [Fact]
    public void MatcherAndSelectionAgree_EndToEnd()
    {
        // The two halves have to compose: what the matcher counts must be what the differ can turn into a
        // claim the server will accept.
        var offer = Offer(Item("grain", 10), Item("sword", 1, "lordly"), Prisoner("looter", 5));

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer,
            new[] { Stack("grain", 4) },
            null,
            new[] { Troop("looter", 5) });

        var result = BattleLootSelection.FromRemaining(offer, remaining);

        Assert.True(BattleLootValidator.TryValidate(offer, result, out var resolved, out var rejection));
        Assert.Equal(BattleLootRejection.None, rejection);

        // 6 grain and the lordly sword taken; the looters left behind.
        Assert.Equal(2, resolved.Count);
        Assert.Equal(6, resolved[0].Count);
        Assert.Equal("lordly", resolved[1].Line.ModifierId);
    }
}
