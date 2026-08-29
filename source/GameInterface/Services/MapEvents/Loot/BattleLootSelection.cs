using System.Collections.Generic;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>
/// Works out what a player took by looking at what they LEFT.
/// </summary>
/// <remarks>
/// The staged rosters vanilla hands the loot screen start out holding the whole offer, and the screen removes
/// from them as the player takes things. So whatever is still there when the encounter closes is what was
/// declined, and the difference is the answer.
///
/// Deriving it from the STAGED rosters rather than from the party is the important part. The party is live:
/// a replicated update landing while the screen is open - another player donating troops, a garrison
/// handover, wounded recovering - would show up in a before/after comparison of the party and be attributed
/// to the player as loot they took. The staged rosters are local scratch space that nothing else writes to,
/// so the subtraction is exact.
///
/// Pure, and parallel-array shaped so it can be tested without a campaign: the caller counts what remains per
/// offer line, and this turns those counts into claims.
/// </remarks>
public static class BattleLootSelection
{
    /// <summary>
    /// Turns "these heroes were freed" into the per-line dispositions <see cref="FromRemaining"/> expects.
    /// </summary>
    /// <remarks>
    /// This existed only as a parameter before, and every production caller passed null - so the one line that
    /// can construct a Release claim was unreachable, and a lord the player freed on the after-battle screen
    /// reached the server marked "keep" and was imprisoned. The choice with consequences was the single thing
    /// that never travelled.
    ///
    /// Keyed by the line's ObjectId, which for a hero is their CharacterObject id - the same id the offer was
    /// built with. Only hero PRISONER lines can carry a release: BattleLootValidator rejects one on any other
    /// kind, so producing it would turn a player's choice into a rejected answer.
    /// </remarks>
    public static BattleLootDisposition[] DispositionsFor(
        BattleLootOffer offer,
        ICollection<string> releasedObjectIds)
    {
        var lines = offer.Lines ?? new BattleLootOfferLine[0];
        var dispositions = new BattleLootDisposition[lines.Length];

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            bool freed = releasedObjectIds != null
                && line.IsHero
                && line.Kind == BattleLootLineKind.Prisoner
                && line.ObjectId != null
                && releasedObjectIds.Contains(line.ObjectId);

            dispositions[i] = freed ? BattleLootDisposition.Release : BattleLootDisposition.Keep;
        }

        return dispositions;
    }

    /// <summary>
    /// Builds the answer to <paramref name="offer"/> from how much of each line is still unclaimed.
    /// </summary>
    /// <param name="remainingPerLine">
    /// Parallel to <c>offer.Lines</c>: how many of that line the player left behind. A short or null array is
    /// read as "nothing left", which is the safe reading - it claims only what the offer actually held.
    /// </param>
    /// <param name="dispositionPerLine">
    /// Parallel to <c>offer.Lines</c>: what the player chose for a hero prisoner. Ignored for other lines.
    /// </param>
    public static BattleLootResult FromRemaining(
        BattleLootOffer offer,
        IReadOnlyList<int> remainingPerLine,
        IReadOnlyList<BattleLootDisposition> dispositionPerLine = null)
    {
        var lines = offer.Lines ?? new BattleLootOfferLine[0];
        var claims = new List<BattleLootClaim>(lines.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            int remaining = remainingPerLine != null && i < remainingPerLine.Count ? remainingPerLine[i] : 0;
            if (remaining < 0) remaining = 0;
            if (remaining > line.Count) remaining = line.Count;

            int taken = line.Count - remaining;

            var disposition = dispositionPerLine != null && i < dispositionPerLine.Count
                ? dispositionPerLine[i]
                : BattleLootDisposition.Keep;

            // Releasing a hero prisoner is an ACTION, not an absence. The staged roster looks identical
            // whether the player freed a lord or simply ignored him, so a release has to be carried
            // explicitly - otherwise the one choice with consequences (captivity ends, relations move, the
            // lord walks) would be the one thing that never reaches the server.
            if (line.IsHero && line.Kind == BattleLootLineKind.Prisoner &&
                disposition == BattleLootDisposition.Release)
            {
                claims.Add(new BattleLootClaim(i, 1, BattleLootDisposition.Release));
                continue;
            }

            // Everything else: nothing taken means nothing to say. Vanilla discards what is left on the
            // screen, so silence is the correct way to express "declined".
            if (taken <= 0) continue;

            claims.Add(new BattleLootClaim(i, taken, BattleLootDisposition.Keep));
        }

        return new BattleLootResult(offer.OfferId, claims);
    }
}
