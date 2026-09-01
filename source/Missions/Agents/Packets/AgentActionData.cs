using ProtoBuf;
using Missions.Agents.Handlers;
#if DEBUG
using Missions.Diagnostics;
#endif
using System;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents.Packets
{
    [ProtoContract(SkipConstructor = true)]
    public class AgentActionData
    {
        internal const Agent.MovementControlFlag DefendMovementFlagsMask =
            Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock;

        // MBAPI.IMBAnimation is a non-public static field. The publicizer makes it compile, but the
        // emitted IgnoresAccessChecksTo isn't honored in every runtime load context (it throws
        // FieldAccessException in live play). Reflecting a non-public static field always works, so
        // resolve action names through here instead of touching MBAPI.IMBAnimation directly.
        private static readonly FieldInfo AnimationField =
            typeof(MBAPI).GetField("IMBAnimation", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static MethodInfo getActionNameWithCode;

        internal static string GetActionNameWithCode(int actionCode)
        {
            var animation = AnimationField?.GetValue(null);
            if (animation == null) return null;

            if (getActionNameWithCode == null)
            {
                getActionNameWithCode = animation.GetType().GetMethod("GetActionNameWithCode", new[] { typeof(int) });
            }

            return getActionNameWithCode?.Invoke(animation, new object[] { actionCode }) as string;
        }

        internal static bool TryResolveActionIndex(
            int actionIndex,
            out ActionIndexCache action)
        {
            string actionName = GetActionNameWithCode(actionIndex);
            if (actionName != null)
            {
                action = ActionIndexCache.Create(actionName);
                return action.Index >= 0;
            }

            // The E2E runtime has no animation resolver. Keep its narrow raw-index fallback without
            // allowing an unknown wire index through when the live engine can validate it.
            if (actionIndex >= 0 && AnimationField?.GetValue(null) == null)
            {
                action = new ActionIndexCache(actionIndex);
                return true;
            }

            action = ActionIndexCache.act_none;
            return false;
        }

        internal static Agent.MovementControlFlag GetDefendMovementFlags(
            Agent.MovementControlFlag movementFlags)
        {
            return movementFlags & DefendMovementFlagsMask;
        }

        internal static Agent.MovementControlFlag GetEffectiveDefendMovementFlags(
            Agent agent)
        {
            Agent.MovementControlFlag defendFlags =
                GetDefendMovementFlags(agent.MovementFlags);
            if (defendFlags != Agent.MovementControlFlag.None)
                return defendFlags;

            if (agent.HasMount)
            {
                defendFlags = GetDefendMovementFlags(
                    agent.GetDefendMovementFlag());
                // Mounted aim reports a defend direction even while idle. DefendBlock is the held-input bit.
                if ((defendFlags & Agent.MovementControlFlag.DefendBlock) != 0)
                    return defendFlags;
            }

            Agent.ActionCodeType action0Type = agent.GetCurrentActionType(0);
            Agent.ActionCodeType action1Type = agent.GetCurrentActionType(1);
            if (!IsDefendingAction(action0Type) && !IsDefendingAction(action1Type))
                return Agent.MovementControlFlag.None;

            // Guard actions can outlive the frame's defend flags, so recompute them while the action is active.
            defendFlags = GetDefendMovementFlags(agent.GetDefendMovementFlag());
            if (defendFlags != Agent.MovementControlFlag.None)
                return defendFlags;

            // Mounted directionless guards still need held defend input on the puppet. On foot, a lingering
            // release animation must not synthesize a fresh block after native input has cleared.
            return agent.HasMount
                ? Agent.MovementControlFlag.DefendBlock
                : Agent.MovementControlFlag.None;
        }

        internal static void ApplyDefendMovementFlags(
            Agent agent,
            Agent.MovementControlFlag defendFlags)
        {
            Agent.MovementControlFlag movementFlags =
                agent.MovementFlags & ~DefendMovementFlagsMask;
            agent.MovementFlags = movementFlags | GetDefendMovementFlags(defendFlags);
        }

        internal static void ApplyGuardState(
            Agent agent,
            Agent.GuardMode guardMode,
            bool force = false)
        {
            if (IsGuardMode(guardMode))
            {
                if (force || agent.CurrentGuardMode != guardMode)
                    agent.SetWeaponGuard(GuardModeToUsageDirection(guardMode));
                return;
            }

            if (guardMode == Agent.GuardMode.None &&
                (force || IsGuardMode(agent.CurrentGuardMode)))
            {
                agent.ResetGuard();
            }
        }

        internal static void ApplyGuardDirectionTransition(
            Agent agent,
            Agent.GuardMode guardMode)
        {
            if (!IsGuardMode(guardMode))
                return;

            agent.ResetGuard();
            ApplyGuardState(agent, guardMode, force: true);
        }

        private static void ClearMountedGuardDirectionAction(
            Agent agent,
            int channel)
        {
            // Retire the cyclic sibling before native guard input can select it again.
#if DEBUG
            MissionActionDiagnostics.RecordActionCommand(
                agent,
                channel,
                ActionIndexCache.act_none.Index,
                startProgress: 0f,
                AnimFlags.anf_restart,
                "mounted-guard-clear");
#endif
#if DEBUG
            ActionWriteLog.Record(ActionWriteLog.Source.MountedGuardClear, 0f, restart: true);
#endif
            agent.SetActionChannel(
                channel,
                ActionIndexCache.act_none,
                ignorePriority: true,
                additionalFlags: AnimFlags.anf_restart,
                forceFaceMorphRestart: false);
        }

        internal static bool IsGuardMode(Agent.GuardMode guardMode) =>
            guardMode == Agent.GuardMode.Up
            || guardMode == Agent.GuardMode.Down
            || guardMode == Agent.GuardMode.Left
            || guardMode == Agent.GuardMode.Right;

        internal static Agent.GuardMode GetGuardModeFromDefendFlags(
            Agent.MovementControlFlag defendFlags)
        {
            if ((defendFlags & Agent.MovementControlFlag.DefendDown) != 0)
                return Agent.GuardMode.Down;
            if ((defendFlags & Agent.MovementControlFlag.DefendUp) != 0)
                return Agent.GuardMode.Up;
            if ((defendFlags & Agent.MovementControlFlag.DefendLeft) != 0)
                return Agent.GuardMode.Left;
            if ((defendFlags & Agent.MovementControlFlag.DefendRight) != 0)
                return Agent.GuardMode.Right;

            return Agent.GuardMode.None;
        }

        internal static Agent.GuardMode GetEffectiveGuardMode(
            Agent agent,
            Agent.MovementControlFlag defendFlags)
        {
            Agent.GuardMode guardMode = agent.CurrentGuardMode;
            if (!agent.HasMount && IsGuardMode(guardMode))
                return guardMode;

            if (defendFlags == Agent.MovementControlFlag.None)
            {
                return IsGuardMode(guardMode)
                    ? guardMode
                    : Agent.GuardMode.None;
            }

            if (agent.HasMount)
            {
                guardMode = GetGuardModeFromDefendingAction(agent);
                if (IsGuardMode(guardMode))
                    return guardMode;
            }

            guardMode = agent.CurrentGuardMode;
            if (IsGuardMode(guardMode))
                return guardMode;

            Agent.GuardMode flagGuardMode =
                GetGuardModeFromDefendFlags(defendFlags);
            if (IsGuardMode(flagGuardMode))
                return flagGuardMode;

            guardMode = GetGuardModeFromDefendDirection(
                agent.GetCurrentActionDirection(1));
            if (IsGuardMode(guardMode))
                return guardMode;

            return GetGuardModeFromDefendDirection(
                agent.GetCurrentActionDirection(0));
        }

        internal static Agent.MovementControlFlag AlignDefendDirection(
            Agent.MovementControlFlag defendFlags,
            Agent.GuardMode guardMode)
        {
            if (defendFlags == Agent.MovementControlFlag.None ||
                !IsGuardMode(guardMode))
            {
                return defendFlags;
            }

            return (defendFlags &
                    ~Agent.MovementControlFlag.DefendDirMask) |
                GuardModeToDefendFlag(guardMode);
        }

        internal static bool IsDefendingAction(Agent.ActionCodeType actionType)
        {
            return (actionType >= Agent.ActionCodeType.DefendAllBegin
                    && actionType < Agent.ActionCodeType.DefendAllEnd)
                || actionType == Agent.ActionCodeType.Guard;
        }

        internal static bool IsGuardPresentationAction(
            Agent.ActionCodeType actionType)
        {
            return IsDefendingAction(actionType)
                || IsGuardReactionAction(actionType);
        }

        /// <summary>Whether this packet reports the agent mid melee swing on either channel.</summary>
        internal static bool CarriesMeleeSwing(AgentActionData data) =>
            data != null && (data.Action0IsMeleeSwing || data.Action1IsMeleeSwing);
        /// <summary>A real melee swing, as opposed to the parries, blocks, reloads and drawn bows that also
        /// live inside AttackMeleeAndRangedAllBegin..End.</summary>
        /// <summary>
        /// Whether a replicated action is written over the puppet's own action priority.
        /// </summary>
        /// <remarks>
        /// A replicated MELEE SWING must be, or the engine arbitrates our write against the puppet's
        /// current action and the native controller - which has no attack input for this agent, since the
        /// attack is happening on the owner's machine - takes the channel straight back. The call still
        /// succeeds, so nothing reports a failure; the animation is simply gone before the next frame.
        /// Measured on two clients: the puppet rendered the owner's wind-up in 1.5-4.6% of sampled moments
        /// before this, and 84-86% after, with the release going from 37-42% to 90-92%.
        ///
        /// Everything else keeps normal arbitration, so death, fall and dismount still win over a stale
        /// swing. That is why this is not simply `true`.
        /// </remarks>
        internal static bool ShouldIgnorePriority(
            bool forceGuardDirectionTransition,
            bool incomingIsMeleeSwing) =>
            forceGuardDirectionTransition || incomingIsMeleeSwing;
        internal static bool IsMeleeSwingType(Agent.ActionCodeType actionType) =>
            actionType == Agent.ActionCodeType.ReadyMelee
            || actionType == Agent.ActionCodeType.ReleaseMelee;

        /// <summary>
        /// Whether a guard reaction should be preserved against THIS incoming action.
        /// </summary>
        /// <remarks>
        /// Preserving a parry or block flinch is deliberate and correct - against other guard and defend
        /// updates. It must not swallow an ATTACK. Measured live, 992 incoming melee swings were refused this
        /// way in a few minutes of fighting, every one of them by the guard-reaction predicate, and the swing
        /// then never rendered at all: no animation, no sound, and damage arriving out of nowhere. Hosts were
        /// unaffected because locally simulated agents never take this path.
        /// </remarks>
        internal static bool ShouldSuppressForGuardReaction(
            bool incomingIsMeleeSwing,
            bool guardReactionWouldPreserve) =>
            !incomingIsMeleeSwing && guardReactionWouldPreserve;
        internal static bool IsGuardReactionAction(
            Agent.ActionCodeType actionType)
        {
            return actionType == Agent.ActionCodeType.ParriedMelee
                || actionType == Agent.ActionCodeType.BlockedMelee;
        }

        private static int GetGuardPresentationChannel(Agent agent)
        {
            if (!agent.HasMount)
                return -1;

            Agent.ActionCodeType action1Type = agent.GetCurrentActionType(1);
            Agent.ActionCodeType action0Type = agent.GetCurrentActionType(0);
            if (IsGuardReactionAction(action1Type))
                return 1;
            if (IsGuardReactionAction(action0Type))
                return 0;
            if (IsDefendingAction(action1Type))
                return 1;
            if (IsDefendingAction(action0Type))
                return 0;

            return -1;
        }

        private static int GetGuardActionChannel(
            Agent agent,
            int guardPresentationChannel)
        {
            if (guardPresentationChannel >= 0)
                return guardPresentationChannel;

            if (IsDefendingAction(agent.GetCurrentActionType(1)))
                return 1;
            if (IsDefendingAction(agent.GetCurrentActionType(0)))
                return 0;

            return -1;
        }

        private static Agent.GuardMode GetGuardModeFromDefendDirection(
            Agent.UsageDirection direction) =>
            direction switch
            {
                Agent.UsageDirection.DefendUp => Agent.GuardMode.Up,
                Agent.UsageDirection.DefendDown => Agent.GuardMode.Down,
                Agent.UsageDirection.DefendLeft => Agent.GuardMode.Left,
                Agent.UsageDirection.DefendRight => Agent.GuardMode.Right,
                _ => Agent.GuardMode.None
            };

        internal static Agent.GuardMode GetGuardModeFromDefendingAction(
            Agent agent)
        {
            Agent.GuardMode guardMode =
                GetGuardModeFromDefendingAction(agent, 1);
            return IsGuardMode(guardMode)
                ? guardMode
                : GetGuardModeFromDefendingAction(agent, 0);
        }

        internal static Agent.GuardMode GetGuardModeFromDefendingAction(
            Agent agent,
            int channel)
        {
            if (!IsDefendingAction(agent.GetCurrentActionType(channel)))
                return Agent.GuardMode.None;

            return GetGuardModeFromDefendDirection(
                agent.GetCurrentActionDirection(channel));
        }

        private static Agent.MovementControlFlag GuardModeToDefendFlag(
            Agent.GuardMode guardMode) =>
            guardMode switch
            {
                Agent.GuardMode.Up =>
                    Agent.MovementControlFlag.DefendUp,
                Agent.GuardMode.Down =>
                    Agent.MovementControlFlag.DefendDown,
                Agent.GuardMode.Left =>
                    Agent.MovementControlFlag.DefendLeft,
                Agent.GuardMode.Right =>
                    Agent.MovementControlFlag.DefendRight,
                _ => Agent.MovementControlFlag.None
            };

        private static Agent.UsageDirection GuardModeToUsageDirection(
            Agent.GuardMode guardMode) =>
            guardMode switch
            {
                Agent.GuardMode.Up => Agent.UsageDirection.AttackUp,
                Agent.GuardMode.Down => Agent.UsageDirection.AttackDown,
                Agent.GuardMode.Left => Agent.UsageDirection.AttackLeft,
                Agent.GuardMode.Right => Agent.UsageDirection.AttackRight,
                _ => Agent.UsageDirection.None
            };

        private static int ToWireGuardState(Agent.GuardMode guardMode) =>
            IsGuardMode(guardMode) ? (int)guardMode + 1 : 0;

        private static Agent.GuardMode FromWireGuardState(int guardState) =>
            guardState > 0 ? (Agent.GuardMode)(guardState - 1) : Agent.GuardMode.None;

        public AgentActionData(Agent agent)
            : this(agent, GetEffectiveDefendMovementFlags(agent))
        {
        }

        private AgentActionData(
            Agent agent,
            Agent.MovementControlFlag defendFlags)
            : this(
                agent,
                defendFlags,
                GetEffectiveGuardMode(agent, defendFlags))
        {
        }

        internal AgentActionData(
            Agent agent,
            Agent.MovementControlFlag defendFlags,
            Agent.GuardMode guardMode,
            int guardReactionChannel = -1)
            : this(
                agent,
                defendFlags,
                guardMode,
                guardReactionChannel,
                GetCurrentActionSpeed(agent, 0),
                GetCurrentActionSpeed(agent, 1))
        {
        }

        internal AgentActionData(
            Agent agent,
            Agent.MovementControlFlag defendFlags,
            Agent.GuardMode guardMode,
            int guardReactionChannel,
            float? action0Speed,
            float? action1Speed)
        {
            ActionIndexCache cache0 = agent.GetCurrentAction(0);
            ActionIndexCache cache1 = agent.GetCurrentAction(1);
            bool isPlayerControlled =
                agent.Controller == AgentControllerType.Player;

            if (IsGuardMode(guardMode))
            {
                defendFlags = AlignDefendDirection(
                    defendFlags,
                    guardMode);
                if (isPlayerControlled)
                {
                    defendFlags |=
                        Agent.MovementControlFlag.DefendBlock;
                }
            }

            Agent.MovementControlFlag movementFlags = agent.MovementFlags;
            movementFlags &= ~DefendMovementFlagsMask;
            movementFlags |= defendFlags;

            MovementFlag = (uint)movementFlags;
            EventFlag = (uint)agent.EventControlFlags;
            CrouchMode = agent.CrouchMode;
            GuardState = ToWireGuardState(guardMode);

            Action0IsMeleeSwing = IsMeleeSwingType(agent.GetCurrentActionType(0));
            Action1IsMeleeSwing = IsMeleeSwingType(agent.GetCurrentActionType(1));
            Action0Index = cache0.Index;
            Action0Progress = agent.GetCurrentActionProgress(0);
            Action0Flag = (ulong)agent.GetCurrentAnimationFlag(0);
            Action0Speed = action0Speed;
            Action1Index = cache1.Index;
            Action1Progress = agent.GetCurrentActionProgress(1);
            Action1Flag = (ulong)agent.GetCurrentAnimationFlag(1);
            Action1Speed = action1Speed;
            int validGuardReactionChannel =
                guardReactionChannel >= 0
                && guardReactionChannel <= 1
                    ? guardReactionChannel
                    : -1;
            GuardPresentationChannel =
                agent.HasMount && validGuardReactionChannel >= 0
                    ? validGuardReactionChannel
                    : GetGuardPresentationChannel(agent);
            GuardActionChannel =
                validGuardReactionChannel >= 0
                    ? validGuardReactionChannel
                    : GetGuardActionChannel(
                        agent,
                        GuardPresentationChannel);
            GuardActionIsDefending =
                GuardActionChannel >= 0
                && IsDefendingAction(
                    agent.GetCurrentActionType(
                        GuardActionChannel));
            GuardActionIsReaction =
                GuardActionChannel >= 0
                && (guardReactionChannel == GuardActionChannel
                    || IsGuardReactionAction(
                        agent.GetCurrentActionType(
                            GuardActionChannel)));
            IsMounted = agent.HasMount;
            IsPlayerControlled = isPlayerControlled;
        }

        public void Apply(
            Agent agent,
            IAgentVisualActionAccessor visualActionAccessor,
            bool suppressMountedGuardActionTransition = false,
            bool neutralizeMountedGuardDirection = false)
        {
            Agent.MovementControlFlag movementFlags = (Agent.MovementControlFlag)MovementFlag;
            agent.EventControlFlags |= (Agent.EventControlFlag)EventFlag;
            if (neutralizeMountedGuardDirection)
            {
                movementFlags &=
                    ~Agent.MovementControlFlag.DefendDirMask;
            }
            // Apply held input before action transitions so an explicit guard direction remains the final native command.
            ApplyDefendMovementFlags(agent, movementFlags);

            // Install action transitions, but let an unchanged native action advance on its local timeline.
            ApplyActionChannel(
                agent,
                visualActionAccessor,
                channel: 0,
                Action0Index,
                Action0Progress,
                Action0Flag,
                Action0Speed,
                suppressMountedGuardActionTransition);
            ApplyActionChannel(
                agent,
                visualActionAccessor,
                channel: 1,
                Action1Index,
                Action1Progress,
                Action1Flag,
                Action1Speed,
                suppressMountedGuardActionTransition);

#if DEBUG
            // AFTER both channels, so this is the guard the puppet is actually left holding - the copy an
            // attacking client would judge a block against. Compared with what THIS apply asked for.
            if (GuardSyncDiagnostics.Enabled)
            {
                try
                {
                    // The wire's guard mode against the direction the puppet's action actually ends up on.
                    // The first version compared GetDefendMovementFlags on both sides, which returned 0 for
                    // every one of 68,906 observations - a 100% match rate that compared 0 to 0 and meant
                    // nothing at all.
                    GuardSyncDiagnostics.Record(
                        agent.Index,
                        (int)GuardMode,
                        (int)GetDefendMovementFlags(movementFlags),
                        (int)GetDefendMovementFlags(agent.MovementFlags),
                        (int)GetGuardModeFromDefendDirection(
                            agent.GetCurrentActionDirection(1)),
                        (int)agent.GetCurrentActionType(1));
                }
                catch (Exception)
                {
                    // A diagnostic must never take a battle down.
                }
            }
#endif
        }

        private void ApplyActionChannel(
            Agent agent,
            IAgentVisualActionAccessor visualActionAccessor,
            int channel,
            int actionIndex,
            float actionProgress,
            ulong actionFlag,
            float? actionSpeed,
            bool suppressMountedGuardActionTransition)
        {
#if DEBUG
            // Puppet state captured BEFORE any decision, so every outcome reports what the puppet was doing
            // when the call was made. The previous version read it back after SetActionChannel on the applied
            // path, which just echoed the value it had written.
            int tracePuppetIndex = 0;
            float tracePuppetProgress = 0f;
            int tracePuppetType = 0;
            bool traceEnabled = ActionApplyTrace.Enabled;
            if (traceEnabled)
            {
                try
                {
                    tracePuppetIndex = agent.GetCurrentAction(channel).Index;
                    tracePuppetProgress = agent.GetCurrentActionProgress(channel);
                    tracePuppetType = (int)agent.GetCurrentActionType(channel);
                }
                catch (Exception) { traceEnabled = false; }
            }

#endif
            // Evaluated separately so the trace can name WHICH predicate refused, while keeping the exact
            // short-circuit order the original expression had.
            bool suppressReleasedPlayerGuard = ShouldSuppressReleasedPlayerGuardAction(channel);
            bool suppressMountedGuard =
                !suppressReleasedPlayerGuard
                && suppressMountedGuardActionTransition
                && GuardActionChannel == channel;
            bool incomingIsMeleeSwing = channel == 0
                ? Action0IsMeleeSwing
                : Action1IsMeleeSwing;
            bool suppressGuardReaction =
                !suppressReleasedPlayerGuard
                && !suppressMountedGuard
                && ShouldSuppressForGuardReaction(
                    incomingIsMeleeSwing,
                    ShouldPreserveCurrentGuardReaction(agent, channel));
            if (suppressReleasedPlayerGuard || suppressMountedGuard || suppressGuardReaction)
            {
#if DEBUG
                ActionWriteLog.SwingArrival(IsWindupForChannel(channel), 1);
                TraceApply(
                    suppressReleasedPlayerGuard
                        ? ActionApplyTrace.Outcome.SuppressedPlayerGuard
                        : suppressMountedGuard
                            ? ActionApplyTrace.Outcome.SuppressedMountedGuard
                            : ActionApplyTrace.Outcome.SuppressedGuardReaction,
                    agent, channel, actionIndex, actionProgress,
                    traceEnabled, tracePuppetIndex, tracePuppetProgress, tracePuppetType);
#endif
                return;
            }

            float resolvedActionSpeed = actionSpeed ?? 1f;
            if (!NeedsActionTransition(
                    agent,
                    channel,
                    actionIndex,
                    visualActionAccessor,
                    preserveVisibleAction:
                        IsMounted && GuardActionChannel == channel,
                    preserveCurrentGuardReaction: false))
            {
                if (actionSpeed.HasValue
                    && actionIndex >= 0
                    && agent.GetCurrentAction(channel).Index == actionIndex)
                {
                    agent.SetCurrentActionSpeed(channel, resolvedActionSpeed);
                }
#if DEBUG
                // The blind spot every earlier hypothesis was argued inside: roughly 9,300 swing-starts a
                // battle were declined here and nothing recorded why.
                ActionWriteLog.SwingArrival(IsWindupForChannel(channel), 2);
                TraceApply(
                    tracePuppetIndex == actionIndex
                        ? ActionApplyTrace.Outcome.NoTransitionSameIndex
                        : ActionApplyTrace.Outcome.NoTransitionPreserved,
                    agent, channel, actionIndex, actionProgress,
                    traceEnabled, tracePuppetIndex, tracePuppetProgress, tracePuppetType);
                // Nothing transitioned, so the state read before the decision is still the current state.
                if (traceEnabled
                    && channel == 1
                    && MeleeSwingDiagnostics.Enabled
                    && (tracePuppetType == (int)Agent.ActionCodeType.ReadyMelee
                        || tracePuppetType == (int)Agent.ActionCodeType.ReleaseMelee))
                {
                    bool readable = TryGetCurrentActionSpeed(agent, channel, out float currentSpeed);
                    MeleeSwingDiagnostics.Record(
                        agent.Index,
                        channel,
                        tracePuppetIndex,
                        actionProgress,
                        tracePuppetProgress,
                        actionSpeed.HasValue,
                        resolvedActionSpeed,
                        currentSpeed,
                        readable,
                        applied: false,
                        restartFlag: ((AnimFlags)actionFlag & AnimFlags.anf_restart) != 0);
                }
#endif
                return;
            }

            if (!TryResolveActionTransition(
                    agent,
                    channel,
                    actionIndex,
                    out ActionIndexCache action))
            {
#if DEBUG
                ActionWriteLog.SwingArrival(IsWindupForChannel(channel), 3);
                TraceApply(
                    ActionApplyTrace.Outcome.ResolveFailed,
                    agent, channel, actionIndex, actionProgress,
                    traceEnabled, tracePuppetIndex, tracePuppetProgress, tracePuppetType);
#endif
                return;
            }

            bool forceGuardDirectionTransition =
                ShouldForceMountedGuardDirectionTransition(
                    agent,
                    channel);
            AnimFlags actionFlags = (AnimFlags)actionFlag;
            if (forceGuardDirectionTransition)
            {
                ClearMountedGuardDirectionAction(agent, channel);
                ApplyGuardDirectionTransition(agent, GuardMode);
                actionFlags |= AnimFlags.anf_restart;
            }
#if DEBUG
            MissionActionDiagnostics.RecordActionCommand(
                agent,
                channel,
                action.Index,
                actionProgress,
                actionFlags,
                "action-packet");
#endif
#if DEBUG
            // Read BEFORE the set: what the puppet was doing when this attack replaced it. That is what
            // separates 'the apply arrived late' from 'the puppet keeps falling out and re-entering the same
            // swing', and those two want opposite fixes.
            int previousActionIndex = agent.GetCurrentAction(channel).Index;
#endif
#if DEBUG
            ActionWriteLog.Record(
                ActionWriteLog.Source.ActionApply,
                actionProgress,
                ((AnimFlags)actionFlag & AnimFlags.anf_restart) != 0);
#endif
            // ignorePriority also has to cover a replicated MELEE SWING, not just guard transitions.
            // Without it the engine arbitrates our write against the puppet's own action priority and the
            // native controller - which has no attack input for this agent, because the attack is happening
            // on the owner's machine - takes the channel straight back. The write returns successfully and
            // the animation is gone before the next sample.
            //
            // That asymmetry is exactly what the measurements showed. The guard paths already pass
            // ignorePriority: true, and guard state was reached 100% of the time (90,517 commands, 0
            // failures) while the puppet rendered the owner's wind-up in 1.5-4.6% of sampled moments. The
            // guard was never overwriting the swing - DESTROYED_WINDUP=0 - the swing was never there to
            // overwrite, and 75% of arriving wind-up packets had to transition the action again because the
            // previous one had already been dropped.
            //
            // The owner is authority for its own agent's animation, so local priority must not veto it.
            // Scoped to melee swings: everything else keeps normal arbitration, so death, fall and
            // dismount animations still win over a stale swing.
            agent.SetActionChannel(
                channel,
                action,
                ignorePriority: ShouldIgnorePriority(forceGuardDirectionTransition, incomingIsMeleeSwing),
                additionalFlags: actionFlags,
                actionSpeed: resolvedActionSpeed,
                startProgress: actionProgress);

#if DEBUG
            ActionWriteLog.SwingArrival(IsWindupForChannel(channel), 0);
            TraceApply(
                ActionApplyTrace.Outcome.Applied,
                agent, channel, actionIndex, actionProgress,
                traceEnabled, tracePuppetIndex, tracePuppetProgress, tracePuppetType);

            // Speed and progress read AFTER the set, paired with the request from THIS call. Reading before
            // compared a new request against the previous value and reported ordinary churn as failure.
            if (traceEnabled
                && channel == 1
                && MeleeSwingDiagnostics.Enabled
                && IsMeleeSwingType(agent, channel))
            {
                bool readable = TryGetCurrentActionSpeed(agent, channel, out float appliedSpeed);
                MeleeSwingDiagnostics.Record(
                    agent.Index,
                    channel,
                    actionIndex,
                    actionProgress,
                    agent.GetCurrentActionProgress(channel),
                    actionSpeed.HasValue,
                    resolvedActionSpeed,
                    appliedSpeed,
                    readable,
                    applied: true,
                    restartFlag: ((AnimFlags)actionFlag & AnimFlags.anf_restart) != 0);
            }
            // After the set, so the agent reports the action that was just started rather than the one it
            // replaced. This is what measures how much wind-up the defender never got to see.
            WindupDiagnostics.RecordAppliedAction(
                agent,
                channel,
                action,
                actionProgress,
                previousActionIndex);
#endif
        }

#if DEBUG
        /// <summary>
        /// Captures what the puppet was doing at the moment an apply decision was taken. Reads only
        /// GetCurrentAction / GetCurrentActionType / GetCurrentActionProgress, all of which this path already
        /// calls - deliberately nothing new, after a native ActionSet read crashed a live client.
        /// </summary>
        private static bool IsMeleeSwingType(Agent agent, int channel)
        {
            try
            {
                Agent.ActionCodeType type = agent.GetCurrentActionType(channel);
                return type == Agent.ActionCodeType.ReadyMelee
                    || type == Agent.ActionCodeType.ReleaseMelee;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Whether THIS packet says the given channel carries a wind-up, read straight off the wire
        /// rather than from a type map learned only from actions that succeeded in being applied.</summary>
        private bool IsWindupForChannel(int channel) =>
            channel == 0 ? Action0IsMeleeSwing : Action1IsMeleeSwing;

        private static void TraceApply(
            ActionApplyTrace.Outcome outcome,
            Agent agent,
            int channel,
            int actionIndex,
            float actionProgress,
            bool captured,
            int puppetActionIndex,
            float puppetProgress,
            int puppetActionType)
        {
            if (!captured || agent == null) return;

            ActionApplyTrace.Record(
                outcome,
                agent,
                channel,
                actionIndex,
                actionProgress,
                puppetActionIndex,
                puppetProgress,
                puppetActionType);
        }
#endif
        // Agent exposes only a speed setter, so read the rendered channel through its publicized skeleton API.
        /// <summary>
        /// Reads the rendered animation speed, distinguishing UNREADABLE from 1.0x.
        /// </summary>
        /// <remarks>
        /// <see cref="GetCurrentActionSpeed"/> returns 1f on every failure path, so a failed read is
        /// indistinguishable from an animation genuinely playing at normal speed. A measurement built on that
        /// put 44% of its samples in the 1.0x bucket and concluded the puppet was playing too fast; the bucket
        /// may simply have been the failures. Anything comparing speeds must use this instead.
        /// </remarks>
        internal static bool TryGetCurrentActionSpeed(
            Agent agent,
            int channel,
            out float speed)
        {
            speed = 1f;
            Skeleton skeleton = null;
            try
            {
                MBAgentVisuals visuals = agent?.AgentVisuals;
                if (ReferenceEquals(visuals, null) || !visuals.IsValid()) return false;

                skeleton = visuals.GetSkeleton();
                if (ReferenceEquals(skeleton, null)) return false;

                float read = skeleton.GetAnimationSpeedAtChannel(channel);
                if (float.IsNaN(read) || float.IsInfinity(read) || read < 0f) return false;

                speed = read;
                return true;
            }
            catch (NullReferenceException)
            {
                return false;
            }
            finally
            {
                if (!ReferenceEquals(skeleton, null))
                    skeleton.ManualInvalidate();
            }
        }
        internal static float GetCurrentActionSpeed(Agent agent, int channel)
        {
            Skeleton skeleton = null;
            try
            {
                MBAgentVisuals visuals = agent?.AgentVisuals;
                if (ReferenceEquals(visuals, null) || !visuals.IsValid()) return 1f;

                skeleton = visuals.GetSkeleton();
                if (ReferenceEquals(skeleton, null)) return 1f;

                float speed = skeleton.GetAnimationSpeedAtChannel(channel);
                return float.IsNaN(speed) || float.IsInfinity(speed)
                    ? 1f
                    : Math.Max(0f, speed);
            }
            catch (NullReferenceException)
            {
                return 1f;
            }
            finally
            {
                if (!ReferenceEquals(skeleton, null))
                    skeleton.ManualInvalidate();
            }
        }

        private bool ShouldSuppressReleasedPlayerGuardAction(
            int channel)
        {
            return IsPlayerControlled
                && GuardActionChannel == channel
                && GuardActionIsDefending
                && DefendFlags == Agent.MovementControlFlag.None
                && !IsGuardMode(GuardMode);
        }

        private bool TryResolveActionTransition(
            Agent agent,
            int channel,
            int actionIndex,
            out ActionIndexCache action)
        {
            string actionName = GetActionNameWithCode(actionIndex);
            if (actionName != null)
            {
                action = ActionIndexCache.Create(actionName);
                return true;
            }

            Agent.GuardMode currentActionGuardMode =
                GetGuardModeFromDefendDirection(
                    agent.GetCurrentActionDirection(channel));
            if (actionIndex >= 0
                && IsMounted
                && GuardActionChannel == channel
                && IsGuardMode(GuardMode)
                && IsGuardMode(currentActionGuardMode)
                && currentActionGuardMode != GuardMode)
            {
                // The synchronized native index is sufficient when a stale sibling guard must transition.
                action = new ActionIndexCache(actionIndex);
                return true;
            }

            action = new ActionIndexCache(-1);
            return false;
        }

        private bool ShouldForceMountedGuardDirectionTransition(
            Agent agent,
            int channel)
        {
            if (!IsMounted
                || GuardActionChannel != channel
                || !IsGuardMode(GuardMode))
            {
                return false;
            }

            Agent.GuardMode currentActionGuardMode =
                GetGuardModeFromDefendDirection(
                    agent.GetCurrentActionDirection(channel));
            // Equal-priority mounted guard siblings can reject this one-shot transition.
            return IsGuardMode(currentActionGuardMode)
                && currentActionGuardMode != GuardMode;
        }

        private static bool NeedsActionTransition(
            Agent agent,
            int channel,
            int expectedActionIndex,
            IAgentVisualActionAccessor visualActionAccessor,
            bool preserveVisibleAction,
            bool preserveCurrentGuardReaction)
        {
            if (preserveCurrentGuardReaction)
                return false;

            ActionIndexCache currentAction =
                agent.GetCurrentAction(channel);
            if (currentAction != ActionIndexCache.act_none)
                return currentAction.Index != expectedActionIndex;
            if (expectedActionIndex == ActionIndexCache.act_none.Index)
                return false;
            if (!preserveVisibleAction)
                return true;

            ActionIndexCache expectedAction =
                new ActionIndexCache(expectedActionIndex);
            return !visualActionAccessor.IsActionVisible(
                agent,
                channel,
                in expectedAction);
        }

        internal bool ShouldPreserveCurrentGuardReaction(
            Agent agent,
            int channel)
        {
            if (GuardActionIsReaction
                || GuardActionChannel != channel
                || !GuardActionIsDefending
                || (DefendFlags == Agent.MovementControlFlag.None
                    && !IsGuardMode(GuardMode)))
            {
                return false;
            }

            Agent.ActionCodeType actionType =
                agent.GetCurrentActionType(channel);
            if (IsGuardReactionAction(actionType))
                return true;

            Agent.GuardMode currentActionGuardMode =
                GetGuardModeFromDefendDirection(
                    agent.GetCurrentActionDirection(channel));
            if (IsGuardMode(GuardMode)
                && IsGuardMode(currentActionGuardMode)
                && currentActionGuardMode != GuardMode)
            {
                return false;
            }

            return IsDefendingAction(actionType)
                && agent.GetCurrentActionStage(channel)
                    == Agent.ActionStage.DefendParry;
        }

        [ProtoMember(1)]
        public float Action0Progress { get; }
        [ProtoMember(2)]
        public ulong Action0Flag { get; }
        [ProtoMember(3)]
        public int Action0Index { get; }
        [ProtoMember(4)]
        public float Action1Progress { get; }
        [ProtoMember(5)]
        public ulong Action1Flag { get; }
        [ProtoMember(6)]
        public int Action1Index { get; }
        [ProtoMember(7)]
        public uint MovementFlag { get; }
        [ProtoMember(8)]
        public uint EventFlag { get; }
        [ProtoMember(9)]
        public byte StateFlags { get; private set; }
        [ProtoMember(10)]
        public int GuardState { get; }
        [ProtoMember(11)]
        public int GuardPresentationChannel { get; }
        [ProtoMember(12)]
        public int GuardActionChannel { get; }
        // Nullable keeps packets from older peers (where these fields are absent) at the native 1x default.
        [ProtoMember(13)]
        public float? Action0Speed { get; }
        [ProtoMember(14)]
        public float? Action1Speed { get; }
        [ProtoIgnore]
        public bool CrouchMode
        {
            get => HasFlag(1);
            private set => SetFlag(1, value);
        }
        [ProtoIgnore]
        public bool IsMounted
        {
            get => HasFlag(2);
            private set => SetFlag(2, value);
        }
        [ProtoIgnore]
        public bool GuardActionIsDefending
        {
            get => HasFlag(4);
            private set => SetFlag(4, value);
        }
        [ProtoIgnore]
        public bool IsPlayerControlled
        {
            get => HasFlag(8);
            private set => SetFlag(8, value);
        }
        [ProtoIgnore]
        public bool GuardActionIsReaction
        {
            get => HasFlag(16);
            private set => SetFlag(16, value);
        }

        /// <summary>
        /// Whether this channel's action is a melee swing (ReadyMelee / ReleaseMelee).
        /// </summary>
        /// <remarks>
        /// Set by the sender, which already knows the action's type. The receiver only has a bare action
        /// index, and the one engine call that could resolve a type from an index reaches through
        /// <c>agent.ActionSet</c> - which crashed a live client with an access violation earlier in this
        /// investigation. Riding a spare bit of an existing byte costs nothing on the wire.
        /// </remarks>
        [ProtoIgnore]
        public bool Action0IsMeleeSwing
        {
            get => HasFlag(32);
            private set => SetFlag(32, value);
        }

        [ProtoIgnore]
        public bool Action1IsMeleeSwing
        {
            get => HasFlag(64);
            private set => SetFlag(64, value);
        }

        private bool HasFlag(byte flag) =>
            (StateFlags & flag) != 0;

        private void SetFlag(
            byte flag,
            bool enabled)
        {
            StateFlags = enabled
                ? (byte)(StateFlags | flag)
                : (byte)(StateFlags & ~flag);
        }

        internal Agent.MovementControlFlag DefendFlags =>
            GetDefendMovementFlags((Agent.MovementControlFlag)MovementFlag);
        internal Agent.GuardMode GuardMode => FromWireGuardState(GuardState);
    }
}
