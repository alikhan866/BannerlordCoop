using GameInterface.Services.Fixtures;
using System;
using System.Collections.Generic;
using Xunit;

namespace GameInterface.Tests.Services.Fixtures;

/// <summary>
/// C20's bookkeeping. Every rule here fails SILENTLY when it is wrong - a fixture that reports a clean restore
/// while leaving the world altered poisons every run after it, and nothing downstream reveals the cause. That
/// is why they are tested with plain lambdas rather than left to be discovered against a live campaign.
/// </summary>
public class FixtureUndoLogTests
{
    [Fact]
    public void Restore_UndoesInReverseOrder()
    {
        // Changes interact: a hero taken prisoner after their settlement changed hands has to be released
        // before the settlement goes back. Forward order is right only by luck.
        var order = new List<string>();
        var log = new FixtureUndoLog();
        log.Record("first", "first", true, () => order.Add("first"));
        log.Record("second", "second", true, () => order.Add("second"));
        log.Record("third", "third", true, () => order.Add("third"));

        log.Restore();

        Assert.Equal(new[] { "third", "second", "first" }, order);
    }

    [Fact]
    public void Record_KeepsTheFirstOriginal_NotTheLatest()
    {
        // A scenario that sets gold twice must restore to the value from before the fixture. Keeping the later
        // record would restore to an intermediate value the world only passed through.
        var gold = 100;
        var log = new FixtureUndoLog();

        var beforeFixture = gold;
        log.Record("gold:hero", "gold 100", true, () => gold = beforeFixture);
        gold = 5000;

        var intermediate = gold;
        log.Record("gold:hero", "gold 5000", true, () => gold = intermediate);
        gold = 9000;

        log.Restore();

        Assert.Equal(100, gold);
    }

    [Fact]
    public void Record_KeepingTheFirstOriginal_DoesNotStackEntries()
    {
        var log = new FixtureUndoLog();
        log.Record("gold:hero", "first", true, () => { });
        log.Record("gold:hero", "second", true, () => { });

        Assert.Equal(1, log.Count);
        Assert.Equal("first", log.Entries[0].Description);
    }

    [Fact]
    public void Record_TreatsDifferentKeysAsDifferentTargets()
    {
        var log = new FixtureUndoLog();
        log.Record("gold:hero_a", "a", true, () => { });
        log.Record("gold:hero_b", "b", true, () => { });

        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void Restore_ContinuesAfterAFailingUndo()
    {
        // A half-restored world is worse than a mostly-restored one. The first exception must not cost the
        // remaining undos.
        var restored = new List<string>();
        var log = new FixtureUndoLog();
        log.Record("ok:one", "one", true, () => restored.Add("one"));
        log.Record("broken", "broken", true, () => throw new InvalidOperationException("could not undo"));
        log.Record("ok:two", "two", true, () => restored.Add("two"));

        var outcome = log.Restore();

        Assert.Equal(new[] { "two", "one" }, restored);
        Assert.Equal(2, outcome.Restored);
        Assert.Equal(3, outcome.Attempted);
        Assert.Single(outcome.Failures);
        Assert.Contains("could not undo", outcome.Failures[0]);
    }

    [Fact]
    public void Restore_IsNotExactWhenAnUndoFailed()
    {
        var log = new FixtureUndoLog();
        log.Record("broken", "broken", true, () => throw new InvalidOperationException("no"));

        Assert.False(log.Restore().Exact);
    }

    [Fact]
    public void Restore_IsNotExactWhenAnUndoWasOnlyApproximate()
    {
        // The dangerous case: everything "restored", nothing threw, and the world is still not what it was.
        // Captivity is the real instance - releasing a prisoner runs consequences that capturing never applied.
        var log = new FixtureUndoLog();
        log.Record("captive:hero", "hero was free", exact: false, undo: () => { });

        var outcome = log.Restore();

        Assert.Equal(1, outcome.Restored);
        Assert.Empty(outcome.Failures);
        Assert.False(outcome.Exact);
        Assert.Equal("captive:hero", Assert.Single(outcome.Approximate));
    }

    [Fact]
    public void Restore_IsExactOnlyWhenEverythingCameBackCleanly()
    {
        var log = new FixtureUndoLog();
        log.Record("gold:hero", "gold", true, () => { });
        log.Record("owner:town", "owner", true, () => { });

        var outcome = log.Restore();

        Assert.True(outcome.Exact);
        Assert.Equal(2, outcome.Restored);
        Assert.Empty(outcome.Approximate);
    }

    [Fact]
    public void Restore_EmptiesTheLogSoAFixtureCannotBeUndoneTwice()
    {
        var undos = 0;
        var log = new FixtureUndoLog();
        log.Record("gold:hero", "gold", true, () => undos++);

        log.Restore();
        var second = log.Restore();

        Assert.Equal(1, undos);
        Assert.Equal(0, log.Count);
        Assert.Equal(0, second.Attempted);
    }

    [Fact]
    public void Restore_OfAnEmptyLogIsExact()
    {
        Assert.True(new FixtureUndoLog().Restore().Exact);
    }

    [Fact]
    public void Record_RejectsAnEntryItCouldNeverUndo()
    {
        var log = new FixtureUndoLog();

        Assert.Throws<ArgumentNullException>(() => log.Record("key", "description", true, null));
        Assert.Throws<ArgumentException>(() => log.Record("", "description", true, () => { }));
        Assert.Equal(0, log.Count);
    }
}
