using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;

namespace GameInterface.Services.Fixtures;

/// <summary>
/// C21 - make the campaign's rolls repeatable, so a path with chance in it can be a stable capability.
/// </summary>
/// <remarks>
/// NO HARMONY PATCH, BECAUSE THE ENGINE ALREADY EXPOSES THIS
/// <c>MBRandom.SetSeed(uint, uint)</c> is public. Patching the global RNG instead would have been both
/// unnecessary and dangerous: a stub returning a constant hangs any engine code that samples until a condition
/// is met, and a patch on a method whose signature is wrong throws inside PatchAll and takes the mod's ENTIRE
/// patch set down with it.
///
/// SEEDING RATHER THAN STUBBING
/// The plan allows either. Seeding keeps the distribution honest while making the sequence repeatable, and it
/// cannot hang anything. It gives *a* repeatable outcome rather than a *chosen* one - to exercise a specific
/// branch, search seeds for one that produces it, which stays safe because the game is still rolling normally.
///
/// WHAT SEEDING CANNOT REACH
/// MBRandom keeps a SECOND generator, <c>NondeterministicRandom</c>, behind NondeterministicRandomFloat and
/// NondeterministicRandomInt. SetSeed does not touch it, by design - code that asks for a nondeterministic roll
/// gets one. So a scenario depending on such a path is flaky no matter what seed is set. That is why
/// <see cref="Describe"/> reports both generators: when a run is not reproducible despite a fixed seed,
/// comparing the deterministic state across the two runs tells you whether the roll sequence even diverged, or
/// whether the cause lies somewhere else entirely.
///
/// DETERMINISM ASSUMES ONE CONSUMER
/// MBRandom is global mutable state, so a seed only makes the sequence repeatable for code drawing from it in
/// one order. Campaign logic runs on the game thread and satisfies that, but anything drawing concurrently
/// interleaves and the sequence stops being reproducible. This is not theoretical - the unit tests below
/// failed exactly this way once xUnit ran them beside other classes that also draw.
///
/// THE STATE IS THE DIAGNOSTIC
/// The generator is an xorshift128 - four uints. Reading them after a run says how far the sequence advanced,
/// so two runs that consumed a different number of rolls are visibly different even when their outcomes matched.
/// </remarks>
public static class CampaignRandomControl
{
    private static readonly FieldInfo InternalRandomField =
        typeof(MBRandom).GetField("_internalRandom", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly FieldInfo NondeterministicRandomField =
        typeof(MBRandom).GetField("NondeterministicRandom", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly string[] StateFieldNames = { "_x", "_y", "_z", "_w" };

    /// <summary>
    /// Seeds the campaign generator from a single value.
    /// </summary>
    /// <remarks>
    /// SetSeed wants two values; a scenario wants to quote one. The second is derived with the golden-ratio
    /// constant so one seed still spreads across both, and zero is nudged off because an all-zero xorshift
    /// state produces nothing but zeroes forever.
    /// </remarks>
    public static void Seed(uint seed)
    {
        var primary = seed == 0 ? 1u : seed;
        MBRandom.SetSeed(primary, primary ^ 0x9E3779B9u);
    }

    /// <summary>Reads the campaign generator's state, or null when the engine's internals have moved.</summary>
    public static uint[] CaptureState() => ReadState(InternalRandomField);

    /// <summary>
    /// Puts a previously captured generator state back, so seeding can be undone like any other fixture change.
    /// </summary>
    public static bool TryRestoreState(uint[] state)
    {
        if (state == null || state.Length != StateFieldNames.Length) return false;

        var generator = InternalRandomField?.GetValue(null);
        if (generator == null) return false;

        for (var index = 0; index < StateFieldNames.Length; index++)
        {
            var field = generator.GetType().GetField(StateFieldNames[index], BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return false;
            field.SetValue(generator, state[index]);
        }

        return true;
    }

    /// <summary>Both generators, because the one seeding cannot reach is the one that explains flakiness.</summary>
    public static string Describe()
    {
        return $"deterministic={Format(CaptureState())} " +
               $"nondeterministic={Format(ReadState(NondeterministicRandomField))} " +
               "note=nondeterministic is NOT affected by seeding";
    }

    public static string Format(uint[] state)
    {
        if (state == null) return "unavailable";
        return string.Join(",", state.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    }

    private static uint[] ReadState(FieldInfo generatorField)
    {
        var generator = generatorField?.GetValue(null);
        if (generator == null) return null;

        var state = new uint[StateFieldNames.Length];
        for (var index = 0; index < StateFieldNames.Length; index++)
        {
            var field = generator.GetType().GetField(StateFieldNames[index], BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return null;
            state[index] = Convert.ToUInt32(field.GetValue(generator));
        }

        return state;
    }
}
