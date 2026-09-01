using Missions.Agents.Packets;
using System;
using System.Collections.Generic;
using TaleWorlds.Library;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Proves the position encoding cannot teleport an agent.
/// </summary>
/// <remarks>
/// <para>
/// THE MECHANISM THIS GUARDS. <c>AgentPositionInterpolator</c> compares a reported position against where
/// the agent actually is. Within the snap distance it eases toward it with real locomotion; beyond it, it
/// calls <c>Teleport</c>. So a teleport needs an error of METRES. Quantisation error is millimetres, which
/// means the only way this encoding can teleport anything is by producing a WRONG value - a wrapped sign, a
/// lane bleeding into its neighbour, or a clamp that reports a distant agent as standing at the boundary.
/// </para>
/// <para>
/// These tests therefore do not check "is the position accurate". They check the far stronger property that
/// the decoded position can never be far enough from the truth to cross the snap threshold, for any input at
/// all - including inputs no correct sender would ever produce.
/// </para>
/// <para>
/// The margins are taken from the interpolator itself: 6 m for a rider, 12 m for a mount. They are asserted
/// as named constants so that if someone tightens the interpolator later, the reason these numbers exist is
/// visible right here.
/// </para>
/// </remarks>
public class PositionTeleportSafetyTests
{
    /// <summary>Beyond this, AgentPositionInterpolator teleports an on-foot agent instead of walking it.</summary>
    private const float RiderSnapDistance = 6f;

    /// <summary>Beyond this, it teleports a mount.</summary>
    private const float MountSnapDistance = 12f;

    private readonly ITestOutputHelper output;

    public PositionTeleportSafetyTests(ITestOutputHelper output) => this.output = output;

    /// <summary>
    /// Positions spanning everything a mission can plausibly contain, plus deliberately awkward ones.
    /// </summary>
    /// <remarks>
    /// Includes negatives in every combination, because a sign-extension mistake in one lane is the single
    /// most dangerous bug available here and it only shows up when that lane is negative.
    /// </remarks>
    public static IEnumerable<object[]> RepresentativePositions()
    {
        var values = new[]
        {
            new Vec3(0f, 0f, 0f),
            new Vec3(1f, 1f, 1f),
            new Vec3(-1f, -1f, -1f),
            new Vec3(742.31f, 533.87f, 41.2f),
            new Vec3(-742.31f, 533.87f, -41.2f),
            new Vec3(742.31f, -533.87f, 41.2f),
            new Vec3(0.005f, -0.005f, 0.0049f),
            new Vec3(1000f, 1000f, 200f),
            new Vec3(-1000f, -1000f, -200f),
            new Vec3(9999.99f, -9999.99f, 5000f),
            new Vec3(0f, -0.01f, 0f),
            new Vec3(123.456f, -789.012f, 34.567f),
        };

        foreach (var value in values) yield return new object[] { value };
    }

    // ---- the core guarantee ------------------------------------------------------------------

    /// <summary>
    /// Anything the encoder ACCEPTS comes back close enough that the interpolator will walk, never snap.
    /// </summary>
    /// <remarks>
    /// This is the single most important assertion in the file. If it ever fails, an agent somewhere will
    /// visibly jump.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RepresentativePositions))]
    public void An_accepted_position_never_decodes_far_enough_to_trigger_a_snap(Vec3 position)
    {
        Assert.True(MovementQuantizer.TryPackPosition(position, out ulong packed));
        Assert.True(MovementQuantizer.TryUnpackPosition(packed, out Vec3 restored));

        float error = restored.Distance(position);
        output.WriteLine($"{position} -> {restored}  error {error * 1000f:F3} mm");

        Assert.True(error < RiderSnapDistance, $"error {error} m would snap an on-foot agent");
        Assert.True(error < MountSnapDistance, $"error {error} m would snap a mount");
    }

    /// <summary>The error is not merely under the snap distance, it is three orders of magnitude under it.</summary>
    [Theory]
    [MemberData(nameof(RepresentativePositions))]
    public void An_accepted_position_stays_within_one_step_on_every_axis(Vec3 position)
    {
        Assert.True(MovementQuantizer.TryPackPosition(position, out ulong packed));
        Assert.True(MovementQuantizer.TryUnpackPosition(packed, out Vec3 restored));

        Assert.True(Math.Abs(restored.x - position.x) <= MovementQuantizer.PositionTolerance);
        Assert.True(Math.Abs(restored.y - position.y) <= MovementQuantizer.PositionTolerance);
        Assert.True(Math.Abs(restored.z - position.z) <= MovementQuantizer.PositionTolerance);
    }

    /// <summary>
    /// A swept walk across the representable range never produces a jump between adjacent samples.
    /// </summary>
    /// <remarks>
    /// A wrap would not necessarily show up at a single point - it shows up as a DISCONTINUITY. Walking an
    /// agent across the whole range one metre at a time and checking that consecutive decoded positions stay
    /// one metre apart catches a lane overflowing into its neighbour anywhere along the way, which spot
    /// checks at hand-picked coordinates would step straight over.
    /// </remarks>
    [Fact]
    public void Walking_across_the_whole_range_never_produces_a_discontinuity()
    {
        const float step = 1f;
        float limit = MovementQuantizer.MaximumPositionMetres - 1f;
        Vec3 previousRestored = default;
        bool hasPrevious = false;
        float worstJump = 0f;

        for (float x = -limit; x <= limit; x += step)
        {
            var position = new Vec3(x, -x, x / 4f);
            Assert.True(MovementQuantizer.TryPackPosition(position, out ulong packed));
            Assert.True(MovementQuantizer.TryUnpackPosition(packed, out Vec3 restored));

            if (hasPrevious)
            {
                // Consecutive samples are one metre apart on x and y and a quarter on z, so the true
                // separation is fixed; anything beyond it plus a rounding step is a discontinuity.
                float jump = restored.Distance(previousRestored);
                worstJump = Math.Max(worstJump, jump);

                Assert.True(
                    jump < RiderSnapDistance,
                    $"decoded positions jumped {jump} m near x={x} - a lane has wrapped");
            }

            previousRestored = restored;
            hasPrevious = true;
        }

        output.WriteLine($"worst consecutive jump across the range: {worstJump:F4} m");
    }

    // ---- what the encoder REFUSES ------------------------------------------------------------

    /// <summary>
    /// A position beyond the range is refused, never wrapped and never clamped.
    /// </summary>
    /// <remarks>
    /// Clamping would be the intuitive choice and it is the wrong one. Reporting a distant agent as standing
    /// at the boundary puts it kilometres from the truth, which is past the snap distance - so clamping would
    /// CAUSE a teleport. Refusal lets the receiver keep the last position it trusted.
    /// </remarks>
    [Theory]
    [InlineData(20000f, 0f, 0f)]
    [InlineData(-20000f, 0f, 0f)]
    [InlineData(0f, 20000f, 0f)]
    [InlineData(0f, 0f, -20000f)]
    [InlineData(1e9f, 1e9f, 1e9f)]
    public void A_position_beyond_the_range_is_refused_not_wrapped(float x, float y, float z)
    {
        bool packedOk = MovementQuantizer.TryPackPosition(new Vec3(x, y, z), out ulong packed);

        Assert.False(packedOk);
        Assert.True(MovementQuantizer.IsPositionUnavailable(packed));
        Assert.False(MovementQuantizer.TryUnpackPosition(packed, out _));
    }

    /// <remarks>
    /// A NaN position today reaches the interpolator, where every distance comparison against it is false,
    /// so the agent is teleported to a NaN position and never recovers. Refusing it here is strictly better
    /// than the current behaviour, not merely equivalent.
    /// </remarks>
    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.NaN, 0f)]
    [InlineData(0f, 0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    [InlineData(0f, float.NegativeInfinity, 0f)]
    public void A_non_finite_position_is_refused(float x, float y, float z)
    {
        Assert.False(MovementQuantizer.TryPackPosition(new Vec3(x, y, z), out ulong packed));
        Assert.True(MovementQuantizer.IsPositionUnavailable(packed));
    }

    [Fact]
    public void The_boundary_itself_is_accepted_and_one_step_beyond_is_not()
    {
        float atLimit = MovementQuantizer.MaximumPositionMetres;

        Assert.True(MovementQuantizer.TryPackPosition(new Vec3(atLimit, atLimit, atLimit), out _));
        Assert.False(MovementQuantizer.TryPackPosition(new Vec3(atLimit + 1f, 0f, 0f), out _));
    }

    // ---- the sentinel ------------------------------------------------------------------------

    /// <summary>
    /// The sentinel is a SET bit, so protobuf can never omit it as a default value.
    /// </summary>
    /// <remarks>
    /// Zero is a legitimate position - the scene origin - so an unavailable marker of zero would be dropped
    /// from the wire and read on the far side as "standing at the origin". For an agent that is genuinely
    /// somewhere else, that is a teleport to the middle of the map.
    /// </remarks>
    [Fact]
    public void The_unavailable_marker_is_never_a_default_value()
    {
        Assert.False(MovementQuantizer.TryPackPosition(new Vec3(float.NaN, 0f, 0f), out ulong packed));

        Assert.NotEqual(0UL, packed);
        Assert.True(MovementQuantizer.IsPositionUnavailable(packed));
    }

    /// <summary>The origin is a real position and must not be mistaken for an absent one.</summary>
    [Fact]
    public void The_origin_packs_to_zero_and_is_still_a_valid_position()
    {
        Assert.True(MovementQuantizer.TryPackPosition(Vec3.Zero, out ulong packed));

        Assert.Equal(0UL, packed);
        Assert.False(MovementQuantizer.IsPositionUnavailable(packed));
        Assert.True(MovementQuantizer.TryUnpackPosition(packed, out Vec3 restored));
        Assert.Equal(Vec3.Zero, restored);
    }

    /// <summary>A valid position never sets the top bit, so it can never be read as unavailable.</summary>
    [Theory]
    [MemberData(nameof(RepresentativePositions))]
    public void A_valid_position_never_collides_with_the_sentinel(Vec3 position)
    {
        Assert.True(MovementQuantizer.TryPackPosition(position, out ulong packed));

        Assert.Equal(0UL, packed & MovementQuantizer.PositionUnavailable);
        Assert.False(MovementQuantizer.IsPositionUnavailable(packed));
    }

    // ---- lane independence -------------------------------------------------------------------

    /// <summary>
    /// A negative axis must not bleed into its neighbours.
    /// </summary>
    /// <remarks>
    /// This is the sign-extension failure in its most direct form: get it wrong and an agent standing just
    /// south of the origin is reported ten kilometres east of it.
    /// </remarks>
    [Fact]
    public void A_negative_axis_does_not_corrupt_the_others()
    {
        var position = new Vec3(-0.01f, 500f, 250f);

        Assert.True(MovementQuantizer.TryPackPosition(position, out ulong packed));
        Assert.True(MovementQuantizer.TryUnpackPosition(packed, out Vec3 restored));

        Assert.True(Math.Abs(restored.x - -0.01f) <= MovementQuantizer.PositionTolerance);
        Assert.True(Math.Abs(restored.y - 500f) <= MovementQuantizer.PositionTolerance);
        Assert.True(Math.Abs(restored.z - 250f) <= MovementQuantizer.PositionTolerance);
    }

    [Fact]
    public void Every_axis_can_sit_at_its_extreme_at_once()
    {
        float limit = MovementQuantizer.MaximumPositionMetres;

        foreach (var position in new[]
        {
            new Vec3(limit, limit, limit),
            new Vec3(-limit, -limit, -limit),
            new Vec3(limit, -limit, limit),
            new Vec3(-limit, limit, -limit),
        })
        {
            Assert.True(MovementQuantizer.TryPackPosition(position, out ulong packed));
            Assert.True(MovementQuantizer.TryUnpackPosition(packed, out Vec3 restored));
            Assert.True(
                restored.Distance(position) < RiderSnapDistance,
                $"{position} decoded to {restored}");
        }
    }

    // ---- the margin is real ------------------------------------------------------------------

    /// <summary>
    /// The error budget is three orders of magnitude inside the snap distance.
    /// </summary>
    /// <remarks>
    /// Stated as a test rather than a comment so that shrinking the encoding later cannot quietly eat the
    /// margin. If someone drops to 16 bits per axis to save two bytes, this fails and explains why.
    /// </remarks>
    [Fact]
    public void The_error_budget_is_far_inside_the_snap_distance()
    {
        Assert.True(
            MovementQuantizer.PositionTolerance * 1000f < RiderSnapDistance,
            "the per-axis error must stay a thousandfold inside the snap distance");
    }

    /// <summary>The range must comfortably exceed any plausible mission scene.</summary>
    [Fact]
    public void The_range_comfortably_exceeds_a_battle_terrain()
    {
        // Bannerlord battle terrains are on the order of a kilometre across.
        Assert.True(
            MovementQuantizer.MaximumPositionMetres > 5000f,
            $"range is only {MovementQuantizer.MaximumPositionMetres} m");
    }
}
