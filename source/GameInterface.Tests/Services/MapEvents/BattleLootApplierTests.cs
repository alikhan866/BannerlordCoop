using Common.Util;
using GameInterface.Services.MapEvents.Loot;
using GameInterface.Services.ObjectManager;
using Moq;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// The one part of <see cref="BattleLootApplier"/> that can be tested without a campaign: deciding which
/// object a hero loot line refers to.
/// </summary>
/// <remarks>
/// This is where captured lords were lost. An offer keys every line - items, troops and heroes alike - by the
/// id of the object the roster element holds, and for a hero that is a CharacterObject. The applier asked for
/// a Hero with that id instead, which can never match, so every claimed lord was skipped with a warning and
/// the player got nothing. Measured over one evening: 13 offers carried heroes, 14 claims, 14 failures, zero
/// lords imprisoned.
/// </remarks>
public class BattleLootApplierTests
{
    private const string HeroLineId = "CharacterObject_lord_1_68";

    [Fact]
    public void HeroLine_ResolvesThroughTheCharacterObjectSpace()
    {
        var hero = ObjectHelper.SkipConstructor<Hero>();
        var character = ObjectHelper.SkipConstructor<CharacterObject>();
        character.HeroObject = hero;

        var objectManager = new Mock<IObjectManager>();
        SetupObject(objectManager, HeroLineId, character);

        Assert.True(BattleLootApplier.TryResolveHero(objectManager.Object, HeroLineId, out var resolved));
        Assert.Same(hero, resolved);
    }

    [Fact]
    public void HeroLine_IsNotResolvedThroughTheHeroIdSpace()
    {
        // Guards the decision NOT to probe both id spaces. The hero is reachable here under the very id being
        // asked for - but only in the Hero space - so a resolver that fell back to it would succeed. It must
        // not: a field whose meaning depends on which lookup happens to answer is the defect this fix removes,
        // and a silent second attempt would hide the next mismatch exactly as the first one was hidden.
        var hero = ObjectHelper.SkipConstructor<Hero>();
        var objectManager = new Mock<IObjectManager>();
        SetupObject(objectManager, HeroLineId, hero);

        Assert.False(BattleLootApplier.TryResolveHero(objectManager.Object, HeroLineId, out var resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void RegularTroop_IsNeverTreatedAsAHero()
    {
        // A CharacterObject with no hero behind it must not resolve. Heroes move by TakePrisonerAction, and
        // handing that a troop would be worse than skipping it.
        var character = ObjectHelper.SkipConstructor<CharacterObject>();
        character.HeroObject = null;

        var objectManager = new Mock<IObjectManager>();
        SetupObject(objectManager, "CharacterObject_imperial_recruit", character);

        Assert.False(BattleLootApplier.TryResolveHero(
            objectManager.Object, "CharacterObject_imperial_recruit", out var resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void UnknownId_DoesNotResolve()
    {
        var objectManager = new Mock<IObjectManager>();

        Assert.False(BattleLootApplier.TryResolveHero(objectManager.Object, "CharacterObject_missing", out var resolved));
        Assert.Null(resolved);
    }

    private static void SetupObject<T>(Mock<IObjectManager> objectManager, string id, T instance)
        where T : class
    {
        objectManager.Setup(manager => manager.TryGetObject(id, out instance)).Returns(true);
    }
}
