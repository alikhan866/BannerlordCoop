using GameInterface.Services.MobileParties;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

/// <summary>
/// Proves the drift threshold, which is the whole of the correction decision.
/// </summary>
/// <remarks>
/// The property that matters is that a party seen for the FIRST time is never corrected. The sweep runs
/// against every party on the map, so a first sighting that counted as drift would emit one forced-position
/// message per party the moment a campaign loads - roughly 1,500 on the measured save - which is precisely the
/// flood the budget exists to prevent, and it would arrive at exactly the worst moment: while clients are
/// still settling after a join.
///
/// The threshold itself is shared with the client's own gate
/// (<c>MobilePartyBehaviorHandler.ShouldApplyAuthoritativePosition</c>) so the two ends cannot disagree about
/// what counts as drift - the server re-states a position every CorrectionDistance travelled, and the client
/// accepts a correction once it disagrees by that much. A test pins that shared constant, because silently
/// splitting the two is how the correction would start being sent and then ignored.
/// </remarks>
public class PartyPositionCorrectionTests
{
    private const float Threshold = PartyPositionCorrection.CorrectionDistanceSquared;

    [Fact]
    public void A_party_seen_for_the_first_time_is_never_corrected()
    {
        // However far the number says it moved: with no previous report there is nothing to have drifted from.
        Assert.False(PartyPositionCorrection.ShouldCorrect(hasPrevious: false, distanceSquared: 0f));
        Assert.False(PartyPositionCorrection.ShouldCorrect(hasPrevious: false, distanceSquared: Threshold * 100f));
    }

    [Fact]
    public void Drift_below_the_threshold_is_left_to_the_clients_own_simulation()
    {
        Assert.False(PartyPositionCorrection.ShouldCorrect(hasPrevious: true, distanceSquared: 0f));
        Assert.False(PartyPositionCorrection.ShouldCorrect(hasPrevious: true, distanceSquared: Threshold - 0.01f));
    }

    [Fact]
    public void Drift_at_or_beyond_the_threshold_is_corrected()
    {
        Assert.True(PartyPositionCorrection.ShouldCorrect(hasPrevious: true, distanceSquared: Threshold));
        Assert.True(PartyPositionCorrection.ShouldCorrect(hasPrevious: true, distanceSquared: Threshold * 4f));
    }

    /// <summary>The squared constant must stay the square of the distance it is named after.</summary>
    [Fact]
    public void The_squared_threshold_matches_the_distance_it_is_derived_from()
    {
        Assert.Equal(
            PartyPositionCorrection.CorrectionDistance * PartyPositionCorrection.CorrectionDistance,
            PartyPositionCorrection.CorrectionDistanceSquared);
    }

    /// <summary>
    /// A budget of zero or less would silently disable the sweep; a huge one would defeat its purpose.
    /// </summary>
    [Fact]
    public void The_per_pass_budget_is_a_real_bound()
    {
        Assert.InRange(PartyPositionCorrection.MaxCorrectionsPerTick, 1, 128);
    }
}
