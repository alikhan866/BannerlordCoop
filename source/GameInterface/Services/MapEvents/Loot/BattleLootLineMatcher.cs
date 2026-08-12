using GameInterface.Services.TroopRosters.Data;
using System.Collections.Generic;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>
/// Maps what is still sitting on the staged rosters back onto the offer lines it came from.
/// </summary>
/// <remarks>
/// The staged rosters do not carry line numbers - they are ordinary game rosters - so the client has to
/// recognise its own offer in them. Matching is on the full identity of a line, not just its id: an offer can
/// hold five plain swords on one line and a lordly one on another, and treating those as the same line is
/// exactly how a player could end up claiming the good sword by leaving the plain ones.
///
/// Consumption is greedy in line order, which makes the result deterministic when an offer does contain two
/// lines with the same identity. Any other rule would have to invent a reason to prefer one, and both lines
/// describe the same goods anyway, so the split between them cannot change what the player ends up with.
///
/// Pure and parallel-array shaped: it takes packed contents and returns a remaining-count per line, which is
/// exactly what <see cref="BattleLootSelection.FromRemaining"/> consumes.
/// </remarks>
public static class BattleLootLineMatcher
{
    /// <summary>
    /// Counts how much of each offer line is still unclaimed.
    /// </summary>
    /// <returns>An array parallel to <c>offer.Lines</c>.</returns>
    public static int[] CountRemaining(
        BattleLootOffer offer,
        IReadOnlyList<BattleLootItemStack> remainingItems,
        IReadOnlyList<TroopRosterElementData> remainingMembers,
        IReadOnlyList<TroopRosterElementData> remainingPrisoners)
    {
        var lines = offer.Lines ?? new BattleLootOfferLine[0];
        var remaining = new int[lines.Length];

        var pool = new Dictionary<string, int>();
        Pool(pool, BattleLootLineKind.Item, remainingItems);
        Pool(pool, BattleLootLineKind.Member, remainingMembers);
        Pool(pool, BattleLootLineKind.Prisoner, remainingPrisoners);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var key = Key(line.Kind, line.ObjectId, line.ModifierId);

            if (!pool.TryGetValue(key, out var available) || available <= 0)
            {
                // Nothing of this line is left, so all of it was taken.
                remaining[i] = 0;
                continue;
            }

            int matched = available < line.Count ? available : line.Count;
            remaining[i] = matched;
            pool[key] = available - matched;
        }

        return remaining;
    }

    private static void Pool(
        Dictionary<string, int> pool,
        BattleLootLineKind kind,
        IReadOnlyList<BattleLootItemStack> stacks)
    {
        if (stacks == null) return;

        foreach (var stack in stacks)
        {
            if (stack.Count <= 0) continue;
            Add(pool, Key(kind, stack.ItemId, stack.ModifierId), stack.Count);
        }
    }

    private static void Pool(
        Dictionary<string, int> pool,
        BattleLootLineKind kind,
        IReadOnlyList<TroopRosterElementData> elements)
    {
        if (elements == null) return;

        foreach (var element in elements)
        {
            if (element.Number <= 0) continue;
            Add(pool, Key(kind, element.CharacterId, null), element.Number);
        }
    }

    private static void Add(Dictionary<string, int> pool, string key, int count)
    {
        pool.TryGetValue(key, out var existing);
        pool[key] = existing + count;
    }

    /// <summary>
    /// A line's full identity. Null and empty modifiers are the same thing - one comes off the wire, the
    /// other out of a roster with no modifier - and letting them differ would split one line into two.
    /// </summary>
    private static string Key(BattleLootLineKind kind, string objectId, string modifierId)
        => (int)kind + "|" + (objectId ?? string.Empty) + "|" + (modifierId ?? string.Empty);
}
