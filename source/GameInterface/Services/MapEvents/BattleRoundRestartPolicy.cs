namespace GameInterface.Services.MapEvents;

/// <summary>
/// Decides whether reinforcements that have arrived mid-battle are worth restarting the round for.
/// </summary>
/// <remarks>
/// The engine sizes a battle once, when it starts: <c>InitWithSinglePhase</c> splits the battle size between the
/// two sides in proportion to the totals it is handed, and every later wave is drawn against that split. A side
/// that begins 100 strong against 300 therefore keeps feeding in its original share for the rest of the fight,
/// even after twelve lords ride in behind it - which is why relief that arrives late trickles in at the old
/// side's rate instead of changing the shape of the battle.
///
/// Restarting the round is what re-derives the split from the totals as they now stand. It is deliberately not
/// free - everyone returns to their spawn - so it must answer to a real change, not to every party that wanders
/// in. Hence a threshold, and hence a policy object rather than an inline comparison: this is the rule most
/// likely to need tuning once it has been played.
/// </remarks>
internal static class BattleRoundRestartPolicy
{
    /// <summary>
    /// The share of a side's strength that has to arrive at once before the round is worth restarting.
    /// </summary>
    /// <remarks>
    /// A tenth is low enough that genuine relief always triggers it (the reported case was a side more than
    /// doubling) and high enough that a single scout joining a 900-man battle does not send 900 men back to
    /// their spawn points.
    /// </remarks>
    internal const double MaterialGrowthFraction = 0.10;

    /// <summary>
    /// True when a side's total has grown enough that the battle should be re-sized around it.
    /// </summary>
    /// <param name="sizedAtTotal">The side total the current round was sized from.</param>
    /// <param name="currentTotal">The side total right now, after reinforcements.</param>
    internal static bool IsMaterialGrowth(int sizedAtTotal, int currentTotal)
    {
        if (currentTotal <= sizedAtTotal) return false;

        // A side that was empty when the round was sized is always material: there is no proportion to take,
        // and anything arriving is the whole of it.
        if (sizedAtTotal <= 0) return true;

        var growth = currentTotal - sizedAtTotal;
        return growth >= sizedAtTotal * MaterialGrowthFraction;
    }

    /// <summary>
    /// True when either side has grown materially. Both are checked because a restart re-sizes the whole
    /// battle: a defender side that doubles changes the attackers' share just as much as its own.
    /// </summary>
    internal static bool ShouldRestart(
        int defenderSizedAt, int defenderNow,
        int attackerSizedAt, int attackerNow)
        => IsMaterialGrowth(defenderSizedAt, defenderNow)
        || IsMaterialGrowth(attackerSizedAt, attackerNow);
}
