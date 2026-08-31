using Missions.Agents.Packets;
using System;
using TaleWorlds.Library;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Pins the movement wire encoding: what it preserves exactly, how far it can ever be wrong, and what it
/// refuses to pass on.
/// </summary>
/// <remarks>
/// <para>
/// This encoding is deliberately lossy, so the useful question is not "is it exact" but "is the error
/// smaller than the smallest change the system acts on". The sender will not transmit a direction change
/// below 0.57 degrees; a step here is under 0.002 degrees. These tests hold that margin in place, so a later
/// attempt to shave another byte cannot quietly cross it.
/// </para>
/// <para>
/// The endpoint cases matter more than the average case. Axis-aligned facings and a zero input vector are
/// what actually occur - a stationary agent, a soldier facing down a lane - and those must survive
/// bit-exactly or a still agent starts drifting.
/// </para>
/// </remarks>
public class MovementQuantizerTests
{
    // ---- exactness where it counts -----------------------------------------------------------

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void The_endpoints_round_trip_bit_exactly(float value)
    {
        Assert.Equal(value, MovementQuantizer.DecodeUnit(MovementQuantizer.EncodeUnit(value)));
    }

    [Fact]
    public void A_zero_vector_survives_exactly()
    {
        Assert.Equal(Vec2.Zero, MovementQuantizer.UnpackVec2(MovementQuantizer.PackVec2(Vec2.Zero)));
        Assert.Equal(Vec3.Zero, MovementQuantizer.UnpackVec3(MovementQuantizer.PackVec3(Vec3.Zero)));
    }

    /// <remarks>
    /// The reason per-component fixed point was chosen over angle encoding: an angle cannot express a
    /// zero-length vector, so a standing agent would have been given an arbitrary facing.
    /// </remarks>
    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(0f, -1f)]
    public void Axis_aligned_facings_survive_exactly(float x, float y)
    {
        var direction = new Vec2(x, y);

        Assert.Equal(direction, MovementQuantizer.UnpackVec2(MovementQuantizer.PackVec2(direction)));
    }

    [Fact]
    public void A_zero_speed_round_trips_exactly()
    {
        Assert.Equal(0f, MovementQuantizer.DecodeSpeed(MovementQuantizer.EncodeSpeed(0f)));
    }

    // ---- the error bound ---------------------------------------------------------------------

    [Theory]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(0.70710678f)]
    [InlineData(0.001f)]
    [InlineData(-0.999f)]
    [InlineData(0.33333333f)]
    public void A_component_is_never_wrong_by_more_than_one_step(float value)
    {
        float restored = MovementQuantizer.DecodeUnit(MovementQuantizer.EncodeUnit(value));

        Assert.True(
            Math.Abs(restored - value) <= MovementQuantizer.UnitTolerance,
            $"{value} -> {restored} exceeded {MovementQuantizer.UnitTolerance}");
    }

    /// <summary>
    /// The margin that justifies the whole encoding: a step is far finer than the smallest change sent.
    /// </summary>
    /// <remarks>
    /// <c>AgentMovementHandler</c> suppresses a direction change whose squared delta is under its
    /// 0.57-degree threshold. A quantiser step must stay well inside that or the encoding would start
    /// inventing changes the sender had decided were not worth transmitting.
    /// </remarks>
    [Fact]
    public void One_step_is_far_finer_than_the_smallest_direction_change_ever_sent()
    {
        double stepRadians = Math.Asin(MovementQuantizer.UnitTolerance);
        double stepDegrees = stepRadians * 180d / Math.PI;

        Assert.True(stepDegrees < 0.01d, $"a step is {stepDegrees:F5} degrees");
    }

    [Theory]
    [InlineData(0.001f)]
    [InlineData(1.5f)]
    [InlineData(6.25f)]
    [InlineData(12.75f)]
    public void A_speed_is_never_wrong_by_more_than_one_millimetre_per_second(float speed)
    {
        float restored = MovementQuantizer.DecodeSpeed(MovementQuantizer.EncodeSpeed(speed));

        Assert.True(
            Math.Abs(restored - speed) <= MovementQuantizer.SpeedTolerance,
            $"{speed} -> {restored}");
    }

    // ---- refusing bad input ------------------------------------------------------------------

    /// <remarks>
    /// A NaN reaching a puppet's LookDirection is unrecoverable: the engine stores it, and every later
    /// equality check against it is false, so the agent never converges again. It is cheaper to refuse it
    /// here than to detect it afterwards.
    /// </remarks>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void A_non_finite_component_becomes_zero_rather_than_garbage(float value)
    {
        Assert.Equal(0, MovementQuantizer.EncodeUnit(value));
    }

    [Fact]
    public void A_non_finite_speed_becomes_zero()
    {
        Assert.Equal(0, MovementQuantizer.EncodeSpeed(float.NaN));
    }

    [Theory]
    [InlineData(2f, 1f)]
    [InlineData(-2f, -1f)]
    [InlineData(1000f, 1f)]
    public void An_out_of_range_component_is_clamped_not_wrapped(float value, float expected)
    {
        Assert.Equal(expected, MovementQuantizer.DecodeUnit(MovementQuantizer.EncodeUnit(value)));
    }

    /// <remarks>
    /// Clamping rather than wrapping is the whole point: a wrapped value would turn a fast agent into one
    /// sprinting backwards, which is worse than reporting the ceiling.
    /// </remarks>
    [Fact]
    public void A_speed_beyond_the_ceiling_is_clamped_not_wrapped()
    {
        float restored = MovementQuantizer.DecodeSpeed(MovementQuantizer.EncodeSpeed(1_000f));

        Assert.Equal(MovementQuantizer.MaximumSpeed, restored);
    }

    [Fact]
    public void A_negative_speed_becomes_zero()
    {
        Assert.Equal(0f, MovementQuantizer.DecodeSpeed(MovementQuantizer.EncodeSpeed(-5f)));
    }

    // ---- packing -----------------------------------------------------------------------------

    [Fact]
    public void Packing_keeps_the_components_in_their_own_lanes()
    {
        var direction = new Vec3(0.25f, -0.5f, 0.75f);

        Vec3 restored = MovementQuantizer.UnpackVec3(MovementQuantizer.PackVec3(direction));

        Assert.True(Math.Abs(restored.x - 0.25f) <= MovementQuantizer.UnitTolerance);
        Assert.True(Math.Abs(restored.y - -0.5f) <= MovementQuantizer.UnitTolerance);
        Assert.True(Math.Abs(restored.z - 0.75f) <= MovementQuantizer.UnitTolerance);
    }

    /// <remarks>
    /// A negative component must not bleed into the lane above it through sign extension - the failure that
    /// would make a downward-looking agent also face sideways.
    /// </remarks>
    [Fact]
    public void A_negative_component_does_not_corrupt_its_neighbours()
    {
        var direction = new Vec3(-1f, 0f, 0f);

        Vec3 restored = MovementQuantizer.UnpackVec3(MovementQuantizer.PackVec3(direction));

        Assert.Equal(-1f, restored.x);
        Assert.Equal(0f, restored.y);
        Assert.Equal(0f, restored.z);
    }

    /// <summary>
    /// All three lanes can be negative at once, and the heading survives.
    /// </summary>
    /// <remarks>
    /// (-1, -1, -1) is not a unit vector - it is root three long - so the length guard shortens it to the
    /// unit ball. What must survive is the DIRECTION: all three components equal and negative. This test
    /// originally asserted the components came back as -1, which was the per-component clamping the guard
    /// deliberately replaced.
    /// </remarks>
    [Fact]
    public void All_three_lanes_can_be_negative_at_once()
    {
        var direction = new Vec3(-1f, -1f, -1f);
        float expected = -1f / (float)Math.Sqrt(3d);

        Vec3 restored = MovementQuantizer.UnpackVec3(MovementQuantizer.PackVec3(direction));

        Assert.True(Math.Abs(restored.x - expected) <= MovementQuantizer.UnitTolerance);
        Assert.True(Math.Abs(restored.y - expected) <= MovementQuantizer.UnitTolerance);
        Assert.True(Math.Abs(restored.z - expected) <= MovementQuantizer.UnitTolerance);
    }

    [Fact]
    public void Vec2_packing_keeps_its_lanes_too()
    {
        var value = new Vec2(-1f, 1f);

        Assert.Equal(value, MovementQuantizer.UnpackVec2(MovementQuantizer.PackVec2(value)));
    }

    /// <summary>
    /// An over-long direction keeps its HEADING; only its length is reduced.
    /// </summary>
    /// <remarks>
    /// Clamping each component independently would silently re-aim the agent: (2, 1, 0) would become
    /// (1, 1, 0), which points somewhere else entirely. Nothing in the engine's contract promises
    /// Agent.LookDirection is normalised, so this is the difference between a marginally short vector and
    /// an agent looking in a direction it never looked.
    /// </remarks>
    [Fact]
    public void An_over_long_direction_keeps_its_heading()
    {
        Vec3 restored = MovementQuantizer.UnpackVec3(
            MovementQuantizer.PackVec3(new Vec3(2f, 1f, 0f)));

        // The original heading has x exactly twice y. Component clamping would have made them equal.
        Assert.True(restored.x > restored.y, $"heading was lost: {restored.x} vs {restored.y}");
        Assert.Equal(2d, restored.x / restored.y, 2);
        Assert.Equal(0f, restored.z);
    }

    [Fact]
    public void A_unit_direction_is_not_disturbed_by_the_length_guard()
    {
        var direction = new Vec3(0f, 1f, 0f);

        Assert.Equal(direction, MovementQuantizer.UnpackVec3(MovementQuantizer.PackVec3(direction)));
    }

    /// <remarks>A zero vector must not reach a square root and come back as NaN.</remarks>
    [Fact]
    public void The_length_guard_does_not_divide_by_zero()
    {
        Assert.Equal(Vec3.Zero, MovementQuantizer.UnpackVec3(MovementQuantizer.PackVec3(Vec3.Zero)));
    }

    [Fact]
    public void A_non_finite_direction_still_becomes_zero_through_the_length_guard()
    {
        Vec3 restored = MovementQuantizer.UnpackVec3(
            MovementQuantizer.PackVec3(new Vec3(float.NaN, 1f, float.PositiveInfinity)));

        Assert.Equal(0f, restored.x);
        Assert.Equal(0f, restored.z);
    }

    /// <summary>A packed direction never exceeds 48 bits, leaving the top 16 free for later use.</summary>
    [Fact]
    public void A_packed_direction_uses_only_the_low_48_bits()
    {
        ulong packed = MovementQuantizer.PackVec3(new Vec3(-1f, -1f, -1f));

        Assert.Equal(0uL, packed >> 48);
    }
}
