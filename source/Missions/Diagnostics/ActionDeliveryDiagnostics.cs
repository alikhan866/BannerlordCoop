using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Missions.Diagnostics;

/// <summary>
/// Counts what happens to every remote action packet on arrival, by outcome.
/// </summary>
/// <remarks>
/// <para>
/// Remote attacks were measured starting up to 88% into the swing at ZERO latency. That should be impossible:
/// action packets are <c>ReliableOrdered</c>, so nothing is lost or reordered, and the sender emits a packet on
/// the very tick an action index changes. The packet carrying the START of the swing must therefore arrive, and
/// arrive first. If the animation still begins at 88%, something on THIS side threw the start away.
/// </para>
/// <para>
/// <c>RemoteAgentActionProcessor</c> already names every way that can happen - <c>Applied</c>,
/// <c>AgentNotReady</c>, <c>Stale</c>, <c>WrongAuthority</c> - and used them for control flow without ever
/// counting them. This counts them. Each points at a different bug with a different fix:
/// </para>
/// <list type="bullet">
/// <item><description><b>Stale</b> - the sequence guard is discarding the start packet.</description></item>
/// <item><description>
/// <b>WrongAuthority</b> - agents changing hands mid-swing. Strongest untested candidate: the defect was observed
/// to follow agent ownership when the two players swapped roles between battles.
/// </description></item>
/// <item><description>
/// <b>AgentNotReady</b> - the puppet did not exist yet when the start arrived.
/// </description></item>
/// <item><description>
/// <b>None of them</b> - the start never arrives, so the fault is a sender-side filter.
/// </description></item>
/// </list>
/// <para>
/// Progress is bucketed per outcome because the decisive shape is asymmetric: if DROPPED packets carry low
/// progress while APPLIED ones carry high progress, the starts are being discarded and the fix is whichever guard
/// is discarding them. This is measurement only - it changes no behaviour and makes no native calls.
/// </para>
/// </remarks>
internal static class ActionDeliveryDiagnostics
{
    /// <summary>
    /// Mirrors <c>RemoteAgentActionProcessor.RemoteActionApplyResult</c> by ordinal. That enum is private, so the
    /// caller passes its int value; keep these names and this order aligned with it.
    /// </summary>
    private static readonly string[] OutcomeNames = { "applied", "agentNotReady", "stale", "wrongAuthority" };

    private static readonly float[] BucketEdges = { 0.01f, 0.025f, 0.05f, 0.10f, 0.20f, 0.35f, 0.50f };

    /// <summary>Channels an action packet drives. Bannerlord runs melee attacks on the upper-body channel.</summary>
    private const int Channels = 2;

    /// <summary>Slots are (outcome, channel) pairs, indexed by <c>outcome * Channels + channel</c>.</summary>
    private static int SlotCount => OutcomeNames.Length * Channels;

    private static readonly object Gate = new object();
    private static readonly long[] Counts = new long[OutcomeNames.Length * Channels];
    private static readonly double[] ProgressTotals = new double[OutcomeNames.Length * Channels];
    private static readonly int[][] Buckets = BuildBuckets();

    /// <summary>Distinct controller ids seen per slot - a handover shows up as more than one.</summary>
    private static readonly HashSet<string>[] Controllers = BuildControllers();

    private static bool enabled;
    private static long unknownOutcome;

    public static bool Enabled => enabled;

    private static int[][] BuildBuckets()
    {
        var buckets = new int[OutcomeNames.Length * Channels][];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new int[BucketEdges.Length + 1];
        return buckets;
    }

    private static HashSet<string>[] BuildControllers()
    {
        var controllers = new HashSet<string>[OutcomeNames.Length * Channels];
        for (int i = 0; i < controllers.Length; i++) controllers[i] = new HashSet<string>(StringComparer.Ordinal);
        return controllers;
    }

    public static void Start()
    {
        lock (Gate)
        {
            Array.Clear(Counts, 0, Counts.Length);
            Array.Clear(ProgressTotals, 0, ProgressTotals.Length);
            for (int i = 0; i < Buckets.Length; i++) Array.Clear(Buckets[i], 0, Buckets[i].Length);
            foreach (HashSet<string> set in Controllers) set.Clear();
            unknownOutcome = 0;
            enabled = true;
        }
    }

    /// <summary>
    /// Records one arrival, for one channel. Call once per channel: an action packet drives both, and only the
    /// upper-body one carries melee attacks.
    /// </summary>
    public static void Record(int outcome, int channel, float progress, string controllerId)
    {
        if (!enabled) return;

        if (outcome < 0 || outcome >= OutcomeNames.Length || channel < 0 || channel >= Channels)
        {
            lock (Gate) unknownOutcome++;
            return;
        }

        int slot = outcome * Channels + channel;

        // Progress arrives off the wire; treat it as untrusted rather than assuming a sane 0..1.
        if (float.IsNaN(progress) || float.IsInfinity(progress)) progress = 0f;
        else if (progress < 0f) progress = 0f;
        else if (progress > 1f) progress = 1f;

        lock (Gate)
        {
            Counts[slot]++;
            ProgressTotals[slot] += progress;
            Buckets[slot][BucketFor(progress)]++;
            if (!string.IsNullOrEmpty(controllerId)) Controllers[slot].Add(controllerId);
        }
    }

    private static int BucketFor(float progress)
    {
        for (int i = 0; i < BucketEdges.Length; i++)
            if (progress < BucketEdges[i]) return i;

        return BucketEdges.Length;
    }

    public static string Snapshot(bool stop)
    {
        lock (Gate)
        {
            if (stop) enabled = false;

            long total = 0;
            foreach (long count in Counts) total += count;
            if (total == 0) return "delivery: no remote action packets observed";

            var text = new StringBuilder();
            text.Append("delivery: packets=").Append(total.ToString(CultureInfo.InvariantCulture));
            if (unknownOutcome > 0)
                text.Append(" unknownOutcome=").Append(unknownOutcome.ToString(CultureInfo.InvariantCulture));

            for (int i = 0; i < SlotCount; i++)
            {
                // Silent slots are omitted; with drops at zero this keeps the line readable.
                if (Counts[i] == 0 && i % Channels != 0) continue;

                text.Append(" | ").Append(OutcomeNames[i / Channels]).Append(".ch").Append(i % Channels);
                text.Append('=').Append(Counts[i].ToString(CultureInfo.InvariantCulture));
                if (Counts[i] == 0) continue;

                text.Append('(').Append(Percent(Counts[i], total)).Append(')');
                text.Append(" meanProgress=").Append(Fraction((float)(ProgressTotals[i] / Counts[i])));
                text.Append(" controllers=").Append(Controllers[i].Count.ToString(CultureInfo.InvariantCulture));

                text.Append(" buckets=[");
                for (int b = 0; b < Buckets[i].Length; b++)
                {
                    if (b > 0) text.Append(' ');
                    text.Append(b < BucketEdges.Length
                        ? "<" + Fraction(BucketEdges[b])
                        : ">=" + Fraction(BucketEdges[BucketEdges.Length - 1]));
                    text.Append(':').Append(Buckets[i][b].ToString(CultureInfo.InvariantCulture));
                }
                text.Append(']');
            }

            return text.ToString();
        }
    }

    private static string Fraction(float value) =>
        (value * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Percent(long part, long whole) =>
        whole == 0
            ? "0%"
            : (100d * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
