using System;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// How many troops a side has on the field beyond what the battle is sized for, and therefore how many should
/// stand down.
/// </summary>
/// <remarks>
/// The engine sizes a battle once and every later wave is drawn against that split, so a side that opens 350
/// against 50 keeps 350 men on the field after the other side has been reinforced to 1050 - by then it should
/// be holding 100. Feeding the under-strength side alone cannot fix that: it would put the field at 650 on a
/// battle sized for 400. The over-strength side has to give ground.
///
/// PLAYERS ARE NOT TROOPS for this purpose, and that is deliberate on two counts:
///
///   A player is never stood down. Being pulled off the field mid-fight is not something that should happen to
///   somebody who is playing, whatever the arithmetic says.
///
///   A player does not displace a troop either. A client joining a 200-v-200 battle makes it 200 v 201, not
///   200 v 200 with one of their men pushed out to make room. So players are excluded from BOTH the count and
///   the target, and the limit is applied to troops alone.
/// </remarks>
internal static class BattleFieldBalance
{
    /// <summary>
    /// How many of this side's troops should withdraw. Players are excluded by the caller from
    /// <paramref name="troopsOnField"/>, so they can neither be withdrawn nor crowd anyone out.
    /// </summary>
    /// <param name="troopsOnField">Active non-player troops this side currently has fielded.</param>
    /// <param name="sideTarget">What the battle size allows this side, from the usual proportional split.</param>
    internal static int Surplus(int troopsOnField, int sideTarget)
        => Math.Max(0, troopsOnField - Math.Max(0, sideTarget));

    /// <summary>
    /// This client's share of a side-wide withdrawal.
    /// </summary>
    /// <remarks>
    /// Each client owns the agents it spawned and only it can take them off the field, so a side-wide surplus
    /// has to be divided the same way a side-wide allocation is - by ownership. Rounded DOWN, and deliberately:
    /// every owner rounding up would withdraw more than the surplus between them and leave the side short. A
    /// remainder of one or two men over the limit is corrected on the next pass as casualties move the numbers
    /// anyway.
    /// </remarks>
    internal static int OwnedShareOfSurplus(int surplus, int ownedTroopsOnField, int sideTroopsOnField)
    {
        if (surplus <= 0 || ownedTroopsOnField <= 0 || sideTroopsOnField <= 0) return 0;
        if (ownedTroopsOnField >= sideTroopsOnField) return Math.Min(surplus, ownedTroopsOnField);

        var share = (int)((long)surplus * ownedTroopsOnField / sideTroopsOnField);
        return Math.Min(share, ownedTroopsOnField);
    }
}
