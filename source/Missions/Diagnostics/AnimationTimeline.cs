using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TaleWorlds.MountAndBlade;

namespace Missions.Diagnostics;

/// <summary>
/// A tick-by-tick record of the same agents' animation state, captured identically on every client, so the two
/// recordings can be laid side by side and diffed.
/// </summary>
/// <remarks>
/// <para>
/// Every aggregate counter in this investigation reports the remote path as correct - playback speed 1.0x,
/// progress advancing in step, actions applied on first sight, start offsets small, hit timing identical to the
/// local control - while the swings are plainly wrong on screen. Fifteen mechanisms were eliminated that way. If
/// the averages are right and the picture is wrong, the thing to capture is not another average.
/// </para>
/// <para>
/// So this records raw state per tick and answers a different question: not "is the mean correct" but "here is
/// what this exact agent's swing looked like on the machine that owns it, and here is what it looked like on the
/// machine that only receives it". A divergence shows up as two timelines that stop matching, at a readable
/// moment, with the values that differ.
/// </para>
/// <para>
/// Agents are keyed by their NETWORK id, because <c>Agent.Index</c> is local to each client and the same index is
/// a different soldier on each machine. Selection is by sorted GUID so that every client independently follows
/// the SAME agents without needing to agree on anything.
/// </para>
/// <para>
/// Bounded on purpose: a few agents, a few hundred samples each. An earlier trace held 20,000 events and could
/// not be retrieved from a running client at all - its snapshot exceeded the live-test message limit.
/// </para>
/// </remarks>
internal static class AnimationTimeline
{
    // Widened from 3. Picking three soldiers out of six hundred by sorted id meant whether the recording
    // caught any fighting at all was luck: one run captured no swings, another 17, another 194. Every
    // comparison drawn from those was worthless.
    private const int TrackedAgents = 28;

    /// <summary>
    /// Samples per agent, taken every <see cref="NarrativeEveryNTicks"/> ticks.
    /// </summary>
    /// <remarks>
    /// 16 agents at 200 samples sounded generous and was 3.3 SECONDS of recording at 60Hz - a slice far too
    /// narrow to be sure of catching anyone swinging, which is exactly what happened. Sampling at a quarter rate
    /// over four times the entries covers about half a minute instead, which is long enough that a melee cannot
    /// hide from it.
    /// </remarks>
    // ~53s of window. The two machines arm independently - each waits until IT has seen enough agents
    // swinging - so a short buffer leaves their windows barely overlapping and the comparison collapses
    // to "(no sample)": one run came back 80% unmatched. The window has to be long enough to absorb the
    // gap between the two arming moments. Kept under the 1MB live-test message cap: 28 x 800 dumps at
    // roughly 470KB.
    private const int SamplesPerAgent = 800;
    private const int NarrativeEveryNTicks = 4;

    /// <summary>Melee range plus a margin: what the player can actually see swinging at them.</summary>
    private const float NearRadius = 15f;

    /// <summary>
    /// Minimum pool to choose from, so both machines are looking at the same battle when they choose.
    /// </summary>
    /// <remarks>
    /// Selection is by sorted network id so that both machines independently follow the SAME soldiers, which only
    /// works once both registries hold the same agents. There is deliberately no timer: the armies have to WALK
    /// to each other first, so anything counted from battle start records the approach and closes before contact.
    /// Recording is armed by hand once the players are actually in melee.
    /// </remarks>
    private const int MinimumCandidatePool = 200;

    /// <summary>
    /// How many DISTINCT agents must have been seen swinging before recording starts.
    /// </summary>
    /// <remarks>
    /// Selecting by sorted id alone makes both machines follow the same soldiers, but says nothing about
    /// whether those soldiers ever fight - one capture followed 32 agents that stood idle for the whole
    /// window and produced no owner-versus-puppet comparison at all. Restricting the pool to agents
    /// OBSERVED SWINGING keeps the sorted-id agreement (a swing is visible on both machines) while
    /// guaranteeing the sample is of soldiers in melee, and it doubles as the arming signal: enough
    /// swingers means the armies have made contact.
    /// </remarks>
    private const int MinimumSwingPool = 48;

    /// <summary>Swinging agents each peer must own before the sample is taken, so both directions fill.</summary>
    private const int MinimumSwingersPerSide = 8;

    private static readonly HashSet<Guid> SwingObserved = new HashSet<Guid>();

    private static readonly object Gate = new object();
    private static readonly List<Guid> Tracked = new List<Guid>(TrackedAgents);
    private static readonly Dictionary<Guid, List<Sample>> Samples = new Dictionary<Guid, List<Sample>>();

    /// <summary>
    /// A census over EVERY agent, taken on both machines, so the comparison needs no per-agent matching.
    /// </summary>
    /// <remarks>
    /// The narrative timeline follows three agents chosen by sorted id. Out of roughly six hundred on the field,
    /// whether any of them happens to swing during the recording is luck - one run captured no swings at all, and
    /// the run-to-run figures (71%, 45%, 100%, 66%) swung far more than any fix could have moved them.
    /// <para>
    /// This instead asks each machine the same question about all of its agents: what fraction of the time is an
    /// agent showing a melee swing? On the host that fraction is the truth for its own soldiers; on the client it
    /// is what those same soldiers look like as puppets. Comparing the two needs no correlation and no luck.
    /// </para>
    /// </remarks>
    private const int CensusEveryNTicks = 6;

    private static long censusSamples;
    private static long localAgentTicks;
    private static long localSwingTicks;
    private static long remoteAgentTicks;
    private static long remoteSwingTicks;

    /// <summary>
    /// The same counts restricted to agents within melee distance of the player.
    /// </summary>
    /// <remarks>
    /// The all-agent census counts hundreds of soldiers scattered across the field, and a client may legitimately
    /// not animate distant puppets at all. So part of the 60% gap it reported may be ordinary level-of-detail
    /// behaviour rather than a defect - which would make the whole baseline meaningless. These counters answer the
    /// question that actually matters: of the soldiers the player can see, how many are visibly swinging.
    /// </remarks>
    private static long localNearTicks;
    private static long localNearSwingTicks;
    private static long remoteNearTicks;
    private static long remoteNearSwingTicks;

    private static bool enabled;
    private static long ticks;

    public static bool Enabled => enabled;

    private struct Sample
    {
        public long Milliseconds;
        public int ActionIndex;
        public int ActionType;
        public short ProgressPercent;
        public short SpeedPercent;
        public bool Local;
        public short DistanceToPlayer;

        // Position in DECIMETRES, so a whole-metre error is 10 counts and the dump stays compact.
        // Both machines sample the same agent on a shared clock, so subtracting one from the other
        // gives exactly how far a puppet is DRAWN from where its owner actually has it - which is what
        // "the enemy hit me from further away than his weapon reaches" would look like.
        public int PosXdm;
        public int PosYdm;

        // The MOUNT's own animation, sampled alongside its rider. Mounts carry no registry id of their
        // own - the presentation code resolves them through the rider - so this is the only way to see
        // whether a horse's legs are animating on one machine and not the other. A horse moved to a
        // position without locomotion slides with its legs still: the ice-skating complaint.
        public int MountActionType;
        public short MountProgress;
        public short MountSpeedPercent;
    }

    public static void Start()
    {
        lock (Gate)
        {
            Tracked.Clear();
            Samples.Clear();
            SwingObserved.Clear();
            ticks = 0;
            censusSamples = 0;
            localAgentTicks = 0;
            localSwingTicks = 0;
            remoteAgentTicks = 0;
            remoteSwingTicks = 0;
            localNearTicks = 0;
            localNearSwingTicks = 0;
            remoteNearTicks = 0;
            remoteNearSwingTicks = 0;
            enabled = true;
        }
    }

    /// <summary>Called once per mission tick. Cheap and bounded; stops recording once every buffer is full.</summary>
    public static void Tick(INetworkAgentRegistry registry)
    {
        if (!enabled || registry == null) return;

        lock (Gate)
        {
            ticks++;

            if (ticks % CensusEveryNTicks == 0) Census(registry);

            if (Tracked.Count == 0 && !SelectAgents(registry)) return;

            if (ticks % NarrativeEveryNTicks != 0) return;

            bool anyRoom = false;
            long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

            for (int i = 0; i < Tracked.Count; i++)
            {
                Guid id = Tracked[i];
                if (!Samples.TryGetValue(id, out List<Sample> log)) continue;
                if (log.Count >= SamplesPerAgent) continue;

                anyRoom = true;
                if (!registry.TryGetAgentInfo(id, out CoopAgentInfo info) || info.Agent == null) continue;

                Agent agent = info.Agent;
                try
                {
                    if (agent.Mission != Mission.Current || !agent.IsActive()) continue;

                    Agent viewer = Mission.Current?.MainAgent;
                    short distance = -1;
                    if (viewer != null && viewer != agent)
                    {
                        float d = agent.Position.Distance(viewer.Position);
                        distance = float.IsNaN(d) || d > 30000f ? (short)-1 : (short)d;
                    }

                    bool readable = AgentActionDataSpeedProbe(agent, out float speed);
                    log.Add(new Sample
                    {
                        Milliseconds = now,
                        ActionIndex = agent.GetCurrentAction(1).Index,
                        ActionType = (int)agent.GetCurrentActionType(1),
                        ProgressPercent = ToPercent(agent.GetCurrentActionProgress(1)),
                        SpeedPercent = readable ? ToPercent(speed) : (short)-1,
                        Local = registry.IsLocallyControlled(agent),
                        DistanceToPlayer = distance,
                        PosXdm = ToDecimetres(agent.Position.x),
                        PosYdm = ToDecimetres(agent.Position.y),
                        MountActionType = MountActionTypeOf(agent),
                        MountProgress = MountProgressOf(agent),
                        MountSpeedPercent = MountSpeedOf(agent),
                    });
                }
                catch (Exception)
                {
                    // A diagnostic must never take a battle down.
                }
            }


        }
    }

    /// <summary>Counts how many agents are showing a melee swing this instant, split local versus puppet.</summary>
    private static void Census(INetworkAgentRegistry registry)
    {
        censusSamples++;
        Agent player = Mission.Current?.MainAgent;
        foreach (string controllerId in registry.GetControllerIds())
        {
            foreach (CoopAgentInfo info in registry.GetAgents(controllerId))
            {
                Agent agent = info?.Agent;
                if (agent == null) continue;
                try
                {
                    if (agent.Mission != Mission.Current || !agent.IsActive()) continue;

                    bool swinging =
                        Missions.Agents.Packets.AgentActionData.IsMeleeSwingType(agent.GetCurrentActionType(1));
                    // Selection must also notice ARCHERS, or a ranged battle is never sampled: the pool
                    // fills only with melee fighters and the ranged comparison reports nothing at all.
                    Agent.ActionCodeType upper = agent.GetCurrentActionType(1);
                    bool attacking = swinging
                        || upper == Agent.ActionCodeType.ReadyRanged
                        || upper == Agent.ActionCodeType.ReleaseRanged
                        || upper == Agent.ActionCodeType.ReleaseThrowing
                        || upper == Agent.ActionCodeType.Reload;
                    if (attacking) SwingObserved.Add(info.AgentId);
                    bool near = player != null
                        && player != agent
                        && agent.Position.Distance(player.Position) <= NearRadius;

                    if (registry.IsLocallyControlled(agent))
                    {
                        localAgentTicks++;
                        if (swinging) localSwingTicks++;
                        if (near)
                        {
                            localNearTicks++;
                            if (swinging) localNearSwingTicks++;
                        }
                    }
                    else
                    {
                        remoteAgentTicks++;
                        if (swinging) remoteSwingTicks++;
                        if (near)
                        {
                            remoteNearTicks++;
                            if (swinging) remoteNearSwingTicks++;
                        }
                    }
                }
                catch (Exception)
                {
                    // A diagnostic must never take a battle down.
                }
            }
        }
    }

    private static bool AgentActionDataSpeedProbe(Agent agent, out float speed) =>
        Missions.Agents.Packets.AgentActionData.TryGetCurrentActionSpeed(agent, 1, out speed);

    /// <summary>
    /// Picks the same agents on every client without coordination: lowest network ids among agents currently in
    /// the mission. Both machines see the same id set, so both follow the same soldiers.
    /// </summary>
    private static bool SelectAgents(INetworkAgentRegistry registry)
    {
        // A set, not a list: the grouping pass below tests membership once per agent per controller and
        // re-runs every tick until both peers have soldiers fighting. With ~600 agents on the field a
        // linear scan there is ~360k comparisons per tick at 60Hz, which costs frames in the battle this
        // is supposed to be measuring.
        var candidates = new HashSet<Guid>();
        foreach (string controllerId in registry.GetControllerIds())
        {
            foreach (CoopAgentInfo info in registry.GetAgents(controllerId))
            {
                if (info?.Agent == null) continue;
                try
                {
                    if (info.Agent.Mission != Mission.Current || !info.Agent.IsActive()) continue;
                }
                catch (Exception) { continue; }

                candidates.Add(info.AgentId);
            }
        }

        // Wait for a pool big enough that both machines are looking at the same battle.
        if (candidates.Count < MinimumCandidatePool) return false;

        // ...and for enough of them to be FIGHTING, or the window records an idle crowd.
        if (SwingObserved.Count < MinimumSwingPool) return false;

        // Take an EVEN share from each controller. Selecting by sorted id alone tends to land on agents
        // that all belong to one peer, and then only one direction of the comparison has any data: every
        // tracked agent is LOCAL here and PUPPET there, so the reverse direction reports nothing at all.
        // Grouping by authority and interleaving keeps both directions populated, and stays deterministic
        // - both machines sort the same controller ids and the same agent ids - so they still follow the
        // same soldiers.
        var byController = new SortedDictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (string controllerId in registry.GetControllerIds())
        {
            var owned = new List<Guid>();
            foreach (CoopAgentInfo info in registry.GetAgents(controllerId))
            {
                if (info == null || !SwingObserved.Contains(info.AgentId)) continue;
                if (!candidates.Contains(info.AgentId)) continue;
                owned.Add(info.AgentId);
            }
            if (owned.Count == 0) continue;
            owned.Sort();
            byController[controllerId] = owned;
        }
        // Both peers must have soldiers in the fight before choosing, or the whole sample comes from
        // whichever side made contact first: one run tracked 28 agents that were LOCAL on one machine and
        // PUPPET on the other WITHOUT EXCEPTION, so the reverse direction had nothing to compare and the
        // host-side control could not be reported at all.
        int sidesFighting = 0;
        foreach (List<Guid> owned in byController.Values)
        {
            if (owned.Count >= MinimumSwingersPerSide) sidesFighting++;
        }
        if (sidesFighting < 2) return false;

        for (int round = 0; Tracked.Count < TrackedAgents; round++)
        {
            bool tookAny = false;
            foreach (List<Guid> owned in byController.Values)
            {
                if (round >= owned.Count) continue;
                if (Tracked.Count >= TrackedAgents) break;
                Tracked.Add(owned[round]);
                Samples[owned[round]] = new List<Sample>(SamplesPerAgent);
                tookAny = true;
            }
            if (!tookAny) break;
        }
        return Tracked.Count > 0;
    }

    /// <summary>-1 when the agent has no mount, so "no horse" is never confused with "horse idle".</summary>
    private static int MountActionTypeOf(Agent agent)
    {
        try
        {
            Agent mount = agent.MountAgent;
            if (mount == null || !mount.IsActive()) return -1;
            return (int)mount.GetCurrentActionType(0);
        }
        catch (Exception) { return -1; }
    }

    private static short MountProgressOf(Agent agent)
    {
        try
        {
            Agent mount = agent.MountAgent;
            if (mount == null || !mount.IsActive()) return -1;
            return ToPercent(mount.GetCurrentActionProgress(0));
        }
        catch (Exception) { return -1; }
    }

    /// <summary>Movement speed in decimetres/second: a horse that SLIDES moves while its legs do not.</summary>
    private static short MountSpeedOf(Agent agent)
    {
        try
        {
            Agent mount = agent.MountAgent;
            if (mount == null || !mount.IsActive()) return -1;
            float speed = mount.Velocity.Length * 10f;
            if (float.IsNaN(speed) || speed > short.MaxValue) return -1;
            return (short)speed;
        }
        catch (Exception) { return -1; }
    }

    private static int ToDecimetres(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return int.MinValue;
        float scaled = value * 10f;
        if (scaled > 2000000f || scaled < -2000000f) return int.MinValue;
        return (int)scaled;
    }

    private static short ToPercent(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return -1;
        float scaled = value * 100f;
        if (scaled < -1f) return -1;
        if (scaled > 32000f) return 32000;
        return (short)scaled;
    }

    private static string Share(long part, long whole) =>
        whole == 0
            ? "n/a"
            : (100d * part / whole).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    public static string Snapshot(bool stop)
    {
        lock (Gate)
        {
            if (stop) enabled = false;
            if (Tracked.Count == 0 && censusSamples == 0) return "timeline: nothing recorded yet";

            var text = new StringBuilder();
            text.Append("timeline: ticks=").Append(ticks.ToString(CultureInfo.InvariantCulture));
            if (censusSamples > 0)
            {
                text.Append(" || CENSUS samples=").Append(censusSamples.ToString(CultureInfo.InvariantCulture));
                text.Append(" localAgentTicks=").Append(localAgentTicks.ToString(CultureInfo.InvariantCulture));
                text.Append(" localSwinging=").Append(Share(localSwingTicks, localAgentTicks));
                text.Append(" remoteAgentTicks=").Append(remoteAgentTicks.ToString(CultureInfo.InvariantCulture));
                text.Append(" remoteSwinging=").Append(Share(remoteSwingTicks, remoteAgentTicks));
                text.Append(" || NEAR localTicks=").Append(localNearTicks.ToString(CultureInfo.InvariantCulture));
                text.Append(" localSwinging=").Append(Share(localNearSwingTicks, localNearTicks));
                text.Append(" remoteTicks=").Append(remoteNearTicks.ToString(CultureInfo.InvariantCulture));
                text.Append(" remoteSwinging=").Append(Share(remoteNearSwingTicks, remoteNearTicks));
            }

            foreach (Guid id in Tracked)
            {
                if (!Samples.TryGetValue(id, out List<Sample> log) || log.Count == 0) continue;

                // First 8 hex chars identify the agent; the same id appears on both clients.
                text.Append(" || ").Append(id.ToString("N").Substring(0, 8));
                text.Append(' ').Append(log[0].Local ? "LOCAL" : "PUPPET");
                text.Append(" n=").Append(log.Count.ToString(CultureInfo.InvariantCulture));
                text.Append(" t0=").Append(log[0].Milliseconds.ToString(CultureInfo.InvariantCulture));
                text.Append(" [");

                long start = log[0].Milliseconds;
                for (int i = 0; i < log.Count; i++)
                {
                    if (i > 0) text.Append(' ');
                    Sample sample = log[i];
                    text.Append((sample.Milliseconds - start).ToString(CultureInfo.InvariantCulture));
                    text.Append(':').Append(sample.ActionIndex.ToString(CultureInfo.InvariantCulture));
                    text.Append('/').Append(sample.ActionType.ToString(CultureInfo.InvariantCulture));
                    text.Append('@').Append(sample.ProgressPercent.ToString(CultureInfo.InvariantCulture));
                    text.Append('x').Append(sample.SpeedPercent.ToString(CultureInfo.InvariantCulture));
                    text.Append('d').Append(sample.DistanceToPlayer.ToString(CultureInfo.InvariantCulture));
                    text.Append('p').Append(sample.PosXdm.ToString(CultureInfo.InvariantCulture));
                    text.Append(',').Append(sample.PosYdm.ToString(CultureInfo.InvariantCulture));
                    text.Append('m').Append(sample.MountActionType.ToString(CultureInfo.InvariantCulture));
                    text.Append('/').Append(sample.MountProgress.ToString(CultureInfo.InvariantCulture));
                    text.Append('/').Append(sample.MountSpeedPercent.ToString(CultureInfo.InvariantCulture));
                }
                text.Append(']');
            }

            return text.ToString();
        }
    }
}
