using System;
using TaleWorlds.Library;

namespace Missions.Agents.Packets;

/// <summary>
/// Fixed-point encodings for the continuous movement state that travels on every movement packet.
/// </summary>
/// <remarks>
/// <para>
/// WHY. Movement is 97% of the mission mesh by bytes, and a measured 57 bytes per agent per update. Nearly
/// all of that is full IEEE precision spent on values that are then thrown away: direction vectors are unit
/// length, an input vector is a throttle in [-1, 1], and a speed is metres per second in a range a footman
/// or a horse can actually reach. The surrogates already in use cost 15 bytes for a Vec3 and 11 for a Vec2,
/// so a single agent spends 52 of its 57 bytes on four vectors and a float.
/// </para>
/// <para>
/// PRECISION, AND WHY THIS IS NOT LOSSY IN ANY WAY THAT MATTERS. A direction component is stored as a signed
/// 16-bit fraction of one, so a step is 1/32767 - about 3.05e-5. The worst angular error that can produce is
/// under 0.002 degrees, and the sender already refuses to transmit a direction change below
/// <c>DirectionDeltaThresholdSq</c>, which is 0.57 degrees. The quantiser is therefore roughly 280 times
/// finer than the smallest change the system will even send.
/// </para>
/// <para>
/// PER-COMPONENT, NOT ANGLES. Encoding a direction as an angle is smaller still, but an angle cannot
/// represent a zero-length vector and cannot carry magnitude. Both occur: a stationary agent's movement
/// direction is zero in the test mirrors, and nothing in the engine's contract promises
/// <see cref="Agent.LookDirection"/> is normalised. Per-component fixed point represents zero exactly,
/// preserves magnitude up to one, and needs no sentinel value - so it cannot silently turn a still agent
/// into one facing an arbitrary direction.
/// </para>
/// <para>
/// EXACTNESS AT THE ENDPOINTS. -1, 0 and +1 round trip bit-exactly, because the scale is 32767 rather than
/// 32768. Those are the values that appear in practice - axis-aligned facings, a zero input - so the common
/// cases lose nothing at all.
/// </para>
/// </remarks>
internal static class MovementQuantizer
{
    /// <summary>Scale for a value in [-1, 1]. 32767, not 32768, so +1 and -1 are both exact.</summary>
    public const short UnitScale = 32767;

    /// <summary>Largest error a unit component can pick up: half a step.</summary>
    public const float UnitTolerance = 1f / UnitScale;

    /// <summary>Millimetres per second per step, so 65.535 m/s is representable.</summary>
    public const float SpeedScale = 1000f;

    /// <summary>Fastest speed the encoding can carry, in m/s.</summary>
    public const float MaximumSpeed = ushort.MaxValue / SpeedScale;

    /// <summary>Largest error a speed can pick up.</summary>
    public const float SpeedTolerance = 1f / SpeedScale;

    /// <summary>
    /// Encodes one component of a direction, clamped to [-1, 1].
    /// </summary>
    /// <remarks>
    /// A non-finite input becomes zero rather than an arbitrary integer. NaN reaching a puppet's
    /// <c>LookDirection</c> corrupts it permanently - the engine will happily store it and every subsequent
    /// comparison against it is false - so it is refused here, at the only point where it can still be
    /// refused cheaply.
    /// </remarks>
    public static short EncodeUnit(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return 0;

        float clamped = value < -1f ? -1f : value > 1f ? 1f : value;
        return (short)Math.Round(clamped * UnitScale, MidpointRounding.AwayFromZero);
    }

    public static float DecodeUnit(short value) => (float)value / UnitScale;

    /// <summary>
    /// Encodes a speed in m/s, clamped to [0, <see cref="MaximumSpeed"/>].
    /// </summary>
    /// <remarks>
    /// Negative is clamped to zero: this carries the MAGNITUDE of a velocity
    /// (<c>GetRealGlobalVelocity().AsVec2.Length</c>), which cannot be negative, and a negative value would
    /// wrap into an enormous positive one. The ceiling is far above anything reachable - a charging horse is
    /// around 10 m/s - and clamping there is still the right behaviour, because the value only feeds a
    /// throttle that is itself clamped to [0, 1].
    /// </remarks>
    public static ushort EncodeSpeed(float metersPerSecond)
    {
        if (float.IsNaN(metersPerSecond)) return 0;

        if (metersPerSecond <= 0f) return 0;
        if (metersPerSecond >= MaximumSpeed) return ushort.MaxValue;

        return (ushort)Math.Round(metersPerSecond * SpeedScale, MidpointRounding.AwayFromZero);
    }

    public static float DecodeSpeed(ushort value) => value / SpeedScale;

    /// <summary>Packs a direction into 48 bits: three signed 16-bit components, X in the low bits.</summary>
    /// <remarks>
    /// A vector longer than one is scaled down rather than clamped per component. Clamping would change
    /// the DIRECTION, not just the magnitude - (2, 1, 0) would land on (1, 1, 0), a different heading - and
    /// this carries <c>Agent.LookDirection</c>, which the engine gives no written guarantee of normalising.
    /// Scaling is a no-op in the expected case and preserves the heading in the unexpected one, so the
    /// failure mode is a slightly short vector instead of an agent aiming somewhere it never aimed.
    /// </remarks>
    public static ulong PackVec3(Vec3 value)
    {
        Vec3 safe = ScaleIntoUnitBall(value);
        ulong x = (ushort)EncodeUnit(safe.x);
        ulong y = (ushort)EncodeUnit(safe.y);
        ulong z = (ushort)EncodeUnit(safe.z);

        return x | (y << 16) | (z << 32);
    }

    private static Vec3 ScaleIntoUnitBall(Vec3 value)
    {
        float lengthSquared = (value.x * value.x) + (value.y * value.y) + (value.z * value.z);

        // Non-finite is left to EncodeUnit, which turns each component into zero; taking a square root of
        // it here would only spread the NaN across all three.
        if (float.IsNaN(lengthSquared) || float.IsInfinity(lengthSquared)) return value;
        if (lengthSquared <= 1f) return value;

        float scale = 1f / (float)Math.Sqrt(lengthSquared);
        return new Vec3(value.x * scale, value.y * scale, value.z * scale);
    }

    public static Vec3 UnpackVec3(ulong packed) => new Vec3(
        DecodeUnit((short)(ushort)packed),
        DecodeUnit((short)(ushort)(packed >> 16)),
        DecodeUnit((short)(ushort)(packed >> 32)));

    // ---- position --------------------------------------------------------------------------

    /// <summary>Steps per metre. One step is 1 cm; the worst error is half of that.</summary>
    /// <remarks>
    /// Deliberately coarser than the direction encoding, and chosen from how position is CONSUMED rather
    /// than from how precisely it could be sent. <c>AgentPositionInterpolator.MoveTowardTarget</c> hands
    /// the value to <c>Agent.SetTargetPositionAndDirection</c>, so the agent WALKS there under its own
    /// locomotion - it is a destination, not a snap target. Sub-centimetre precision in a walk destination
    /// buys nothing.
    ///
    /// Half a centimetre is also half of the 1 cm change below which the sender refuses to transmit at all
    /// (<c>PositionDeltaThresholdSq</c>), so the encoding cannot invent movement the sender judged too
    /// small to mention.
    ///
    /// Spending the spare bits on RANGE rather than precision is the point: it turns the range from an
    /// assumption that might be wrong into one that cannot plausibly be.
    /// </remarks>
    public const float PositionScale = 100f;

    /// <summary>Bits per axis. Three axes fit in 63, leaving the top bit for the sentinel.</summary>
    public const int PositionBits = 21;

    private const ulong PositionMask = (1UL << PositionBits) - 1UL;
    private const int PositionSignBit = PositionBits - 1;
    private const long PositionMaxSteps = (1L << PositionSignBit) - 1L;
    private const long PositionMinSteps = -(1L << PositionSignBit);

    /// <summary>Furthest representable coordinate in metres - about 10.4 km from the scene origin.</summary>
    /// <remarks>
    /// A Bannerlord battle terrain is on the order of a kilometre across, so this is roughly a tenfold
    /// margin. That margin is why no fallback FIELD is needed; the sentinel covers the case where the
    /// margin still turns out to be wrong.
    /// </remarks>
    public const float MaximumPositionMetres = PositionMaxSteps / PositionScale;

    /// <summary>Largest error a coordinate can pick up: half a step, 5 mm.</summary>
    public const float PositionTolerance = 0.5f / PositionScale;

    /// <summary>Set in the top bit to mean THIS PACKET CARRIES NO USABLE POSITION.</summary>
    /// <remarks>
    /// <para>
    /// The contingency lives inside the value rather than in a second field, and each alternative is ruled
    /// out by something concrete.
    /// </para>
    /// <para>
    /// A second Vec3 fallback field would make the struct BIGGER than it is today. AgentData is built per
    /// agent per poll, thousands of times a second, so growing it to guard a branch that should never run
    /// gives back more than the encoding saves.
    /// </para>
    /// <para>
    /// Dropping the agent from the batch instead would mean removing one entry from the id array and one
    /// from the data array. Those are parallel arrays, and a mistake there hands one agent another agent's
    /// position - precisely the teleport this design exists to prevent. Keeping every agent in the batch
    /// and marking the value makes that class of bug impossible rather than merely unlikely.
    /// </para>
    /// <para>
    /// It must be a SET bit rather than a reserved zero, because protobuf omits a default-valued field
    /// entirely. Zero is a legitimate position - the scene origin - so an unavailable marker of zero would
    /// vanish off the wire and be read as 'standing at the origin'.
    /// </para>
    /// </remarks>
    public const ulong PositionUnavailable = 1UL << 63;

    /// <summary>True when a packed position carries no usable value.</summary>
    public static bool IsPositionUnavailable(ulong packed) => (packed & PositionUnavailable) != 0UL;

    /// <summary>Packs a position into 63 bits, or reports that it cannot be represented.</summary>
    /// <remarks>
    /// Refusing rather than clamping is a correctness decision, not caution. Clamping a genuinely distant
    /// agent would place it at the boundary, potentially kilometres from where it really is, and
    /// <c>AgentPositionInterpolator</c> TELEPORTS an agent whose reported position is more than six metres
    /// away. Clamping would therefore cause the very teleport it appears to prevent. Reporting the value as
    /// unavailable lets the receiver keep easing toward the last position it had reason to trust.
    /// </remarks>
    public static bool TryPackPosition(Vec3 value, out ulong packed)
    {
        packed = PositionUnavailable;

        if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z)) return false;

        if (!TryToSteps(value.x, out long x) ||
            !TryToSteps(value.y, out long y) ||
            !TryToSteps(value.z, out long z))
        {
            return false;
        }

        packed = ((ulong)x & PositionMask)
            | (((ulong)y & PositionMask) << PositionBits)
            | (((ulong)z & PositionMask) << (PositionBits * 2));
        return true;
    }

    /// <summary>Reads a packed position. False when the sentinel is set.</summary>
    public static bool TryUnpackPosition(ulong packed, out Vec3 value)
    {
        if (IsPositionUnavailable(packed))
        {
            value = Vec3.Zero;
            return false;
        }

        value = new Vec3(
            FromSteps(packed),
            FromSteps(packed >> PositionBits),
            FromSteps(packed >> (PositionBits * 2)));
        return true;
    }

    private static bool TryToSteps(float metres, out long steps)
    {
        steps = (long)Math.Round(metres * PositionScale, MidpointRounding.AwayFromZero);
        return steps >= PositionMinSteps && steps <= PositionMaxSteps;
    }

    /// <remarks>
    /// Sign-extends from the top bit of the lane before scaling. Without this a negative coordinate reads
    /// back as a large positive one - the wrap that would put a soldier standing just behind the origin ten
    /// kilometres in front of it, and the single most dangerous mistake available in this file.
    /// </remarks>
    private static float FromSteps(ulong lane)
    {
        long steps = (long)(lane & PositionMask);
        if ((steps & (1L << PositionSignBit)) != 0L) steps -= 1L << PositionBits;

        return steps / PositionScale;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    /// <summary>Packs a two-component direction or input into 32 bits.</summary>
    public static uint PackVec2(Vec2 value)
    {
        uint x = (ushort)EncodeUnit(value.x);
        uint y = (ushort)EncodeUnit(value.y);

        return x | (y << 16);
    }

    public static Vec2 UnpackVec2(uint packed) => new Vec2(
        DecodeUnit((short)(ushort)packed),
        DecodeUnit((short)(ushort)(packed >> 16)));
}
