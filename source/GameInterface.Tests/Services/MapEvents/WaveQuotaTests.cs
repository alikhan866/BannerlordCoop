using GameInterface.Services.MapEvents.TroopSupply;
using System;
using System.Collections.Generic;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// Proves the wave-quota invariant instead of sampling it.
/// </summary>
/// <remarks>
/// The two properties below are the ones the battle actually depends on, and neither is obvious from reading
/// the arithmetic:
///
///   EXACTLY the target is handed out. Deployment reserves InitialSpawnNumber - ReservedTroopsCount and skips
///   the whole side while the count falls short, so a wave that under-delivers by one stops the side being
///   planned rather than merely arriving thin. Over-delivering is just as bad in the other direction: what
///   this client contributes is its slice of a partition every owner computes independently, so exceeding it
///   spends capacity that belonged to somebody else - which is exactly the "my troops filled the field and he
///   could not spawn" failure this option must not introduce.
///
///   No party gives more than it holds.
///
/// Driven over pseudo-random shapes rather than a handful of hand-picked cases, because the failures that
/// matter here are rounding ones: they hide in awkward ratios, not in round numbers. The generator is seeded,
/// so a failure is reproducible rather than a heisenbug.
/// </remarks>
public class WaveQuotaTests
{
    private const int Cases = 20000;

    public static IEnumerable<object[]> Modes => new[]
    {
        new object[] { true },   // own-party-first
        new object[] { false },  // proportional
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void HandsOutExactlyTheTarget_AndNeverMoreThanAPartyHolds(bool ownFirst)
    {
        var random = new Random(20260829);

        for (int iteration = 0; iteration < Cases; iteration++)
        {
            int partyCount = random.Next(1, 9);
            var remaining = new int[partyCount];
            for (int i = 0; i < partyCount; i++)
            {
                // Include empty parties: a drained party is the common late-battle shape and it is where an
                // off-by-one in the cumulative flooring would surface.
                remaining[i] = random.Next(0, 400);
            }

            long total = 0;
            foreach (int value in remaining) total += value;

            // Over-ask deliberately: the engine asks for a side's whole deficit, so target > total is normal.
            long target = random.Next(0, (int)Math.Max(1, total * 2));
            int ownIndex = random.Next(-1, partyCount);

            int[] quota = ownFirst
                ? WaveQuota.OwnPartyFirst(remaining, target, ownIndex)
                : WaveQuota.Proportional(remaining, target);

            long handedOut = 0;
            for (int i = 0; i < partyCount; i++)
            {
                Assert.True(quota[i] >= 0, Describe("negative quota", remaining, target, ownIndex, quota, i));
                Assert.True(
                    quota[i] <= remaining[i],
                    Describe("party gave more than it holds", remaining, target, ownIndex, quota, i));
                handedOut += quota[i];
            }

            long expected = Math.Min(Math.Max(0, target), total);
            Assert.True(
                handedOut == expected,
                Describe($"handed out {handedOut}, expected {expected}", remaining, target, ownIndex, quota, -1));
        }
    }

    /// <summary>
    /// Own-party-first actually prefers the own party: it takes everything it can before anyone else is asked.
    /// </summary>
    [Fact]
    public void OwnPartyFirst_FillsTheOwnPartyBeforeTheOthers()
    {
        var random = new Random(20260830);

        for (int iteration = 0; iteration < Cases; iteration++)
        {
            int partyCount = random.Next(1, 9);
            var remaining = new int[partyCount];
            for (int i = 0; i < partyCount; i++) remaining[i] = random.Next(0, 400);

            long total = 0;
            foreach (int value in remaining) total += value;

            long target = random.Next(0, (int)Math.Max(1, total * 2));
            int ownIndex = random.Next(0, partyCount);

            int[] quota = WaveQuota.OwnPartyFirst(remaining, target, ownIndex);

            long capped = Math.Min(Math.Max(0, target), total);
            long expectedOwn = Math.Min(remaining[ownIndex], capped);
            Assert.True(
                quota[ownIndex] == expectedOwn,
                Describe($"own party got {quota[ownIndex]}, expected {expectedOwn}",
                    remaining, target, ownIndex, quota, ownIndex));

            // Nobody else is asked while the own party still has troops the wave could have used.
            if (quota[ownIndex] < remaining[ownIndex])
            {
                for (int i = 0; i < partyCount; i++)
                {
                    if (i == ownIndex) continue;
                    Assert.True(
                        quota[i] == 0,
                        Describe("another party supplied while the own party still had troops",
                            remaining, target, ownIndex, quota, i));
                }
            }
        }
    }

    /// <summary>
    /// The default path is untouched by the new option - byte-for-byte the old distribution.
    /// </summary>
    /// <remarks>
    /// The proportional split is what every existing battle already uses. Adding an option must not quietly
    /// re-tune it, so this pins the shape: a wave that fits is spread across the parties rather than drained
    /// from the first.
    /// </remarks>
    [Fact]
    public void Proportional_SpreadsAcrossParties()
    {
        int[] remaining = { 100, 100, 100 };

        int[] quota = WaveQuota.Proportional(remaining, 30);

        Assert.Equal(new[] { 10, 10, 10 }, quota);
    }

    [Fact]
    public void OwnPartyFirst_DrainsTheOwnPartyForTheSameWave()
    {
        int[] remaining = { 100, 100, 100 };

        int[] quota = WaveQuota.OwnPartyFirst(remaining, 30, ownIndex: 1);

        Assert.Equal(new[] { 0, 30, 0 }, quota);
    }

    [Fact]
    public void OwnPartyFirst_SpillsToTheOthersOnceTheOwnPartyIsExhausted()
    {
        int[] remaining = { 100, 20, 100 };

        int[] quota = WaveQuota.OwnPartyFirst(remaining, 60, ownIndex: 1);

        Assert.Equal(20, quota[1]);
        Assert.Equal(40, quota[0] + quota[2]);
    }

    [Fact]
    public void NoPartiesOrNothingLeft_HandsOutNothing()
    {
        Assert.Empty(WaveQuota.Proportional(Array.Empty<int>(), 10));
        Assert.Empty(WaveQuota.OwnPartyFirst(Array.Empty<int>(), 10, 0));
        Assert.Equal(new[] { 0, 0 }, WaveQuota.Proportional(new[] { 0, 0 }, 10));
        Assert.Equal(new[] { 0, 0 }, WaveQuota.OwnPartyFirst(new[] { 0, 0 }, 10, 1));
    }

    private static string Describe(
        string what, IReadOnlyList<int> remaining, long target, int ownIndex, IReadOnlyList<int> quota, int index)
        => $"{what} | remaining=[{string.Join(",", remaining)}] target={target} ownIndex={ownIndex} " +
           $"quota=[{string.Join(",", quota)}]" + (index >= 0 ? $" at index {index}" : string.Empty);
}
