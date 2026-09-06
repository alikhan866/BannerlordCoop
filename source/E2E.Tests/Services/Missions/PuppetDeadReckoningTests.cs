using Missions.Agents;
using TaleWorlds.Library;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// A puppet is aimed at where its owner IS, not where it was when the frame was sent.
/// </summary>
/// <remarks>
/// Below the six-metre snap distance a puppet WALKS to its replicated position under its own
/// locomotion. While the owner keeps moving it never arrives, and the drift is never corrected because
/// it stays under the snap threshold. Measured across 20,311 paired samples the drawn position trailed
/// the owner's by a mean of 0.41m, 1.08m at p90 and up to 3.33m - against a melee reach of about 2m, so
/// a legitimate blow could appear to land from over three metres away.
///
/// Leading by the owner's velocity times the age of the frame cancels that trail. These tests are about
/// the guards: a dropped or reordered update must never be multiplied into a fling.
/// </remarks>
public class PuppetDeadReckoningTests
{
    private static Vec3 Step(float metres) => new Vec3(metres, 0f, 0f);

    [Fact]
    public void SteadyWalk_IsLedByTheFrameAge()
    {
        // 1 m in 100 ms = 10 m/s is too fast; use 0.2m per 100ms = 2 m/s, aged by another 100ms.
        Assert.True(AgentPositionInterpolator.TryComputeLead(Step(0.2f), 0.1f, 0.1f, out Vec3 lead));
        Assert.Equal(0.2f, lead.x, 3);
    }

    [Fact]
    public void HalfAFrameOfAge_LeadsHalfAStep()
    {
        Assert.True(AgentPositionInterpolator.TryComputeLead(Step(0.2f), 0.1f, 0.05f, out Vec3 lead));
        Assert.Equal(0.1f, lead.x, 3);
    }

    /// <summary>A standing agent must not be nudged around by jitter.</summary>
    [Fact]
    public void StandingStill_IsNotLed()
    {
        Assert.False(AgentPositionInterpolator.TryComputeLead(Step(0.01f), 0.1f, 0.1f, out _));
    }

    /// <summary>A dropped update makes two frames look like a leap; that is not a sprint.</summary>
    [Fact]
    public void ImpossibleSpeed_IsRefused()
    {
        Assert.False(AgentPositionInterpolator.TryComputeLead(Step(50f), 0.1f, 0.1f, out _));
    }

    [Theory]
    [InlineData(0.001f)]   // interval too short to trust
    [InlineData(5f)]       // a gap this long says nothing about current velocity
    public void UnusableInterval_IsRefused(float interval)
    {
        Assert.False(AgentPositionInterpolator.TryComputeLead(Step(0.5f), interval, 0.1f, out _));
    }

    [Theory]
    [InlineData(0f)]       // no age, nothing to compensate for
    [InlineData(-0.1f)]    // clock went backwards
    [InlineData(5f)]       // frame is ancient; leading it would invent a position
    public void UnusableAge_IsRefused(float age)
    {
        Assert.False(AgentPositionInterpolator.TryComputeLead(Step(0.2f), 0.1f, age, out _));
    }

    /// <summary>
    /// The lead is capped well inside the 6m snap distance, so the worst case is a puppet slightly
    /// ahead of itself rather than one thrown across the field.
    /// </summary>
    [Fact]
    public void LongLead_IsClampedNotRefused()
    {
        // 1m per 100ms = 10 m/s (allowed), aged 400ms. The age ratio is clamped to one period, giving
        // 1m of travel, which is then the distance cap as well.
        Assert.True(AgentPositionInterpolator.TryComputeLead(Step(1f), 0.1f, 0.4f, out Vec3 lead));
        Assert.Equal(1f, lead.Length, 3);
    }

    /// <summary>
    /// Leading several update periods ahead invents a position the owner never occupied. Measured with
    /// the ratio unclamped the median improved but the tail worsened - p99 1.57m to 2.76m, max 3.33m to
    /// 6.08m, past the snap distance so puppets began teleporting.
    /// </summary>
    [Fact]
    public void AgeBeyondOnePeriod_LeadsAtMostOneStep()
    {
        Assert.True(AgentPositionInterpolator.TryComputeLead(Step(0.3f), 0.1f, 0.5f, out Vec3 five));
        Assert.True(AgentPositionInterpolator.TryComputeLead(Step(0.3f), 0.1f, 0.1f, out Vec3 one));
        Assert.Equal(one.Length, five.Length, 3);
        Assert.Equal(0.3f, five.Length, 3);
    }

    [Fact]
    public void NonFiniteInputs_AreRefused()
    {
        Assert.False(AgentPositionInterpolator.TryComputeLead(Step(0.2f), float.NaN, 0.1f, out _));
        Assert.False(AgentPositionInterpolator.TryComputeLead(Step(0.2f), 0.1f, float.NaN, out _));
        Assert.False(AgentPositionInterpolator.TryComputeLead(
            new Vec3(float.NaN, 0f, 0f), 0.1f, 0.1f, out _));
    }

    /// <summary>Direction is preserved; only the magnitude is scaled by age.</summary>
    [Fact]
    public void LeadFollowsTheDirectionOfTravel()
    {
        Assert.True(AgentPositionInterpolator.TryComputeLead(
            new Vec3(0f, 0.2f, 0f), 0.1f, 0.1f, out Vec3 lead));
        Assert.Equal(0f, lead.x, 3);
        Assert.Equal(0.2f, lead.y, 3);
    }

    // ---- mounted lead: the owner's sent horse velocity x (frame age, capped 0.1 s + one-way delay, capped 0.3 s
    //      + one frame of presentation lag, 20 ms) ----

    private static Vec2 Gallop(float metresPerSecond) => new Vec2(metresPerSecond, 0f);

    /// <summary>A canter at 8 m/s read 10 ms after arrival on a zero-delay link: 8 x (0.010 + 0.020) = 0.24 m.</summary>
    [Fact]
    public void Canter_IsLedByAgeAndPresentationLag()
    {
        Assert.True(AgentPositionInterpolator.TryComputeMountLead(Gallop(8f), 0.01f, 0f, out Vec3 lead));
        Assert.Equal(8f * (0.01f + 0.02f), lead.x, 3);
    }

    /// <summary>A stalled stream is extrapolated 100 ms at most, not for ever.</summary>
    [Fact]
    public void StaleMountFrame_LeadsAtMostTheAgeCap()
    {
        Assert.True(AgentPositionInterpolator.TryComputeMountLead(Gallop(8f), 0.4f, 0f, out Vec3 lead));
        Assert.Equal(8f * (0.1f + 0.02f), lead.x, 3);
    }

    /// <summary>A gallop is a real horse speed and is led; 50 m/s is a bad frame and is not.</summary>
    [Fact]
    public void GallopIsLed_TeleportIsRefused()
    {
        Assert.True(AgentPositionInterpolator.TryComputeMountLead(Gallop(14f), 0.01f, 0f, out _));
        Assert.False(AgentPositionInterpolator.TryComputeMountLead(Gallop(50f), 0.01f, 0f, out _));
    }

    /// <summary>The lead never reaches past 3 m, a quarter of the mount snap distance.</summary>
    [Fact]
    public void MountLead_IsCapped()
    {
        Assert.True(AgentPositionInterpolator.TryComputeMountLead(Gallop(20f), 0.1f, 0.3f, out Vec3 lead));
        Assert.Equal(3f, lead.Length, 3);
    }

    [Fact]
    public void StandingHorse_IsNotLed()
    {
        Assert.False(AgentPositionInterpolator.TryComputeMountLead(Gallop(0.1f), 0.01f, 0f, out _));
    }

    /// <summary>Between two real machines the delay dominates: 12 m/s with 50 ms one way is 0.6 m of lead on its own.</summary>
    [Fact]
    public void NetworkDelay_IsLedToo()
    {
        Assert.True(AgentPositionInterpolator.TryComputeMountLead(Gallop(12f), 0.01f, 0.05f, out Vec3 lead));
        Assert.Equal(12f * (0.01f + 0.05f + 0.02f), lead.x, 3);
    }

    /// <summary>A ping spike is not a delay to lead by: the latency term is capped at 300 ms.</summary>
    [Fact]
    public void PingSpike_IsCapped()
    {
        Assert.True(AgentPositionInterpolator.TryComputeMountLead(Gallop(4f), 0.01f, 2f, out Vec3 lead));
        Assert.Equal(4f * (0.01f + 0.3f + 0.02f), lead.x, 3);
    }

    /// <summary>The velocity is the owner's, whatever direction the horse points: a sideways drift leads sideways.</summary>
    [Fact]
    public void LeadFollowsTheVelocityDirection()
    {
        Assert.True(AgentPositionInterpolator.TryComputeMountLead(new Vec2(0f, 6f), 0.0f, 0.05f, out Vec3 lead));
        Assert.Equal(0f, lead.x, 3);
        Assert.Equal(6f * 0.07f, lead.y, 3);
    }
}
