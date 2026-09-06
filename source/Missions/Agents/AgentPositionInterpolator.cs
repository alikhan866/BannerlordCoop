using Common.Logging;
using Missions.Agents.Packets;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents;

public interface IAgentPositionInterpolator
{
    /// <summary>Record the latest continuous frame the owner reported for a rider puppet.</summary>
    void SetRiderTarget(Agent agent, AgentData data);

    /// <summary>Record the latest continuous frame the owner reported for a mounted rider puppet.</summary>
    void SetMountedRiderTarget(Agent agent, AgentData data, float oneWayLatencySeconds = 0f);

    /// <summary>Record the latest continuous frame the owner reported for a mount puppet.</summary>
    void SetMountTarget(Agent mountAgent, AgentMountData data);

    /// <summary>Stop tracking an agent (e.g. it dismounted or was removed).</summary>
    void Forget(Agent agent);

    /// <summary>Read the latest owner-reported locomotion flags retained for a puppet.</summary>
    bool TryGetTargetMovementFlags(
        Agent agent,
        out uint riderMovementFlags,
        out uint mountMovementFlags);

    /// <summary>Read the latest owner-reported position, look, and local receive sequence.</summary>
    bool TryGetTargetFrame(
        Agent agent,
        out Vec3 position,
        out Vec3 lookDirection,
        out long updateSequence);
    /// <summary>[Game thread] Apply each tracked agent's latest native target frame.</summary>
    void Tick(float dt);

    /// <summary>[Game thread] Restore received look directions at a native Agent cycle boundary.</summary>
    void ReplayLookDirections();

    /// <summary>Drop all tracked targets (mission end).</summary>
    void Clear();
}

/// <summary>
/// [Game thread] Drives received puppets toward the position their owner last reported. On-foot puppets use the
/// engine's native target-frame path; mounted puppets are eased directly onto the owner's reported mount position.
/// A visible guard may snap meaningful drift once per received owner frame, but never chases the same stale frame.
/// Teleport handles large spawn/desync gaps.
/// <para>
/// All access is on the game thread — packet applies run inside <c>AgentMovementHandler</c>'s
/// <c>GameThread.RunSafe</c> and <see cref="Tick"/> runs in <c>OnMissionTick</c>, both serialized on the game
/// loop — so no locking is needed.
/// </para>
/// </summary>
public class AgentPositionInterpolator : IAgentPositionInterpolator
{
    private static readonly ILogger Logger = LogManager.GetLogger<AgentPositionInterpolator>();

    // Snap only when the replicated owner is far enough away that local locomotion has clearly diverged.
    private const float RiderSnapDistance = 6f;
    private const float MountSnapDistance = 12f;
    private const float StaleTargetSeconds = 1f;
    // Exponential ease rate for the mounted-puppet position follow: fraction MountedFollowRate*dt of the gap is
    // closed each frame, so it tracks the owner with a small lag and settles when the owner stops.
    private const float MountedFollowRate = 12f;
    /// <summary>
    /// Raising this to let mounted puppets carry small drift under their own locomotion was TRIED AND
    /// REVERTED. The theory was sound - TeleportToPosition moves an agent without locomotion, so the
    /// engine sees a horse translating while standing still and plays the standing gait, which is the
    /// sliding - but at 0.35m the position error more than doubled at the median (p50 0.14m -> 0.32m,
    /// max 1.53m -> 2.05m) and the sliding did not improve (client puppets 7.6% -> 8.9%). The horse
    /// needs the per-tick correction; skipping it just lets it drift.
    /// </summary>
    private const float MountedPositionEpsilon = 0.0001f;
    private const float MountedGuardPositionTolerance = 0.15f;
    private readonly Dictionary<Agent, TargetFrame> _targets = new Dictionary<Agent, TargetFrame>();
    private readonly Dictionary<Agent, long> _mountedGuardProcessedSequences =
        new Dictionary<Agent, long>();
    private readonly INetworkAgentRegistry agentRegistry;
    // Reused scratch list so eviction doesn't allocate every tick.
    private readonly List<Agent> _evict = new List<Agent>();
    private float elapsed;
    private long updateSequence;

    public AgentPositionInterpolator() : this(null) { }

    internal AgentPositionInterpolator(INetworkAgentRegistry agentRegistry)
    {
        this.agentRegistry = agentRegistry;
    }

    /// <summary>
    /// Keeps the last trusted position when a packet reports it cannot represent one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A movement packet carries position packed into 63 bits with a reserved bit meaning 'unusable'. The
    /// sender sets it when a position is non-finite or beyond about 10 km from the scene origin - neither
    /// of which should ever happen in a real mission.
    /// </para>
    /// <para>
    /// If one does, the worst thing this class could do is believe it. AgentData.Position reads Vec3.Zero
    /// in that case, and zero is a REAL place: the scene origin. Feeding it as a target would walk the agent
    /// to the middle of the map, or - if it is far enough away - trip the snap distance below and teleport it
    /// there outright. Holding the previous target instead leaves the agent moving as it was, and the stale
    /// target logging further down reports it if the condition persists.
    /// </para>
    /// </remarks>
    /// <summary>The position and time of the frame BEFORE the current one, per agent.</summary>
    /// <remarks>
    /// Two consecutive frames give the owner's velocity, which is what lets a puppet be aimed at where its
    /// owner IS rather than where it was. Without it a puppet is told to walk to a stale position and, while
    /// the owner keeps moving, never arrives: measured across 20,311 samples the drawn position trailed the
    /// owner's by a mean of 0.41m, 1.08m at p90 and up to 3.33m. Melee reach is about 2m, so a legitimate
    /// blow could appear to land from over three metres away.
    /// </remarks>
    private readonly Dictionary<Agent, PreviousFrame> _previousTargets = new Dictionary<Agent, PreviousFrame>();

    private readonly struct PreviousFrame
    {
        public PreviousFrame(Vec3 position, float updatedAt)
        {
            Position = position;
            UpdatedAt = updatedAt;
        }

        public Vec3 Position { get; }
        public float UpdatedAt { get; }
    }

    private void StoreTarget(Agent agent, TargetFrame frame)
    {
        if (agent == null) return;
        if (_targets.TryGetValue(agent, out TargetFrame previous))
            _previousTargets[agent] = new PreviousFrame(previous.Position, previous.UpdatedAt);
        _targets[agent] = frame;
    }
    private bool TryResolveTargetPosition(Agent agent, bool hasPosition, Vec3 reported, out Vec3 position)
    {
        if (hasPosition)
        {
            position = reported;
            return true;
        }

        unusablePositionReports++;

        // Keep walking toward the last position we had reason to trust.
        if (agent != null && _targets.TryGetValue(agent, out TargetFrame existing))
        {
            position = existing.Position;
            return true;
        }

        // Nothing trustworthy has ever arrived for this agent, so there is nothing to hold on to. Refusing
        // the update leaves the agent where the engine already has it, which is the only safe answer.
        position = Vec3.Zero;
        return false;
    }

    /// <summary>How many packets arrived without a usable position. Reported by the battle census.</summary>
    /// <remarks>
    /// Non-zero means the coordinate range assumption is wrong, or something upstream is producing
    /// non-finite positions. Either is worth knowing from a log line rather than from a player noticing.
    /// </remarks>
    public long UnusablePositionReports => unusablePositionReports;

    private long unusablePositionReports;
    public void SetRiderTarget(Agent agent, AgentData data)
    {
        if (agent == null) return;
        if (!TryResolveTargetPosition(agent, data.HasPosition, data.Position, out Vec3 riderPosition))
            return;

        StoreTarget(agent, new TargetFrame(
            riderPosition,
            new ContinuousState(
                data.MovementDirection,
                data.LookDirection,
                data.GetMovementInput(agent),
                data.MovementFlag),
            hasMountSnapPosition: false,
            Vec3.Zero,
            default,
            updatedAt: elapsed,
            updateSequence: GetNextUpdateSequence()));
    }

    public void SetMountedRiderTarget(Agent agent, AgentData data, float oneWayLatencySeconds = 0f)
    {
        if (agent == null || data.MountData == null) return;
        if (!TryResolveTargetPosition(agent, data.HasPosition, data.Position, out Vec3 mountedPosition))
            return;

        StoreTarget(agent, new TargetFrame(
            mountedPosition,
            new ContinuousState(
                data.MountData.MountMovementDirection,
                data.MountData.MountLookDirection,
                data.MountData.GetMovementInput(),
                data.MountData.GetMovementFlags()),
            // An unusable mount position must not advertise itself as a snap target: FollowMounted would
            // ease the horse straight to the scene origin.
            hasMountSnapPosition: data.MountData.HasMountPosition,
            data.MountData.HasMountPosition ? data.MountData.MountPosition : Vec3.Zero,
            new ContinuousState(
                data.MovementDirection,
                data.LookDirection,
                data.GetMovementInput(agent),
                data.MovementFlag),
            elapsed,
            GetNextUpdateSequence(),
            oneWayLatencySeconds,
            data.MountData.MountMovementDirection * data.MountData.MountSpeed));
    }

    public void SetMountTarget(Agent mountAgent, AgentMountData data)
    {
        if (mountAgent == null) return;
        StoreTarget(mountAgent, new TargetFrame(
            data.MountPosition,
            new ContinuousState(
                data.MountMovementDirection,
                data.MountLookDirection,
                data.GetMovementInput(),
                data.GetMovementFlags()),
            hasMountSnapPosition: false,
            Vec3.Zero,
            default,
            updatedAt: elapsed,
            updateSequence: GetNextUpdateSequence()));
    }

    public void SetMountedRiderTarget(
        Agent agent,
        Vec3 targetPosition,
        Vec2 riderMovementDirection,
        Vec2 mountMovementDirection,
        Vec3 mountSnapPosition)
    {
        if (agent == null) return;
        Agent mount = agent.MountAgent;
        StoreTarget(agent, new TargetFrame(
            targetPosition,
            ContinuousState.Capture(mount, mountMovementDirection),
            hasMountSnapPosition: true,
            mountSnapPosition,
            ContinuousState.Capture(agent, riderMovementDirection),
            elapsed,
            GetNextUpdateSequence()));
    }

    public void Forget(Agent agent)
    {
        if (agent == null) return;

        _targets.Remove(agent);
        _mountedGuardProcessedSequences.Remove(agent);
    }

    public bool TryGetTargetMovementFlags(
        Agent agent,
        out uint riderMovementFlags,
        out uint mountMovementFlags)
    {
        riderMovementFlags = 0;
        mountMovementFlags = 0;
        if (agent == null || !_targets.TryGetValue(agent, out TargetFrame target))
            return false;

        if (target.HasMountSnapPosition)
        {
            riderMovementFlags = target.MountedRiderState.MovementFlags;
            mountMovementFlags = target.AgentState.MovementFlags;
        }
        else
        {
            riderMovementFlags = target.AgentState.MovementFlags;
        }
        return true;
    }

    public bool TryGetTargetFrame(
        Agent agent,
        out Vec3 position,
        out Vec3 lookDirection,
        out long targetUpdateSequence)
    {
        position = Vec3.Zero;
        lookDirection = Vec3.Zero;
        targetUpdateSequence = 0;
        if (agent == null ||
            !_targets.TryGetValue(agent, out TargetFrame target))
        {
            return false;
        }

        position = target.Position;
        lookDirection = target.HasMountSnapPosition
            ? target.MountedRiderState.LookDirection
            : target.AgentState.LookDirection;
        targetUpdateSequence = target.UpdateSequence;
        return true;
    }
    public void Clear()
    {
        _targets.Clear();
        _mountedGuardProcessedSequences.Clear();
    }

    private long GetNextUpdateSequence()
    {
        updateSequence++;
        return updateSequence;
    }

    public void ReplayLookDirections()
    {
        foreach (var pair in _targets)
        {
            Agent agent = pair.Key;
            if (!agent.IsActive() ||
                agent.Health <= 0f ||
                elapsed - pair.Value.UpdatedAt > StaleTargetSeconds)
            {
                continue;
            }

            // A point-owned puppet's facing belongs to the point it is using — a look write per
            // native cycle is exactly the churn that spun seated NPCs.
            if (LocationPoseLock.IsPointOwned(agent))
                continue;

            TargetFrame target = pair.Value;
            if (agent.MountAgent != null && target.HasMountSnapPosition)
            {
                target.MountedRiderState.ApplyLookDirection(agent);
                target.AgentState.ApplyLookDirection(agent.MountAgent);
            }
            else
            {
                target.AgentState.ApplyLookDirection(agent);
            }
        }
    }

    public void Tick(float dt)
    {
        if (dt <= 0f) return;
        elapsed += dt;
        if (_targets.Count == 0)
        {
            return;
        }

        int trackedBefore = _targets.Count;
        int staleTargets = 0;
        float oldestStaleAge = 0f;
        Agent oldestStaleAgent = null;
        TargetFrame oldestStaleTarget = default;

        foreach (var pair in _targets)
        {
            Agent agent = pair.Key;
            // Evict agents whose native object is gone (mission teardown, death). IsActive() mirrors the guard on
            // every other native-agent touch (see the movement-capture teardown races).
            if (!agent.IsActive() || agent.Health <= 0f)
            {
                _evict.Add(agent);
                continue;
            }

            // A stale target (owner stopped reporting) expires instead of pinning the puppet to an old position.
            float targetAge = elapsed - pair.Value.UpdatedAt;
            if (targetAge > StaleTargetSeconds)
            {
                _evict.Add(agent);
                staleTargets++;
                if (oldestStaleAgent == null || targetAge > oldestStaleAge)
                {
                    oldestStaleAge = targetAge;
                    oldestStaleAgent = agent;
                    oldestStaleTarget = pair.Value;
                }
                continue;
            }

            // Mounted riders are eased onto their horse's reported position directly, so they don't use snapDistance.
            if (agent.MountAgent != null)
            {
                FollowMounted(agent, pair.Value, dt);
                continue;
            }

            // A settlement puppet USING a scene point (seated, at an animation point) is owned by
            // that point: the point's own machinery aligns it, animates it and holds it — on this
            // client exactly as on the host, because the puppet uses the SAME local point
            // (replicated semantically via NetworkNpcPointUse). Driving movement here would only
            // fight it. When the use ends, the point plays its leave action and this seek resumes.
            if (LocationPoseLock.IsPointOwned(agent))
                continue;

            // A mount tolerates more slack before we snap; an on-foot rider is held tighter.
            float snapDistance = agent.IsMount ? MountSnapDistance : RiderSnapDistance;
            if (agent.Position.Distance(pair.Value.Position) <= snapDistance)
                MoveTowardTarget(agent, pair.Value);
            else
                Teleport(agent, pair.Value);
            pair.Value.AgentState.Apply(agent);
        }

        if (_evict.Count > 0)
        {
            foreach (Agent agent in _evict)
            {
                _targets.Remove(agent);
                _mountedGuardProcessedSequences.Remove(agent);
                _previousTargets.Remove(agent);
            }
            _evict.Clear();
        }

        if (staleTargets > 0)
        {
            try
            {
                LogStaleTargets(staleTargets, trackedBefore, oldestStaleAge, oldestStaleAgent, oldestStaleTarget);
            }
            catch (OutOfMemoryException)
            {
                // Stale targets are already released; optional diagnostics must not stop the mission tick.
            }
        }
    }

    private void LogStaleTargets(
        int staleTargets,
        int trackedBefore,
        float oldestAge,
        Agent sampleAgent,
        TargetFrame sampleTarget)
    {
        Guid agentId = Guid.Empty;
        string authority = null;
        string movementScope = null;
        ushort movementId = 0;
        if (agentRegistry != null && agentRegistry.TryGetAgentInfo(sampleAgent, out var info))
        {
            agentId = info.AgentId;
            authority = info.CurrentAuthority;
            movementScope = info.MovementScopeId;
            movementId = info.MovementId;
        }

        Logger.Warning(
            "[BattleDesync] Expired {StaleTargets} active movement target(s): trackedBefore={TrackedBefore} " +
            "oldestAge={OldestAge:0.000}s sampleAgentId={AgentId} sampleAuthority={Authority} " +
            "sampleMovementIdentity={MovementScope}/{MovementId} sampleIndex={AgentIndex} sampleName={AgentName} " +
            "sampleHealth={Health:0.0} sampleController={Controller} sampleAiControlled={AiControlled} " +
            "sampleSpeed={Speed:0.00} sampleDistance={Distance:0.00} samplePosition={Position} " +
            "sampleTarget={Target} sampleSequence={Sequence}",
            staleTargets,
            trackedBefore,
            oldestAge,
            agentId,
            authority,
            movementScope,
            movementId,
            sampleAgent.Index,
            sampleAgent.Name,
            sampleAgent.Health,
            sampleAgent.Controller,
            sampleAgent.IsAIControlled,
            sampleAgent.GetRealGlobalVelocity().AsVec2.Length,
            sampleAgent.Position.Distance(sampleTarget.Position),
            sampleAgent.Position,
            sampleTarget.Position,
            sampleTarget.UpdateSequence);
    }
    /// <summary>Furthest a puppet may be aimed beyond its reported position, in metres.</summary>
    /// <remarks>
    /// Dead reckoning is only worth as much as the frame it is derived from. A dropped or reordered
    /// update can make two frames look like a huge jump, and multiplying that by the target's age would
    /// fling the agent. Capped well inside the 6m snap distance so the worst case is a puppet slightly
    /// ahead of itself rather than one thrown across the field.
    /// </remarks>
    private const float MaximumLeadDistance = 1f;

    /// <summary>Fastest believable ground speed; anything above this is a bad frame, not a sprint.</summary>
    private const float MaximumLeadSpeed = 12f;

    /// <summary>Frame gaps outside this range cannot give a trustworthy velocity.</summary>
    private const float MinimumLeadInterval = 0.02f;
    private const float MaximumLeadInterval = 0.5f;

    /// <summary>Ignore jitter: below this the puppet is standing still and leading it would wobble it.</summary>
    private const float MinimumLeadSpeed = 0.4f;

    private long deadReckonedMoves;
    private long plainMoves;
    private double leadDistanceSum;

    /// <summary>How often the lead below was applied, and how far it reached. Read by the battle census.</summary>
    public long DeadReckonedMoves => deadReckonedMoves;
    public long PlainMoves => plainMoves;
    public double MeanLeadDistance => deadReckonedMoves == 0 ? 0 : leadDistanceSum / deadReckonedMoves;

    /// <summary>
    /// Aim at where the owner IS, not where it was when the frame was sent.
    /// </summary>
    /// <remarks>
    /// The puppet walks to this position under its own locomotion, so pointing it at a stale position
    /// leaves it permanently chasing while the owner keeps moving. Leading by the owner's velocity times
    /// the age of the frame cancels that trail. Every input is checked first - a bad frame simply falls
    /// back to the reported position, which is the previous behaviour.
    /// </remarks>
    private Vec3 ResolveSeekPosition(Agent agent, TargetFrame target)
    {
#if DEBUG
        // Measuring the measurement: this runs once per MOVING PUPPET PER TICK - about 3,000 times a
        // second in a full battle - so it must not exist at all in a shipping build.
        long costStart = Missions.Diagnostics.HotPathCostDiagnostics.Now();
        try
        {
            return ResolveSeekPositionCore(agent, target);
        }
        finally
        {
            Missions.Diagnostics.HotPathCostDiagnostics.AddDeadReckon(costStart);
        }
#else
        return ResolveSeekPositionCore(agent, target);
#endif
    }

    private Vec3 ResolveSeekPositionCore(Agent agent, TargetFrame target)
    {
        if (!_previousTargets.TryGetValue(agent, out PreviousFrame previous))
        {
            plainMoves++;
            return target.Position;
        }

        float interval = target.UpdatedAt - previous.UpdatedAt;
        if (interval < MinimumLeadInterval || interval > MaximumLeadInterval)
        {
            plainMoves++;
            return target.Position;
        }

        if (!TryComputeLead(
                target.Position - previous.Position,
                interval,
                elapsed - target.UpdatedAt,
                out Vec3 lead))
        {
            plainMoves++;
            return target.Position;
        }

        deadReckonedMoves++;
        leadDistanceSum += lead.Length;
        return target.Position + lead;
    }

    /// <summary>Fastest believable horse; a courser gallops at 12-14 m/s, so anything above this is a bad frame.</summary>
    private const float MaximumMountLeadSpeed = 22f;

    /// <summary>Furthest a puppet horse may be aimed beyond its reported position.</summary>
    private const float MaximumMountLeadDistance = 3f;

    /// <summary>A frame older than this is a stalled stream; it is not extrapolated further.</summary>
    private const float MaximumMountLeadAgeSeconds = 0.1f;

    /// <summary>A ping estimate above this is a spike, not a delay to lead by.</summary>
    private const float MaximumLeadLatencySeconds = 0.3f;

    /// <summary>
    /// What the puppet horse trails by on a zero-delay rig with the lead off: about one frame. Measured 0.2 m at
    /// 10 m/s (run v2-lance-before), so the horse's own locomotion, not the ease, carries it between frames; the
    /// earlier 1 / 12 s "ease lag" term over-led and is gone.
    /// </summary>
    private const float MountedPresentationLagSeconds = 0.02f;

    private long mountLeadMoves;
    private double mountLeadDistanceSum;
#if DEBUG
    private float lastMountLeadReport;
#endif

    /// <summary>How often the mounted lead was applied, and how far it reached. Read by the battle census.</summary>
    public long MountLeadMoves => mountLeadMoves;
    public double MeanMountLeadDistance => mountLeadMoves == 0 ? 0 : mountLeadDistanceSum / mountLeadMoves;

    /// <summary>
    /// How far ahead of its reported position a puppet HORSE should be aimed: the owner's horse velocity (sent in
    /// the frame, so no two-frame differencing that fails at a 60 Hz lane where frames are 16.7 ms apart) times the
    /// time the frame has been in flight and on this machine.
    /// </summary>
    /// <remarks>
    /// Pure so the guards can be tested. Age is capped at 0.1 s (a stalled stream must not run away), the network
    /// delay at 0.3 s, speed must be a horse's, and the lead is capped well inside the 12 m mount snap distance.
    /// The delay term is the one that matters between two real machines: at a 12 m/s gallop, 50 ms one way is
    /// 0.6 m of horse the other player never sees where it is.
    /// </remarks>
    internal static bool TryComputeMountLead(Vec2 velocity, float age, float oneWayLatency, out Vec3 lead)
    {
        lead = Vec3.Zero;

        float speed = velocity.Length;
        if (float.IsNaN(speed) || float.IsInfinity(speed)) return false;
        if (speed < MinimumLeadSpeed || speed > MaximumMountLeadSpeed) return false;
        if (float.IsNaN(age) || age < 0f) return false;

        float latency = float.IsNaN(oneWayLatency) ? 0f : Math.Max(0f, Math.Min(oneWayLatency, MaximumLeadLatencySeconds));
        float seconds = Math.Min(age, MaximumMountLeadAgeSeconds) + latency + MountedPresentationLagSeconds;
        Vec3 candidate = new Vec3(velocity.X * seconds, velocity.Y * seconds, 0f);
        float leadDistance = candidate.Length;
        if (float.IsNaN(leadDistance) || float.IsInfinity(leadDistance)) return false;
        if (leadDistance > MaximumMountLeadDistance)
            candidate *= MaximumMountLeadDistance / leadDistance;

        lead = candidate;
        return true;
    }

    /// <summary>
    /// How far ahead of its reported position a puppet should be aimed, given the owner's last step.
    /// </summary>
    /// <remarks>
    /// Pure so the guards can be tested. Every one of them exists to make a bad frame harmless: a
    /// nonsensical interval, an impossible speed, a stale age or an over-long lead all fall back to
    /// aiming at the reported position, which is the behaviour this replaced.
    /// </remarks>
    internal static bool TryComputeLead(Vec3 travelled, float interval, float age, out Vec3 lead)
    {
        lead = Vec3.Zero;

        if (float.IsNaN(interval) || interval < MinimumLeadInterval || interval > MaximumLeadInterval)
            return false;
        if (float.IsNaN(age) || age <= 0f || age > MaximumLeadInterval)
            return false;

        float distance = travelled.Length;
        if (float.IsNaN(distance) || float.IsInfinity(distance)) return false;

        float speed = distance / interval;
        if (speed < MinimumLeadSpeed || speed > MaximumLeadSpeed) return false;

        // Never lead more than ONE update period. If updates are irregular, age can be several times
        // the interval, and multiplying a whole step by that invents a position the owner never
        // occupied. Measured with the ratio unclamped the median improved but the tail got worse:
        // p99 1.57m -> 2.76m and max 3.33m -> 6.08m, past the snap distance, so puppets began
        // teleporting. The tail is what makes a blow look like it landed from out of reach, so it is
        // the half that must not regress.
        float ratio = age / interval;
        if (ratio > 1f) ratio = 1f;
        Vec3 candidate = travelled * ratio;
        float leadDistance = candidate.Length;
        if (float.IsNaN(leadDistance) || float.IsInfinity(leadDistance)) return false;
        if (leadDistance > MaximumLeadDistance)
            candidate *= MaximumLeadDistance / leadDistance;

        lead = candidate;
        return true;
    }

    private void MoveTowardTarget(Agent agent, TargetFrame target)
    {
        Vec3 seek = ResolveSeekPosition(agent, target);
        Vec2 targetPosition = seek.AsVec2;
        Vec3 targetDirection = ResolveDirection(
            agent,
            seek,
            target.AgentState.MovementDirection);
        agent.SetTargetPositionAndDirection(in targetPosition, in targetDirection);
    }

    // Ease the horse directly toward the owner's reported position without a physical seek. A guarded puppet gets
    // at most one semantic teleport per owner frame because TeleportToPosition also resets rider components.
    private void FollowMounted(Agent rider, TargetFrame target, float dt)
    {
        Agent mount = rider.MountAgent;
        if (mount == null || !mount.IsActive()) return;

        Vec3 mountTarget = target.HasMountSnapPosition ? target.MountSnapPosition : target.Position;
        // A moving horse is eased toward a position its owner has already left: the frame is up to one update
        // old and the exponential ease itself trails a moving target by v / MountedFollowRate (83 ms). At a
        // canter that is about a metre - the puppet horse a lance length behind where its rider really is. Lead
        // the target by the owner's last velocity over both, with the same guards the on-foot lead has.
        if (MountedSyncSwitches.LeadEnabled
            && TryComputeMountLead(
                target.MountVelocity,
                elapsed - target.UpdatedAt,
                target.OneWayLatencySeconds,
                out Vec3 mountLead))
        {
            mountTarget += mountLead;
            mountLeadMoves++;
            mountLeadDistanceSum += mountLead.Length;
        }
#if DEBUG
        if (Missions.Diagnostics.DuelEvents.Enabled && elapsed - lastMountLeadReport >= 1f)
        {
            lastMountLeadReport = elapsed;
            Missions.Diagnostics.DuelEvents.Record("mountlead",
                "moves=" + mountLeadMoves.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " meanM=" + MeanMountLeadDistance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                " speed=" + target.MountVelocity.Length.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                " latencyMs=" + (target.OneWayLatencySeconds * 1000f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                " lead=" + (MountedSyncSwitches.LeadEnabled ? "on" : "off"));
        }
#endif
        Vec3 cur = mount.Position;
        float distance = cur.Distance(mountTarget);
        bool hasGuardPresentation = HasGuardPresentation(rider);
        if (hasGuardPresentation)
        {
            if (_mountedGuardProcessedSequences.TryGetValue(
                    rider,
                    out long processedSequence) &&
                processedSequence == target.UpdateSequence)
            {
                target.AgentState.Apply(mount);
                target.MountedRiderState.Apply(rider);
                return;
            }

            _mountedGuardProcessedSequences[rider] =
                target.UpdateSequence;
            if (distance <= MountedGuardPositionTolerance)
            {
                target.AgentState.Apply(mount);
                target.MountedRiderState.Apply(rider);
                return;
            }
        }
        if (distance <= MountedPositionEpsilon)
        {
            target.AgentState.Apply(mount);
            target.MountedRiderState.Apply(rider);
            return;
        }

        float alpha = System.Math.Min(1f, MountedFollowRate * dt);
        // Snap a guarded puppet only after measurable drift, then leave its action timeline alone again.
        Vec3 next = hasGuardPresentation ||
                    distance > MountSnapDistance
            ? mountTarget
            : cur + ((mountTarget - cur) * alpha);

        mount.TeleportToPosition(next);
        // Teleporting a horse rewrites its rider's movement basis. Install both owner snapshots once afterward.
        target.AgentState.Apply(mount);
        target.MountedRiderState.Apply(rider);
    }

    private static bool HasGuardPresentation(Agent rider)
    {
        if (AgentActionData.GetDefendMovementFlags(rider.MovementFlags)
            != Agent.MovementControlFlag.None)
        {
            return true;
        }

        return AgentActionData.IsGuardPresentationAction(
                rider.GetCurrentActionType(0))
            || AgentActionData.IsGuardPresentationAction(
                rider.GetCurrentActionType(1));
    }

    private static Vec3 ResolveDirection(
        Agent agent,
        Vec3 targetPosition,
        Vec2 movementDirection)
    {
        Vec2 direction = movementDirection;
        if (direction.LengthSquared <= 0.0001f)
            direction = targetPosition.AsVec2 - agent.Position.AsVec2;
        if (direction.LengthSquared <= 0.0001f)
            direction = agent.LookDirection.AsVec2;
        if (direction.LengthSquared <= 0.0001f)
            direction = Vec2.Forward;

        direction.Normalize();
        return new Vec3(direction.X, direction.Y, 0f);
    }

    private void Teleport(Agent agent, TargetFrame target)
    {
        var lookDirection = agent.LookDirection;
        var movementDirection = agent.GetMovementDirection();
        if (agent.MountAgent != null && target.HasMountSnapPosition)
        {
            Teleport(agent.MountAgent, new TargetFrame(
                target.MountSnapPosition,
                target.AgentState,
                hasMountSnapPosition: false,
                Vec3.Zero,
                default,
                target.UpdatedAt,
                target.UpdateSequence));
        }
        else
        {
            agent.TeleportToPosition(target.Position);
        }

        agent.LookDirection = lookDirection;
        agent.SetMovementDirection(movementDirection);
        MoveTowardTarget(agent, target);
    }

    private struct TargetFrame
    {
        public TargetFrame(
            Vec3 position,
            ContinuousState agentState,
            bool hasMountSnapPosition,
            Vec3 mountSnapPosition,
            ContinuousState mountedRiderState,
            float updatedAt,
            long updateSequence,
            float oneWayLatencySeconds = 0f,
            Vec2 mountVelocity = default)
        {
            Position = position;
            AgentState = agentState;
            HasMountSnapPosition = hasMountSnapPosition;
            MountSnapPosition = mountSnapPosition;
            MountedRiderState = mountedRiderState;
            UpdatedAt = updatedAt;
            UpdateSequence = updateSequence;
            OneWayLatencySeconds = oneWayLatencySeconds;
            MountVelocity = mountVelocity;
        }

        /// <summary>The sender's estimated one-way delay when this frame arrived (mesh ping / 2, plus any simulated hold).</summary>
        public float OneWayLatencySeconds { get; }

        /// <summary>The owner's horse velocity as sent (direction x speed), the basis of the mounted lead.</summary>
        public Vec2 MountVelocity { get; }

        public Vec3 Position { get; }
        public ContinuousState AgentState { get; }
        public bool HasMountSnapPosition { get; }
        public Vec3 MountSnapPosition { get; }
        public ContinuousState MountedRiderState { get; }
        public float UpdatedAt { get; }
        public long UpdateSequence { get; }
    }

    private readonly struct ContinuousState
    {
        public ContinuousState(
            Vec2 movementDirection,
            Vec3 lookDirection,
            Vec2 movementInput,
            uint movementFlags)
        {
            MovementDirection = movementDirection;
            LookDirection = lookDirection;
            MovementInput = movementInput;
            MovementFlags = movementFlags;
        }

        public Vec2 MovementDirection { get; }
        public Vec3 LookDirection { get; }
        public Vec2 MovementInput { get; }
        public uint MovementFlags { get; }

        public static ContinuousState Capture(
            Agent agent,
            Vec2 movementDirection)
        {
            if (agent == null)
            {
                return new ContinuousState(
                    movementDirection,
                    new Vec3(
                        movementDirection.X,
                        movementDirection.Y,
                        0f),
                    Vec2.Zero,
                    0);
            }

            return new ContinuousState(
                movementDirection,
                agent.LookDirection,
                agent.MovementInputVector,
                (uint)AgentData.GetLocomotionMovementFlags(
                    agent.MovementFlags));
        }

        public void Apply(Agent agent)
        {
            if (agent == null ||
                !agent.IsActive() ||
                agent.Health <= 0f)
            {
                return;
            }

            AgentData.ApplyMovementDirection(agent, MovementDirection);
            AgentData.ApplyLookDirection(agent, LookDirection);
            AgentData.ApplyMovementInput(agent, MovementInput);
            // Native continuous-state setters can consume or rewrite the move mask. Install it last so the
            // upcoming Agent tick sees the owner's translation and turn inputs.
            AgentData.ApplyLocomotionMovementFlags(
                agent,
                (Agent.MovementControlFlag)MovementFlags);
        }

        public void ApplyLookDirection(Agent agent)
        {
            if (agent == null ||
                !agent.IsActive() ||
                agent.Health <= 0f)
            {
                return;
            }

            AgentData.ApplyLookDirection(agent, LookDirection);
        }
    }
}
