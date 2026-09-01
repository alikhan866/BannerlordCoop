using System;
using System.Globalization;
using System.Text;

namespace Missions.Diagnostics;

/// <summary>
/// Where a remote attacker's swing actually is, at the instant its blow lands on you.
/// </summary>
/// <remarks>
/// <para>
/// Every other counter in this investigation aggregates across every puppet on the field - four hundred of them,
/// scattered over a ten-thousand-man battle, nearly all far away and idle. The three or four actually swinging at
/// the player are about one percent of that sample. If those are the pathological ones, the mean cannot show it:
/// churn looked harmless at 0.5/sec until it was reported per busiest agent instead.
/// </para>
/// <para>
/// So this scopes itself the only way that guarantees relevance - it records only when a blow actually connects,
/// which by definition means the attacker was in contact with the victim.
/// </para>
/// <para>
/// The number that matters is the attacker's animation progress at the moment of the blow. A swing whose damage
/// lands at 30% looks instant no matter how correctly the animation renders, because the visible strike has not
/// happened yet. That is a different failure from a wrong speed or a late start, and it is the one thing raised
/// in the first analysis of this problem and never measured since.
/// </para>
/// </remarks>
internal static class HitTimingDiagnostics
{
    /// <summary>Attacker's animation progress when the blow landed.</summary>
    private static readonly float[] ProgressEdges = { 0.10f, 0.25f, 0.40f, 0.60f, 0.80f };

    private const int ReadyMelee = 19;
    private const int ReleaseMelee = 20;

    private static readonly object Gate = new object();
    private static readonly int[] RemoteBuckets = new int[ProgressEdges.Length + 1];
    private static readonly int[] LocalBuckets = new int[ProgressEdges.Length + 1];

    private static bool enabled;
    private static long remoteHits;
    private static double remoteProgressTotal;
    private static long localHits;
    private static double localProgressTotal;
    private static long remoteNotSwinging;

    /// <summary>
    /// The control for <see cref="remoteNotSwinging"/>. Without it, "40% of remote blows land with no visible
    /// swing" cannot be told apart from whatever the engine normally does at the moment of impact - and the whole
    /// point of splitting local from remote was to have that comparison.
    /// </summary>
    private static long localNotSwinging;
    private static long blockedHits;

    public static bool Enabled => enabled;

    public static void Start()
    {
        lock (Gate)
        {
            Array.Clear(RemoteBuckets, 0, RemoteBuckets.Length);
            Array.Clear(LocalBuckets, 0, LocalBuckets.Length);
            remoteHits = 0;
            remoteProgressTotal = 0d;
            localHits = 0;
            localProgressTotal = 0d;
            remoteNotSwinging = 0;
            localNotSwinging = 0;
            blockedHits = 0;
            enabled = true;
        }
    }

    /// <summary>
    /// Records one landed blow. The caller supplies the attacker's state; nothing is read from the engine here.
    /// </summary>
    /// <param name="attackerIsRemote">
    /// Whether the attacker is a puppet. Locally simulated attackers are the control group: if their blows land
    /// at a sane point in the swing and puppets' land early, that is the defect, isolated.
    /// </param>
    public static void Record(
        bool attackerIsRemote,
        int attackerActionType,
        float attackerProgress,
        bool wasBlocked)
    {
        if (!enabled) return;

        float progress = attackerProgress;
        if (float.IsNaN(progress) || float.IsInfinity(progress) || progress < 0f) progress = 0f;
        else if (progress > 1f) progress = 1f;

        bool swinging = attackerActionType == ReadyMelee || attackerActionType == ReleaseMelee;

        lock (Gate)
        {
            if (wasBlocked) blockedHits++;

            if (!swinging)
            {
                // A blow landing while the attacker is not visibly swinging at all is itself worth counting -
                // for BOTH sides, so the remote figure has something to be compared against.
                if (attackerIsRemote) remoteNotSwinging++;
                else localNotSwinging++;
                return;
            }

            if (attackerIsRemote)
            {
                remoteHits++;
                remoteProgressTotal += progress;
                RemoteBuckets[BucketFor(progress)]++;
            }
            else
            {
                localHits++;
                localProgressTotal += progress;
                LocalBuckets[BucketFor(progress)]++;
            }
        }
    }

    private static int BucketFor(float progress)
    {
        for (int i = 0; i < ProgressEdges.Length; i++)
            if (progress < ProgressEdges[i]) return i;

        return ProgressEdges.Length;
    }

    public static string Snapshot(bool stop)
    {
        lock (Gate)
        {
            if (stop) enabled = false;
            if (remoteHits == 0 && localHits == 0 && remoteNotSwinging == 0 && localNotSwinging == 0)
                return "hitTiming: no blows observed";

            var text = new StringBuilder();
            text.Append("hitTiming: blocked=").Append(blockedHits.ToString(CultureInfo.InvariantCulture));
            text.Append(" NOT_SWINGING remote=")
                .Append(remoteNotSwinging.ToString(CultureInfo.InvariantCulture))
                .Append('(').Append(Share(remoteNotSwinging, remoteNotSwinging + remoteHits)).Append(')')
                .Append(" local=")
                .Append(localNotSwinging.ToString(CultureInfo.InvariantCulture))
                .Append('(').Append(Share(localNotSwinging, localNotSwinging + localHits)).Append(')');

            text.Append(" | REMOTE=").Append(remoteHits.ToString(CultureInfo.InvariantCulture));
            if (remoteHits > 0)
            {
                text.Append(" progressAtBlow~").Append(Pct((float)(remoteProgressTotal / remoteHits)));
                text.Append(' ').Append(Histogram(RemoteBuckets));
            }

            text.Append(" | LOCAL=").Append(localHits.ToString(CultureInfo.InvariantCulture));
            if (localHits > 0)
            {
                text.Append(" progressAtBlow~").Append(Pct((float)(localProgressTotal / localHits)));
                text.Append(' ').Append(Histogram(LocalBuckets));
            }

            return text.ToString();
        }
    }

    private static string Histogram(int[] buckets)
    {
        var text = new StringBuilder("at=[");
        for (int i = 0; i < buckets.Length; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append(i < ProgressEdges.Length ? "<" + Pct(ProgressEdges[i]) : "late");
            text.Append(':').Append(buckets[i].ToString(CultureInfo.InvariantCulture));
        }
        return text.Append(']').ToString();
    }

    private static string Share(long part, long whole) =>
        whole == 0
            ? "0%"
            : (100d * part / whole).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Pct(float value) =>
        (value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
}
