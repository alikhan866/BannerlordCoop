using System.Collections.Generic;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// Holds on to a map event that was finalized while players were still fighting its mission, so a result
/// arriving afterwards still has something to commit into.
/// </summary>
/// <remarks>
/// A battle's result is not data that can be replayed: setting <c>MapEvent.BattleState</c> to a victory runs
/// the native cascade - OnBattleWon, CalculateAndCommitMapEventResults, CaptureDefeatedPartyMembers - against
/// the live object graph. So there is no snapshot to apply later; there is only the object, or nothing.
///
/// When a map event is finalized underneath a live mission, the registry drops it and every lookup for it
/// fails. The mission carries on, the players finish the fight, and the conclusion then has nowhere to land:
/// measured live, a DefenderVictory reconciled by every member and refused three times with "the map event is
/// no longer active", so no casualties were applied and the defeated army walked away whole.
///
/// Keeping the reference costs one object and changes nothing about the normal path - the fallback is only
/// consulted after the ordinary lookup has already failed, which today means the result is lost outright. The
/// worst case is therefore exactly today's behaviour: the commit is attempted, and if the finalized event
/// cannot carry it the existing guard logs the throw and the result is lost as before.
///
/// Server only, and deliberately small: a handful of entries, oldest evicted, because each one holds a map
/// event's whole object graph and a battle whose result never arrives must not pin it forever.
/// </remarks>
internal interface IFinalizedBattleRetention
{
    void Retain(string mapEventId, MapEvent mapEvent);

    bool TryGet(string mapEventId, out MapEvent mapEvent);

    void Release(string mapEventId);
}

/// <inheritdoc cref="IFinalizedBattleRetention"/>
internal class FinalizedBattleRetention : IFinalizedBattleRetention
{
    /// <summary>
    /// How many finalized battles may be held at once.
    /// </summary>
    /// <remarks>
    /// Only battles with players still fighting them are ever retained, so this is bounded by how many
    /// missions can be running at once - a handful. The cap exists for the case where a conclusion never
    /// arrives at all (the player alt-F4s mid-battle), which would otherwise pin the object for the session.
    /// </remarks>
    private const int Capacity = 8;

    private readonly object gate = new object();
    private readonly Dictionary<string, MapEvent> retained = new Dictionary<string, MapEvent>();
    private readonly Queue<string> order = new Queue<string>();

    public void Retain(string mapEventId, MapEvent mapEvent)
    {
        if (string.IsNullOrEmpty(mapEventId) || mapEvent == null) return;

        lock (gate)
        {
            if (retained.ContainsKey(mapEventId))
            {
                retained[mapEventId] = mapEvent;
                return;
            }

            while (order.Count >= Capacity)
            {
                var oldest = order.Dequeue();
                retained.Remove(oldest);
            }

            retained[mapEventId] = mapEvent;
            order.Enqueue(mapEventId);
        }
    }

    public bool TryGet(string mapEventId, out MapEvent mapEvent)
    {
        mapEvent = null;
        if (string.IsNullOrEmpty(mapEventId)) return false;

        lock (gate)
        {
            if (!retained.TryGetValue(mapEventId, out mapEvent) || mapEvent == null) return false;
        }

        if (CanStillCarryAResult(mapEvent)) return true;

        mapEvent = null;
        return false;
    }

    /// <summary>
    /// Whether a finalized map event still has enough of itself left to commit a result.
    /// </summary>
    /// <remarks>
    /// This is the difference between recovering a battle and corrupting one. Committing walks both sides and
    /// their parties - casualties, captures, loot - and it is not atomic: a graph that has been half torn down
    /// would throw PART WAY THROUGH, leaving some of the result applied and the rest not. A lost result is
    /// recoverable by playing on; a half-applied one is a save with rosters nobody can reconcile.
    ///
    /// So the bar is the same shape as what the commit walks: both sides present, and parties on them. If the
    /// finalize has already emptied it, refuse - which lands exactly where this battle already was, with the
    /// result lost and said so in the log.
    /// </remarks>
    private static bool CanStillCarryAResult(MapEvent mapEvent)
    {
        var attackers = mapEvent.GetMapEventSide(BattleSideEnum.Attacker);
        var defenders = mapEvent.GetMapEventSide(BattleSideEnum.Defender);

        return attackers?.Parties != null && attackers.Parties.Count > 0
            && defenders?.Parties != null && defenders.Parties.Count > 0;
    }

    public void Release(string mapEventId)
    {
        if (string.IsNullOrEmpty(mapEventId)) return;

        // The queue keeps insertion order for eviction and is not searched; a released id simply finds no
        // entry when its turn to be evicted comes.
        lock (gate) retained.Remove(mapEventId);
    }
}
