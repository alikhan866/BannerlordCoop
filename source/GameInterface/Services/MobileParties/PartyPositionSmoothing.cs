using System;
using System.Collections.Generic;
using Common.Util;
using GameInterface.Services.MobileParties.Extensions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace GameInterface.Services.MobileParties;

/// <summary>
/// Delivers an authoritative party-position correction over a few frames instead of in one assignment.
/// </summary>
/// <remarks>
/// Every machine simulates the AI parties itself, so the copies drift apart. Measured on 5 Sep 2026 over five
/// campaign-map minutes with the clock running, across about 290 lord parties: the median copy was 0.6 map units
/// from the server's, the 90th percentile 4.9, the worst 43, and one party in five was more than 8 units out at
/// some point. The server re-states a position once its own copy has moved 8 units since it last reported
/// (<see cref="PartyPositionCorrection"/>), and the client applied that in a single assignment - the party was
/// standing in one place and in the next frame it was somewhere else. That frame is what a player reads as
/// "lords teleport to besiege a town" or "an army teleported onto me".
///
/// The correction is right and is still obeyed in full; only its delivery changes. The gap is held as a residual
/// and a fraction of it is added to the party's position each frame, so the copy closes the distance in about a
/// third of a second while its own AI keeps walking it. Nothing new goes on the wire and no new state is
/// replicated - this is presentation, on the machine that was going to jump.
///
/// Two cases stay instant. Beyond <see cref="HardSnapDistance"/> the two machines are not describing the same
/// journey at all (a settlement exit, an army attaching, a spawn), and sliding a party across that distance would
/// be a worse lie than the jump. Below <see cref="NegligibleDistance"/> there is nothing to see.
/// </remarks>
internal static class PartyPositionSmoothing
{
    /// <summary>A correction this large is a relocation, not drift; the party must be there at once.</summary>
    internal const float HardSnapDistance = 40f;

    /// <summary>Below this nobody could see the difference between sliding and assigning.</summary>
    internal const float NegligibleDistance = 0.25f;

    /// <summary>
    /// How fast the gap closes: after this many seconds about 63 percent of it is gone, after three times it
    /// 95 percent. A quarter of a second reads as a party hurrying, a second reads as a party sliding.
    /// </summary>
    internal const float SmoothingTimeConstantSeconds = 0.08f;

    /// <summary>A frame longer than this (a load, a screen) applies its share and no more.</summary>
    internal const float MaxFrameSeconds = 0.25f;

    /// <summary>The last sliver is applied in one go rather than halved forever.</summary>
    internal const float FinishDistance = 0.05f;

    /// <summary>Corrections outstanding at once are bounded by the parties on the map; this is a safety net.</summary>
    private const int MaxTrackedParties = 2048;

    internal enum Delivery
    {
        /// <summary>Assign the authoritative position now.</summary>
        Assign,

        /// <summary>Hold the difference and bleed it in over the next frames.</summary>
        Smooth,
    }

    private static readonly Dictionary<MobileParty, (float X, float Y)> residuals =
        new Dictionary<MobileParty, (float X, float Y)>();

    // One lock for the residuals and the counters alike. In the game everything here runs on the game thread, but
    // the type is static and the test environments drive several campaigns at once: an unguarded Dictionary that
    // is read and written from two threads does not throw, it spins.
    private static readonly object gate = new object();

    // The keys are copied out before stepping them: writing a value back into a dictionary while enumerating it
    // throws on this runtime, and the drain then advanced one correction per frame instead of all of them
    // (6 Sep 2026 - the guard reported it once and swallowed it silently after that).
    private static readonly List<MobileParty> drainBuffer = new List<MobileParty>();

    /// <summary>Off restores the previous behaviour (assign every correction), so a run can measure both.</summary>
    public static bool Enabled { get; set; } = true;

    // Counters for coop.debug.mobile_party.position_smoothing: the sizes of the corrections that arrive and the
    // largest position change actually applied in one frame, which is the number a player sees as a jump.
    private static long correctionsSeen;
    private static long correctionsAssignedBig;
    private static long correctionsAssignedSmall;
    private static long correctionsSmoothed;
    private static double correctionSizeSum;
    private static float correctionSizeMax;
    private static float singleFrameMax;
    private static double singleFrameSum;
    private static long singleFrameCount;

    // The same, counting only corrections small enough to be drift. A relocation is applied in one frame either
    // way, so including those hides the whole effect of the change behind one settlement exit.
    private static float driftFrameMax;
    private static double driftFrameSum;
    private static long driftFrameCount;

    // How often the drain actually runs. Two hooks were wrong about this before it was measured: the hourly
    // campaign tick (about 1 Hz) and Campaign.RealTick (about 2 Hz), both of which stretched a correction over
    // tens of seconds instead of a third of one.
    private static long drainCalls;
    private static readonly System.Diagnostics.Stopwatch drainClock = new System.Diagnostics.Stopwatch();
    private static long lastDrainTicks;

    /// <summary>How a correction of this size is delivered. Pure, so the rule can be asserted without a campaign.</summary>
    internal static Delivery Decide(float distance, bool enabled = true)
    {
        if (!enabled) return Delivery.Assign;
        if (distance <= NegligibleDistance) return Delivery.Assign;
        if (distance >= HardSnapDistance) return Delivery.Assign;
        return Delivery.Smooth;
    }

    /// <summary>
    /// One frame of the drain: the step to apply now and what is left, for a frame of <paramref name="dt"/>
    /// seconds. Pure, and framed in seconds rather than frames so the party closes the same gap in the same time
    /// whatever the frame rate.
    /// </summary>
    internal static ((float X, float Y) Step, (float X, float Y) Remaining, bool Finished) NextStep(float x, float y, float dt)
    {
        float length = (float)Math.Sqrt(x * x + y * y);
        if (length <= FinishDistance)
            return ((x, y), (0f, 0f), true);
        float clamped = dt <= 0f ? 0f : Math.Min(dt, MaxFrameSeconds);
        float fraction = 1f - (float)Math.Exp(-clamped / SmoothingTimeConstantSeconds);
        var step = (x * fraction, y * fraction);
        return (step, (x - step.Item1, y - step.Item2), false);
    }

    /// <summary>
    /// Applies the server's position for a party the client does not control, smoothly where that is safe.
    /// </summary>
    public static void Apply(MobileParty party, CampaignVec2 authoritative)
    {
        if (party == null) return;
        var current = party.Position;
        float dx = authoritative.X - current.X;
        float dy = authoritative.Y - current.Y;
        float distance = (float)Math.Sqrt(dx * dx + dy * dy);

        lock (gate)
        {
            correctionsSeen++;
            correctionSizeSum += distance;
            if (distance > correctionSizeMax) correctionSizeMax = distance;
        }

        // A party the player is steering, or one parked in a settlement, is placed exactly: sliding either of
        // them would fight the input or the settlement it is standing in.
        int outstanding;
        lock (gate) outstanding = residuals.Count;
        bool exact = !Enabled ||
            party.CurrentSettlement != null ||
            party.IsControlledByThisInstance() ||
            outstanding >= MaxTrackedParties;

        var delivery = exact ? Delivery.Assign : Decide(distance);
        if (delivery == Delivery.Assign)
        {
            lock (gate)
            {
                if (distance >= HardSnapDistance) correctionsAssignedBig++;
                else correctionsAssignedSmall++;
                RecordFrameMove(distance, isDrift: distance < HardSnapDistance);
            }
            lock (gate) residuals.Remove(party);
            party.Position = authoritative;
            return;
        }

        lock (gate)
        {
            correctionsSmoothed++;
            residuals[party] = (dx, dy);
        }
    }

    /// <summary>
    /// Where the party will be once its outstanding correction has finished arriving, which is the position to
    /// judge the next correction against. Judging against the DRAWN position instead counts the part of the gap
    /// that is already on its way, so more updates cross the correction threshold and each one restarts the
    /// residual: measured 6 Sep 2026, 7,633 corrections against 4,298 with the corrections assigned outright.
    /// </summary>
    public static CampaignVec2 SettledPosition(MobileParty party)
    {
        if (party == null) return default;
        var position = party.Position;
        (float X, float Y) residual;
        lock (gate)
        {
            if (!residuals.TryGetValue(party, out residual)) return position;
        }
        return position + new Vec2(residual.X, residual.Y);
    }

    /// <summary>Drops a party's outstanding correction, for a party that is leaving the map.</summary>
    public static void Forget(MobileParty party)
    {
        if (party == null) return;
        lock (gate) residuals.Remove(party);
    }

    public static void Clear()
    {
        lock (gate)
        {
            residuals.Clear();
            correctionsSeen = 0;
            correctionsAssignedBig = 0;
            correctionsAssignedSmall = 0;
            correctionsSmoothed = 0;
            correctionSizeSum = 0;
            correctionSizeMax = 0;
            singleFrameMax = 0;
            singleFrameSum = 0;
            singleFrameCount = 0;
            driftFrameMax = 0;
            driftFrameSum = 0;
            driftFrameCount = 0;
            drainCalls = 0;
            drainClock.Restart();
            lastDrainTicks = 0;
        }
    }

    /// <summary>
    /// Adds one frame of every outstanding correction. Called from the client's per-frame campaign tick: the
    /// hourly campaign tick was tried first and ran about once a second, which stretched every correction over
    /// twenty seconds and left the copy lagging rather than smoothed (measured 6 Sep 2026).
    /// </summary>
    public static void Drain()
    {
        // The elapsed time is measured here rather than taken from the hook: the two hooks tried before this one
        // passed a delta that did not match the wall clock, and a drain that trusts it silently stops smoothing.
        if (!drainClock.IsRunning)
        {
            drainClock.Start();
            lastDrainTicks = drainClock.ElapsedTicks;
        }
        long nowTicks = drainClock.ElapsedTicks;
        float dt = (float)((nowTicks - lastDrainTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
        lastDrainTicks = nowTicks;
        lock (gate)
        {
            drainCalls++;
            if (residuals.Count == 0) return;
            if (Campaign.Current == null)
            {
                residuals.Clear();
                return;
            }

            drainBuffer.Clear();
            drainBuffer.AddRange(residuals.Keys);
        }

        // The engine writes party positions on the game thread and refuses client writes outside an allowed
        // scope, which is how the one-shot assignment this replaces was applied too.
        using (new AllowedThread())
        {
            for (int i = 0; i < drainBuffer.Count; i++)
            {
                var party = drainBuffer[i];
                (float X, float Y) residual;
                lock (gate)
                {
                    if (!residuals.TryGetValue(party, out residual)) continue;
                }
                if (party == null || !party.IsActive || party.CurrentSettlement != null)
                {
                    lock (gate) residuals.Remove(party);
                    continue;
                }

                var (step, remaining, done) = NextStep(residual.X, residual.Y, dt);
                try
                {
                    party.Position += new Vec2(step.X, step.Y);
                }
                catch (Exception)
                {
                    lock (gate) residuals.Remove(party);
                    continue;
                }

                lock (gate)
                {
                    RecordFrameMove((float)Math.Sqrt(step.X * step.X + step.Y * step.Y), isDrift: true);
                    if (done) residuals.Remove(party);
                    else residuals[party] = remaining;
                }
            }
        }

        lock (gate) drainBuffer.Clear();
    }

    private static void RecordFrameMove(float distance, bool isDrift)
    {
        if (distance <= 0f) return;
        singleFrameCount++;
        singleFrameSum += distance;
        if (distance > singleFrameMax) singleFrameMax = distance;
        if (!isDrift) return;
        driftFrameCount++;
        driftFrameSum += distance;
        if (distance > driftFrameMax) driftFrameMax = distance;
    }

    public static string Snapshot()
    {
        lock (gate)
        {
            return "POSITION_SMOOTHING enabled=" + (Enabled ? "1" : "0") +
                   " outstanding=" + residuals.Count +
                   " corrections=" + correctionsSeen +
                   " smoothed=" + correctionsSmoothed +
                   " assignedBig=" + correctionsAssignedBig +
                   " assignedSmall=" + correctionsAssignedSmall +
                   " correctionMean=" + (correctionsSeen > 0 ? (correctionSizeSum / correctionsSeen).ToString("0.00") : "-") +
                   " correctionMax=" + correctionSizeMax.ToString("0.0") +
                   " DRIFT_MOVE_PER_FRAME_MAX=" + driftFrameMax.ToString("0.00") +
                   " driftMovePerFrameMean=" + (driftFrameCount > 0 ? (driftFrameSum / driftFrameCount).ToString("0.000") : "-") +
                   " anyMovePerFrameMax=" + singleFrameMax.ToString("0.00") +
                   " movePerFrameMean=" + (singleFrameCount > 0 ? (singleFrameSum / singleFrameCount).ToString("0.000") : "-") +
                   " frameMoves=" + singleFrameCount +
                   " drainHz=" + (drainClock.Elapsed.TotalSeconds > 1 ? (drainCalls / drainClock.Elapsed.TotalSeconds).ToString("0.0") : "-");
        }
    }
}
