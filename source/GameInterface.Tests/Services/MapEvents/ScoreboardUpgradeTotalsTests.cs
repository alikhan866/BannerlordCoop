using GameInterface.Services.MapEvents;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// A troop's scoreboard upgrade total must never run negative.
/// </summary>
/// <remarks>
/// Vanilla's TroopUpgradeTracker.CheckUpgradedCount reports a negative count to withdraw a troop type that
/// has left the party roster, and does not clear its own bookkeeping, so it keeps returning that same
/// negative. This mod asks on every hit reward and every agent removal - thousands of times per battle -
/// and the client ADDS each answer to a running total. One party's scoreboard reached -201406 while the
/// other, whose roster still held its troops, showed a normal 239.
/// </remarks>
public class ScoreboardUpgradeTotalsTests
{
    private const string Event = "MapEvent_1";
    private const string Party = "PartyBase_1";
    private const string Troop = "CharacterObject_infantryman";

    [Fact]
    public void PositiveCounts_PassThroughUnchanged()
    {
        var totals = new ScoreboardUpgradeTotals();
        Assert.Equal(3, totals.Accept(Event, Party, Troop, 3));
        Assert.Equal(2, totals.Accept(Event, Party, Troop, 2));
    }

    /// <summary>The one real withdrawal is forwarded in full.</summary>
    [Fact]
    public void FirstWithdrawal_IsForwarded()
    {
        var totals = new ScoreboardUpgradeTotals();
        totals.Accept(Event, Party, Troop, 5);
        Assert.Equal(-5, totals.Accept(Event, Party, Troop, -5));
    }

    /// <summary>The thousands of repeats after it are the bug, and must send nothing.</summary>
    [Fact]
    public void RepeatedWithdrawals_SendNothing()
    {
        var totals = new ScoreboardUpgradeTotals();
        totals.Accept(Event, Party, Troop, 5);
        Assert.Equal(-5, totals.Accept(Event, Party, Troop, -5));

        for (int i = 0; i < 2000; i++)
            Assert.Equal(0, totals.Accept(Event, Party, Troop, -5));
    }

    /// <summary>A withdrawal larger than the total is clamped, never driving the scoreboard below zero.</summary>
    [Fact]
    public void OversizedWithdrawal_ClampsAtZero()
    {
        var totals = new ScoreboardUpgradeTotals();
        totals.Accept(Event, Party, Troop, 2);
        Assert.Equal(-2, totals.Accept(Event, Party, Troop, -9));
        Assert.Equal(0, totals.Accept(Event, Party, Troop, -9));
    }

    [Fact]
    public void PartiesAndTroops_AreTrackedSeparately()
    {
        var totals = new ScoreboardUpgradeTotals();
        totals.Accept(Event, Party, Troop, 4);
        Assert.Equal(7, totals.Accept(Event, "PartyBase_2", Troop, 7));
        Assert.Equal(-4, totals.Accept(Event, Party, Troop, -4));
        Assert.Equal(-7, totals.Accept(Event, "PartyBase_2", Troop, -7));
    }

    [Fact]
    public void UpgradesAfterAWithdrawal_CountAgain()
    {
        var totals = new ScoreboardUpgradeTotals();
        totals.Accept(Event, Party, Troop, 5);
        totals.Accept(Event, Party, Troop, -5);
        Assert.Equal(3, totals.Accept(Event, Party, Troop, 3));
    }

    [Fact]
    public void Forget_ClearsOnlyThatBattle()
    {
        var totals = new ScoreboardUpgradeTotals();
        totals.Accept(Event, Party, Troop, 5);
        totals.Accept("MapEvent_2", Party, Troop, 6);

        totals.Forget(Event);

        Assert.Equal(0, totals.Accept(Event, Party, Troop, -5));
        Assert.Equal(-6, totals.Accept("MapEvent_2", Party, Troop, -6));
    }
}
