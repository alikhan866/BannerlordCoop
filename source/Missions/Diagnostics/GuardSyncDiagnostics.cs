using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Missions.Diagnostics;

/// <summary>
/// Whether the guard a player is actually holding is the guard other clients see them holding.
/// </summary>
/// <remarks>
/// <para>
/// A hit is resolved on the ATTACKER's client, against ITS copy of the defender's guard direction. A block can be
/// perfectly timed and correctly aimed on your own screen and still fail if that copy points elsewhere. So guard
/// AGREEMENT is the thing worth measuring, not your own guard.
/// </para>
/// <para>
/// Two independent views of "did the guard land" are kept, because either alone can mislead:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Defend flags</b> - the movement-flag bits, compared request against result. Always meaningful.
/// </description></item>
/// <item><description>
/// <b>Action direction</b> - derived from the animation playing on the guard channel. Only meaningful while the
/// puppet is actually playing a DEFEND action: mid-swing that direction belongs to the attack, and comparing it
/// to a requested guard compares unlike things. Every earlier reading in this investigation that went wrong went
/// wrong exactly that way, so the breakdown by action category is not optional.
/// </description></item>
/// </list>
/// <para>
/// A first version compared <c>GetDefendMovementFlags</c> on both sides and reported a 100% match across 68,906
/// observations - every value was 0, so it compared 0 to 0 and meant nothing.
/// </para>
/// </remarks>
internal static class GuardSyncDiagnostics
{
    private static readonly int[] LatencyEdges = { 1, 2, 4, 8, 16 };

    private const int AgentMapLimit = 8192;
    private const int DirectionCount = 8;

    // Exact Agent.ActionCodeType values, read off the shipped assembly.
    private const int DefendFirst = 1;
    private const int DefendLast = 14;
    private const int ReadyRanged = 15;
    private const int Reload = 18;
    private const int ReadyMelee = 19;
    private const int ReleaseMelee = 20;
    private const int ParriedMelee = 21;
    private const int BlockedMelee = 22;
    private const int Idle = 35;
    private const int Guard = 36;

    /// <summary>What the puppet was playing when the comparison was taken.</summary>
    private enum Category
    {
        Defending = 0,
        Reaction = 1,
        MeleeSwing = 2,
        Ranged = 3,
        IdleOrNone = 4,
        Other = 5,
    }

    private static readonly string[] CategoryNames =
        { "defending", "reaction", "swing", "ranged", "idle", "other" };

    private static readonly object Gate = new object();
    private static readonly int[] LatencyBuckets = new int[LatencyEdges.Length + 1];
    private static readonly Dictionary<int, Pending> Pendings = new Dictionary<int, Pending>();

    private static readonly long[] RequestedByDirection = new long[DirectionCount];
    private static readonly long[] MismatchByDirection = new long[DirectionCount];
    private static readonly long[] ObservedByCategory = new long[CategoryNames.Length];
    private static readonly long[] MismatchByCategory = new long[CategoryNames.Length];

    private static bool enabled;
    private static long observations;
    private static long guardMatched;
    private static long guardMismatched;
    private static long flagsMatched;
    private static long flagsMismatched;
    private static long neverResolved;
    private static long blockedMelee;
    private static long parriedMelee;

    public static bool Enabled => enabled;

    private struct Pending
    {
        public int RequestedDirection;
        public int AppliesWaiting;
        public bool Active;
    }

    public static void Start()
    {
        lock (Gate)
        {
            Array.Clear(LatencyBuckets, 0, LatencyBuckets.Length);
            Array.Clear(RequestedByDirection, 0, RequestedByDirection.Length);
            Array.Clear(MismatchByDirection, 0, MismatchByDirection.Length);
            Array.Clear(ObservedByCategory, 0, ObservedByCategory.Length);
            Array.Clear(MismatchByCategory, 0, MismatchByCategory.Length);
            Pendings.Clear();
            observations = 0;
            guardMatched = 0;
            guardMismatched = 0;
            flagsMatched = 0;
            flagsMismatched = 0;
            neverResolved = 0;
            blockedMelee = 0;
            parriedMelee = 0;
            enabled = true;
        }
    }

    /// <summary>Records one completed apply. Every "applied" value must be read AFTER the apply.</summary>
    public static void Record(
        int agentIndex,
        int requestedGuardMode,
        int requestedDefendFlags,
        int appliedDefendFlags,
        int appliedGuardMode,
        int puppetActionType)
    {
        if (!enabled) return;

        int requested = Clamp(requestedGuardMode);
        int applied = Clamp(appliedGuardMode);
        Category category = Classify(puppetActionType);

        lock (Gate)
        {
            observations++;
            ObservedByCategory[(int)category]++;

            if (puppetActionType == BlockedMelee) blockedMelee++;
            else if (puppetActionType == ParriedMelee) parriedMelee++;

            // View 1: the flag bits. Meaningful whatever the puppet is playing.
            if (requestedDefendFlags == appliedDefendFlags) flagsMatched++;
            else flagsMismatched++;

            // View 2: the animation's direction. Only meaningful while actually defending.
            RequestedByDirection[requested]++;
            bool agrees = requested == applied;
            if (agrees) guardMatched++;
            else
            {
                guardMismatched++;
                MismatchByDirection[requested]++;
                MismatchByCategory[(int)category]++;
            }

            Pendings.TryGetValue(agentIndex, out Pending pending);
            if (!pending.Active || pending.RequestedDirection != requested)
            {
                if (pending.Active && pending.RequestedDirection != applied) neverResolved++;

                if (Pendings.Count >= AgentMapLimit && !Pendings.ContainsKey(agentIndex)) Pendings.Clear();
                Pendings[agentIndex] = new Pending
                {
                    RequestedDirection = requested,
                    AppliesWaiting = 0,
                    Active = !agrees,
                };

                if (agrees) LatencyBuckets[BucketFor(0)]++;
                return;
            }

            pending.AppliesWaiting++;
            if (agrees)
            {
                LatencyBuckets[BucketFor(pending.AppliesWaiting)]++;
                pending.Active = false;
            }
            Pendings[agentIndex] = pending;
        }
    }

    private static Category Classify(int actionType)
    {
        if (actionType == Guard) return Category.Defending;
        if (actionType >= DefendFirst && actionType <= DefendLast) return Category.Defending;
        if (actionType == ParriedMelee || actionType == BlockedMelee) return Category.Reaction;
        if (actionType == ReadyMelee || actionType == ReleaseMelee) return Category.MeleeSwing;
        if (actionType >= ReadyRanged && actionType <= Reload) return Category.Ranged;
        if (actionType == 0 || actionType == Idle) return Category.IdleOrNone;
        return Category.Other;
    }

    private static int Clamp(int direction) =>
        direction < 0 || direction >= DirectionCount ? 0 : direction;

    private static int BucketFor(int applies)
    {
        for (int i = 0; i < LatencyEdges.Length; i++)
            if (applies < LatencyEdges[i]) return i;

        return LatencyEdges.Length;
    }

    public static string Snapshot(bool stop)
    {
        lock (Gate)
        {
            if (stop) enabled = false;
            if (observations == 0) return "guardSync: nothing observed";

            var text = new StringBuilder();
            text.Append("guardSync: observations=").Append(observations.ToString(CultureInfo.InvariantCulture));
            text.Append(" flagsMatched=").Append(Percent(flagsMatched, observations));
            text.Append(" dirMatched=").Append(Percent(guardMatched, observations));
            text.Append(" mismatched=").Append(guardMismatched.ToString(CultureInfo.InvariantCulture));
            text.Append(" neverResolved=").Append(neverResolved.ToString(CultureInfo.InvariantCulture));
            text.Append(" blocked=").Append(blockedMelee.ToString(CultureInfo.InvariantCulture));
            text.Append(" parried=").Append(parriedMelee.ToString(CultureInfo.InvariantCulture));

            text.Append(" latency=[");
            for (int i = 0; i < LatencyBuckets.Length; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append(i < LatencyEdges.Length
                    ? "<" + LatencyEdges[i].ToString(CultureInfo.InvariantCulture)
                    : ">=" + LatencyEdges[LatencyEdges.Length - 1].ToString(CultureInfo.InvariantCulture));
                text.Append(':').Append(LatencyBuckets[i].ToString(CultureInfo.InvariantCulture));
            }
            text.Append(']');

            text.Append(" byDir=[");
            for (int i = 0; i < DirectionCount; i++)
            {
                if (RequestedByDirection[i] == 0) continue;
                text.Append('d').Append(i.ToString(CultureInfo.InvariantCulture)).Append(':');
                text.Append(MismatchByDirection[i].ToString(CultureInfo.InvariantCulture)).Append('/');
                text.Append(RequestedByDirection[i].ToString(CultureInfo.InvariantCulture)).Append(' ');
            }
            text.Append(']');

            // The decisive split: a direction mismatch only means anything while the puppet is DEFENDING.
            text.Append(" byWhatPuppetWasDoing=[");
            for (int i = 0; i < CategoryNames.Length; i++)
            {
                if (ObservedByCategory[i] == 0) continue;
                text.Append(CategoryNames[i]).Append(':');
                text.Append(MismatchByCategory[i].ToString(CultureInfo.InvariantCulture)).Append('/');
                text.Append(ObservedByCategory[i].ToString(CultureInfo.InvariantCulture)).Append(' ');
            }
            text.Append(']');

            return text.ToString();
        }
    }

    private static string Percent(long part, long whole) =>
        whole == 0
            ? "0%"
            : (100d * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
