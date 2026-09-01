using System.Collections.Generic;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// Keeps a troop's scoreboard upgrade total from running away below zero.
/// </summary>
/// <remarks>
/// Vanilla's <c>TroopUpgradeTracker.CheckUpgradedCount</c> reports a NEGATIVE count to withdraw a troop
/// type that is no longer in the party roster:
///
/// <code>
/// else if (_upgradedRegulars.TryGetValue((party, character), out value2) &amp;&amp; value2 > 0)
///     result = -value2;
/// </code>
///
/// It never clears <c>_upgradedRegulars</c> on that branch, so it returns the same negative EVERY time it
/// is asked. Vanilla asks rarely. This mod asks on every hit reward and every agent removal - thousands of
/// times in one battle - and broadcasts each answer to the clients, where
/// <c>BattleObserver.TroopNumberChanged</c> ADDS it to a running total. The scoreboard walked to -201406
/// for one party while the other showed a normal 239.
///
/// A troop's upgrade count is a tally of upgrades and cannot be negative, so the running total is clamped
/// at zero and only the difference is sent. The single legitimate withdrawal still gets through; the
/// thousands of repeats after it become no-ops.
/// </remarks>
internal sealed class ScoreboardUpgradeTotals
{
    private readonly Dictionary<(string MapEventId, string PartyId, string CharacterId), int> totals = new();

    /// <summary>
    /// Returns the amount that should actually be broadcast, which is zero when there is nothing to change.
    /// </summary>
    public int Accept(string mapEventId, string partyId, string characterId, int reportedCount)
    {
        if (reportedCount == 0) return 0;

        var key = (mapEventId, partyId, characterId);
        totals.TryGetValue(key, out int running);

        int desired = running + reportedCount;
        if (desired < 0) desired = 0;

        int delta = desired - running;
        if (delta == 0) return 0;

        totals[key] = desired;
        return delta;
    }

    /// <summary>Drops the totals for a finished battle so a later one starts clean.</summary>
    public void Forget(string mapEventId)
    {
        if (string.IsNullOrEmpty(mapEventId)) return;
        var stale = new List<(string, string, string)>();
        foreach (var key in totals.Keys)
        {
            if (key.MapEventId == mapEventId) stale.Add(key);
        }
        foreach (var key in stale) totals.Remove(key);
    }
}
