using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Missions.Diagnostics;

/// <summary>
/// Counts EVERY write of an action onto a puppet, tagged by which code path made it.
/// </summary>
/// <remarks>
/// <para>
/// Every earlier counter watched one function - <c>AgentActionData.ApplyActionChannel</c> - and everything it
/// measured came back correct: playback speed 1.0x, progress advancing in step, actions applied the instant they
/// arrive (99.5%), start offsets around 8%, transition churn 1.5/sec at the busiest agent. Meanwhile the swings
/// are plainly wrong on screen.
/// </para>
/// <para>
/// At least four other places drive a puppet's animation, and none were ever counted. One of them,
/// <c>ReplayRemoteGuardReactions</c>, runs from the mission tick - up to 60 times a second. An animation
/// re-imposed by an uncounted path would leave every value on the counted path looking perfect, which is exactly
/// the contradiction to resolve.
/// </para>
/// <para>
/// Deliberately cheap so it cannot destabilise a battle: no native calls, no collections that grow, no
/// allocation per record, and every caller passes values it already holds. A diagnostic crashed a live client
/// earlier in this investigation by reaching for <c>agent.ActionSet</c>; nothing here touches the engine.
/// </para>
/// </remarks>
internal static class ActionWriteLog
{
    /// <summary>Which code path wrote the action.</summary>
    internal enum Source
    {
        /// <summary>The main packet apply path.</summary>
        ActionApply = 0,

        /// <summary>The main path clearing a mounted guard to act_none.</summary>
        MountedGuardClear = 1,

        /// <summary>A locally synthesised guard reaction.</summary>
        SyntheticGuardReaction = 2,

        /// <summary>A remote guard reaction replayed from a message - carries its own progress.</summary>
        RemoteGuardReaction = 3,

        /// <summary>Retained guard released to act_none.</summary>
        RetainedGuardRelease = 4,

        /// <summary>Mount channels.</summary>
        MountAction = 5,
    }

    private static readonly string[] SourceNames =
    {
        "actionApply", "mountedGuardClear", "syntheticReaction",
        "remoteReaction", "retainedRelease", "mount",
    };

    private static readonly float[] ProgressEdges = { 0.01f, 0.05f, 0.20f, 0.50f };

    private static readonly object Gate = new object();
    private static readonly long[] Counts = new long[SourceNames.Length];
    private static readonly double[] ProgressTotals = new double[SourceNames.Length];
    private static readonly long[] RestartFlags = new long[SourceNames.Length];
    private static readonly int[][] ProgressBuckets = BuildBuckets();
    private static readonly Stopwatch Clock = new Stopwatch();

    /// <summary>
    /// The retained guard re-commanding itself. This path does NOT go through SetActionChannel - it writes with
    /// ApplyGuardState / ApplyGuardDirectionTransition - so every write counter built before this one missed it
    /// entirely, which is why the client's animation could be overwritten while all of them read clean.
    /// </summary>
    /// <remarks>
    /// The reason breakdown matters most for <c>nativeMissing</c>:
    /// <c>agent.CurrentGuardMode != guardMode &amp;&amp; !HasDefendingAction(agent)</c> becomes true at exactly
    /// the moment a swing begins, because a swinging agent is no longer showing a defending action. If that is
    /// what fires, the guard is restored precisely when it must not be, and the wind-up is overwritten every
    /// time - which fits it being missed in 100% of samples across four recordings.
    /// </remarks>
    private static long guardCommands;
    private static long guardReacquiring;
    private static long guardMountChanged;
    private static long guardModeChanged;
    private static long guardNativeMissing;

    /// <summary>
    /// Whether the retained guard is actually released when a swing packet arrives.
    /// </summary>
    /// <remarks>
    /// A fix was added to release it, and NATIVE_MISSING barely moved afterwards - 112,174 to 106,923. Either the
    /// release almost never fires, or something re-retains immediately. This says which, instead of inferring it
    /// from a downstream number the way the last three attempts did.
    /// </remarks>
    private static long retentionCalls;
    private static long retentionOwnerMidSwing;
    private static long retentionRetained;
    private static long retentionReleased;
    private static long retentionReleasedBySwing;

    /// <summary>
    /// Swing arrivals counted from the packet's own melee-swing flag, at the moment of the apply decision.
    /// </summary>
    /// <remarks>
    /// The previous swing-refusal counter classified an action by looking its index up in a map learned from
    /// SUCCESSFUL applies. A wind-up that is never applied never enters that map, so refusals of it were filed
    /// under "unknown index" and the counter reported zero refused wind-ups - the exact thing it existed to
    /// detect. The packet already carries the flag, so nothing needs to be learned.
    /// </remarks>
    private static long windupArrived;
    private static long windupApplied;
    private static long windupSuppressed;
    private static long windupNoTransition;
    private static long windupResolveFailed;
    private static long releaseArrived;
    private static long releaseApplied;

    private static bool enabled;

    public static bool Enabled => enabled;

    private static int[][] BuildBuckets()
    {
        var buckets = new int[SourceNames.Length][];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new int[ProgressEdges.Length + 1];
        return buckets;
    }

    public static void Start()
    {
        lock (Gate)
        {
            Array.Clear(Counts, 0, Counts.Length);
            Array.Clear(ProgressTotals, 0, ProgressTotals.Length);
            Array.Clear(RestartFlags, 0, RestartFlags.Length);
            for (int i = 0; i < ProgressBuckets.Length; i++)
                Array.Clear(ProgressBuckets[i], 0, ProgressBuckets[i].Length);
            windupArrived = 0;
            windupApplied = 0;
            windupSuppressed = 0;
            windupNoTransition = 0;
            windupResolveFailed = 0;
            releaseArrived = 0;
            releaseApplied = 0;
            retentionCalls = 0;
            retentionOwnerMidSwing = 0;
            retentionRetained = 0;
            retentionReleased = 0;
            retentionReleasedBySwing = 0;
            guardCommands = 0;
            guardReacquiring = 0;
            guardMountChanged = 0;
            guardModeChanged = 0;
            guardNativeMissing = 0;
            Clock.Reset();
            Clock.Start();
            enabled = true;
        }
    }

    /// <summary>Records one action write. Callers pass values they already hold; nothing is read from the engine.</summary>
    public static void Record(Source source, float startProgress, bool restart)
    {
        if (!enabled) return;

        int index = (int)source;
        if (index < 0 || index >= SourceNames.Length) return;

        float progress = startProgress;
        if (float.IsNaN(progress) || float.IsInfinity(progress) || progress < 0f) progress = 0f;
        else if (progress > 1f) progress = 1f;

        lock (Gate)
        {
            Counts[index]++;
            ProgressTotals[index] += progress;
            if (restart) RestartFlags[index]++;
            ProgressBuckets[index][BucketFor(progress)]++;
        }
    }

    /// <summary>One retained-guard re-command, and which condition asked for it.</summary>
    public static void GuardCommand(
        bool reacquiring,
        bool mountChanged,
        bool guardModeChangedNow,
        bool nativeMissing)
    {
        if (!enabled) return;

        lock (Gate)
        {
            guardCommands++;
            if (reacquiring) guardReacquiring++;
            if (mountChanged) guardMountChanged++;
            if (guardModeChangedNow) guardModeChanged++;
            if (nativeMissing) guardNativeMissing++;
        }
    }

    /// <summary>One retained-guard retain/release decision.</summary>
    public static void GuardRetention(bool ownerIsMidSwing, bool guardConditionsHold, bool retained)
    {
        if (!enabled) return;

        lock (Gate)
        {
            retentionCalls++;
            if (ownerIsMidSwing) retentionOwnerMidSwing++;
            if (retained) retentionRetained++;
            else
            {
                retentionReleased++;
                // Released ONLY because the owner is mid-swing: the guard conditions would have kept it.
                if (ownerIsMidSwing && guardConditionsHold) retentionReleasedBySwing++;
            }
        }
    }

    /// <summary>outcome: 0 applied, 1 suppressed, 2 no-transition, 3 resolve-failed.</summary>
    public static void SwingArrival(bool isWindup, int outcome)
    {
        if (!enabled) return;

        lock (Gate)
        {
            if (isWindup)
            {
                windupArrived++;
                if (outcome == 0) windupApplied++;
                else if (outcome == 1) windupSuppressed++;
                else if (outcome == 2) windupNoTransition++;
                else windupResolveFailed++;
            }
            else
            {
                releaseArrived++;
                if (outcome == 0) releaseApplied++;
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

            long total = 0;
            foreach (long count in Counts) total += count;
            if (total == 0) return "actionWrites: nothing recorded";

            double seconds = Clock.Elapsed.TotalSeconds;
            if (seconds < 0.001d) seconds = 0.001d;

            var text = new StringBuilder();
            text.Append("actionWrites: total=").Append(total.ToString(CultureInfo.InvariantCulture));
            text.Append(" over=").Append(seconds.ToString("0", CultureInfo.InvariantCulture)).Append('s');
            text.Append(" allPerSec=").Append((total / seconds).ToString("0", CultureInfo.InvariantCulture));

            for (int i = 0; i < SourceNames.Length; i++)
            {
                if (Counts[i] == 0) continue;

                text.Append(" | ").Append(SourceNames[i]).Append('=');
                text.Append(Counts[i].ToString(CultureInfo.InvariantCulture));
                text.Append('(').Append(Percent(Counts[i], total)).Append(')');
                text.Append(" perSec=").Append((Counts[i] / seconds).ToString("0", CultureInfo.InvariantCulture));
                text.Append(" progress~").Append(Pct((float)(ProgressTotals[i] / Counts[i])));
                text.Append(" restart=").Append(RestartFlags[i].ToString(CultureInfo.InvariantCulture));

                text.Append(" at=[");
                for (int b = 0; b < ProgressBuckets[i].Length; b++)
                {
                    if (b > 0) text.Append(' ');
                    text.Append(b < ProgressEdges.Length ? "<" + Pct(ProgressEdges[b]) : "deep");
                    text.Append(':').Append(ProgressBuckets[i][b].ToString(CultureInfo.InvariantCulture));
                }
                text.Append(']');
            }

            if (windupArrived > 0 || releaseArrived > 0)
            {
                text.Append(" || WINDUP arrived=").Append(windupArrived.ToString(CultureInfo.InvariantCulture));
                text.Append(" APPLIED=").Append(windupApplied.ToString(CultureInfo.InvariantCulture));
                text.Append(" suppressed=").Append(windupSuppressed.ToString(CultureInfo.InvariantCulture));
                text.Append(" noTransition=").Append(windupNoTransition.ToString(CultureInfo.InvariantCulture));
                text.Append(" resolveFailed=").Append(windupResolveFailed.ToString(CultureInfo.InvariantCulture));
                text.Append(" | RELEASE arrived=").Append(releaseArrived.ToString(CultureInfo.InvariantCulture));
                text.Append(" applied=").Append(releaseApplied.ToString(CultureInfo.InvariantCulture));
            }

            if (retentionCalls > 0)
            {
                text.Append(" || GUARD_RETENTION calls=").Append(retentionCalls.ToString(CultureInfo.InvariantCulture));
                text.Append(" ownerMidSwing=").Append(retentionOwnerMidSwing.ToString(CultureInfo.InvariantCulture));
                text.Append(" retained=").Append(retentionRetained.ToString(CultureInfo.InvariantCulture));
                text.Append(" released=").Append(retentionReleased.ToString(CultureInfo.InvariantCulture));
                text.Append(" RELEASED_BY_SWING=").Append(retentionReleasedBySwing.ToString(CultureInfo.InvariantCulture));
            }

            if (guardCommands > 0)
            {
                text.Append(" || GUARD_RECOMMANDS=").Append(guardCommands.ToString(CultureInfo.InvariantCulture));
                text.Append(" perSec=").Append((guardCommands / seconds).ToString("0", CultureInfo.InvariantCulture));
                text.Append(" reasons=[reacquiring:").Append(guardReacquiring.ToString(CultureInfo.InvariantCulture));
                text.Append(" mountChanged:").Append(guardMountChanged.ToString(CultureInfo.InvariantCulture));
                text.Append(" guardModeChanged:").Append(guardModeChanged.ToString(CultureInfo.InvariantCulture));
                text.Append(" NATIVE_MISSING:").Append(guardNativeMissing.ToString(CultureInfo.InvariantCulture));
                text.Append(']');
            }

            return text.ToString();
        }
    }

    private static string Pct(float value) =>
        (value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Percent(long part, long whole) =>
        whole == 0
            ? "0%"
            : (100d * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
