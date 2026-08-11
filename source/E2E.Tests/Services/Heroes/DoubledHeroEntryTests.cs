using GameInterface.Services.Heroes.Commands;
using Xunit;

namespace E2E.Tests.Services.Heroes;

/// <summary>
/// A hero is one person, so any roster entry above one is corruption.
/// </summary>
/// <remarks>
/// Phycaon's dungeon held "Agnathea x3", "Phadon x2", "Lucon x2" and more, and the player's own party held
/// "Porphalios x2" - captures being applied more than once. The scan could not even see them: it read only
/// member rosters, only over <c>MobileParty.All</c> (which contains no settlement parties, so no dungeon was
/// ever examined), and only asked WHICH rosters held a hero, never how many.
///
/// The repair must use <c>SetElementNumber</c>. <c>AddToCountsAtIndex</c> calls
/// <c>PartyBase.OnHeroRemoved</c> for ANY negative change, whatever the resulting count, so trimming 2 -> 1
/// through it would release the prisoner instead of de-duplicating him.
/// </remarks>
public class DoubledHeroEntryTests
{
    [Fact]
    public void TwoOfTheSameHeroIsCorruption()
    {
        Assert.True(HeroDuplicateDebugCommands.IsDoubledEntry(2));
        Assert.True(HeroDuplicateDebugCommands.IsDoubledEntry(3)); // Agnathea
    }

    [Fact]
    public void OneOfAHeroIsCorrect()
    {
        Assert.False(HeroDuplicateDebugCommands.IsDoubledEntry(1));
        Assert.False(HeroDuplicateDebugCommands.IsDoubledEntry(0));
    }

    [Fact]
    public void ADoubledHeroIsTrimmedToExactlyOne()
    {
        Assert.Equal(1, HeroDuplicateDebugCommands.ClampedHeroCount(2));
        Assert.Equal(1, HeroDuplicateDebugCommands.ClampedHeroCount(3));
    }

    [Fact]
    public void AHealthySingleEntryIsLeftExactlyAsItIs()
    {
        // THE safety property: the repair must never touch a correct entry, and must never reach zero -
        // zero would remove the prisoner from the dungeon altogether.
        Assert.Equal(1, HeroDuplicateDebugCommands.ClampedHeroCount(1));
        Assert.Equal(0, HeroDuplicateDebugCommands.ClampedHeroCount(0));
    }

    [Fact]
    public void WoundedIsPulledDownWithTheCount()
    {
        // A doubled entry can carry two wounded. Leaving that against a count of 1 makes the roster report
        // negative healthy troops.
        Assert.Equal(1, HeroDuplicateDebugCommands.ClampedWoundedCount(wounded: 2, number: 1));
    }

    [Fact]
    public void WoundedWithinTheCountIsUntouched()
    {
        Assert.Equal(0, HeroDuplicateDebugCommands.ClampedWoundedCount(wounded: 0, number: 1));
        Assert.Equal(1, HeroDuplicateDebugCommands.ClampedWoundedCount(wounded: 1, number: 1));
    }
}
