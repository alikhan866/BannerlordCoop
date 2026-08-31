using Missions.Battles;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Proves the reinforcement interval is only ever shortened, never lengthened.
/// </summary>
/// <remarks>
/// The direction is the whole safety property. This runs against every non-sally-out co-op battle, and a
/// battle type whose own settings already ask for a brisker interval must keep its value - raising it would
/// make reinforcements arrive LESS often in exactly the battles someone had already tuned, and it would do so
/// silently, because nothing about a thin field says which side of a Math.Min it came from.
///
/// The zero case is guarded separately: the engine treats a non-positive interval as "no timing of my own",
/// and clamping that to a positive number would invent a cadence where the battle type asked for none.
///
/// Why the constant exists at all is in the remarks on <see cref="CoopReinforcementPacing"/>: a co-op
/// side's waves are capped to the room the field has, so the engine starts its interval believing it topped
/// the side up when it did not, and whatever dies before the timer next fires is replaced by nothing.
/// </remarks>
public class CoopReinforcementIntervalTests
{
    private const float Cap = CoopReinforcementPacing.MaximumInterval;

    [Fact]
    public void An_interval_longer_than_the_cap_is_shortened_to_it()
    {
        Assert.Equal(Cap, CoopReinforcementPacing.Interval(Cap + 1f));
        Assert.Equal(Cap, CoopReinforcementPacing.Interval(240f));
    }

    /// <summary>The property the safety of this change rests on.</summary>
    [Fact]
    public void An_interval_already_shorter_than_the_cap_is_left_alone()
    {
        Assert.Equal(1f, CoopReinforcementPacing.Interval(1f));
        Assert.Equal(Cap - 0.5f, CoopReinforcementPacing.Interval(Cap - 0.5f));
    }

    [Fact]
    public void An_interval_exactly_at_the_cap_is_unchanged()
    {
        Assert.Equal(Cap, CoopReinforcementPacing.Interval(Cap));
    }

    /// <summary>
    /// A non-positive interval means the battle type asked for no timing of its own; do not invent one.
    /// </summary>
    [Fact]
    public void A_non_positive_interval_passes_through_untouched()
    {
        Assert.Equal(0f, CoopReinforcementPacing.Interval(0f));
        Assert.Equal(-1f, CoopReinforcementPacing.Interval(-1f));
    }

    [Fact]
    public void The_cap_is_a_sane_wave_gap()
    {
        // Long enough not to trickle men in continuously, far short of the 2-4 minute gaps measured live.
        Assert.InRange(Cap, 5f, 60f);
    }
}
