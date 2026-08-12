using System.Collections.Generic;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>
/// Decides whether a player's answer to a loot offer is one the server is willing to apply.
/// </summary>
/// <remarks>
/// Deliberately pure and free of Bannerlord types: it takes an offer and a result, and returns resolved claims
/// or a reason. That means the whole trust boundary can be tested without a campaign, a mission or a network,
/// which is the only way it will actually stay covered.
///
/// It answers ONE question - "may this be applied" - and nothing about whether it will succeed. Capacity
/// limits, a hero who stopped being capturable between the offer and the answer, a party that no longer
/// exists: all of that is live campaign state and belongs to the apply step, which must re-check anyway
/// because time passes between validation and application.
/// </remarks>
public static class BattleLootValidator
{
    /// <summary>
    /// Validates <paramref name="result"/> against <paramref name="offer"/>.
    /// </summary>
    /// <returns>True when the result may be applied; false with <paramref name="rejection"/> set otherwise.</returns>
    public static bool TryValidate(
        BattleLootOffer offer,
        BattleLootResult result,
        out IReadOnlyList<BattleLootResolvedClaim> resolved,
        out BattleLootRejection rejection)
    {
        resolved = null;
        rejection = BattleLootRejection.None;

        // An offer answered with a different offer's id is the stale-answer case, not a malformed one: a
        // siege runs assault after assault, so an answer to the previous battle can genuinely arrive here.
        if (!string.Equals(offer.OfferId, result.OfferId, System.StringComparison.Ordinal))
        {
            rejection = BattleLootRejection.OfferIdMismatch;
            return false;
        }

        var lines = offer.Lines ?? new BattleLootOfferLine[0];
        var claims = result.Claims ?? new BattleLootClaim[0];

        var accepted = new List<BattleLootResolvedClaim>(claims.Length);
        var seen = new HashSet<int>();

        foreach (var claim in claims)
        {
            if (claim.LineIndex < 0 || claim.LineIndex >= lines.Length)
            {
                rejection = BattleLootRejection.UnknownLineIndex;
                return false;
            }

            // One claim per line. Without this, two claims on the same line would each pass the
            // "no more than the line holds" check while together taking twice what was offered - and for a
            // hero line it would also be the client asking to both keep and release the same person.
            if (!seen.Add(claim.LineIndex))
            {
                rejection = BattleLootRejection.DuplicateLineIndex;
                return false;
            }

            if (claim.Count <= 0)
            {
                rejection = BattleLootRejection.NonPositiveCount;
                return false;
            }

            var line = lines[claim.LineIndex];

            // Checked BEFORE the count bound, so the specific reason wins and the rule stays reachable. A
            // well-formed hero line offers exactly one, so an over-claim on it would otherwise always be
            // reported as "exceeds offered" - true, but it hides that the client asked for two of a person,
            // and it would leave this rule dead for every line the server actually builds.
            if (line.IsHero && claim.Count != 1)
            {
                rejection = BattleLootRejection.HeroCountMustBeOne;
                return false;
            }

            if (claim.Count > line.Count)
            {
                rejection = BattleLootRejection.ExceedsOfferedCount;
                return false;
            }

            // Releasing is only a thing you can do to a prisoner. Allowing it elsewhere would mean the apply
            // step had to decide what "release" means for a sack of grain.
            if (claim.Disposition == BattleLootDisposition.Release && line.Kind != BattleLootLineKind.Prisoner)
            {
                rejection = BattleLootRejection.DispositionOnNonPrisonerLine;
                return false;
            }

            accepted.Add(new BattleLootResolvedClaim(line, claim.Count, claim.Disposition));
        }

        resolved = accepted;
        return true;
    }
}
