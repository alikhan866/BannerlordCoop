using System.Globalization;
using System.Text;
using System.Threading;

namespace Missions.Diagnostics;

/// <summary>
/// Follows a shield-block sound from the attacker's machine to the blocker's screen.
/// </summary>
/// <remarks>
/// Nothing plays a shield impact locally for a REMOTE attacker. `Handle_LocalPresentation` only sends,
/// and the native engine never resolves a puppet's melee collision at all - measured, Mission.MeleeHitCallback
/// fired 21,633 times across two clients and not once with a remote attacker. So on the blocking client the
/// sound exists only if the whole replication chain holds:
///
///     patch sees AttackBlockedWithShield (attacker locally controlled)
///       -> published    MeleeHitPresentation
///       -> sent         NetworkMeleeHitPresentation   (victim identity must resolve)
///       -> received     on the blocker's machine
///       -> played       victim resolves, is active, and a sound index is found
///
/// Each stage is counted on both machines, so a single run says which one drops it rather than which one
/// looks suspicious.
/// </remarks>
internal static class ShieldImpactDiagnostics
{
    private static bool enabled;

    private static long blockedSeen;
    private static long published;
    private static long sent;
    private static long sendFailedNoIdentity;
    private static long received;
    private static long playedOk;
    private static long dropVictimUnknown;
    private static long dropVictimInactive;
    private static long dropNoSound;
    private static long soundIndexInvalid;
    private static long farFromVictim;
    private static long usedBlockEvent;
    private static long fellBackToItemPhysics;
    private static readonly object SoundGate = new object();
    private static readonly System.Collections.Generic.Dictionary<int, long> SoundIndexSeen = new();
    private static readonly System.Collections.Generic.Dictionary<int, long> WeaponClassSeen = new();

    public static bool Enabled => enabled;

    public static void Start()
    {
        Interlocked.Exchange(ref blockedSeen, 0);
        Interlocked.Exchange(ref published, 0);
        Interlocked.Exchange(ref sent, 0);
        Interlocked.Exchange(ref sendFailedNoIdentity, 0);
        Interlocked.Exchange(ref received, 0);
        Interlocked.Exchange(ref playedOk, 0);
        Interlocked.Exchange(ref dropVictimUnknown, 0);
        Interlocked.Exchange(ref dropVictimInactive, 0);
        Interlocked.Exchange(ref dropNoSound, 0);
        Interlocked.Exchange(ref soundIndexInvalid, 0);
        Interlocked.Exchange(ref usedBlockEvent, 0);
        Interlocked.Exchange(ref fellBackToItemPhysics, 0);
        lock (SoundGate) { SoundIndexSeen.Clear(); WeaponClassSeen.Clear(); farFromVictim = 0; }
        enabled = true;
    }

    public static void BlockedSeen() { if (enabled) Interlocked.Increment(ref blockedSeen); }
    public static void Published() { if (enabled) Interlocked.Increment(ref published); }
    public static void Sent() { if (enabled) Interlocked.Increment(ref sent); }
    public static void SendFailedNoIdentity() { if (enabled) Interlocked.Increment(ref sendFailedNoIdentity); }
    public static void Received() { if (enabled) Interlocked.Increment(ref received); }
    public static void PlayedOk() { if (enabled) Interlocked.Increment(ref playedOk); }

    /// <summary>The sound actually asked for, and how far from the blocker it was placed.</summary>
    public static void SoundChosen(
        int soundIndex,
        int weaponClass,
        float distanceToVictim,
        bool usedRealBlockEvent)
    {
        if (!enabled) return;
        if (usedRealBlockEvent) Interlocked.Increment(ref usedBlockEvent);
        else Interlocked.Increment(ref fellBackToItemPhysics);
        if (soundIndex < 0) Interlocked.Increment(ref soundIndexInvalid);
        lock (SoundGate)
        {
            SoundIndexSeen.TryGetValue(soundIndex, out long n);
            SoundIndexSeen[soundIndex] = n + 1;
            WeaponClassSeen.TryGetValue(weaponClass, out long w);
            WeaponClassSeen[weaponClass] = w + 1;
            if (distanceToVictim > 5f) farFromVictim++;
        }
    }
    public static void DropVictimUnknown() { if (enabled) Interlocked.Increment(ref dropVictimUnknown); }
    public static void DropVictimInactive() { if (enabled) Interlocked.Increment(ref dropVictimInactive); }
    public static void DropNoSound() { if (enabled) Interlocked.Increment(ref dropNoSound); }

    public static string Snapshot(bool stop)
    {
        var text = new StringBuilder();
        text.Append("shieldImpact: blockedSeen=").Append(Interlocked.Read(ref blockedSeen).ToString(CultureInfo.InvariantCulture));
        text.Append(" published=").Append(Interlocked.Read(ref published).ToString(CultureInfo.InvariantCulture));
        text.Append(" SENT=").Append(Interlocked.Read(ref sent).ToString(CultureInfo.InvariantCulture));
        text.Append(" sendFailedNoIdentity=").Append(Interlocked.Read(ref sendFailedNoIdentity).ToString(CultureInfo.InvariantCulture));
        text.Append(" | RECEIVED=").Append(Interlocked.Read(ref received).ToString(CultureInfo.InvariantCulture));
        text.Append(" PLAYED=").Append(Interlocked.Read(ref playedOk).ToString(CultureInfo.InvariantCulture));
        text.Append(" dropped=[victimUnknown:").Append(Interlocked.Read(ref dropVictimUnknown).ToString(CultureInfo.InvariantCulture));
        text.Append(" victimInactive:").Append(Interlocked.Read(ref dropVictimInactive).ToString(CultureInfo.InvariantCulture));
        text.Append(" noSound:").Append(Interlocked.Read(ref dropNoSound).ToString(CultureInfo.InvariantCulture)).Append(']');
        text.Append(" | REAL_BLOCK_EVENT=").Append(Interlocked.Read(ref usedBlockEvent).ToString(CultureInfo.InvariantCulture));
        text.Append(" fellBackToItemPhysics=").Append(Interlocked.Read(ref fellBackToItemPhysics).ToString(CultureInfo.InvariantCulture));
        text.Append(" | SOUND_INDEX_INVALID=").Append(Interlocked.Read(ref soundIndexInvalid).ToString(CultureInfo.InvariantCulture));
        lock (SoundGate)
        {
            text.Append(" farFromVictim=").Append(farFromVictim.ToString(CultureInfo.InvariantCulture));
            text.Append(" indices=[");
            foreach (var kv in SoundIndexSeen)
                text.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append(':').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(' ');
            text.Append("] weaponClasses=[");
            foreach (var kv in WeaponClassSeen)
                text.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append(':').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(' ');
            text.Append(']');
        }
        if (stop) enabled = false;
        return text.ToString();
    }
}
