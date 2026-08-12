using GameInterface.Services.MapEvents.Loot;
using GameInterface.Services.TroopRosters.Data;
using System;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// Building the indexed offer whose line order every later answer depends on.
/// </summary>
public class BattleLootOfferBuilderTests
{
    private static readonly Func<string, bool> NoHeroes = _ => false;

    private static BattleLootItemStack Stack(string id, int count, string modifier = null)
        => new BattleLootItemStack(id, modifier, count);

    private static TroopRosterElementData Troop(string id, int count, int wounded = 0, int xp = 0)
        => new TroopRosterElementData(id, count, wounded, xp);

    private static BattleLootOffer Build(
        BattleLootItemStack[] items = null,
        TroopRosterElementData[] members = null,
        TroopRosterElementData[] prisoners = null,
        Func<string, bool> isHero = null)
        => BattleLootOfferBuilder.Build(
            "offer-1", "MapEvent_1", "Party_1", items, members, prisoners, isHero ?? NoHeroes);

    [Fact]
    public void ItemsMembersAndPrisoners_BecomeLinesInThatOrder()
    {
        var offer = Build(
            items: new[] { Stack("grain", 5) },
            members: new[] { Troop("recruit", 2) },
            prisoners: new[] { Troop("looter", 3) });

        Assert.Equal(BattleLootLineKind.Item, offer.Lines[0].Kind);
        Assert.Equal(BattleLootLineKind.Member, offer.Lines[1].Kind);
        Assert.Equal(BattleLootLineKind.Prisoner, offer.Lines[2].Kind);
    }

    [Fact]
    public void EmptyStacksAreDropped_NotKeptAsZeroLines()
    {
        // A line nobody can take is only an index for a malformed claim to aim at.
        var offer = Build(
            items: new[] { Stack("grain", 0), Stack("meat", 2) },
            prisoners: new[] { Troop("looter", 0) });

        Assert.Equal("meat", Assert.Single(offer.Lines).ObjectId);
    }

    [Fact]
    public void ItemModifiersAreCarriedOntoTheirLine()
    {
        var offer = Build(items: new[] { Stack("sword", 1, "lordly") });

        Assert.Equal("lordly", Assert.Single(offer.Lines).ModifierId);
    }

    [Fact]
    public void WoundedAndXpSurviveForOrdinaryTroops()
    {
        var offer = Build(members: new[] { Troop("recruit", 5, wounded: 2, xp: 40) });

        var line = Assert.Single(offer.Lines);
        Assert.Equal(2, line.WoundedNumber);
        Assert.Equal(40, line.Xp);
    }

    [Fact]
    public void AHeroLineIsAlwaysExactlyOne()
    {
        // Packed data claiming several of one hero is already corrupt - the "appears 3x" shape that broke a
        // live save. The offer must not pass that on.
        var offer = Build(
            prisoners: new[] { Troop("lord_1_68", 3) },
            isHero: id => id == "lord_1_68");

        var line = Assert.Single(offer.Lines);
        Assert.True(line.IsHero);
        Assert.Equal(1, line.Count);
    }

    [Fact]
    public void AnOfferWithNothingInIt_IsEmptyRatherThanNull()
    {
        // Killing every enemy outright yields no prisoners, so this is a real outcome that still has to be
        // offered and answered.
        var offer = Build();

        Assert.True(offer.IsEmpty);
        Assert.NotNull(offer.Lines);
    }

    [Fact]
    public void NamelessEntriesAreSkipped()
    {
        var offer = Build(
            items: new[] { Stack(null, 5) },
            members: new[] { Troop(string.Empty, 5) });

        Assert.True(offer.IsEmpty);
    }

    [Fact]
    public void ABuiltOffer_CanBeAnsweredAndValidated()
    {
        // End to end across every pure piece: build, take some, match, answer, validate.
        var offer = Build(
            items: new[] { Stack("grain", 10) },
            prisoners: new[] { Troop("lord_1_68", 1) },
            isHero: id => id == "lord_1_68");

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer, new[] { Stack("grain", 6) }, null, null);

        var result = BattleLootSelection.FromRemaining(offer, remaining);

        Assert.True(BattleLootValidator.TryValidate(offer, result, out var resolved, out _));
        Assert.Equal(4, resolved.Single(c => c.Line.Kind == BattleLootLineKind.Item).Count);
        Assert.Contains(resolved, c => c.Line.IsHero);
    }
}
