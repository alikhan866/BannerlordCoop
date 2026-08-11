using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.SiegeEvents;

/// <summary>
/// [Server] Sieges that must not advance because a player is away fighting a battle for them.
/// </summary>
/// <remarks>
/// Vanilla stops a siege while its besieger is in a fight, and it decides that by looking for a MapEvent:
/// <c>SiegeEvent.Tick</c> returns early when <c>BesiegerCamp.LeaderParty.MapEvent</c> or the besieged
/// settlement's party has one. In single player that is airtight, because the battle the player is fighting IS
/// that map event.
///
/// In co-op it is not. A client can be inside a battle mission for which the server holds no map event at all -
/// a battle restored from a save is the reproducible case, since the save carries the mission but the server
/// never re-registers the event. The gate then reads "no map event, nobody is fighting" and the siege keeps
/// bombarding while the player is in the middle of the assault. Observed at Phycaon: the client sat in a live
/// mission while the server reported <c>Jian's Party MapEvent: null</c> and the trebuchets kept landing.
///
/// The server does know, though - it records mission membership independently of map events ("Controller X
/// entered instance Y"). This carries that knowledge to the tick gate, so the freeze follows who is actually
/// fighting rather than whether a particular campaign object happens to exist.
/// </remarks>
internal static class SiegeBattleFreeze
{
    private static readonly object Gate = new object();
    private static readonly HashSet<string> FrozenSettlementIds = new HashSet<string>();

    /// <summary>Stop advancing the siege of this settlement. Idempotent.</summary>
    public static void Freeze(string settlementStringId)
    {
        if (string.IsNullOrEmpty(settlementStringId)) return;
        lock (Gate) FrozenSettlementIds.Add(settlementStringId);
    }

    /// <summary>Let the siege of this settlement advance again. Idempotent.</summary>
    public static void Thaw(string settlementStringId)
    {
        if (string.IsNullOrEmpty(settlementStringId)) return;
        lock (Gate) FrozenSettlementIds.Remove(settlementStringId);
    }

    public static bool IsFrozen(Settlement settlement)
    {
        var id = settlement?.StringId;
        if (string.IsNullOrEmpty(id)) return false;
        lock (Gate) return FrozenSettlementIds.Contains(id);
    }

    /// <summary>
    /// Drops every freeze. Called when a session ends so a siege frozen by a battle that never reported its
    /// end cannot stay frozen into the next session.
    /// </summary>
    public static void Clear()
    {
        lock (Gate) FrozenSettlementIds.Clear();
    }

    internal static IReadOnlyCollection<string> FrozenIds
    {
        get { lock (Gate) return new List<string>(FrozenSettlementIds); }
    }
}
