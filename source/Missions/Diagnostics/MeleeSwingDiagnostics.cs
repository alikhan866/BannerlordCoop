using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Missions.Diagnostics;

/// <summary>
/// Everything that can make a remote MELEE SWING render at the wrong speed, measured together.
/// </summary>
/// <remarks>
/// <para>
/// The reported symptom is that the attack animation is "too fast once it started" - which is playback RATE, not
/// start offset. An entire night went into measuring when animations begin; this measures how they play.
/// </para>
/// <para>
/// Melee swings only, by exact action type. <c>AttackMeleeAndRangedAllBegin..End</c> spans ReadyRanged(15) to
/// Fall(23) and therefore includes parries, blocks, reloads and drawn bows - all of which sit deep in their
/// animations normally. Classifying by that range manufactured a large fake population of "attacks starting at
/// 87%" which, measured properly, does not exist at all.
/// </para>
/// <para>
/// Four independent mechanisms are captured in one pass, so no more one-per-rebuild rounds:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Speed never published</b> - <c>resolvedActionSpeed = actionSpeed ?? 1f</c>. A swing the attacker played at
/// 0.8x renders at 1.0x here: 25% too fast, with the wind-up perfectly intact.
/// </description></item>
/// <item><description>
/// <b>Speed published but not applied</b> - what the engine reports back afterwards versus what was asked for.
/// </description></item>
/// <item><description>
/// <b>Playback rate</b> - the puppet's progress advance compared against the ATTACKER's over the same interval.
/// A ratio near 1 means they play in step; above 1 means the puppet is genuinely running fast. This needs no
/// clock agreement between machines because both deltas are read on this one.
/// </description></item>
/// <item><description>
/// <b>Restart flag</b> - how often <c>anf_restart</c> arrives, which re-triggers the animation from the top.
/// </description></item>
/// </list>
/// </remarks>
internal static class MeleeSwingDiagnostics
{
    /// <summary>Ratio of puppet progress advance to attacker progress advance. 1.0 is in step.</summary>
    private static readonly float[] RateEdges = { 0.5f, 0.8f, 1.2f, 2.0f, 4.0f };

    /// <summary>Action speed multiplier buckets.</summary>
    private static readonly float[] SpeedEdges = { 0.5f, 0.8f, 0.95f, 1.05f, 1.3f, 2.0f };

    private const float MinimumAdvance = 0.02f;
    private const int AgentMapLimit = 8192;

    private static readonly object Gate = new object();
    private static readonly int[] RateBuckets = new int[RateEdges.Length + 1];
    private static readonly int[] IncomingSpeedBuckets = new int[SpeedEdges.Length + 1];
    private static readonly int[] PuppetSpeedBuckets = new int[SpeedEdges.Length + 1];
    private static readonly Dictionary<int, Sample> Last = new Dictionary<int, Sample>();

    private static bool enabled;
    private static long swings;
    private static long speedAbsent;
    private static long speedPresent;
    private static long speedMismatch;
    private static long restartFlagged;
    private static long appliedObservations;
    private static long speedUnreadable;
    private static long appliedSpeedMismatch;
    private static long rateSamples;
    private static double rateTotal;
    private static double incomingSpeedTotal;
    private static double puppetSpeedTotal;

    public static bool Enabled => enabled;

    private struct Sample
    {
        public int ActionIndex;
        public float Incoming;
        public float Puppet;
    }

    public static void Start()
    {
        lock (Gate)
        {
            Array.Clear(RateBuckets, 0, RateBuckets.Length);
            Array.Clear(IncomingSpeedBuckets, 0, IncomingSpeedBuckets.Length);
            Array.Clear(PuppetSpeedBuckets, 0, PuppetSpeedBuckets.Length);
            Last.Clear();
            swings = 0;
            speedAbsent = 0;
            speedPresent = 0;
            speedMismatch = 0;
            restartFlagged = 0;
            appliedObservations = 0;
            speedUnreadable = 0;
            appliedSpeedMismatch = 0;
            rateSamples = 0;
            rateTotal = 0d;
            incomingSpeedTotal = 0d;
            puppetSpeedTotal = 0d;
            enabled = true;
        }
    }

    /// <summary>
    /// Records one observation of a melee swing. Called for every apply decision, not only transitions, because
    /// a swing that is CONTINUING is where the playback rate can be seen at all.
    /// </summary>
    public static void Record(
        int agentIndex,
        int channel,
        int actionIndex,
        float incomingProgress,
        float puppetProgress,
        bool speedWasPublished,
        float requestedSpeed,
        float puppetSpeed,
        bool puppetSpeedReadable,
        bool applied,
        bool restartFlag)
    {
        if (!enabled) return;

        float incoming = Sanitise(incomingProgress);
        float puppet = Sanitise(puppetProgress);

        lock (Gate)
        {
            swings++;
            if (restartFlag) restartFlagged++;
            if (applied) appliedObservations++;
            if (!puppetSpeedReadable)
            {
                // Never folded into a speed bucket: the old reader returned 1f on failure, so unreadable and
                // "playing at normal speed" were the same value and 44% of samples landed there.
                speedUnreadable++;
                return;
            }

            if (speedWasPublished)
            {
                speedPresent++;
                float requested = SanitiseSpeed(requestedSpeed);
                incomingSpeedTotal += requested;
                IncomingSpeedBuckets[BucketFor(SpeedEdges, requested)]++;

                float actual = SanitiseSpeed(puppetSpeed);
                puppetSpeedTotal += actual;
                PuppetSpeedBuckets[BucketFor(SpeedEdges, actual)]++;
                if (Math.Abs(actual - requested) > 0.05f)
                {
                    speedMismatch++;
                    // The only comparison that means anything: read AFTER this apply, against THIS request.
                    if (applied) appliedSpeedMismatch++;
                }
            }
            else
            {
                // The default path: the puppet will play this at 1.0x whatever the attacker was doing.
                speedAbsent++;
                float actual = SanitiseSpeed(puppetSpeed);
                puppetSpeedTotal += actual;
                PuppetSpeedBuckets[BucketFor(SpeedEdges, actual)]++;
            }

            int key = agentIndex * 2 + (channel & 1);
            if (Last.TryGetValue(key, out Sample previous)
                && previous.ActionIndex == actionIndex)
            {
                float incomingAdvance = incoming - previous.Incoming;
                float puppetAdvance = puppet - previous.Puppet;

                // Both must be moving forward; a wrap or a restart is not a rate sample.
                if (incomingAdvance >= MinimumAdvance && puppetAdvance >= 0f)
                {
                    float ratio = puppetAdvance / incomingAdvance;
                    if (!float.IsNaN(ratio) && !float.IsInfinity(ratio))
                    {
                        rateSamples++;
                        rateTotal += ratio;
                        RateBuckets[BucketFor(RateEdges, ratio)]++;
                    }
                }
            }

            if (Last.Count >= AgentMapLimit && !Last.ContainsKey(key)) Last.Clear();
            Last[key] = new Sample
            {
                ActionIndex = actionIndex,
                Incoming = incoming,
                Puppet = puppet,
            };
        }
    }

    private static float Sanitise(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
        return value < 0f ? 0f : value > 1f ? 1f : value;
    }

    private static float SanitiseSpeed(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) return 1f;
        return value > 8f ? 8f : value;
    }

    private static int BucketFor(float[] edges, float value)
    {
        for (int i = 0; i < edges.Length; i++)
            if (value < edges[i]) return i;

        return edges.Length;
    }

    public static string Snapshot(bool stop)
    {
        lock (Gate)
        {
            if (stop) enabled = false;
            if (swings == 0) return "meleeSwing: no melee swings observed";

            var text = new StringBuilder();
            text.Append("meleeSwing: observations=").Append(swings.ToString(CultureInfo.InvariantCulture));
            text.Append(" speedAbsent=").Append(speedAbsent.ToString(CultureInfo.InvariantCulture));
            text.Append('(').Append(Percent(speedAbsent, swings)).Append(')');
            text.Append(" speedPresent=").Append(speedPresent.ToString(CultureInfo.InvariantCulture));
            text.Append(" applied=").Append(appliedObservations.ToString(CultureInfo.InvariantCulture));
            text.Append(" unreadable=").Append(speedUnreadable.ToString(CultureInfo.InvariantCulture));
            text.Append(" mismatchAny=").Append(speedMismatch.ToString(CultureInfo.InvariantCulture));
            text.Append(" MISMATCH_ON_APPLY=").Append(appliedSpeedMismatch.ToString(CultureInfo.InvariantCulture));
            text.Append(" restartFlag=").Append(restartFlagged.ToString(CultureInfo.InvariantCulture));

            if (speedPresent > 0)
                text.Append(" reqSpeed~").Append(Num(incomingSpeedTotal / speedPresent));
            text.Append(" pupSpeed~").Append(Num(puppetSpeedTotal / swings));

            text.Append(" reqSpeeds=").Append(Histogram(SpeedEdges, IncomingSpeedBuckets));
            text.Append(" pupSpeeds=").Append(Histogram(SpeedEdges, PuppetSpeedBuckets));

            text.Append(" | rateSamples=").Append(rateSamples.ToString(CultureInfo.InvariantCulture));
            if (rateSamples > 0)
            {
                text.Append(" rate~").Append(Num(rateTotal / rateSamples));
                text.Append(" rates=").Append(Histogram(RateEdges, RateBuckets));
            }

            return text.ToString();
        }
    }

    private static string Histogram(float[] edges, int[] buckets)
    {
        var text = new StringBuilder("[");
        for (int i = 0; i < buckets.Length; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append(i < edges.Length
                ? "<" + Num(edges[i])
                : ">=" + Num(edges[edges.Length - 1]));
            text.Append(':').Append(buckets[i].ToString(CultureInfo.InvariantCulture));
        }
        return text.Append(']').ToString();
    }

    private static string Num(double value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Percent(long part, long whole) =>
        whole == 0
            ? "0%"
            : (100d * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
