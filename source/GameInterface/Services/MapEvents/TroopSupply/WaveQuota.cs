using System;
using System.Collections.Generic;

namespace GameInterface.Services.MapEvents.TroopSupply;

/// <summary>
/// How one wave is divided between the parties a single client supplies.
/// </summary>
/// <remarks>
/// Pure arithmetic, deliberately separated from <see cref="CoopTroopSupplier"/> so it can be proven rather
/// than sampled: the supplier needs an object manager, an agent budget and a live reserve to exist, which is
/// far too much scaffolding to stand up for a question that is really about integers.
///
/// THE INVARIANT, and why it is not negotiable. Every function here returns a quota that sums to EXACTLY
/// <c>min(target, total remaining)</c> and never gives a party more than it holds. Deployment reserves
/// <c>InitialSpawnNumber - ReservedTroopsCount</c> and skips the whole side while the count falls short, so a
/// wave that quietly under-delivers does not merely look thin - it stops the side being planned at all. A
/// wave that over-delivers breaks the other direction: the number this client contributes is its slice of a
/// partition computed from <c>SideOffset</c>, and those slices sum to the side's allocation across every
/// owner with no coordination between them. Hand back more than the slice and one client eats another's
/// capacity.
///
/// Both are why the remainder is spread by CUMULATIVE FLOORING rather than by proportional rounding: taking
/// the difference between the wave scaled to the END of a party's range and to its START distributes the
/// rounding error exactly once across the whole set, where per-party proportional rounding loses or gains a
/// troop per party.
/// </remarks>
internal static class WaveQuota
{
    /// <summary>
    /// Each party gives up a share of the wave in proportion to what it has LEFT.
    /// </summary>
    internal static int[] Proportional(IReadOnlyList<int> remaining, long target)
    {
        var quota = new int[remaining?.Count ?? 0];
        long totalRemaining = TotalOf(remaining);
        if (totalRemaining <= 0) return quota;

        target = Clamp(target, totalRemaining);
        if (target <= 0) return quota;

        long cumulative = 0;
        long allocated = 0;
        for (int i = 0; i < remaining.Count; i++)
        {
            long partyRemaining = Math.Max(0, remaining[i]);
            cumulative += partyRemaining;
            // long throughout: cumulative * target overflows int for a large army and a large wave, and an
            // overflow here would hand out a negative or wrapped share.
            long end = cumulative * target / totalRemaining;
            quota[i] = (int)Math.Min(end - allocated, partyRemaining);
            allocated += quota[i];
        }

        return quota;
    }

    /// <summary>
    /// The client's own party fills the wave first; whatever is left over is shared out among the rest.
    /// </summary>
    /// <remarks>
    /// This is the "Prefer My Troops" shape, and it deliberately re-creates the behaviour
    /// <see cref="Proportional"/> exists to avoid - a player in an army fielding their whole party against
    /// the enemy's mixed wave and fighting it alone while the allied lords trickle in behind. That is the
    /// point of the option; it is a choice here rather than an accident, which is why the proportional split
    /// stays the default.
    ///
    /// What it must NOT change is how many troops this client contributes in total. That number arrives as
    /// <paramref name="target"/> from the side partition and is spent in full either way - only its
    /// distribution across this client's own parties differs. Reordering inside the slice cannot starve
    /// another client, because another client's slice was never in this array.
    ///
    /// The leftover is spread with the same cumulative flooring as <see cref="Proportional"/>, over the other
    /// parties alone, so the total stays exact.
    /// </remarks>
    internal static int[] OwnPartyFirst(IReadOnlyList<int> remaining, long target, int ownIndex)
    {
        var quota = new int[remaining?.Count ?? 0];
        long totalRemaining = TotalOf(remaining);
        if (totalRemaining <= 0) return quota;

        target = Clamp(target, totalRemaining);
        if (target <= 0) return quota;

        bool hasOwn = ownIndex >= 0 && ownIndex < remaining.Count;
        long ownRemaining = hasOwn ? Math.Max(0, remaining[ownIndex]) : 0;

        if (hasOwn)
        {
            quota[ownIndex] = (int)Math.Min(ownRemaining, target);
            target -= quota[ownIndex];
        }
        if (target <= 0) return quota;

        long othersTotal = totalRemaining - ownRemaining;
        if (othersTotal <= 0) return quota;

        long cumulative = 0;
        long allocated = 0;
        for (int i = 0; i < remaining.Count; i++)
        {
            if (i == ownIndex) continue;

            long partyRemaining = Math.Max(0, remaining[i]);
            cumulative += partyRemaining;
            long end = cumulative * target / othersTotal;
            quota[i] = (int)Math.Min(end - allocated, partyRemaining);
            allocated += quota[i];
        }

        return quota;
    }

    private static long TotalOf(IReadOnlyList<int> remaining)
    {
        if (remaining == null) return 0;

        long total = 0;
        for (int i = 0; i < remaining.Count; i++)
            total += Math.Max(0, remaining[i]);
        return total;
    }

    /// <summary>
    /// Asking for more than exists is normal - the engine asks for a side's whole deficit - and apportioning
    /// the capped figure is what keeps each party's share inside its own remainder.
    /// </summary>
    private static long Clamp(long target, long totalRemaining)
        => target <= 0 ? 0 : Math.Min(target, totalRemaining);
}
