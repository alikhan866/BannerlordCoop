#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Common;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Missions.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace Missions.Battles.DuelRig;

/// <summary>
/// Drives the LOCAL player's agent through scripted input for the PvP duel rig: walk up to the other player, then
/// hold and release attack and block directions on a fixed timeline, so two machines can be compared step for step.
/// </summary>
/// <remarks>
/// <para>
/// The driver writes the same fields the input layer writes - <see cref="Agent.MovementFlags"/> for attacks and
/// blocks, <see cref="Agent.EventControlFlags"/> for kicks and weapon-mode toggles, <see cref="Agent.MovementInputVector"/>
/// and <see cref="Agent.LookDirection"/> for footwork - and never calls <c>SetActionChannel</c> on the owner. A human's
/// swing goes through the engine's own attack state machine and collision; so must the rig's, or the rig measures a
/// path nobody plays.
/// </para>
/// <para>
/// While a script runs, the vanilla <see cref="MissionMainAgentController"/> is disabled so mouse-look and idle input do
/// not fight the script; it is re-enabled by <see cref="Stop"/> and at mission end.
/// </para>
/// <para>
/// Diagnostic only: compiled out of a release build and attached on demand by <c>coop.debug.duel.*</c>.
/// </para>
/// </remarks>
internal sealed class DuelDriver : MissionBehavior
{
    /// <summary>How close the driver walks before it stops approaching; melee reach is about two metres.</summary>
    private const float StandOffMetres = 1.4f;
    /// <summary>Mounted: a horse cannot stop on a coin; stand off further and approach at a walk (P6).</summary>
    private const float MountedStandOffMetres = 3.0f;
    private const float MountedReengageMetres = 4.5f;

    /// <summary>Beyond this the script pauses attacking and walks in again, so a knockback does not turn every later swing into air.</summary>
    private const float ReengageMetres = 1.5f;   // a landed kick pushes the target to ~2 m; step back in before the next one

    private enum Phase { Idle, Facing, Scripting, Braking }

    private readonly struct Step
    {
        public Step(float seconds, Agent.MovementControlFlag flags, Agent.EventControlFlag events, string label)
            : this(seconds, flags, events, label, Vec2.Zero)
        {
        }

        public Step(float seconds, Agent.MovementControlFlag flags, Agent.EventControlFlag events, string label, Vec2 input)
            : this(seconds, flags, events, label, input, null)
        {
        }

        public Step(float seconds, Agent.MovementControlFlag flags, Agent.EventControlFlag events, string label, Vec2 input, Action<Agent> onEnter)
            : this(seconds, flags, events, label, input, onEnter, 0f, 0f)
        {
        }

        public Step(float seconds, Agent.MovementControlFlag flags, Agent.EventControlFlag events, string label, Vec2 input, Action<Agent> onEnter,
            float holdFrom, float releaseAt)
        {
            Seconds = seconds;
            Flags = flags;
            Events = events;
            Label = label;
            Input = input;
            OnEnter = onEnter;
            HoldFrom = holdFrom;
            ReleaseAt = releaseAt;
        }

        public float Seconds { get; }
        public Agent.MovementControlFlag Flags { get; }
        public Agent.EventControlFlag Events { get; }
        public string Label { get; }
        /// <summary>Movement input for the step (x = strafe right, y = forward), in the agent's look frame.</summary>
        public Vec2 Input { get; }
        /// <summary>Runs once when the step starts, with the live agent (wield, ammo refill, usage toggle).</summary>
        public Action<Agent> OnEnter { get; }
        /// <summary>
        /// Proximity-triggered attack (riding scripts): the attack flags are held only while the opponent is between
        /// <see cref="ReleaseAt"/> and <see cref="HoldFrom"/> metres, so the wind-up starts on the way in and the release
        /// happens at reach, whatever the pass timing. 0 = the flags apply for the whole step (timed).
        /// </summary>
        public float HoldFrom { get; }
        public float ReleaseAt { get; }
    }

    private readonly List<Step> steps = new List<Step>();
    private Phase phase = Phase.Idle;
    private int stepIndex = -1;
    private float stepElapsed;
    private bool stepEventsSent;
    private string scriptName = "";
    private int scriptSeed;
    private bool controllerDisabled;
    private float scriptElapsed;
    private int approachTicks;
    private float probeElapsed;
    /// <summary>Stand-off requested by <c>face &lt;id&gt; &lt;metres&gt;</c>; javelins want ~12 m, melee ~1.4 m.</summary>
    private float? standOffOverride;
    /// <summary>The throw script aims at the opponent's chest with a gravity lead instead of looking level.</summary>
    private bool aimAtOpponentChest;
    private Vec2 lastMountedInput;
    private float lastSteerAngle;
    /// <summary>Why the driver last went idle (arrived, done, stop, error ...); printed by <c>state</c>.</summary>
    private string idleReason = "-";
    private float brakingElapsed;
    /// <summary>A horse this fast inside the stand-off is reined in before the driver lets go of it.</summary>
    private const float BrakeSpeedMetresPerSecond = 0.3f;
    private const float BrakeTimeoutSeconds = 6f;

    public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

    /// <summary>The controller id of the opponent, once <see cref="Face"/> has been called.</summary>
    public string OpponentControllerId { get; private set; }

    public static DuelDriver GetOrAttach(Mission mission)
    {
        var driver = mission.GetMissionBehavior<DuelDriver>();
        if (driver == null)
        {
            driver = new DuelDriver();
            mission.AddMissionBehavior(driver);
        }
        return driver;
    }

    public static IReadOnlyList<string> ScriptNames => new[] { "swings", "thrusts", "blocks", "kick_bash", "mixed", "footwork", "ride", "ride_swing", "ride_throw", "couch", "throw", "hold" };

    /// <summary>Turn toward the given controller's hero agent and walk up to stand-off distance.</summary>
    public string Face(string controllerId, float? standOffMetres = null)
    {
        OpponentControllerId = controllerId;
        standOffOverride = standOffMetres.HasValue && standOffMetres.Value > 0f ? standOffMetres : null;
        Agent opponent = ResolveOpponent();
        if (opponent == null)
            return "Opponent " + controllerId + " has no hero agent in this mission yet; will keep looking each tick.";
        phase = Phase.Facing;
        DuelEvents.Record("face", "opponent=" + controllerId + " agent=" + DuelEvents.Id8(opponent));
        return "Facing " + controllerId + " (" + DuelEvents.Id8(opponent) + ") at " +
               DistanceToOpponent().ToString("0.0", CultureInfo.InvariantCulture) + " m";
    }

    /// <summary>Start a named input script on the local player agent.</summary>
    public string RunScript(string name, int seed)
    {
        Agent me = Mission?.MainAgent;
        if (me == null) return "No local player agent.";

        steps.Clear();
        switch (name.ToLowerInvariant())
        {
            case "swings": BuildSwings(); break;
            case "thrusts": BuildThrusts(); break;
            case "blocks": BuildBlocks(); break;
            case "kick_bash": BuildKickBash(); break;
            case "mixed": BuildMixed(seed); break;
            case "footwork": BuildFootwork(); break;
            case "ride": BuildRide(); break;
            case "ride_swing": BuildRideSwing(); break;
            case "ride_throw": BuildRideThrow(); break;
            case "couch": BuildCouch(); break;
            case "throw": BuildThrow(); break;
            case "hold": steps.Add(new Step(60f, Agent.MovementControlFlag.None, 0, "hold")); break;
            default:
                return "Unknown script '" + name + "'. Known: " + string.Join(", ", ScriptNames);
        }

        scriptName = name.ToLowerInvariant();
        aimAtOpponentChest = scriptName == "throw" || scriptName == "ride_throw";
        scriptSeed = seed;
        stepIndex = -1;
        stepElapsed = 0f;
        scriptElapsed = 0f;
        stepEventsSent = false;
        phase = Phase.Scripting;
        DuelEvents.Record("script", "start name=" + scriptName + " seed=" + seed.ToString(CultureInfo.InvariantCulture) +
                                    " steps=" + steps.Count.ToString(CultureInfo.InvariantCulture));
        DuelEvents.Record("engine", EngineState(Mission?.MainAgent));
        return "Script '" + scriptName + "' started: " + steps.Count.ToString(CultureInfo.InvariantCulture) +
               " steps, " + steps.Sum(s => s.Seconds).ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }

    public string Stop()
    {
        Agent me = Mission?.MainAgent;
        bool wasRunning = phase != Phase.Idle;
        if (me != null)
        {
            try
            {
                me.MovementFlags = Agent.MovementControlFlag.None;
                me.MovementInputVector = Vec2.Zero;
            }
            catch (Exception) { }
        }
        if (!wasRunning)
        {
            RestoreController();
            return "Nothing was running.";
        }
        DuelEvents.Record("script", "stop name=" + scriptName + " step=" + stepIndex.ToString(CultureInfo.InvariantCulture));
        BeginBraking(me, "stop");
        return "Stopped '" + scriptName + "' at step " + stepIndex + (phase == Phase.Braking ? " (braking the horse)" : "");
    }

    private void SetIdle(string reason)
    {
        phase = Phase.Idle;
        idleReason = reason;
        RestoreController();
    }

    /// <summary>
    /// Let go of the agent. A rider's horse keeps its gait when the reins go slack (vanilla: releasing W does not
    /// stop a horse), so a moving horse is reined in (S) until it stands, and only then is the controller handed
    /// back - run m-lance-before: both horses met, the driver went idle, and they galloped on out of the boundary.
    /// </summary>
    private void BeginBraking(Agent me, string reason)
    {
        if (me != null && me.HasMount && MountSpeed(me) > BrakeSpeedMetresPerSecond)
        {
            phase = Phase.Braking;
            idleReason = reason;
            brakingElapsed = 0f;
            DisableController();
            return;
        }
        SetIdle(reason);
    }

    private static float MountSpeed(Agent me)
    {
        try
        {
            Agent mount = me.MountAgent;
            return (mount ?? me).Velocity.Length;
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    private bool OutsideBoundary(Agent me)
    {
        try
        {
            return Mission != null && !Mission.IsPositionInsideBoundaries(me.Position.AsVec2);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string State()
    {
        Agent me = Mission?.MainAgent;
        Agent opponent = ResolveOpponent();
        string step = stepIndex >= 0 && stepIndex < steps.Count ? steps[stepIndex].Label : "-";
        return "DUEL phase=" + phase +
               " script=" + (scriptName.Length == 0 ? "-" : scriptName) +
               " step=" + stepIndex.ToString(CultureInfo.InvariantCulture) + "/" + steps.Count.ToString(CultureInfo.InvariantCulture) +
               " label=" + step +
               " elapsed=" + scriptElapsed.ToString("0.0", CultureInfo.InvariantCulture) +
               " me=" + (me == null ? "-" : DuelEvents.Id8(me)) +
               " meHealth=" + (me == null ? "-" : me.Health.ToString("0", CultureInfo.InvariantCulture)) +
               " opponent=" + (OpponentControllerId ?? "-") +
               " opponentAgent=" + (opponent == null ? "-" : DuelEvents.Id8(opponent)) +
               " opponentHealth=" + (opponent == null ? "-" : opponent.Health.ToString("0", CultureInfo.InvariantCulture)) +
               " distance=" + (opponent == null ? "-" : DistanceToOpponent().ToString("0.0", CultureInfo.InvariantCulture)) +
               " flags=" + (me == null ? "-" : ((uint)me.MovementFlags).ToString(CultureInfo.InvariantCulture)) +
               " controllerDisabled=" + (controllerDisabled ? "1" : "0") +
               " idleReason=" + idleReason.Replace(' ', '_') +
               " speed=" + (me == null ? "-" : MountSpeed(me).ToString("0.0", CultureInfo.InvariantCulture)) +
               " " + EngineState(me);
    }

    /// <summary>
    /// What the ENGINE thinks of the local agent: which controller owns it, whether it is AI, whether the input we
    /// wrote is still there. Added when a scripted approach left the owner standing 305 m away with flags set.
    /// </summary>
    private string EngineState(Agent me)
    {
        var deployment = Mission?.GetMissionBehavior<DeploymentMissionController>();
        return "agentController=" + (me == null ? "-" : me.Controller.ToString()) +
               " agentState=" + (me == null ? "-" : me.State.ToString()) +
               " isAI=" + (me == null ? "-" : (me.IsAIControlled ? "1" : "0")) +
               " isMain=" + (me != null && me.IsMainAgent ? "1" : "0") +
               " input=" + (me == null ? "-" : me.MovementInputVector.X.ToString("0.00", CultureInfo.InvariantCulture) + "," +
                                                me.MovementInputVector.Y.ToString("0.00", CultureInfo.InvariantCulture)) +
               " missionMode=" + (Mission == null ? "-" : Mission.Mode.ToString()) +
               " deployment=" + (deployment == null ? "none" : (deployment.TeamSetupOver ? "setupOver" : "setup"));
    }

    private string Probe(Agent me, float distance)
    {
        var mission = Mission;
        var controller = mission?.GetMissionBehavior<MissionMainAgentController>();
        Vec3 pos = me.Position;
        return EngineState(me) +
               " flagsRead=" + ((uint)me.MovementFlags).ToString(CultureInfo.InvariantCulture) +
               " vel=" + me.Velocity.Length.ToString("0.00", CultureInfo.InvariantCulture) +
               " pos=" + pos.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + pos.y.ToString("0.0", CultureInfo.InvariantCulture) +
               " distance=" + (float.IsNaN(distance) ? "-" : distance.ToString("0.0", CultureInfo.InvariantCulture)) +
               " act0=" + me.GetCurrentActionType(0) + " act1=" + me.GetCurrentActionType(1) +
               " mount=" + (me.HasMount ? "1" : "0") +
               " steer=" + lastMountedInput.X.ToString("0.00", CultureInfo.InvariantCulture) + "," + lastMountedInput.Y.ToString("0.00", CultureInfo.InvariantCulture) +
               " steerAngle=" + lastSteerAngle.ToString("0.00", CultureInfo.InvariantCulture) +
               " inside=" + (mission != null && mission.IsPositionInsideBoundaries(pos.AsVec2) ? "1" : "0") +
               " ammo=" + (me.WieldedWeapon.IsEmpty ? "-" : me.WieldedWeapon.Amount.ToString(CultureInfo.InvariantCulture)) +
               " paused=" + (me.IsPaused ? "1" : "0") +
               " timeSpeed=" + (mission?.Scene == null ? "-" : mission.Scene.TimeSpeed.ToString("0.00", CultureInfo.InvariantCulture)) +
               " watch=" + me.CurrentWatchState + " runningAway=" + (me.IsRunningAway ? "1" : "0") +
               " aiTick=" + (mission != null && mission.AllowAiTicking ? "1" : "0") +
               " teleporting=" + (mission != null && mission.IsTeleportingAgents ? "1" : "0") +
               " deployHandler=" + (mission?.GetMissionBehavior<DeploymentHandler>() != null ? "1" : "0") +
               " ctrl=" + (controller == null ? "none" : (controller.IsDisabled ? "disabled" : "enabled")) +
               " ctrlIndex=" + (controller == null || mission == null ? -1 : mission.MissionBehaviors.IndexOf(controller)).ToString(CultureInfo.InvariantCulture) +
               " meIndex=" + (mission == null ? -1 : mission.MissionBehaviors.IndexOf(this)).ToString(CultureInfo.InvariantCulture) +
               " mainIsMe=" + (mission != null && mission.MainAgent == me ? "1" : "0");
    }

    /// <summary>
    /// Runs in the PRE-tick, not <c>OnMissionTick</c>. The vanilla <c>MissionMainAgentController</c> rewrites the
    /// player's <c>MovementFlags</c> and <c>MovementInputVector</c> from input every <c>OnPreMissionTick</c>, and the
    /// engine consumes them in the native tick that follows - before <c>OnMissionTick</c>. Input written there was
    /// visible to coop's replication (the puppet blocked on the other machine) and never to the owner's own engine
    /// (the owner stood still). This behaviour is added after the controller, so its pre-tick runs after the
    /// controller's and its flags are the ones the native tick reads.
    /// </summary>
    public override void OnPreMissionTick(float dt)
    {
        base.OnPreMissionTick(dt);
        if (phase == Phase.Idle) return;

        Agent me = Mission?.MainAgent;
        if (me == null || !me.IsActive())
        {
            if (phase == Phase.Scripting) DuelEvents.Record("script", "abort reason=no-local-agent");
            SetIdle("no-local-agent");
            return;
        }

        try
        {
            DisableController();
            Agent opponent = ResolveOpponent();
            float distance = opponent == null ? float.NaN : DistanceToOpponent();

            // Once a second, what the ENGINE did with last tick's input: the flags and input vector read back,
            // the velocity, the action the agent is in, and every gate that can freeze a player agent.
            probeElapsed += dt;
            if (probeElapsed >= 1f)
            {
                probeElapsed = 0f;
                DuelEvents.Record("probe", Probe(me, distance));
            }

            if (opponent != null)
            {
                if (aimAtOpponentChest)
                {
                    // A thrown weapon follows the look direction, so look at the chest and lead it by the drop the
                    // javelin takes over the distance (0.5 g t^2 at the item's missile speed); a level look from a
                    // 1.7 m eye sails over a target 12 m away.
                    Vec3 eye = me.GetEyeGlobalPosition();
                    Vec3 target = opponent.GetChestGlobalPosition();
                    float horizontal = (target - eye).AsVec2.Length;
                    float missileSpeed = MissileSpeedOf(me);
                    if (missileSpeed > 1f)
                        target.z += 0.5f * 9.806f * (horizontal / missileSpeed) * (horizontal / missileSpeed);
                    Vec3 aim = target - eye;
                    if (aim.Length > 0.05f)
                        me.LookDirection = aim.NormalizedCopy();
                }
                else
                {
                    Vec3 toward = opponent.Position - me.Position;
                    toward.z = 0f;
                    if (toward.Length > 0.05f)
                        me.LookDirection = toward.NormalizedCopy();
                }
            }

            if (phase == Phase.Braking)
            {
                // Rein the horse in until it stands, then hand the controller back.
                me.MovementFlags = Agent.MovementControlFlag.None;
                brakingElapsed += dt;
                float speed = MountSpeed(me);
                if (!me.HasMount || speed <= BrakeSpeedMetresPerSecond || brakingElapsed > BrakeTimeoutSeconds)
                {
                    me.MovementInputVector = Vec2.Zero;
                    DuelEvents.Record("brake", "done reason=" + idleReason + " speed=" + speed.ToString("0.00", CultureInfo.InvariantCulture) +
                                               " seconds=" + brakingElapsed.ToString("0.0", CultureInfo.InvariantCulture));
                    SetIdle(idleReason);
                }
                else
                {
                    me.MovementInputVector = new Vec2(0f, -1f);
                }
                return;
            }

            if (phase == Phase.Facing)
            {
                if (opponent == null) return;
                me.MovementFlags = Agent.MovementControlFlag.None;
                bool mounted = me.HasMount;
                float speed = mounted ? MountSpeed(me) : 0f;
                float standOff = StandOff(me);
                // A horse needs room to stop: brake once it is inside the stand-off OR fast enough to overshoot it.
                // Both riders approach at once, so the CLOSING speed decides (run m-jav-after: a 12 m stand-off
                // ended at 2.8 m because each horse braked for its own speed only).
                float closing = speed + (opponent.HasMount ? MountSpeed(opponent) : 0f);
                bool mustBrake = mounted && speed > 1.5f && distance < standOff + closing * 1.6f;
                if (distance > standOff && !mustBrake)
                {
                    // A rider canters in and trots the last metres; a galloping horse overshoots by tens of metres
                    // and the boundary handler retreats the whole mission for that player (run 14). A horse does
                    // NOT follow its rider's look: it is steered with the sideways input (A/D), so the mounted
                    // approach steers explicitly (run p6-ride: both riders left the boundary in 32 s).
                    if (mounted)
                    {
                        // Throttle by the distance still to cover to the stand-off, so a 12 m javelin stand-off is
                        // approached as gently as a 3 m melee one (run v2-jav-after: 12 m asked, 2.5 m reached).
                        float toGo = distance - standOff;
                        float forward = toGo > 60f ? 0.7f : toGo > 25f ? 0.45f : toGo > 12f ? 0.3f : 0.2f;
                        me.MovementInputVector = MountedInput(me, opponent, forward, 0f);
                    }
                    else
                    {
                        me.MovementInputVector = new Vec2(0f, 1f);
                    }
                    approachTicks++;
                }
                else if (mounted && speed > BrakeSpeedMetresPerSecond)
                {
                    me.MovementInputVector = new Vec2(0f, -1f);   // S: rein in
                    approachTicks++;
                }
                else
                {
                    me.MovementInputVector = Vec2.Zero;
                    DuelEvents.Record("face", "arrived distance=" + distance.ToString("0.00", CultureInfo.InvariantCulture) +
                                              " ticks=" + approachTicks.ToString(CultureInfo.InvariantCulture));
                    approachTicks = 0;
                    SetIdle("arrived");
                }
                if (mounted && OutsideBoundary(me))
                    me.MovementInputVector = MountedInput(me, opponent, 0.4f, 0f);   // the opponent is inside; go there
                return;
            }

            // Scripting.
            scriptElapsed += dt;
            if (stepIndex < 0 || stepElapsed >= steps[stepIndex].Seconds)
            {
                stepIndex++;
                stepElapsed = 0f;
                stepEventsSent = false;
                if (stepIndex >= steps.Count)
                {
                    me.MovementFlags = Agent.MovementControlFlag.None;
                    me.MovementInputVector = Vec2.Zero;
                    DuelEvents.Record("script", "done name=" + scriptName +
                                                " elapsed=" + scriptElapsed.ToString("0.00", CultureInfo.InvariantCulture));
                    BeginBraking(me, "done");
                    return;
                }
                DuelEvents.Record("step", scriptName + " " + stepIndex.ToString(CultureInfo.InvariantCulture) + " " + steps[stepIndex].Label +
                                          " flags=" + ((uint)steps[stepIndex].Flags).ToString(CultureInfo.InvariantCulture) +
                                          " distance=" + (float.IsNaN(distance) ? "-" : distance.ToString("0.00", CultureInfo.InvariantCulture)));
                steps[stepIndex].OnEnter?.Invoke(me);
            }
            stepElapsed += dt;

            Step step = steps[stepIndex];
            if (step.HoldFrom > 0f)
            {
                bool inWindow = !float.IsNaN(distance) && distance <= step.HoldFrom && distance > step.ReleaseAt;
                me.MovementFlags = inWindow ? step.Flags : Agent.MovementControlFlag.None;
            }
            else
            {
                me.MovementFlags = step.Flags;
            }
            if (!stepEventsSent && step.Events != 0)
            {
                // The vanilla controller only raises Kick when the engine says the kick is clear (a target in
                // range, nothing in the way); an unconditional flag is dropped. Say what the engine thought.
                bool kickClear = (step.Events & Agent.EventControlFlag.Kick) == 0 || me.KickClear() || me.MountAgent != null;
                me.EventControlFlags |= step.Events;
                stepEventsSent = true;
                DuelEvents.Record("event", step.Label + " flags=" + ((uint)step.Events).ToString(CultureInfo.InvariantCulture) +
                                           " kickClear=" + (kickClear ? "1" : "0") +
                                           " distance=" + (float.IsNaN(distance) ? "-" : distance.ToString("0.00", CultureInfo.InvariantCulture)));
            }

            // Footwork: stay in reach. A knockback or a stagger that leaves the pair 3 m apart would otherwise turn
            // the rest of the script into swings at air, and the puppet comparison would be of an agent hitting nothing.
            if (opponent != null && distance > Reengage(me) && step.Input.y <= 0f && step.Input.x == 0f)
                me.MovementInputVector = me.HasMount ? MountedInput(me, opponent, 0.3f, 0f) : new Vec2(0f, 1f);
            else if (me.HasMount && opponent != null && step.Input.x == 0f && step.Input.y > 0f)
                // Riding scripts: steer at a point beside the opponent so the pair pass and wheel round (joust).
                me.MovementInputVector = MountedInput(me, opponent, step.Input.y, JoustPassOffsetMetres);
            else
                me.MovementInputVector = step.Input;
            if (me.HasMount && opponent != null && OutsideBoundary(me))
                me.MovementInputVector = MountedInput(me, opponent, 0.4f, 0f);   // never ride out of the boundary
        }
        catch (Exception ex)
        {
            DuelEvents.Record("script", "error " + ex.GetType().Name + " " + ex.Message);
            SetIdle("error " + ex.GetType().Name);
        }
    }

    public override void OnEndMissionInternal()
    {
        base.OnEndMissionInternal();
        SetIdle("mission-end");
    }

    /// <summary>
    /// Re-asserted EVERY driven tick, not once. The first live run disabled the controller once and the engine
    /// probe still read <c>ctrl=enabled</c> a second later: something in the mission (the order UI's screen tick)
    /// sets <c>IsDisabled</c> back, and because the pre-tick loop runs behaviours from the LAST added to the first,
    /// the re-enabled controller ticked after this driver and overwrote its flags with the real (empty) input -
    /// which is why the owner stood still with flags set while the puppet performed them.
    /// </summary>
    private void DisableController()
    {
        var controller = Mission?.GetMissionBehavior<MissionMainAgentController>();
        if (controller == null) return;
        if (!controller.IsDisabled) controller.IsDisabled = true;
        controllerDisabled = true;
    }

    private void RestoreController()
    {
        if (!controllerDisabled) return;
        var controller = Mission?.GetMissionBehavior<MissionMainAgentController>();
        if (controller != null) controller.IsDisabled = false;
        controllerDisabled = false;
    }

    /// <summary>
    /// The opponent PLAYER's own hero agent. Resolved through the player's registered hero, not "any hero this
    /// controller owns": a player leading an army owns every attached lord too, and the first run picked a lord
    /// standing 20 m away while the actual player was 300 m off.
    /// </summary>
    /// <summary>The opponent's agent on this machine, for commands that act on both duellists.</summary>
    public Agent ResolveOpponentForCommands() => ResolveOpponent();

    private Agent ResolveOpponent()
    {
        if (string.IsNullOrEmpty(OpponentControllerId)) return null;
        return TryResolvePlayerHeroAgent(OpponentControllerId, Mission, out Agent agent, out _) ? agent : null;
    }

    /// <summary>Finds the agent of <paramref name="controllerId"/>'s player hero in <paramref name="mission"/>.</summary>
    internal static bool TryResolvePlayerHeroAgent(string controllerId, Mission mission, out Agent agent, out Guid agentId)
    {
        agent = null;
        agentId = Guid.Empty;
        if (string.IsNullOrEmpty(controllerId) || mission == null) return false;
        if (!ContainerProvider.TryResolve<INetworkAgentRegistry>(out var registry)) return false;
        try
        {
            // Preferred: the player's registered hero, looked up by id, then that hero's agent.
            if (ContainerProvider.TryResolve<IPlayerManager>(out var players)
                && players.TryGetPlayer(controllerId, out Player player)
                && !string.IsNullOrEmpty(player.HeroId)
                && ContainerProvider.TryResolve<IObjectManager>(out var objects)
                && objects.TryGetObject(player.HeroId, out Hero hero)
                && hero != null
                && registry.TryGetHeroAgentInfo(hero, out CoopAgentInfo heroInfo)
                && heroInfo?.Agent != null
                && heroInfo.Agent.Mission == mission
                && heroInfo.Agent.IsActive())
            {
                agent = heroInfo.Agent;
                agentId = heroInfo.AgentId;
                return true;
            }

            // Fallback: a human hero agent this controller owns (right whenever the player owns only their own party).
            Agent best = null;
            Guid bestId = Guid.Empty;
            foreach (CoopAgentInfo info in registry.GetAgents(controllerId))
            {
                Agent candidate = info?.Agent;
                if (candidate == null || candidate.Mission != mission || !candidate.IsActive() || !candidate.IsHuman) continue;
                if (candidate.Character != null && candidate.Character.IsHero)
                {
                    agent = candidate;
                    agentId = info.AgentId;
                    return true;
                }
                if (best == null) { best = candidate; bestId = info.AgentId; }
            }
            agent = best;
            agentId = bestId;
            return best != null;
        }
        catch (Exception)
        {
            agent = null;
            agentId = Guid.Empty;
            return false;
        }
    }

    private float StandOff(Agent me) =>
        standOffOverride ?? (me.HasMount ? MountedStandOffMetres : StandOffMetres);

    private float Reengage(Agent me) =>
        standOffOverride.HasValue ? standOffOverride.Value + 3f : (me.HasMount ? MountedReengageMetres : ReengageMetres);

    /// <summary>Riding scripts aim this far to the opponent's right, so two chargers pass instead of colliding head-on.</summary>
    private const float JoustPassOffsetMetres = 1.5f;

    /// <summary>
    /// Steering input for a rider: the engine turns a horse with the SIDEWAYS input (x &gt; 0 turns right), not with
    /// the rider's look. The angle between the horse's heading and the line to the aim point becomes the steer,
    /// and the forward input is cut while the horse is pointing well away from it.
    /// </summary>
    private Vec2 MountedInput(Agent me, Agent opponent, float forward, float passOffsetMetres)
    {
        Agent mount = me.MountAgent;
        if (mount == null || opponent == null) return new Vec2(0f, forward);
        Vec2 heading = mount.GetMovementDirection();
        if (heading.LengthSquared < 0.01f) heading = mount.LookDirection.AsVec2;
        if (heading.LengthSquared < 0.01f) heading = me.LookDirection.AsVec2;
        Vec2 toward = (opponent.Position - me.Position).AsVec2;
        if (passOffsetMetres != 0f && toward.Length > 0.5f)
        {
            Vec2 forwardDir = toward.Normalized();
            Vec2 right = new Vec2(forwardDir.Y, -forwardDir.X);
            toward += right * passOffsetMetres;
        }
        if (toward.LengthSquared < 0.01f || heading.LengthSquared < 0.01f) return new Vec2(0f, forward);
        heading.Normalize();
        toward.Normalize();
        float cross = heading.X * toward.Y - heading.Y * toward.X;   // > 0: the aim point is to the LEFT
        float dot = Vec2.DotProduct(heading, toward);
        float angle = (float)Math.Atan2(cross, dot);
        float steer = MBMath.ClampFloat(-angle / 0.6f, -1f, 1f);
        float speed = Math.Abs(angle) > 1.0f ? Math.Min(forward, 0.25f) : forward;
        // The vanilla controller turns a horse with the TurnRight / TurnLeft MOVEMENT FLAGS (0x10 / 0x20), set
        // alongside the input vector's x (MissionMainAgentController, "MountAgent != null && !_strafeModeActive").
        // The input vector alone does nothing to a horse: run m-lance-before, full left input for 4 s, heading
        // unchanged, both riders out of the boundary.
        if (steer > 0.05f) me.MovementFlags |= Agent.MovementControlFlag.TurnRight;
        else if (steer < -0.05f) me.MovementFlags |= Agent.MovementControlFlag.TurnLeft;
        lastSteerAngle = angle;
        lastMountedInput = new Vec2(steer, speed);
        return lastMountedInput;
    }

    private static float MissileSpeedOf(Agent me)
    {
        try
        {
            MissionWeapon wielded = me.WieldedWeapon;
            if (wielded.IsEmpty || wielded.CurrentUsageItem == null) return 0f;
            return wielded.CurrentUsageItem.MissileSpeed;
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    /// <summary>The first weapon slot holding a thrown weapon (javelins, axes, knives), or -1.</summary>
    private static int ThrownSlotOf(Agent me)
    {
        for (EquipmentIndex i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumAllWeaponSlots; i++)
        {
            MissionWeapon weapon = me.Equipment[i];
            if (weapon.IsEmpty || weapon.Item?.Weapons == null) continue;
            if (weapon.Item.Weapons.Any(u => u.IsRangedWeapon && u.IsConsumable)) return (int)i;
        }
        return -1;
    }

    /// <summary>Wield the thrown weapon (event flag, the way the input layer does) and say which slot it was.</summary>
    private static void WieldThrown(Agent me)
    {
        int slot = ThrownSlotOf(me);
        if (slot < 0)
        {
            DuelEvents.Record("throw", "wield slot=-1 reason=no-thrown-weapon");
            return;
        }
        me.EventControlFlags |= (Agent.EventControlFlag)((uint)Agent.EventControlFlag.Wield0 << slot);
        DuelEvents.Record("throw", "wield slot=" + slot.ToString(CultureInfo.InvariantCulture) +
                                   " item=" + (me.Equipment[(EquipmentIndex)slot].Item?.StringId ?? "-"));
    }

    /// <summary>
    /// Before each throw: make sure the wielded weapon is in its THROWING usage (X toggles a javelin between
    /// throw and melee) and put a full stack back in the slot, so six throws never run dry or switch weapons.
    /// </summary>
    private static void PrepareThrow(Agent me)
    {
        try
        {
            MissionWeapon wielded = me.WieldedWeapon;
            EquipmentIndex slot = me.GetPrimaryWieldedItemIndex();
            if (wielded.IsEmpty || slot < EquipmentIndex.WeaponItemBeginSlot)
            {
                DuelEvents.Record("throw", "prepare wielded=none");
                return;
            }
            bool ranged = wielded.CurrentUsageItem != null && wielded.CurrentUsageItem.IsRangedWeapon;
            if (!ranged && wielded.Item?.Weapons != null && wielded.Item.Weapons.Any(u => u.IsRangedWeapon))
                me.EventControlFlags |= Agent.EventControlFlag.ToggleAlternativeWeapon;
            short max = (short)(wielded.Item?.PrimaryWeapon?.MaxDataValue ?? 0);
            short before = wielded.Amount;
            if (max > 0 && before < max)
                me.SetWeaponAmountInSlot(slot, max, true);
            DuelEvents.Record("throw", "prepare slot=" + ((int)slot).ToString(CultureInfo.InvariantCulture) +
                                       " item=" + (wielded.Item?.StringId ?? "-") +
                                       " usage=" + wielded.CurrentUsageIndex.ToString(CultureInfo.InvariantCulture) +
                                       " ranged=" + (ranged ? "1" : "0") +
                                       " ammo=" + before.ToString(CultureInfo.InvariantCulture) + "->" + max.ToString(CultureInfo.InvariantCulture) +
                                       " speed=" + MissileSpeedOf(me).ToString("0", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            DuelEvents.Record("throw", "prepare error=" + ex.GetType().Name);
        }
    }

    private float DistanceToOpponent()
    {
        Agent me = Mission?.MainAgent;
        Agent opponent = ResolveOpponent();
        if (me == null || opponent == null) return float.NaN;
        return me.Position.AsVec2.Distance(opponent.Position.AsVec2);
    }

    // ---- scripts -------------------------------------------------------------------------------------------

    private static readonly Agent.MovementControlFlag[] AttackFlags =
    {
        Agent.MovementControlFlag.AttackLeft,
        Agent.MovementControlFlag.AttackRight,
        Agent.MovementControlFlag.AttackUp,
        Agent.MovementControlFlag.AttackDown,
    };

    private static readonly Agent.MovementControlFlag[] DefendFlags =
    {
        Agent.MovementControlFlag.DefendLeft,
        Agent.MovementControlFlag.DefendRight,
        Agent.MovementControlFlag.DefendUp,
        Agent.MovementControlFlag.DefendDown,
    };

    private static readonly string[] DirectionNames = { "left", "right", "up", "down" };

    /// <summary>Hold the attack flag (the engine winds up while it is held), then release it (the swing lands).</summary>
    private void Attack(int direction, float holdSeconds = 0.7f, float restSeconds = 0.6f)
    {
        steps.Add(new Step(holdSeconds, AttackFlags[direction], 0, "attack_" + DirectionNames[direction] + "_hold"));
        steps.Add(new Step(restSeconds, Agent.MovementControlFlag.None, 0, "attack_" + DirectionNames[direction] + "_release"));
    }

    private void Block(int direction, float holdSeconds = 1.0f, float restSeconds = 0.3f)
    {
        steps.Add(new Step(holdSeconds, Agent.MovementControlFlag.DefendBlock | DefendFlags[direction], 0, "block_" + DirectionNames[direction]));
        steps.Add(new Step(restSeconds, Agent.MovementControlFlag.None, 0, "block_" + DirectionNames[direction] + "_release"));
    }

    /// <summary>
    /// Circles the opponent (the driver keeps looking at it, so strafing orbits) with a swing thrown mid-strafe,
    /// then a step in and out. The only script that moves, so it is where position error between the two
    /// machines is measured (plan H9, P1, P8).
    /// </summary>
    private void BuildFootwork()
    {
        for (int i = 0; i < 3; i++)
        {
            steps.Add(new Step(1.2f, Agent.MovementControlFlag.None, 0, "strafe_left", new Vec2(-1f, 0f)));
            steps.Add(new Step(0.6f, Agent.MovementControlFlag.AttackLeft, 0, "attack_left_hold_strafing", new Vec2(-1f, 0f)));
            steps.Add(new Step(0.5f, Agent.MovementControlFlag.None, 0, "attack_left_release_strafing", new Vec2(-1f, 0f)));
            steps.Add(new Step(1.2f, Agent.MovementControlFlag.None, 0, "strafe_right", new Vec2(1f, 0f)));
            steps.Add(new Step(0.6f, Agent.MovementControlFlag.AttackRight, 0, "attack_right_hold_strafing", new Vec2(1f, 0f)));
            steps.Add(new Step(0.5f, Agent.MovementControlFlag.None, 0, "attack_right_release_strafing", new Vec2(1f, 0f)));
            steps.Add(new Step(0.5f, Agent.MovementControlFlag.None, 0, "step_back", new Vec2(0f, -1f)));
            steps.Add(new Step(0.6f, Agent.MovementControlFlag.None, 0, "step_in", new Vec2(0f, 1f)));
        }
    }

    /// <summary>
    /// Mounted movement test (P6, M8): ride at the opponent for 20 s with a couched-style thrust held on the way in.
    /// The driver keeps looking at the opponent, so the horse steers at it, passes, wheels round and comes back -
    /// the joust pattern - which is where the puppet horse's sliding and lag show. Both riders run it at once.
    /// </summary>
    private void BuildRide()
    {
        // One pass per 5 s: wind up the thrust when the opponent is inside 12 m, release inside 5 m (a thrust takes
        // ~0.3 s to extend and the pair close at up to 25 m/s), then wheel round on the steering and come back.
        for (int i = 0; i < 4; i++)
            steps.Add(new Step(5f, Agent.MovementControlFlag.AttackDown, 0, "ride_pass_thrust", new Vec2(0f, 0.8f), null, 12f, 5f));
    }

    /// <summary>Mounted drive-by: ride at (past) the opponent and swing right, then left, while passing.</summary>
    private void BuildRideSwing()
    {
        // Drive-by: a right swing on one pass, a left on the next; wind up inside 9 m, release inside 3.5 m.
        for (int i = 0; i < 4; i++)
        {
            int direction = i % 2 == 0 ? 1 : 0;   // right, left
            steps.Add(new Step(5f, AttackFlags[direction], 0, "ride_pass_swing_" + DirectionNames[direction], new Vec2(0f, 0.8f), null, 9f, 3.5f));
        }
    }

    /// <summary>Javelins from the saddle: ride at the opponent, aim inside 25 m, throw inside 12 m, wheel and repeat.</summary>
    private void BuildRideThrow()
    {
        steps.Add(new Step(0.8f, Agent.MovementControlFlag.None, 0, "throw_wield", Vec2.Zero, WieldThrown));
        steps.Add(new Step(0.6f, Agent.MovementControlFlag.None, 0, "throw_settle", Vec2.Zero, PrepareThrow));
        for (int i = 0; i < 4; i++)
            steps.Add(new Step(6f, Agent.MovementControlFlag.AttackUp, 0, "ride_pass_throw", new Vec2(0f, 0.8f), PrepareThrow, 25f, 12f));
    }

    /// <summary>
    /// Couched lance: gallop, press X (ToggleAlternativeWeapon couches a lance once the horse is fast enough), hold
    /// the couch through the pass, uncouch, wheel round; four passes.
    /// </summary>
    private void BuildCouch()
    {
        for (int i = 0; i < 4; i++)
        {
            steps.Add(new Step(2.0f, Agent.MovementControlFlag.None, 0, "couch_ride_in", new Vec2(0f, 1f)));
            steps.Add(new Step(0.1f, Agent.MovementControlFlag.None, Agent.EventControlFlag.ToggleAlternativeWeapon, "couch_toggle", new Vec2(0f, 1f)));
            steps.Add(new Step(3.5f, Agent.MovementControlFlag.None, 0, "couch_ride", new Vec2(0f, 1f)));
            steps.Add(new Step(0.1f, Agent.MovementControlFlag.None, Agent.EventControlFlag.ToggleAlternativeWeapon, "couch_untoggle", new Vec2(0f, 1f)));
            steps.Add(new Step(1.5f, Agent.MovementControlFlag.None, 0, "couch_wheel", new Vec2(0f, 0.6f)));
        }
    }

    /// <summary>
    /// Javelins: wield the thrown weapon, then six times hold the attack (aim) and release (throw). The driver aims
    /// at the opponent's chest with a gravity lead while this script runs; the stack is refilled before each throw.
    /// </summary>
    private void BuildThrow()
    {
        steps.Add(new Step(0.8f, Agent.MovementControlFlag.None, 0, "throw_wield", Vec2.Zero, WieldThrown));
        steps.Add(new Step(0.6f, Agent.MovementControlFlag.None, 0, "throw_settle", Vec2.Zero, PrepareThrow));
        for (int i = 0; i < 6; i++)
        {
            steps.Add(new Step(1.0f, Agent.MovementControlFlag.AttackUp, 0, "throw_hold", Vec2.Zero, PrepareThrow));
            steps.Add(new Step(1.4f, Agent.MovementControlFlag.None, 0, "throw_release"));
        }
    }

    private void BuildSwings()
    {
        for (int round = 0; round < 2; round++)
            for (int direction = 0; direction < 4; direction++)
                Attack(direction);
    }

    private void BuildThrusts()
    {
        // Down is the thrust for the weapons that can thrust; the analyser reports the action the engine actually
        // played, so a weapon that cannot thrust is visible as such rather than assumed.
        for (int i = 0; i < 5; i++) Attack(3);
        steps.Add(new Step(0.1f, Agent.MovementControlFlag.None, Agent.EventControlFlag.ToggleAlternativeWeapon, "toggle_usage"));
        steps.Add(new Step(0.6f, Agent.MovementControlFlag.None, 0, "toggle_usage_settle"));
        for (int i = 0; i < 5; i++) Attack(3);
        steps.Add(new Step(0.1f, Agent.MovementControlFlag.None, Agent.EventControlFlag.ToggleAlternativeWeapon, "toggle_usage_back"));
        steps.Add(new Step(0.6f, Agent.MovementControlFlag.None, 0, "toggle_usage_settle"));
    }

    private void BuildBlocks()
    {
        for (int round = 0; round < 2; round++)
            for (int direction = 0; direction < 4; direction++)
                Block(direction);
    }

    private void BuildKickBash()
    {
        for (int i = 0; i < 3; i++)
        {
            steps.Add(new Step(0.05f, Agent.MovementControlFlag.None, Agent.EventControlFlag.Kick, "kick"));
            steps.Add(new Step(1.4f, Agent.MovementControlFlag.None, 0, "kick_recover"));
        }
        // Vanilla has no dedicated bash input; a bash is what the engine plays for some weapons on an attack at very
        // close range. The analyser reports WeaponBash actions wherever they occur; there is nothing to script here.
    }

    private void BuildMixed(int seed)
    {
        var random = new Random(seed);
        float total = 0f;
        while (total < 60f)
        {
            int roll = random.Next(0, 10);
            if (roll < 5)
            {
                int direction = random.Next(0, 4);
                float hold = 0.4f + (float)random.NextDouble() * 0.5f;
                float rest = 0.3f + (float)random.NextDouble() * 0.4f;
                Attack(direction, hold, rest);
                total += hold + rest;
            }
            else if (roll < 8)
            {
                int direction = random.Next(0, 4);
                float hold = 0.5f + (float)random.NextDouble() * 0.6f;
                Block(direction, hold, 0.2f);
                total += hold + 0.2f;
            }
            else if (roll == 8)
            {
                steps.Add(new Step(0.05f, Agent.MovementControlFlag.None, Agent.EventControlFlag.Kick, "kick"));
                steps.Add(new Step(1.2f, Agent.MovementControlFlag.None, 0, "kick_recover"));
                total += 1.25f;
            }
            else
            {
                float rest = 0.3f + (float)random.NextDouble() * 0.5f;
                steps.Add(new Step(rest, Agent.MovementControlFlag.None, 0, "idle"));
                total += rest;
            }
        }
    }
}
#endif
