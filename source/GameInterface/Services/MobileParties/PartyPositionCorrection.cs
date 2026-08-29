using GameInterface.Services.MobileParties.Extensions;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties;

/// <summary>
/// Picks the parties whose replicated position has drifted far enough to be worth re-stating.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS, AND WHY THE OBVIOUS FIX IS THE WRONG ONE.
///
/// A client adopts the server's position for a party only when that party is HOLDING, when the update is
/// explicitly forced, or when the two disagree about land versus sea - see
/// <c>MobilePartyBehaviorHandler.ShouldApplyAuthoritativePosition</c>. A party that is MOVING is deliberately
/// left to the client's own simulation, because it is already walking the same replicated target and an
/// in-flight snapshot is a frame or two stale. That reasoning is right per-message and wrong over time:
/// nothing bounds how far the two simulations may part company.
///
/// It compounds with the other half of the design. Position travels only inside a behaviour update, and
/// behaviour updates are published ONLY when the behaviour CHANGES - <c>PartyBehaviorPatch</c> dedups the rest
/// to keep hundreds of identical updates off the wire. So a lord marching for ten campaign hours under one
/// unchanged <c>GoToSettlement</c> sends nothing at all for the whole journey, and the client's copy is
/// corrected by exactly nothing until the party stops or something forces it.
///
/// At that moment the entire accumulated error is applied in a single assignment. That is the "lords teleport
/// across the map to join an army" report: the jump is not a move, it is a correction that was never allowed
/// to happen gradually. It looks worst at an army join because attaching forces a position, which is precisely
/// the event that discharges the debt - and it is why the party cannot be intercepted on the way, since on the
/// client it never travelled the ground in between.
///
/// The remedy is a slow trickle of small corrections instead of one large one. Bounded twice, because this
/// runs against every party on the map and the wire is the scarce resource here, not the CPU:
///
///   BY DISTANCE - a party reports only once it has moved <see cref="CorrectionDistance"/> since its last
///   correction. An idle, besieging or garrisoned party therefore costs nothing whatsoever, and a marching one
///   costs a message per leg rather than per tick.
///
///   BY COUNT - at most <see cref="MaxCorrectionsPerTick"/> corrections leave in any one pass, so a
///   fast-forward burst cannot become a flood. The scan resumes where it stopped, so a party at the far end of
///   the list is never starved by always being scanned last.
/// </remarks>
internal sealed class PartyPositionCorrection
{
    /// <summary>
    /// Map distance a party may cover before its position is worth re-stating.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse. This is not trying to keep the two simulations identical - that is what the
    /// client's own movement is for - only to stop the disagreement growing without limit. The same figure
    /// gates the client's side of the bargain, so the two cannot argue about what "drifted" means: the server
    /// re-states a position every <see cref="CorrectionDistance"/> travelled, and the client accepts a
    /// correction once it disagrees by that much. Below it, an in-flight snapshot really is just stale and is
    /// still ignored.
    /// </remarks>
    internal const float CorrectionDistance = 8f;

    internal const float CorrectionDistanceSquared = CorrectionDistance * CorrectionDistance;

    /// <summary>Ceiling on corrections emitted by a single pass.</summary>
    internal const int MaxCorrectionsPerTick = 32;

    private readonly Dictionary<string, CampaignVec2> lastReported = new Dictionary<string, CampaignVec2>();
    private int resumeIndex;

    /// <summary>
    /// Whether a party that has drifted <paramref name="distanceSquared"/> from its last reported position is
    /// due a correction.
    /// </summary>
    /// <remarks>
    /// Takes the squared distance rather than the two positions so the decision is arithmetic a test can drive
    /// without standing up a campaign - <see cref="CampaignVec2"/> is only meaningful against a loaded map.
    ///
    /// A party seen for the FIRST time is recorded and not corrected. Its position is already right, because
    /// the join baseline placed it there, and correcting on first sight would emit one message for every party
    /// on the map the first time this runs - the exact flood the budget exists to prevent.
    /// </remarks>
    internal static bool ShouldCorrect(bool hasPrevious, float distanceSquared)
        => hasPrevious && distanceSquared >= CorrectionDistanceSquared;

    /// <summary>
    /// The parties due a position correction this pass, at most <see cref="MaxCorrectionsPerTick"/> of them.
    /// </summary>
    /// <remarks>
    /// Only parties this instance actually controls are considered. Re-stating the position of a party a
    /// client owns would fight that player's own movement, which is the one thing a correction must never do.
    /// </remarks>
    public List<MobileParty> SelectPartiesToCorrect(IReadOnlyList<MobileParty> parties)
    {
        var due = new List<MobileParty>();
        if (parties == null || parties.Count == 0)
        {
            lastReported.Clear();
            resumeIndex = 0;
            return due;
        }

        // ponytail: stale entries are pruned by size, not by watching for party destruction. A destroyed party
        // leaves one dead entry behind; when the table outgrows the world twice over it is dropped whole and
        // refilled, costing one silent pass. Subscribe to destruction only if that ever measurably matters.
        if (lastReported.Count > parties.Count * 2) lastReported.Clear();

        if (resumeIndex >= parties.Count) resumeIndex = 0;
        int start = resumeIndex;

        for (int scanned = 0; scanned < parties.Count && due.Count < MaxCorrectionsPerTick; scanned++)
        {
            int index = (start + scanned) % parties.Count;
            resumeIndex = index + 1;

            var party = parties[index];
            if (party == null || !party.IsActive) continue;
            if (!party.IsControlledByThisInstance()) continue;

            var position = party.Position;
            bool hasPrevious = lastReported.TryGetValue(party.StringId, out var previous);

            if (!ShouldCorrect(hasPrevious, hasPrevious ? previous.DistanceSquared(position) : 0f))
            {
                // Only the first sighting is recorded here. A party that has moved but not far enough must keep
                // measuring from where it was last REPORTED, or the threshold is never reached by a slow party.
                if (!hasPrevious) lastReported[party.StringId] = position;
                continue;
            }

            lastReported[party.StringId] = position;
            due.Add(party);
        }

        return due;
    }
}
