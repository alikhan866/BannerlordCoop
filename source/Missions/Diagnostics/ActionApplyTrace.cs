using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Diagnostics;

/// <summary>
/// Records the outcome of EVERY remote action apply, not just the ones that start an animation.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the previous counters only recorded applies that produced a transition. In one battle
/// 11,582 channel-1 packets arrived carrying under 1% progress - the start of a swing - and only 2,286 of them
/// started an animation. Roughly 9,300 swing-starts were declined, and nothing recorded why. Every hypothesis
/// about the missing wind-up was argued in that blind spot, and three of them were wrong.
/// </para>
/// <para>
/// So all four exits from <c>ApplyActionChannel</c> are recorded, each with the incoming progress AND the state
/// the puppet was in when the decision was taken. The decisive cell is
/// <see cref="Outcome.NoTransitionSameIndex"/> at low incoming progress with the puppet already far through the
/// animation: that is a swing being thrown again while the puppet still plays the previous one, and the count
/// says how much of the problem it really is rather than how much it feels like.
/// </para>
/// <para>
/// A short per-agent narrative is kept alongside the totals, because the sequence of decisions for one agent
/// explains a swing in a way aggregates cannot. Both are bounded: an aggregate snapshot that exceeded the
/// one-megabyte live-test limit could not be pulled off a running client at all, which is how the first
/// animation trace was lost.
/// </para>
/// <para>
/// Only calls already proven on this path are used - <see cref="Agent.GetCurrentAction"/>,
/// <see cref="Agent.GetCurrentActionType"/>, <see cref="Agent.GetCurrentActionProgress"/>. Nothing here touches
/// <c>agent.ActionSet</c>, which crashed a live client with an access violation earlier.
/// </para>
/// </remarks>
internal static class ActionApplyTrace
{
    internal enum Outcome
    {
        /// <summary>Refused by the released-player-guard predicate.</summary>
        SuppressedPlayerGuard = 0,

        /// <summary>Refused by the mounted-guard rearm predicate.</summary>
        SuppressedMountedGuard = 5,

        /// <summary>Refused to preserve a parry or block reaction the puppet is playing.</summary>
        SuppressedGuardReaction = 6,

        /// <summary>The puppet is already playing this exact action index.</summary>
        NoTransitionSameIndex = 1,

        /// <summary>The puppet is idle but the visible action was preserved instead.</summary>
        NoTransitionPreserved = 2,

        /// <summary>The action index could not be resolved to an animation.</summary>
        ResolveFailed = 3,

        /// <summary>The animation was started.</summary>
        Applied = 4,
    }

    private static readonly string[] OutcomeNames =
    {
        "supPlayerGuard", "sameIndex", "preserved", "resolveFailed", "applied",
        "supMountedGuard", "supGuardReaction",
    };

    private static readonly float[] BucketEdges = { 0.01f, 0.05f, 0.20f, 0.50f };

    // Exact Agent.ActionCodeType values, read off the shipped assembly rather than assumed.
    private const int ReadyRanged = 15;
    private const int Reload = 18;
    private const int ReadyMelee = 19;
    private const int ReleaseMelee = 20;
    private const int ParriedMelee = 21;
    private const int BlockedMelee = 22;

    private const int TracedAgents = 3;
    private const int EventsPerAgent = 24;
    private const int AgentMapLimit = 8192;

    private static readonly object Gate = new object();

    /// <summary>Slots are (outcome, channel), indexed by <c>outcome * 2 + channel</c>.</summary>
    private static readonly Slot[] Slots = BuildSlots();

    private static readonly Dictionary<int, List<string>> Narratives = new Dictionary<int, List<string>>();

    /// <summary>How far a melee swing got before something replaced it.</summary>
    private static readonly float[] CompletionEdges = { 0.25f, 0.50f, 0.70f, 0.85f, 0.95f };

    private static readonly int[] CompletionBuckets = new int[CompletionEdges.Length + 1];
    private static readonly Dictionary<int, Swing> Swings = new Dictionary<int, Swing>();

    /// <summary>Samples taken across one swing's lifetime. Declared BEFORE the array sized from it: static
    /// initialisers run in declaration order, so the other way round leaves the length read off a null.</summary>
    private static readonly int[] ObservationEdges = { 2, 4, 8, 16, 32 };

    private static readonly int[] ObservationBuckets = new int[ObservationEdges.Length + 1];

    private static long swingsCompleted;
    private static double completionTotal;
    private static double observationTotal;
    private static long endedAtLastSample;
    private static long replacedByNone;
    private static long replacedByMelee;
    private static long replacedByReaction;
    private static long replacedByOther;

    /// <summary>
    /// Churn: how often the weapon channel is re-driven, and whether the incoming stream is alternating between
    /// a real action and act_none.
    /// </summary>
    /// <remarks>
    /// This is the architectural difference between a host and a client. A locally simulated agent plays ONE
    /// continuous animation driven by the engine. A puppet has its animation re-imposed by SetActionChannel every
    /// time a packet says the action index differs - and 87% of melee swings land on a puppet sitting in
    /// act_none, which means the index differs constantly. An animation re-set dozens of times a second cannot
    /// play smoothly and cannot reach the frames that fire its sound events, which is exactly the reported
    /// symptom set: quick-looking swings, missing weapon and shield sounds, clients only, hosts fine.
    /// </remarks>
    private static readonly Stopwatch Clock = new Stopwatch();
    private static readonly Dictionary<int, int> LastIncomingCh1 = new Dictionary<int, int>();
    private static readonly HashSet<int> AgentsSeen = new HashSet<int>();

    /// <summary>
    /// How far FORWARD a transition shoves the puppet's animation: the progress being written minus the progress
    /// the puppet was already at.
    /// </summary>
    /// <remarks>
    /// This is the one thing every earlier measurement structurally could not see. The playback-rate figure only
    /// sampled intervals where the action index was UNCHANGED - the smooth stretches between transitions - so a
    /// snap forward at the transition itself never entered it, and 1.04x was never evidence that nothing jumped.
    /// A locally simulated agent is never re-seeked at all, which is why hosts look correct and clients do not,
    /// and a forward jump skips the animation frames that fire weapon and shield sounds.
    /// </remarks>
    private static readonly float[] JumpEdges = { 0.02f, 0.05f, 0.10f, 0.20f, 0.40f };

    private static readonly int[] ForwardJumpBuckets = new int[JumpEdges.Length + 1];
    private static readonly int[] SwingForwardJumpBuckets = new int[JumpEdges.Length + 1];
    private static readonly Dictionary<int, int> TransitionsPerAgent = new Dictionary<int, int>();

    private static long forwardJumps;
    private static double forwardJumpTotal;
    private static long swingForwardJumps;
    private static double swingForwardJumpTotal;
    private static long backwardJumps;

    /// <summary>
    /// How far an incoming action's progress had already advanced between the moment this client FIRST saw that
    /// action and the moment it finally applied it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This separates "the information arrived late" from "we sat on it". Both clients are on localhost at 0 ms
    /// ping and packets arrive at ~46/sec, so a swing should be known within about 2% of an animation. Forward
    /// jumps average 25%. That gap cannot be the network.
    /// </para>
    /// <para>
    /// It decides whether a fix costs anything. If the action arrives already advanced, the client is stuck
    /// choosing between correct playback speed and correct timing. If the client received it early and deferred
    /// applying it, there is no trade-off at all - stop deferring and the swing starts at the beginning, plays at
    /// normal speed, and fires every sound.
    /// </para>
    /// </remarks>
    private static readonly float[] DeferralEdges = { 0.01f, 0.05f, 0.10f, 0.20f, 0.40f };

    private static readonly int[] DeferralBuckets = new int[DeferralEdges.Length + 1];
    private static readonly Dictionary<int, PendingIncoming> Pendings = new Dictionary<int, PendingIncoming>();

    private static long deferralsRecorded;
    private static double deferralTotal;
    private static long appliedImmediately;
    private static long heldWhileSuppressed;
    private static long heldWhileSameIndex;
    private static long heldWhilePreserved;
    private static long heldDecisionsTotal;

    /// <summary>
    /// Suppressions counted by what the INCOMING action was, not by what the puppet happened to be playing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transition suppression was "eliminated" twice in this investigation, wrongly both times. First by reading
    /// the predicates and concluding they only touch defend packets. Then by checking the suppressed slot's
    /// swing counter - which reports the PUPPET's action, not the action being refused. A suppressed attack
    /// therefore showed up there as a reaction or a defend, and looked like proof of innocence.
    /// </para>
    /// <para>
    /// A side-by-side timeline diff later caught the puppet sitting in DefendShield across the owner's entire
    /// ReadyMelee to ReleaseMelee, missing 71% of the owner's swing time. So this counts the thing that actually
    /// matters: incoming MELEE SWINGS that were refused, and which predicate refused them.
    /// </para>
    /// <para>
    /// The incoming index is a bare integer, so its type is learned from applies - when an index is applied and
    /// the puppet then reports a type, that mapping is remembered and used to classify refusals of the same
    /// index. No extra engine calls, and the map is bounded.
    /// </para>
    /// </remarks>
    private const int IndexMapLimit = 1024;

    private static readonly Dictionary<int, int> IndexTypes = new Dictionary<int, int>();
    private static readonly Dictionary<int, long[]> SuppressedByIndex = new Dictionary<int, long[]>();

    private static long ch1Decisions;
    private static long ch1Transitions;
    private static long ch1IncomingNone;
    private static long ch1IncomingReal;
    private static long ch1Alternations;

    private static bool enabled;
    private static long events;

    public static bool Enabled => enabled;

    private sealed class Slot
    {
        public readonly int[] IncomingBuckets = new int[BucketEdges.Length + 1];

        /// <summary>Cross-tab: incoming bucket against how far the PUPPET had already got.</summary>
        public readonly int[] PuppetAheadByIncoming = new int[BucketEdges.Length + 1];

        public long Count;
        public double IncomingTotal;
        public double PuppetTotal;

        /// <summary>Real melee swings: ReadyMelee / ReleaseMelee.</summary>
        public long MeleeSwing;

        /// <summary>ParriedMelee / BlockedMelee - reactions, NOT attacks, though the engine files them
        /// inside AttackMeleeAndRangedAllBegin..End along with reloads and drawn bows.</summary>
        public long GuardReaction;

        /// <summary>ReadyRanged / ReleaseRanged / ReleaseThrowing / Reload - a held bow sits deep in its
        /// animation as a matter of course, so counting these as attacks manufactures skipped wind-ups.</summary>
        public long RangedOrReload;

        public void Clear()
        {
            Array.Clear(IncomingBuckets, 0, IncomingBuckets.Length);
            Array.Clear(PuppetAheadByIncoming, 0, PuppetAheadByIncoming.Length);
            Count = 0;
            IncomingTotal = 0d;
            PuppetTotal = 0d;
            MeleeSwing = 0;
            GuardReaction = 0;
            RangedOrReload = 0;
        }
    }

    private static Slot[] BuildSlots()
    {
        var slots = new Slot[OutcomeNames.Length * 2];
        for (int i = 0; i < slots.Length; i++) slots[i] = new Slot();
        return slots;
    }

    private struct PendingIncoming
    {
        public int ActionIndex;
        public float FirstSeenProgress;
        public int Decisions;
        public int Suppressed;
        public int SameIndex;
        public int Preserved;
    }

    private struct Swing
    {
        public int ActionIndex;
        public float MaxProgress;
        public float LastProgress;

        /// <summary>
        /// How many times this swing was actually looked at. Without it, "no swing ever completed" cannot be
        /// told apart from "the samples never landed near the end", and that difference decides whether the
        /// truncation is real.
        /// </summary>
        public int Observations;
    }

    public static void Start()
    {
        lock (Gate)
        {
            foreach (Slot slot in Slots) slot.Clear();
            Narratives.Clear();
            Array.Clear(CompletionBuckets, 0, CompletionBuckets.Length);
            Array.Clear(ObservationBuckets, 0, ObservationBuckets.Length);
            Swings.Clear();
            LastIncomingCh1.Clear();
            AgentsSeen.Clear();
            ch1Decisions = 0;
            ch1Transitions = 0;
            ch1IncomingNone = 0;
            ch1IncomingReal = 0;
            ch1Alternations = 0;
            Array.Clear(ForwardJumpBuckets, 0, ForwardJumpBuckets.Length);
            Array.Clear(SwingForwardJumpBuckets, 0, SwingForwardJumpBuckets.Length);
            TransitionsPerAgent.Clear();
            Pendings.Clear();
            IndexTypes.Clear();
            SuppressedByIndex.Clear();
            Array.Clear(DeferralBuckets, 0, DeferralBuckets.Length);
            deferralsRecorded = 0;
            deferralTotal = 0d;
            appliedImmediately = 0;
            heldWhileSuppressed = 0;
            heldWhileSameIndex = 0;
            heldWhilePreserved = 0;
            heldDecisionsTotal = 0;
            forwardJumps = 0;
            forwardJumpTotal = 0d;
            swingForwardJumps = 0;
            swingForwardJumpTotal = 0d;
            backwardJumps = 0;
            Clock.Reset();
            Clock.Start();
            swingsCompleted = 0;
            completionTotal = 0d;
            observationTotal = 0d;
            endedAtLastSample = 0;
            replacedByNone = 0;
            replacedByMelee = 0;
            replacedByReaction = 0;
            replacedByOther = 0;
            events = 0;
            enabled = true;
        }
    }

    /// <summary>
    /// Follows a melee swing on the puppet and records how far it got before something replaced it.
    /// </summary>
    /// <remarks>
    /// Sound in Bannerlord is driven by animation events embedded at specific frames, so a swing cut off before
    /// its later frames never fires them - no whoosh, no impact, no shield clang. A truncated animation also
    /// LOOKS fast while measuring as correct speed and correct start point, which is exactly the combination
    /// observed: playback rate 1.04, start offsets small, and yet visibly quick with missing sounds.
    /// </remarks>
    private static void TrackSwing(int agentIndex, int channel, int puppetActionIndex, float puppetProgress, int puppetActionType)
    {
        int key = agentIndex * 2 + (channel & 1);
        bool isSwing = puppetActionType == ReadyMelee || puppetActionType == ReleaseMelee;

        if (Swings.TryGetValue(key, out Swing tracked))
        {
            if (tracked.ActionIndex == puppetActionIndex && isSwing)
            {
                tracked.Observations++;
                tracked.LastProgress = puppetProgress;
                if (puppetProgress > tracked.MaxProgress) tracked.MaxProgress = puppetProgress;
                Swings[key] = tracked;
                return;
            }

            // The tracked swing is gone. Record how far it actually got, and what took its place.
            swingsCompleted++;
            completionTotal += tracked.MaxProgress;
            observationTotal += tracked.Observations;
            CompletionBuckets[BucketFor2(CompletionEdges, tracked.MaxProgress)]++;
            ObservationBuckets[BucketForInt(ObservationEdges, tracked.Observations)]++;

            // If the peak was the very last thing seen, the swing may simply have been cut off between samples
            // rather than genuinely stopping there.
            if (tracked.MaxProgress <= tracked.LastProgress + 0.001f) endedAtLastSample++;

            if (puppetActionType == 0) replacedByNone++;
            else if (isSwing) replacedByMelee++;
            else if (puppetActionType == ParriedMelee || puppetActionType == BlockedMelee) replacedByReaction++;
            else replacedByOther++;

            Swings.Remove(key);
        }

        if (isSwing)
        {
            if (Swings.Count >= AgentMapLimit) Swings.Clear();
            Swings[key] = new Swing
            {
                ActionIndex = puppetActionIndex,
                MaxProgress = puppetProgress,
                LastProgress = puppetProgress,
                Observations = 1,
            };
        }
    }

    /// <summary>
    /// Learns what an action index means from applies, and tallies refusals of that index by predicate.
    /// </summary>
    private static void TrackSuppressedIncoming(Outcome outcome, int incomingActionIndex, int puppetActionType)
    {
        if (incomingActionIndex < 0) return;

        if (outcome == Outcome.Applied)
        {
            // After an apply the puppet is playing the incoming action, so its type identifies that index.
            if (IndexTypes.Count < IndexMapLimit || IndexTypes.ContainsKey(incomingActionIndex))
                IndexTypes[incomingActionIndex] = puppetActionType;
            return;
        }

        int slot;
        if (outcome == Outcome.SuppressedPlayerGuard) slot = 0;
        else if (outcome == Outcome.SuppressedMountedGuard) slot = 1;
        else if (outcome == Outcome.SuppressedGuardReaction) slot = 2;
        else return;

        if (!SuppressedByIndex.TryGetValue(incomingActionIndex, out long[] counts))
        {
            if (SuppressedByIndex.Count >= IndexMapLimit) return;
            counts = new long[3];
            SuppressedByIndex[incomingActionIndex] = counts;
        }
        counts[slot]++;
    }

    /// <summary>
    /// Follows one incoming action from the first time this client saw it to the moment it was applied.
    /// </summary>
    private static void TrackDeferral(
        int agentIndex,
        int incomingActionIndex,
        float incomingProgress,
        Outcome outcome,
        bool applied)
    {
        Pendings.TryGetValue(agentIndex, out PendingIncoming pending);

        if (pending.ActionIndex != incomingActionIndex)
        {
            // First sighting of this action.
            if (applied)
            {
                // Applied the instant it was seen: nothing was deferred.
                appliedImmediately++;
                Pendings.Remove(agentIndex);
                return;
            }

            if (Pendings.Count >= AgentMapLimit) Pendings.Clear();
            Pendings[agentIndex] = new PendingIncoming
            {
                ActionIndex = incomingActionIndex,
                FirstSeenProgress = incomingProgress,
                Decisions = 1,
                Suppressed = outcome == Outcome.SuppressedPlayerGuard
                    || outcome == Outcome.SuppressedMountedGuard
                    || outcome == Outcome.SuppressedGuardReaction ? 1 : 0,
                SameIndex = outcome == Outcome.NoTransitionSameIndex ? 1 : 0,
                Preserved = outcome == Outcome.NoTransitionPreserved ? 1 : 0,
            };
            return;
        }

        pending.Decisions++;
        if (outcome == Outcome.SuppressedPlayerGuard
            || outcome == Outcome.SuppressedMountedGuard
            || outcome == Outcome.SuppressedGuardReaction)
        {
            pending.Suppressed++;
        }
        else if (outcome == Outcome.NoTransitionSameIndex) pending.SameIndex++;
        else if (outcome == Outcome.NoTransitionPreserved) pending.Preserved++;

        if (!applied)
        {
            Pendings[agentIndex] = pending;
            return;
        }

        // Applied at last. How much did the action advance while we held it?
        float deferred = incomingProgress - pending.FirstSeenProgress;
        if (deferred < 0f) deferred = 0f;

        deferralsRecorded++;
        deferralTotal += deferred;
        heldDecisionsTotal += pending.Decisions;
        DeferralBuckets[BucketFor2(DeferralEdges, deferred)]++;

        // What was refusing it while it waited.
        if (pending.Suppressed >= pending.SameIndex && pending.Suppressed >= pending.Preserved)
            heldWhileSuppressed++;
        else if (pending.SameIndex >= pending.Preserved) heldWhileSameIndex++;
        else heldWhilePreserved++;

        Pendings.Remove(agentIndex);
    }

    private static int BucketForInt(int[] edges, int value)
    {
        for (int i = 0; i < edges.Length; i++)
            if (value < edges[i]) return i;

        return edges.Length;
    }

    private static int BucketFor2(float[] edges, float value)
    {
        for (int i = 0; i < edges.Length; i++)
            if (value < edges[i]) return i;

        return edges.Length;
    }

    /// <summary>
    /// Records one apply decision. <paramref name="puppetProgress"/> and <paramref name="puppetActionIndex"/>
    /// describe what the puppet was doing when the decision was taken, so a declined start can be explained.
    /// </summary>
    public static void Record(
        Outcome outcome,
        Agent agent,
        int channel,
        int incomingActionIndex,
        float incomingProgress,
        int puppetActionIndex,
        float puppetProgress,
        int puppetActionType)
    {
        if (!enabled || agent == null) return;
        if (channel < 0 || channel > 1) return;

        float incoming = Sanitise(incomingProgress);
        float puppet = Sanitise(puppetProgress);
        int slotIndex = (int)outcome * 2 + channel;

        lock (Gate)
        {
            events++;

            Slot slot = Slots[slotIndex];
            slot.Count++;
            slot.IncomingTotal += incoming;
            slot.PuppetTotal += puppet;

            // Classified by exact type, never by the engine range: that range lumps parries, blocks, reloads
            // and drawn bows in with real swings, which is what made every earlier reading unusable.
            if (puppetActionType == ReadyMelee || puppetActionType == ReleaseMelee) slot.MeleeSwing++;
            else if (puppetActionType == ParriedMelee || puppetActionType == BlockedMelee) slot.GuardReaction++;
            else if (puppetActionType >= ReadyRanged && puppetActionType <= Reload) slot.RangedOrReload++;

            int bucket = BucketFor(incoming);
            slot.IncomingBuckets[bucket]++;

            // The cell that matters: the attacker is near the start while the puppet is well past it.
            if (puppet > incoming + 0.3f) slot.PuppetAheadByIncoming[bucket]++;

            if (channel == 1)
            {
                ch1Decisions++;
                if (outcome == Outcome.Applied)
                {
                    ch1Transitions++;

                    // The jump: what we are writing, minus where the puppet already was.
                    float jump = incoming - puppet;
                    if (jump > 0f)
                    {
                        forwardJumps++;
                        forwardJumpTotal += jump;
                        ForwardJumpBuckets[BucketFor2(JumpEdges, jump)]++;

                        bool puppetWasSwinging =
                            puppetActionType == ReadyMelee || puppetActionType == ReleaseMelee;
                        if (puppetWasSwinging)
                        {
                            swingForwardJumps++;
                            swingForwardJumpTotal += jump;
                            SwingForwardJumpBuckets[BucketFor2(JumpEdges, jump)]++;
                        }
                    }
                    else if (jump < -0.02f)
                    {
                        backwardJumps++;
                    }

                    TrackDeferral(agent.Index, incomingActionIndex, incoming, outcome, applied: true);

                    TransitionsPerAgent.TryGetValue(agent.Index, out int seen);
                    if (TransitionsPerAgent.Count >= AgentMapLimit) TransitionsPerAgent.Clear();
                    TransitionsPerAgent[agent.Index] = seen + 1;
                }
                AgentsSeen.Add(agent.Index);

                if (outcome != Outcome.Applied)
                    TrackDeferral(agent.Index, incomingActionIndex, incoming, outcome, applied: false);

                TrackSuppressedIncoming(outcome, incomingActionIndex, puppetActionType);

                bool incomingIsNone = incomingActionIndex < 0;
                if (incomingIsNone) ch1IncomingNone++;
                else ch1IncomingReal++;

                // A stream that flips real -> none -> real forces a fresh SetActionChannel every flip.
                if (LastIncomingCh1.TryGetValue(agent.Index, out int previousIncoming))
                {
                    if ((previousIncoming < 0) != incomingIsNone) ch1Alternations++;
                }
                if (LastIncomingCh1.Count >= AgentMapLimit) LastIncomingCh1.Clear();
                LastIncomingCh1[agent.Index] = incomingActionIndex;
            }

            TrackSwing(agent.Index, channel, puppetActionIndex, puppet, puppetActionType);

            RecordNarrative(
                agent.Index, outcome, channel, incomingActionIndex, incoming,
                puppetActionIndex, puppet, puppetActionType);
        }
    }

    private static void RecordNarrative(
        int agentIndex,
        Outcome outcome,
        int channel,
        int incomingActionIndex,
        float incoming,
        int puppetActionIndex,
        float puppet,
        int puppetActionType)
    {
        if (!Narratives.TryGetValue(agentIndex, out List<string> log))
        {
            // Only follow a handful of agents; the sequence for one is what explains a swing.
            if (Narratives.Count >= TracedAgents) return;
            if (Narratives.Count >= AgentMapLimit) return;
            log = new List<string>(EventsPerAgent);
            Narratives[agentIndex] = log;
        }

        if (log.Count >= EventsPerAgent) return;

        log.Add(string.Concat(
            "ch", channel.ToString(CultureInfo.InvariantCulture),
            " ", OutcomeNames[(int)outcome],
            " in=", incomingActionIndex.ToString(CultureInfo.InvariantCulture),
            "@", Pct(incoming),
            " pup=", puppetActionIndex.ToString(CultureInfo.InvariantCulture),
            "/t", puppetActionType.ToString(CultureInfo.InvariantCulture),
            "@", Pct(puppet)));
    }

    private static float Sanitise(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
        return value < 0f ? 0f : value > 1f ? 1f : value;
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
            if (events == 0) return "applyTrace: nothing recorded";

            var text = new StringBuilder();
            text.Append("applyTrace: events=").Append(events.ToString(CultureInfo.InvariantCulture));

            for (int i = 0; i < Slots.Length; i++)
            {
                Slot slot = Slots[i];
                if (slot.Count == 0) continue;

                text.Append(" | ").Append(OutcomeNames[i / 2]).Append(".ch").Append(i % 2);
                text.Append('=').Append(slot.Count.ToString(CultureInfo.InvariantCulture));
                text.Append(" swing=").Append(slot.MeleeSwing.ToString(CultureInfo.InvariantCulture));
                text.Append(" react=").Append(slot.GuardReaction.ToString(CultureInfo.InvariantCulture));
                text.Append(" ranged=").Append(slot.RangedOrReload.ToString(CultureInfo.InvariantCulture));
                text.Append(" in~").Append(Pct((float)(slot.IncomingTotal / slot.Count)));
                text.Append(" pup~").Append(Pct((float)(slot.PuppetTotal / slot.Count)));

                text.Append(" byIn=[");
                for (int b = 0; b < slot.IncomingBuckets.Length; b++)
                {
                    if (b > 0) text.Append(' ');
                    text.Append(b < BucketEdges.Length ? "<" + Pct(BucketEdges[b]) : "hi");
                    text.Append(':').Append(slot.IncomingBuckets[b].ToString(CultureInfo.InvariantCulture));
                    // How many of those had the puppet already well ahead of the attacker.
                    text.Append('/').Append(slot.PuppetAheadByIncoming[b].ToString(CultureInfo.InvariantCulture));
                }
                text.Append(']');
            }

            double seconds = Clock.Elapsed.TotalSeconds;
            if (ch1Decisions > 0 && seconds > 0.5d)
            {
                int agents = AgentsSeen.Count;
                text.Append(" || churn: elapsed=").Append(seconds.ToString("0", CultureInfo.InvariantCulture)).Append('s');
                text.Append(" agents=").Append(agents.ToString(CultureInfo.InvariantCulture));
                text.Append(" ch1Decisions=").Append(ch1Decisions.ToString(CultureInfo.InvariantCulture));
                text.Append(" transitions=").Append(ch1Transitions.ToString(CultureInfo.InvariantCulture));
                text.Append('(').Append(Percent(ch1Transitions, ch1Decisions)).Append(')');
                text.Append(" transitionsPerSec=").Append((ch1Transitions / seconds).ToString("0", CultureInfo.InvariantCulture));
                if (agents > 0)
                {
                    text.Append(" PER_AGENT_PER_SEC=")
                        .Append((ch1Transitions / seconds / agents).ToString("0.0", CultureInfo.InvariantCulture));
                }
                text.Append(" incomingNone=").Append(Percent(ch1IncomingNone, ch1Decisions));
                text.Append(" alternations=").Append(ch1Alternations.ToString(CultureInfo.InvariantCulture));
                text.Append('(').Append(Percent(ch1Alternations, ch1Decisions)).Append(')');

                // The average over every agent hides the handful actually in melee with the player, which is the
                // mistake that made 0.5/sec look reassuring. Report the busiest instead.
                int busiest = 0;
                int secondBusiest = 0;
                foreach (KeyValuePair<int, int> entry in TransitionsPerAgent)
                {
                    if (entry.Value > busiest) { secondBusiest = busiest; busiest = entry.Value; }
                    else if (entry.Value > secondBusiest) secondBusiest = entry.Value;
                }
                text.Append(" BUSIEST_AGENT_PER_SEC=")
                    .Append((busiest / seconds).ToString("0.0", CultureInfo.InvariantCulture));
                text.Append(" secondBusiest=")
                    .Append((secondBusiest / seconds).ToString("0.0", CultureInfo.InvariantCulture));
            }

            long windupRefused = 0;
            long releaseRefused = 0;
            long[] swingSuppressed = new long[3];
            long swingSuppressedTotal = 0;
            long unknownIndexSuppressed = 0;
            foreach (KeyValuePair<int, long[]> entry in SuppressedByIndex)
            {
                if (!IndexTypes.TryGetValue(entry.Key, out int type))
                {
                    for (int i = 0; i < 3; i++) unknownIndexSuppressed += entry.Value[i];
                    continue;
                }
                if (type != ReadyMelee && type != ReleaseMelee) continue;

                for (int i = 0; i < 3; i++)
                {
                    swingSuppressed[i] += entry.Value[i];
                    swingSuppressedTotal += entry.Value[i];
                    if (type == ReadyMelee) windupRefused += entry.Value[i];
                    else releaseRefused += entry.Value[i];
                }
            }

            text.Append(" || SWINGS_REFUSED=").Append(swingSuppressedTotal.ToString(CultureInfo.InvariantCulture));
            text.Append(" byPredicate=[playerGuard:").Append(swingSuppressed[0].ToString(CultureInfo.InvariantCulture));
            text.Append(" mountedGuard:").Append(swingSuppressed[1].ToString(CultureInfo.InvariantCulture));
            text.Append(" guardReaction:").Append(swingSuppressed[2].ToString(CultureInfo.InvariantCulture)).Append(']');
            text.Append(" windupRefused=").Append(windupRefused.ToString(CultureInfo.InvariantCulture));
            text.Append(" releaseRefused=").Append(releaseRefused.ToString(CultureInfo.InvariantCulture));
            text.Append(" unknownIndex=").Append(unknownIndexSuppressed.ToString(CultureInfo.InvariantCulture));
            text.Append(" knownIndices=").Append(IndexTypes.Count.ToString(CultureInfo.InvariantCulture));

            if (deferralsRecorded > 0 || appliedImmediately > 0)
            {
                long total = deferralsRecorded + appliedImmediately;
                text.Append(" || DEFERRAL: applied=").Append(total.ToString(CultureInfo.InvariantCulture));
                text.Append(" immediate=").Append(appliedImmediately.ToString(CultureInfo.InvariantCulture));
                text.Append('(').Append(Percent(appliedImmediately, total)).Append(')');
                text.Append(" HELD=").Append(deferralsRecorded.ToString(CultureInfo.InvariantCulture));
                if (deferralsRecorded > 0)
                {
                    text.Append(" advancedWhileHeld~")
                        .Append(Pct((float)(deferralTotal / deferralsRecorded)));
                    text.Append(" decisionsHeld~")
                        .Append(((float)heldDecisionsTotal / deferralsRecorded).ToString("0.0", CultureInfo.InvariantCulture));
                    text.Append(" sizes=[");
                    for (int i = 0; i < DeferralBuckets.Length; i++)
                    {
                        if (i > 0) text.Append(' ');
                        text.Append(i < DeferralEdges.Length ? "<" + Pct(DeferralEdges[i]) : "huge");
                        text.Append(':').Append(DeferralBuckets[i].ToString(CultureInfo.InvariantCulture));
                    }
                    text.Append("] heldBy=[suppressed:").Append(heldWhileSuppressed.ToString(CultureInfo.InvariantCulture));
                    text.Append(" sameIndex:").Append(heldWhileSameIndex.ToString(CultureInfo.InvariantCulture));
                    text.Append(" preserved:").Append(heldWhilePreserved.ToString(CultureInfo.InvariantCulture)).Append(']');
                }
            }

            if (forwardJumps > 0 || backwardJumps > 0)
            {
                text.Append(" || JUMPS: forward=").Append(forwardJumps.ToString(CultureInfo.InvariantCulture));
                text.Append(" mean=").Append(Pct((float)(forwardJumpTotal / Math.Max(1L, forwardJumps))));
                text.Append(" backward=").Append(backwardJumps.ToString(CultureInfo.InvariantCulture));
                text.Append(" sizes=[");
                for (int i = 0; i < ForwardJumpBuckets.Length; i++)
                {
                    if (i > 0) text.Append(' ');
                    text.Append(i < JumpEdges.Length ? "<" + Pct(JumpEdges[i]) : "huge");
                    text.Append(':').Append(ForwardJumpBuckets[i].ToString(CultureInfo.InvariantCulture));
                }
                text.Append(']');

                text.Append(" MID_SWING_JUMPS=").Append(swingForwardJumps.ToString(CultureInfo.InvariantCulture));
                if (swingForwardJumps > 0)
                {
                    text.Append(" mean=").Append(Pct((float)(swingForwardJumpTotal / swingForwardJumps)));
                    text.Append(" sizes=[");
                    for (int i = 0; i < SwingForwardJumpBuckets.Length; i++)
                    {
                        if (i > 0) text.Append(' ');
                        text.Append(i < JumpEdges.Length ? "<" + Pct(JumpEdges[i]) : "huge");
                        text.Append(':').Append(SwingForwardJumpBuckets[i].ToString(CultureInfo.InvariantCulture));
                    }
                    text.Append(']');
                }
            }

            if (swingsCompleted > 0)
            {
                text.Append(" || swingEnd: n=").Append(swingsCompleted.ToString(CultureInfo.InvariantCulture));
                text.Append(" mean=").Append(Pct((float)(completionTotal / swingsCompleted)));
                text.Append(" reachedAt=[");
                for (int i = 0; i < CompletionBuckets.Length; i++)
                {
                    if (i > 0) text.Append(' ');
                    text.Append(i < CompletionEdges.Length ? "<" + Pct(CompletionEdges[i]) : "done");
                    text.Append(':').Append(CompletionBuckets[i].ToString(CultureInfo.InvariantCulture));
                }
                text.Append("] samplesPerSwing~").Append(((float)(observationTotal / swingsCompleted)).ToString("0.0", CultureInfo.InvariantCulture));
                text.Append(" samples=[");
                for (int i = 0; i < ObservationBuckets.Length; i++)
                {
                    if (i > 0) text.Append(' ');
                    text.Append(i < ObservationEdges.Length
                        ? "<" + ObservationEdges[i].ToString(CultureInfo.InvariantCulture)
                        : "many");
                    text.Append(':').Append(ObservationBuckets[i].ToString(CultureInfo.InvariantCulture));
                }
                text.Append("] peakWasLastSample=").Append(endedAtLastSample.ToString(CultureInfo.InvariantCulture));
                text.Append(" replacedBy=[none:").Append(replacedByNone.ToString(CultureInfo.InvariantCulture));
                text.Append(" melee:").Append(replacedByMelee.ToString(CultureInfo.InvariantCulture));
                text.Append(" reaction:").Append(replacedByReaction.ToString(CultureInfo.InvariantCulture));
                text.Append(" other:").Append(replacedByOther.ToString(CultureInfo.InvariantCulture)).Append(']');
            }

            foreach (KeyValuePair<int, List<string>> entry in Narratives)
            {
                if (entry.Value.Count == 0) continue;
                text.Append(" || agent").Append(entry.Key.ToString(CultureInfo.InvariantCulture)).Append(": ");
                text.Append(string.Join(" ; ", entry.Value.ToArray()));
            }

            return text.ToString();
        }
    }

    private static string Percent(long part, long whole) =>
        whole == 0
            ? "0%"
            : (100d * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Pct(float value) =>
        (value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
}
