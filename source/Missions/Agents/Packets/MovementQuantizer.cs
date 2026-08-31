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
