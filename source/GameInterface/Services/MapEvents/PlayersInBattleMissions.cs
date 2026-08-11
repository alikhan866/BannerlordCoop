using System.Collections.Generic;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// [Server] Which players are inside a battle mission right now, independently of whether the campaign still
/// holds a MapEvent for that battle.
/// </summary>
/// <remarks>
/// The server has two ways of answering "is this player fighting", and they disagree in exactly one case.
///
/// Mission membership - "Controller X entered instance Y" - is the honest answer: it says a player is fighting
/// for as long as their mission is running. Reading <c>MobileParty.MapEvent</c> is a proxy for it, and in
/// single player an airtight one, because the battle IS that map event.
///
/// In co-op the map event can be torn down while the mission carries on. Measured live: the server destroyed
/// the map event at 13:39:39 and the player's client was still fighting it at 13:40:49, more than a minute
/// later. Everything reading the proxy concluded he had left. The fast-forward lock released and announced
/// "No more players are in map events", so the campaign was free to run at speed while he was mid-battle.
///
/// <c>SiegeBattleFreeze</c> already carries mission membership out to the siege tick for the same reason and
/// documents the same failure at the same settlement. This carries it to the fast-forward lock, so both gates
/// answer from the same fact instead of one of them guessing from a campaign object that may no longer exist.
///
/// Replaced wholesale rather than incremented per player, matching <c>RecomputeSiegeFreezes</c>, which
/// rebuilds its set for the same reason: a leaked entry here holds the campaign at normal speed forever, and
/// a rebuild cannot leak.
/// </remarks>
public static class PlayersInBattleMissions
{
    private static readonly object Gate = new object();
    private static readonly HashSet<string> Controllers = new HashSet<string>();
    private static readonly HashSet<string> LiveBattles = new HashSet<string>();

    /// <summary>Replace the whole set with the controllers currently inside a battle mission.</summary>
    public static void Replace(IEnumerable<string> controllerIds)
    {
        lock (Gate)
        {
            Controllers.Clear();
            if (controllerIds == null) return;
            foreach (var id in controllerIds)
                if (!string.IsNullOrEmpty(id)) Controllers.Add(id);
        }
    }

    /// <summary>Replace the set of map events that still have someone fighting in them.</summary>
    public static void ReplaceLiveBattles(IEnumerable<string> mapEventIds)
    {
        lock (Gate)
        {
            LiveBattles.Clear();
            if (mapEventIds == null) return;
            foreach (var id in mapEventIds)
                if (!string.IsNullOrEmpty(id)) LiveBattles.Add(id);
        }
    }

    /// <summary>
    /// Whether players are still inside the mission for this map event.
    /// </summary>
    /// <remarks>
    /// Used to notice a map event being finalized out from under a live mission - the condition that produced
    /// the original fault. The lock above now survives it, but a battle whose campaign event has been destroyed
    /// still has nowhere to commit its result, so it is worth saying so loudly rather than only coping.
    /// </remarks>
    public static bool HasFightingMembers(string mapEventId)
    {
        if (string.IsNullOrEmpty(mapEventId)) return false;
        lock (Gate) return LiveBattles.Contains(mapEventId);
    }

    /// <summary>Whether this controller is inside a battle mission.</summary>
    public static bool Contains(string controllerId)
    {
        if (string.IsNullOrEmpty(controllerId)) return false;
        lock (Gate) return Controllers.Contains(controllerId);
    }

    /// <summary>Forget everyone — used when the server tears down, so nothing survives into a new session.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Controllers.Clear();
            LiveBattles.Clear();
        }
    }
}
