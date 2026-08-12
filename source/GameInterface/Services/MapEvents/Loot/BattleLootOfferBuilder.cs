using GameInterface.Services.TroopRosters.Data;
using System;
using System.Collections.Generic;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>
/// Turns the spoils the server computed for one party into the indexed offer it will hold and defend.
/// </summary>
/// <remarks>
/// Line ORDER is the contract. Once this offer is sent, every answer refers to its lines by index, so the
/// order must be produced once and then never re-derived - rebuilding the offer to check an answer against it
/// would risk a different order and silently reinterpret every claim. The registry keeps the built offer for
/// exactly this reason.
///
/// Empty stacks are dropped rather than kept as zero-count lines. A line nobody can take is only an index for
/// a client to aim a malformed claim at.
/// </remarks>
public static class BattleLootOfferBuilder
{
    /// <summary>
    /// Builds an offer. <paramref name="isHero"/> answers whether a character id is a hero, which decides
    /// how the line may be claimed and, later, that it must move by action rather than roster copy.
    /// </summary>
    public static BattleLootOffer Build(
        string offerId,
        string mapEventId,
        string partyId,
        IEnumerable<BattleLootItemStack> items,
        IEnumerable<TroopRosterElementData> members,
        IEnumerable<TroopRosterElementData> prisoners,
        Func<string, bool> isHero)
    {
        var lines = new List<BattleLootOfferLine>();

        if (items != null)
        {
            foreach (var stack in items)
            {
                if (stack.Count <= 0 || string.IsNullOrEmpty(stack.ItemId)) continue;
                lines.Add(new BattleLootOfferLine(
                    BattleLootLineKind.Item, stack.ItemId, stack.ModifierId, stack.Count));
            }
        }

        AddTroops(lines, BattleLootLineKind.Member, members, isHero);
        AddTroops(lines, BattleLootLineKind.Prisoner, prisoners, isHero);

        return new BattleLootOffer(offerId, mapEventId, partyId, lines);
    }

    private static void AddTroops(
        List<BattleLootOfferLine> lines,
        BattleLootLineKind kind,
        IEnumerable<TroopRosterElementData> elements,
        Func<string, bool> isHero)
    {
        if (elements == null) return;

        foreach (var element in elements)
        {
            if (element.Number <= 0 || string.IsNullOrEmpty(element.CharacterId)) continue;

            bool hero = isHero != null && isHero(element.CharacterId);

            // A hero is one person. If the packed data ever says otherwise it is already corrupt - that is
            // precisely the "appears 3x" shape that broke a live save - so the line is written as one and the
            // validator's hero rule keeps it that way rather than passing the corruption along.
            int count = hero ? 1 : element.Number;

            lines.Add(new BattleLootOfferLine(
                kind,
                element.CharacterId,
                null,
                count,
                hero ? 0 : element.WoundedNumber,
                hero ? 0 : element.Xp,
                hero));
        }
    }
}
