using System;
using System.Collections.Generic;

namespace GameInterface.Services.MapEventParties;

/// <summary>
/// Decides whether a map-event party's flattened roster is worth putting on the wire again.
/// </summary>
/// <remarks>
/// WHERE THE TRAFFIC COMES FROM.
///
/// <c>MapEventPartyUpdatePatch</c> broadcasts a party's ENTIRE flattened roster every time vanilla calls
/// <c>MapEventParty.Update()</c>. That call has exactly one caller - <c>MapEventSide.MakeReadyParty</c> - which
/// runs from <c>MakeReady</c>, which runs from <c>MakeReadyForSimulation</c>, which <c>MapEvent.Update</c>
/// invokes once per simulation round for BOTH sides:
///
///     if (... &amp;&amp; _nextSimulationTime.IsPast) { CheckRunAway(); SimulateBattleSessionForMapEvent(); }
///
/// So every round, every party in the battle re-sends its whole roster - whether or not a single man in it
/// changed. Measured live during a siege, one ten-second window:
///
///     NetworkUpdateMapEventParty: 600 packets, 3,226,690 bytes   (~5.4 KB each, ~320 KB/s)
///
/// That burst put 5,320,697 bytes into an 8 MB reliable send buffer at once, and the peer's ping went from
/// 1 ms to 80 ms purely queueing behind it. The link was never the problem: quality read 1.00 and loss was
/// nil. The payload was.
///
/// WHY SUPPRESSION IS SAFE, AND WHY IT IS NOT A DELTA.
///
/// The receiver replaces the roster wholesale -
/// <c>mapEventParty._roster = FlattenedTroopSerializer.Deserialize(...)</c> - so a snapshot is idempotent and
/// re-sending an identical one is a no-op by construction. Skipping it is therefore indistinguishable from
/// sending it, which is a much stronger guarantee than a delta format can offer: nothing about the wire
/// format, the ordering or the receiver changes, and a client cannot end up reconstructing from a base it
/// never had.
///
/// THE KEYFRAME, AND WHY IT IS NOT OPTIONAL.
///
/// Dedup alone would let a party that never changes go silent forever, and anyone who missed its last
/// snapshot - a peer that joined afterwards, or one whose message was lost before the reliable channel
/// retried - would stay wrong with nothing to correct it. So an unchanged roster is still re-sent every
/// <see cref="KeyframeSeconds"/>. That bounds staleness at a known number instead of trusting delivery, and
/// still removes the overwhelming majority of the traffic: at roughly one simulation round per second, an
/// idle party drops from ~1 send/s to ~0.1 send/s.
/// </remarks>
internal sealed class RosterBroadcastGate
{
    /// <summary>
    /// How long an unchanged roster may stay off the wire before it is re-sent anyway.
    /// </summary>
    /// <remarks>
    /// Long enough that idle parties cost almost nothing, short enough that a peer which somehow missed a
    /// snapshot is corrected within a few seconds rather than for the rest of the battle. Ten seconds is also
    /// well inside the window in which a stale roster could matter: rosters feed troop SPAWNING, and a wave
    /// is minutes apart, not seconds.
    /// </remarks>
    internal const double KeyframeSeconds = 10d;

    /// <summary>Entries above this are pruned wholesale; see the note in <see cref="ShouldBroadcast"/>.</summary>
    internal const int MaxTrackedParties = 512;

    private readonly Dictionary<string, Entry> lastSent = new Dictionary<string, Entry>();

    /// <summary>Broadcasts allowed through, for the periodic saving report.</summary>
    internal long Sent { get; private set; }

    /// <summary>Broadcasts suppressed as byte-identical repeats.</summary>
    internal long Suppressed { get; private set; }

    private struct Entry
    {
        public ulong Hash;
        public DateTime SentUtc;
    }

    /// <summary>
    /// Whether this roster should go out now.
    /// </summary>
    /// <remarks>
    /// A party seen for the first time always sends: the gate has no evidence any peer has ever had it.
    ///
    /// <paramref name="force"/> sends regardless AND records the result, which is not the same as bypassing
    /// the gate. It exists for the one case where suppression would be wrong rather than merely wasteful: a
    /// PLAYER joining a battle needs every roster pushed to them, and the gate cannot know that - it tracks
    /// what was last SENT, not what each peer RECEIVED, so a roster broadcast to everyone else moments
    /// earlier looks identical and would be skipped for the newcomer. Recording on the way through is what
    /// then lets the AI joins that follow suppress against it instead of starting from nothing.
    /// </remarks>
    public bool ShouldBroadcast(string partyId, FlattenedTroop[] troops, DateTime nowUtc, bool force = false)
    {
        if (string.IsNullOrEmpty(partyId))
        {
            Sent++;
            return true;
        }

        if (force)
        {
            if (lastSent.Count > MaxTrackedParties) lastSent.Clear();
            lastSent[partyId] = new Entry { Hash = HashRoster(troops), SentUtc = nowUtc };
            Sent++;
            return true;
        }

        // ponytail: pruned by size rather than by tracking battle lifetime. A finished battle leaves its
        // parties behind; when the table outgrows any plausible number of them it is dropped whole, costing
        // one keyframe each. Hook battle end only if this ever shows up as more than a rounding error.
        if (lastSent.Count > MaxTrackedParties) lastSent.Clear();

        ulong hash = HashRoster(troops);
        bool hasPrevious = lastSent.TryGetValue(partyId, out var previous);

        if (!ShouldSend(hasPrevious, previous.Hash, previous.SentUtc, hash, nowUtc))
        {
            Suppressed++;
            return false;
        }

        lastSent[partyId] = new Entry { Hash = hash, SentUtc = nowUtc };
        Sent++;
        return true;
    }

    /// <summary>
    /// The decision itself: changed, never sent, or due a keyframe.
    /// </summary>
    /// <remarks>
    /// Separated and argument-driven so it can be proven rather than sampled - the surrounding class needs a
    /// dictionary and a clock to exist, and the question is really about three comparisons.
    ///
    /// A clock that has gone backwards (a resumed process, an NTP step) must not be able to hold a roster off
    /// the wire indefinitely, so a negative interval counts as due.
    /// </remarks>
    internal static bool ShouldSend(
        bool hasPrevious, ulong previousHash, DateTime previousSentUtc, ulong hash, DateTime nowUtc)
    {
        if (!hasPrevious) return true;
        if (previousHash != hash) return true;

        double elapsed = (nowUtc - previousSentUtc).TotalSeconds;
        return elapsed < 0d || elapsed >= KeyframeSeconds;
    }

    /// <summary>
    /// Order-sensitive FNV-1a over every field that reaches the wire.
    /// </summary>
    /// <remarks>
    /// Written out rather than built from <c>string.GetHashCode</c>, which is randomised per process on
    /// .NET Core: this assembly is loaded into both a .NET Framework client and a .NET 6 server, and a hash
    /// that changes between runs would defeat the keyframe accounting in ways that only show as traffic.
    ///
    /// Every serialised member is folded in, so any change a peer could observe changes the hash. Order
    /// matters and is included implicitly, because the roster is rebuilt in a deterministic order and a
    /// reordering IS a different snapshot to the receiver.
    /// </remarks>
    internal static ulong HashRoster(FlattenedTroop[] troops)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offsetBasis;
        if (troops == null) return hash;

        void Fold(ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= (value >> (i * 8)) & 0xFF;
                hash *= prime;
            }
        }

        Fold((ulong)troops.Length);
        foreach (var troop in troops)
        {
            var id = troop.ObjectId;
            if (id != null)
                foreach (char c in id)
                {
                    hash ^= c;
                    hash *= prime;
                }

            Fold(troop.IsHero ? 1UL : 0UL);
            Fold(unchecked((ulong)troop.UniqueSeed));
            Fold(unchecked((ulong)(int)troop.State));
            Fold(unchecked((ulong)troop.Xp));
            Fold(unchecked((ulong)troop.XpGained));
        }

        return hash;
    }
}
