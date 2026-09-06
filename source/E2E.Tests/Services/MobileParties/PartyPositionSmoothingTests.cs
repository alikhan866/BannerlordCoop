using System.Linq;
using GameInterface.Services.MobileParties;
using Xunit;

namespace E2E.Tests.Services.MobileParties;

/// <summary>
/// The rule and the arithmetic behind delivering a party-position correction over frames instead of in one
/// assignment (see <see cref="PartyPositionSmoothing"/>). Both are pure, so they are asserted without a campaign.
/// </summary>
public class PartyPositionSmoothingTests
{
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.1f)]
    [InlineData(PartyPositionSmoothing.NegligibleDistance)]
    public void ACorrectionTooSmallToSee_IsAssigned(float distance)
    {
        Assert.Equal(PartyPositionSmoothing.Delivery.Assign, PartyPositionSmoothing.Decide(distance));
    }

    [Theory]
    [InlineData(PartyPositionSmoothing.HardSnapDistance)]
    [InlineData(120f)]
    [InlineData(900f)]
    public void ARelocation_IsAssigned_RatherThanSlidAcrossTheMap(float distance)
    {
        // A settlement exit, an army attaching or a spawn is not drift; sliding the party there would draw a
        // journey that never happened.
        Assert.Equal(PartyPositionSmoothing.Delivery.Assign, PartyPositionSmoothing.Decide(distance));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(8f)]
    [InlineData(39f)]
    public void DriftSizedCorrections_AreSmoothed(float distance)
    {
        Assert.Equal(PartyPositionSmoothing.Delivery.Smooth, PartyPositionSmoothing.Decide(distance));
    }

    [Fact]
    public void SwitchedOff_EverythingIsAssigned_SoARunCanMeasureBothWays()
    {
        Assert.Equal(PartyPositionSmoothing.Delivery.Assign, PartyPositionSmoothing.Decide(8f, enabled: false));
    }

    [Fact]
    public void TheDrainClosesTheGapWithoutOvershooting_AndTerminates()
    {
        // 30 units out: the worst drift measured live was 43, and anything over 40 is assigned instead.
        const float dt = 1f / 60f;
        float x = 18f, y = 24f;
        float startLength = 30f;
        float previousLength = startLength;
        int frames = 0;
        float firstStep = 0f;
        while (true)
        {
            var (step, remaining, finished) = PartyPositionSmoothing.NextStep(x, y, dt);
            float stepLength = (float)System.Math.Sqrt(step.X * step.X + step.Y * step.Y);
            if (frames == 0) firstStep = stepLength;
            frames++;
            x = remaining.X;
            y = remaining.Y;
            float length = (float)System.Math.Sqrt(x * x + y * y);
            // Never past the target, and always closer than it was.
            Assert.True(length < previousLength + 1e-4f, "the gap must never grow");
            Assert.True(length >= -1e-4f);
            previousLength = length;
            if (finished)
            {
                Assert.Equal(0f, x);
                Assert.Equal(0f, y);
                break;
            }
            Assert.True(frames < 200, "the drain must terminate");
        }

        // The first frame is what the player sees instead of the whole jump: a fifth of it, not all of it.
        Assert.True(firstStep < startLength * 0.25f, $"first frame moved {firstStep} of {startLength}");
        // And it is over quickly: about a third of a second, whatever the frame rate.
        Assert.InRange(frames * dt, 0.1f, 0.6f);
    }

    [Fact]
    public void TheGapClosesInTheSameTimeAtAnyFrameRate()
    {
        // Frames are not a unit of time: at 30 fps a per-frame share would take twice as long to arrive.
        float Seconds(float dt)
        {
            float x = 20f, y = 0f, t = 0f;
            for (int i = 0; i < 10000; i++)
            {
                var (_, remaining, finished) = PartyPositionSmoothing.NextStep(x, y, dt);
                x = remaining.X;
                y = remaining.Y;
                t += dt;
                if (finished) break;
            }
            return t;
        }

        float at60 = Seconds(1f / 60f);
        float at30 = Seconds(1f / 30f);
        Assert.InRange(at30, at60 * 0.6f, at60 * 1.7f);
    }

    [Fact]
    public void TheDrainIsHookedToAFrameTick_NotACampaignTick()
    {
        // Both campaign ticks were tried and neither is a frame: Campaign.Tick runs about once a second and
        // Campaign.RealTick about twice, which stretched every correction over tens of seconds. If this ever
        // moves back to a campaign tick, the smoothing silently stops smoothing.
        var patch = typeof(GameInterface.Services.MobileParties.PartyPositionSmoothing).Assembly
            .GetType("GameInterface.Services.MobileParties.Patches.PartyPositionSmoothingPatch", throwOnError: true);
        var attributes = patch.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), inherit: false);
        var target = Assert.Single(attributes.OfType<HarmonyLib.HarmonyPatch>().Where(a => a.info.declaringType != null));
        Assert.Equal("MapState", target.info.declaringType.Name);
        Assert.Equal("OnMapModeTick", target.info.methodName);
    }

    [Fact]
    public void APendingCorrectionIsCountedAsAlreadyArrived_WhenJudgingTheNextOne()
    {
        // The drawn position is behind by whatever is still draining. Judging the next correction against it
        // counts that part of the gap twice, so more updates cross the threshold and each restarts the residual;
        // the settled position is the one to compare against. With nothing pending the two are the same, which is
        // all this can assert without a campaign - the party-side behaviour is covered by the live drift runs.
        Assert.Equal(PartyPositionSmoothing.Delivery.Smooth, PartyPositionSmoothing.Decide(9f));
        var (step, remaining, _) = PartyPositionSmoothing.NextStep(9f, 0f, 1f / 60f);
        Assert.True(step.X > 0f && remaining.X > 0f, "part of the gap is still on its way after one frame");
        Assert.Equal(9f, step.X + remaining.X, 3);
    }

    [Fact]
    public void TheLastSliver_IsAppliedExactly_RatherThanHalvedForever()
    {
        var (step, remaining, finished) = PartyPositionSmoothing.NextStep(PartyPositionSmoothing.FinishDistance * 0.5f, 0f, 1f / 60f);
        Assert.True(finished);
        Assert.Equal(PartyPositionSmoothing.FinishDistance * 0.5f, step.X);
        Assert.Equal(0f, remaining.X);
    }
}
