using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Diagnostics;

/// <summary>
/// Measures how much of a remote attack's wind-up never gets rendered, and why.
/// </summary>
/// <remarks>
/// <para>
/// A remote attack is applied with <c>startProgress</c> set to the attacker's animation progress at the moment the
/// packet was sent. Whatever fraction that is, the local puppet never plays it - the swing appears already in
/// motion. The wind-up is the part a defender reads to time a block, so losing it is what makes an attack feel
/// like it came out of nowhere.
/// </para>
/// <para>
/// Measured live at zero latency, the skip turned out BIMODAL: one cluster under 2.5% and a larger one above 50%,
/// with almost nothing between 10% and 35%. Transit delay cannot produce that shape, so the cause is not the
/// network. Measurement also cleared the two obvious suspects: the sender reports an attack on the very tick it
/// starts, and neither transition-suppression predicate can block an attack packet - both require
/// <c>GuardActionIsDefending</c>, so they only ever suppress defend packets.
/// </para>
/// <para>
/// Three explanations remain, and they want DIFFERENT fixes, so every sample is classified and each population
/// keeps its own distribution. Whichever one owns the deep-skip cluster names the bug:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Spawn catch-up</b> - the puppet was created while its source agent was already mid-swing. A deep progress
/// here is CORRECT, not a defect, and clamping it would be wrong: it would rewind a swing the agent never had.
/// </description></item>
/// <item><description>
/// <b>Late first apply</b> - an established puppet gets the swing long after it began. Clamping
/// <c>startProgress</c> is the right fix here.
/// </description></item>
/// <item><description>
/// <b>Re-entry looping</b> - the puppet starts near the end, finishes almost immediately, drops to
/// <c>act_none</c>, and the next packet re-enters the SAME swing even later. Clamping would make this visibly
/// WORSE: every re-entry would restart the animation, so the swing would stutter and repeat instead of being fast.
/// </description></item>
/// </list>
/// <para>
/// A measured run put 93% of deep skips in the established-puppet population rather than re-entry, which is what
/// ruled the stutter risk out. The spawn split exists to separate correct catch-up from genuine lateness.
/// </para>
/// <para>
/// Everything is a FRACTION of the animation, never milliseconds. The millisecond version called
/// <c>MBActionSet.GetActionAnimationDuration(agent.ActionSet, action)</c> and killed a live client with an
/// <c>AccessViolationException</c> that its try/catch could not intercept - a native access violation is not a
/// managed exception. It had also been returning nonsense first, reporting 1,100 ms for <c>act_none</c>. Fractions
/// are the right unit anyway: the candidate fix clamps <c>startProgress</c>, which is itself a fraction.
/// </para>
/// <para>
/// Only calls already proven on this path are used - <c>AgentActionData</c> itself calls
/// <see cref="Agent.GetCurrentActionType"/> and <see cref="Agent.GetCurrentAction"/> on every apply. Nothing here
/// touches <c>agent.ActionSet</c>, which no other code on the apply path goes near.
/// </para>
/// </remarks>
internal static class WindupDiagnostics
{
    /// <summary>Upper edge of each bucket, as a fraction of the animation. The last bucket catches the rest.</summary>
    private static readonly float[] BucketEdges = { 0.01f, 0.025f, 0.05f, 0.10f, 0.20f, 0.35f, 0.50f };

    private const int WorstSampleCount = 6;

    /// <summary>Below this the skip is a rounding artefact rather than a lost wind-up.</summary>
    private const float TrivialProgress = 0.01f;

    /// <summary>Keeps the per-agent map from growing without bound if agents churn for a long battle.</summary>
    private const int AgentMapLimit = 8192;

    /// <summary>How this attack replaced whatever the puppet was doing.</summary>
    internal enum EntryKind
    {
        /// <summary>
        /// The very first action ever applied to this puppet. Its source agent may already have been mid-swing
        /// when the puppet was created, so a deep progress here is CORRECT catch-up, not a defect - a spawning
        /// agent should adopt the attacker's current pose rather than rewind to a wind-up it never had.
        /// </summary>
        Spawn = 0,

        /// <summary>An established puppet was playing something else. A genuine first entry into this swing.</summary>
        FirstEntry = 1,

        /// <summary>
        /// An established puppet had fallen out to <c>act_none</c> and this is the SAME attack it was last given -
        /// it is re-entering a swing it already played part of.
        /// </summary>
        ReEntry = 2,
    }

    private static readonly object Gate = new object();
    /// <summary>Channels an action packet drives. Melee attacks run on the upper-body channel.</summary>
    private const int Channels = 2;

    /// <summary>Populations are (kind, channel) pairs, indexed by <c>kind * Channels + channel</c>.</summary>
    private static readonly Population[] Populations =
    {
        new Population("spawn.ch0"), new Population("spawn.ch1"),
        new Population("first.ch0"), new Population("first.ch1"),
        new Population("re-entry.ch0"), new Population("re-entry.ch1"),
    };

    /// <summary>
    /// Agent indices this measurement has applied ANY action to. Bannerlord reuses agent indices once an agent is
    /// released, so a reused index reads as already-seen - which UNDERCOUNTS spawns. That bias is deliberate: it
    /// works against the spawn hypothesis, so a large spawn population is evidence rather than an artefact.
    /// </summary>
    private static readonly HashSet<int> SeenAgents = new HashSet<int>();

    /// <summary>Last attack action index applied to each agent, keyed by agent index so no Agent is retained.</summary>
    private static readonly Dictionary<int, int> LastAttackByAgent = new Dictionary<int, int>();

    private static bool enabled;
    private static long otherTransitions;
    private static long rejected;
    private static long previousWasNone;

    public static bool Enabled => enabled;

    private sealed class Population
    {
        public Population(string name) { Name = name; }

        public readonly string Name;
        public readonly int[] Buckets = new int[BucketEdges.Length + 1];
        public readonly List<float> Worst = new List<float>();
        public long Attacks;
        public long WithSkip;
        public double SkippedTotal;
        public float SkippedMax;

        public void Clear()
        {
            Array.Clear(Buckets, 0, Buckets.Length);
            Worst.Clear();
            Attacks = 0;
            WithSkip = 0;
            SkippedTotal = 0d;
            SkippedMax = 0f;
        }
    }

    public static void Start()
    {
        lock (Gate)
        {
            foreach (Population population in Populations) population.Clear();
            LastAttackByAgent.Clear();
            SeenAgents.Clear();
            otherTransitions = 0;
            rejected = 0;
            previousWasNone = 0;
            enabled = true;
        }
    }

    /// <summary>
    /// Records a freshly applied remote action. Call AFTER <c>SetActionChannel</c> so the agent reports the action
    /// just started, passing the action index read BEFORE the set as <paramref name="previousActionIndex"/>.
    /// </summary>
    public static void RecordAppliedAction(
        Agent agent,
        int channel,
        ActionIndexCache action,
        float startProgress,
        int previousActionIndex)
    {
        if (!enabled || agent == null) return;

        try
        {
            // Marked for every apply, not just attacks: a puppet that was handed an idle or a walk before its
            // first swing is already established, so that swing is not a spawn.
            bool firstEverForAgent;
            lock (Gate)
            {
                if (SeenAgents.Count >= AgentMapLimit) SeenAgents.Clear();
                firstEverForAgent = SeenAgents.Add(agent.Index);
            }

            int noneIndex = ActionIndexCache.act_none.Index;

            // act_none is the ABSENCE of an action. An earlier version counted it as an attack and it then
            // dominated the worst list, which is what exposed that measurement as broken.
            if (action.Index == noneIndex)
            {
                lock (Gate) otherTransitions++;
                return;
            }

            // MELEE SWINGS ONLY. AttackMeleeAndRangedAllBegin..End spans ReadyRanged(15)..Fall(23), which
            // includes ParriedMelee(21) and BlockedMelee(22) - parry and block REACTIONS - plus Reload(18) and
            // ReadyRanged(15), a drawn bow that sits deep in its animation indefinitely and entirely normally.
            // Counting those as attacks manufactured skipped wind-ups that were never real.
            Agent.ActionCodeType actionType = agent.GetCurrentActionType(channel);
            bool isAttack =
                actionType == Agent.ActionCodeType.ReadyMelee
                || actionType == Agent.ActionCodeType.ReleaseMelee;

            if (!isAttack)
            {
                lock (Gate) otherTransitions++;
                return;
            }

            bool cameFromNone = previousActionIndex == noneIndex;
            EntryKind kind = ClassifyEntry(agent.Index, channel, action.Index, cameFromNone, firstEverForAgent);
            RecordMeasuredAttack(startProgress, kind, channel, cameFromNone);
        }
        catch (Exception)
        {
            // A diagnostic must never take a battle down; losing a sample is the acceptable outcome. Note this
            // cannot catch a native access violation - which is why no native duration lookup happens here.
            lock (Gate) rejected++;
        }
    }

    /// <summary>
    /// A re-entry is the puppet dropping out of a swing and being handed the SAME swing again. Requires both
    /// halves: it fell out to <c>act_none</c>, AND the attack it is being given is the one it last had.
    /// </summary>
    private static EntryKind ClassifyEntry(
        int agentIndex,
        int channel,
        int actionIndex,
        bool cameFromNone,
        bool firstEverForAgent)
    {
        lock (Gate)
        {
            // Keyed by agent AND channel. Keying on the agent alone let channel 0 and channel 1 overwrite each
            // other's last attack, which made `repeated` - and therefore the whole re-entry figure - unreliable.
            int key = agentIndex * Channels + channel;
            bool repeated =
                LastAttackByAgent.TryGetValue(key, out int previousAttack)
                && previousAttack == actionIndex;

            if (LastAttackByAgent.Count >= AgentMapLimit && !LastAttackByAgent.ContainsKey(key))
                LastAttackByAgent.Clear();

            LastAttackByAgent[key] = actionIndex;

            // Spawn wins: a puppet seeing its first action ever cannot meaningfully be "re-entering" anything.
            if (firstEverForAgent) return EntryKind.Spawn;

            // `repeated` alone decides re-entry. An earlier version also demanded the puppet be sitting in
            // act_none, which was wrong: in a melee a puppet is constantly interrupted by hit and parry
            // reactions, so when the attacker's swing is re-asserted it is usually playing some OTHER action.
            // Those are re-entries into a swing already seen, and that gate filed them all as first entries -
            // understating exactly the population that decides whether replaying a swing is safe.
            return repeated ? EntryKind.ReEntry : EntryKind.FirstEntry;
        }
    }

    /// <summary>
    /// The measurement itself, classification already done. Split out so the arithmetic that decides the fix can
    /// be tested without an engine.
    /// </summary>
    internal static void RecordMeasuredAttack(
        float startProgress,
        EntryKind kind,
        int channel = 0,
        bool cameFromNone = false)
    {
        if (!enabled) return;
        if (channel < 0 || channel >= Channels) channel = 0;

        Population population = Populations[(int)kind * Channels + channel];

        // Progress arrives off the wire, so treat it as untrusted rather than assuming a sane 0..1.
        if (float.IsNaN(startProgress) || float.IsInfinity(startProgress))
        {
            lock (Gate)
            {
                population.Attacks++;
                rejected++;
                if (cameFromNone) previousWasNone++;
            }
            return;
        }

        float progress = startProgress < 0f ? 0f : startProgress > 1f ? 1f : startProgress;

        lock (Gate)
        {
            population.Attacks++;
            if (cameFromNone) previousWasNone++;
            if (progress <= TrivialProgress) return;

            population.WithSkip++;
            population.SkippedTotal += progress;
            if (progress > population.SkippedMax) population.SkippedMax = progress;
            population.Buckets[BucketFor(progress)]++;
            RecordWorst(population, progress);
        }
    }

    private static int BucketFor(float progress)
    {
        for (int i = 0; i < BucketEdges.Length; i++)
            if (progress < BucketEdges[i]) return i;

        return BucketEdges.Length;
    }

    private static void RecordWorst(Population population, float progress)
    {
        List<float> worst = population.Worst;
        if (worst.Count == WorstSampleCount && progress <= worst[worst.Count - 1]) return;

        int at = worst.Count;
        for (int i = 0; i < worst.Count; i++)
        {
            if (progress > worst[i]) { at = i; break; }
        }

        worst.Insert(at, progress);
        if (worst.Count > WorstSampleCount) worst.RemoveAt(worst.Count - 1);
    }

    /// <summary>Renders the aggregates. Kept compact so it survives the live-test message limit.</summary>
    public static string Snapshot(bool stop)
    {
        lock (Gate)
        {
            if (stop) enabled = false;

            long total = 0;
            foreach (Population counted in Populations) total += counted.Attacks;
            if (total == 0)
            {
                return "windup: no remote attack transitions observed (otherTransitions="
                    + otherTransitions.ToString(CultureInfo.InvariantCulture) + ")";
            }

            var text = new StringBuilder();
            text.Append("windup: attacks=").Append(total.ToString(CultureInfo.InvariantCulture));
            text.Append(" cameFromNone=").Append(previousWasNone.ToString(CultureInfo.InvariantCulture));
            text.Append(" rejected=").Append(rejected.ToString(CultureInfo.InvariantCulture));
            text.Append(" otherTransitions=").Append(otherTransitions.ToString(CultureInfo.InvariantCulture));

            foreach (Population population in Populations)
            {
                if (population.Attacks == 0) continue;

                text.Append(" | ").Append(population.Name).Append('=');
                text.Append(population.Attacks.ToString(CultureInfo.InvariantCulture));

                text.Append(" withSkip=").Append(population.WithSkip.ToString(CultureInfo.InvariantCulture));
                text.Append('(').Append(Percent(population.WithSkip, population.Attacks)).Append(')');

                if (population.WithSkip > 0)
                {
                    text.Append(" mean=")
                        .Append(Fraction((float)(population.SkippedTotal / population.WithSkip)));
                    text.Append(" max=").Append(Fraction(population.SkippedMax));
                    text.Append(" p50~").Append(Fraction(Percentile(population, 0.50)));
                    text.Append(" p90~").Append(Fraction(Percentile(population, 0.90)));
                }

                text.Append(" buckets=[");
                for (int i = 0; i < population.Buckets.Length; i++)
                {
                    if (i > 0) text.Append(' ');
                    text.Append(i < BucketEdges.Length
                        ? "<" + Fraction(BucketEdges[i])
                        : ">=" + Fraction(BucketEdges[BucketEdges.Length - 1]));
                    text.Append(':').Append(population.Buckets[i].ToString(CultureInfo.InvariantCulture));
                }
                text.Append(']');
            }

            return text.ToString();
        }
    }

    /// <summary>Bucket-interpolated percentile. Approximate by construction - the edges are coarse.</summary>
    private static float Percentile(Population population, double fraction)
    {
        long target = (long)Math.Ceiling(population.WithSkip * fraction);
        if (target < 1) target = 1;

        long running = 0;
        for (int i = 0; i < population.Buckets.Length; i++)
        {
            running += population.Buckets[i];
            if (running >= target)
                return i < BucketEdges.Length ? BucketEdges[i] : population.SkippedMax;
        }

        return population.SkippedMax;
    }

    private static string Fraction(float value) =>
        (value * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Percent(long part, long whole) =>
        whole == 0
            ? "0%"
            : (100d * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
