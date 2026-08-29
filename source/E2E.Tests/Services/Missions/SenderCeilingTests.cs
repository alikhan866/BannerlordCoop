using Missions.Agents.Handlers;
using System;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// What a client is allowed to send at, and why the frame rate alone no longer decides it.
/// </summary>
/// <remarks>
/// Built from a real player's telemetry: 3330 in-battle samples, median 47 fps, sending costing 6% of each
/// second, throttled to 20-30Hz for 87% of the battle. The frame rate was the base game drawing 570-1090
/// agents; throttling movement could never have recovered it, and the only effect was that he saw everyone
/// else at a third of the rate - agents jumping between positions, blows landing where they used to be.
/// </remarks>
public class SenderCeilingTests
{
    /// <summary>
    /// The ladder as it was: frame rate and cost as ALTERNATIVES, so either could step the rate down alone.
    /// </summary>
    private static int PreviousLadderHz(float normalizedFramesPerSecond, double duty)
    {
        if (normalizedFramesPerSecond < 25f || duty > 0.30d) return 10;
        if (normalizedFramesPerSecond < 35f || duty > 0.25d) return 15;
        if (normalizedFramesPerSecond < 45f || duty > 0.20d) return 20;
        if (normalizedFramesPerSecond < 55f || duty > 0.15d) return 30;
        if (normalizedFramesPerSecond < 58f || duty > 0.12d) return 40;
        return 60;
    }

    [Fact]
    public void TheReportedClientIsNoLongerThrottledForAFrameRateItDidNotCause()
    {
        // His medians. Every duty tier begins at 12%, so none of them were ever close to firing.
        Assert.Equal(30, PreviousLadderHz(47.4f, 0.062d));
        Assert.Equal(40, MovementRateController.SenderCeilingHz(47.4f, 0.062d));

        // And the dips below 45 fps, which is where the 20Hz he spent most of the battle at came from.
        Assert.Equal(20, PreviousLadderHz(43f, 0.062d));
        Assert.Equal(40, MovementRateController.SenderCeilingHz(43f, 0.062d));
    }

    [Fact]
    public void AClientWhoseFrameRateEarnsFullRateKeepsIt()
    {
        // The floor raises; it must never cap someone who was already allowed more.
        Assert.Equal(60, MovementRateController.SenderCeilingHz(60f, 0.05d));
    }

    [Theory]
    [InlineData(0.13d, 40)]
    [InlineData(0.16d, 30)]
    [InlineData(0.22d, 20)]
    [InlineData(0.26d, 15)]
    [InlineData(0.31d, 10)]
    public void SendingThatIsGenuinelyExpensiveStillThrottles(double duty, int expected)
    {
        // A healthy frame rate does not excuse real cost - the floor is not applied once sending is the problem.
        Assert.Equal(expected, MovementRateController.SenderCeilingHz(60f, duty));
    }

    [Fact]
    public void AFrameRateInRealTroubleStillShedsWorkWhoeverCausedIt()
    {
        Assert.Equal(10, MovementRateController.SenderCeilingHz(18f, 0.03d));
    }

    [Fact]
    public void TheFloorAppliesRightUpToTheFirstRealCostTier()
    {
        Assert.Equal(40, MovementRateController.SenderCeilingHz(47.4f, 0.12d));   // not yet meaningful
        Assert.Equal(30, MovementRateController.SenderCeilingHz(47.4f, 0.121d));  // now it is: frame ladder bites
    }

    /// <summary>
    /// The safety property: this change can only ever RAISE the ceiling.
    /// </summary>
    /// <remarks>
    /// If it could lower one, it would be throttling someone the old code did not - which is the fault being
    /// fixed, reintroduced from the other side.
    /// </remarks>
    [Fact]
    public void NeverThrottlesHarderThanBefore()
    {
        for (float fps = 0f; fps <= 90f; fps += 0.5f)
        {
            for (double duty = 0d; duty <= 0.45d; duty += 0.005d)
            {
                Assert.True(
                    MovementRateController.SenderCeilingHz(fps, duty) >= PreviousLadderHz(fps, duty),
                    $"fps={fps} duty={duty} lowered the ceiling");
            }
        }
    }

    /// <summary>
    /// Wherever the old ladder was justified - real cost, or a frame rate in real trouble - nothing changes.
    /// </summary>
    [Fact]
    public void IsUnchangedWhereverTheOldLadderWasJustified()
    {
        for (float fps = 0f; fps <= 90f; fps += 0.5f)
        {
            for (double duty = 0d; duty <= 0.45d; duty += 0.005d)
            {
                bool sendingIsTheProblem = duty > MovementRateController.MeaningfulSenderDuty;
                bool frameIsInTrouble = fps < MovementRateController.EmergencyFramesPerSecond;
                if (!sendingIsTheProblem && !frameIsInTrouble) continue;

                Assert.Equal(PreviousLadderHz(fps, duty), MovementRateController.SenderCeilingHz(fps, duty));
            }
        }
    }

    /// <summary>
    /// The ceiling never exceeds what either ladder permits on its own.
    /// </summary>
    [Fact]
    public void NeverExceedsTheFloorWhenNeitherLadderWouldAllowIt()
    {
        for (float fps = 20f; fps <= 90f; fps += 0.5f)
        {
            for (double duty = 0d; duty <= MovementRateController.MeaningfulSenderDuty; duty += 0.005d)
            {
                int actual = MovementRateController.SenderCeilingHz(fps, duty);
                int unaided = Math.Min(
                    MovementRateController.FrameCeilingHz(fps),
                    MovementRateController.DutyCeilingHz(duty));

                Assert.Equal(Math.Max(unaided, MovementRateController.UnattributedFrameFloorHz), actual);
            }
        }
    }
}
