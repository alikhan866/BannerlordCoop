using GameInterface.Services.MapEvents.TroopSupply;
using System;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// The rule that decides when a held battle is allowed to start.
/// </summary>
/// <remarks>
/// Every one of these is a way a battle could fail to start at all, which is the worst outcome this feature can
/// produce - worse than an unasked preference, because the players are left staring at a map.
/// </remarks>
public class TroopPreferenceBarrierTests
{
    private static readonly DateTime Start = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    private static TroopPreferenceBarrier Barrier(params string[] participants) =>
        new TroopPreferenceBarrier("MapEvent_1", participants, Start);

    [Fact]
    public void WaitsUntilEveryoneHasAnswered()
    {
        var sut = Barrier("Alifreeze", "Omar");

        Assert.False(sut.Record("Alifreeze"));
        Assert.False(sut.IsComplete);
        Assert.Equal(new[] { "Omar" }, sut.Outstanding);

        Assert.True(sut.Record("Omar"));
        Assert.True(sut.IsComplete);
        Assert.Empty(sut.Outstanding);
    }

    [Fact]
    public void ASoloBattleStartsImmediately()
    {
        Assert.True(Barrier("Alifreeze").Record("Alifreeze"));
    }

    [Fact]
    public void ABattleWithNoPlayersToAskIsAlreadyComplete()
    {
        // Otherwise an AI-only map event would hang at the barrier forever.
        Assert.True(Barrier().IsComplete);
    }

    [Fact]
    public void AnsweringTwiceDoesNotStartTheBattleEarly()
    {
        var sut = Barrier("Alifreeze", "Omar");

        Assert.False(sut.Record("Alifreeze"));
        Assert.False(sut.Record("Alifreeze"));
        Assert.False(sut.IsComplete);
        Assert.Equal(new[] { "Omar" }, sut.Outstanding);
    }

    [Fact]
    public void AnAnswerFromSomeoneNotInTheBattleIsIgnored()
    {
        // Counting it would let a stranger's message start a battle its participants had not answered for.
        var sut = Barrier("Alifreeze", "Omar");

        Assert.False(sut.Record("SomeoneElse"));
        Assert.Equal(new[] { "Alifreeze", "Omar" }, sut.Outstanding);
    }

    [Fact]
    public void SomeoneWhoLeavesStopsBeingWaitedOn()
    {
        // Without this, closing your game holds everyone at the starting line until the timeout.
        var sut = Barrier("Alifreeze", "Omar");

        Assert.False(sut.Record("Alifreeze"));
        Assert.True(sut.Forget("Omar"));
        Assert.True(sut.IsComplete);
    }

    [Fact]
    public void ForgettingSomeoneWhoAlreadyAnsweredStillLeavesTheBarrierComplete()
    {
        var sut = Barrier("Alifreeze", "Omar");
        sut.Record("Alifreeze");
        sut.Record("Omar");

        Assert.True(sut.Forget("Omar"));
        Assert.True(sut.IsComplete);
    }

    [Fact]
    public void ForgettingTheLastUnansweredPlayerCompletesIt()
    {
        var sut = Barrier("Omar");
        Assert.True(sut.Forget("Omar"));
        Assert.True(sut.IsComplete);
        Assert.Empty(sut.Outstanding);
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(31, true)]
    public void ExpiresSoAnAbsentPlayerCannotStallTheBattleForever(int elapsedSeconds, bool expired)
    {
        var sut = Barrier("Alifreeze", "Omar");

        Assert.Equal(expired, sut.HasExpired(Start.AddSeconds(elapsedSeconds), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void TheWaitingListIsStableSoItDoesNotJitter()
    {
        var a = Barrier("Omar", "Alifreeze", "Kan").Outstanding;
        var b = Barrier("Kan", "Alifreeze", "Omar").Outstanding;

        Assert.Equal(a, b);
    }
}
