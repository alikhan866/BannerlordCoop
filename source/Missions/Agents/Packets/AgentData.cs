using ProtoBuf;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents.Packets
{
    [ProtoContract(SkipConstructor = true)]
    public struct AgentData
    {
        internal static Agent.MovementControlFlag GetLocomotionMovementFlags(
            Agent.MovementControlFlag movementFlags)
        {
            return movementFlags & Agent.MovementControlFlag.MoveMask;
        }

        internal static void ApplyLocomotionMovementFlags(
            Agent agent,
            Agent.MovementControlFlag movementFlags)
        {
            Agent.MovementControlFlag currentMovementFlags = agent.MovementFlags;
            Agent.MovementControlFlag currentFlags =
                currentMovementFlags & ~Agent.MovementControlFlag.MoveMask;
            Agent.MovementControlFlag desiredMovementFlags =
                currentFlags |
                GetLocomotionMovementFlags(movementFlags);
            if (currentMovementFlags == desiredMovementFlags)
                return;

            agent.MovementFlags = desiredMovementFlags;
        }

        internal static void ApplyMovementDirection(
            Agent agent,
            Vec2 movementDirection)
        {
            Vec2 current = agent.GetMovementDirection();
            if (current.X == movementDirection.X &&
                current.Y == movementDirection.Y)
            {
                return;
            }

            agent.SetMovementDirection(movementDirection);
        }

        internal static void ApplyLookDirection(
            Agent agent,
            Vec3 lookDirection)
        {
            Vec3 current = agent.LookDirection;
            if (current.X == lookDirection.X &&
                current.Y == lookDirection.Y &&
                current.Z == lookDirection.Z)
            {
                return;
            }

            agent.LookDirection = lookDirection;
        }

        internal static void ApplyMovementInput(
            Agent agent,
            Vec2 movementInput)
        {
            Vec2 current = agent.MovementInputVector;
            if (current.X == movementInput.X &&
                current.Y == movementInput.Y)
            {
                return;
            }

            agent.MovementInputVector = movementInput;
        }

        public AgentData(
            Agent agent,
            ushort mountMovementId = 0,
            string mountIdentityScopeId = null,
            System.Guid mountAgentId = default,
            int? mountAction0TurnDirection = null,
            int? mountAction0TurnActionIndex = null,
            float? mountAction0TurnProgress = null,
            bool? mountAction0IsSyntheticTurn = null)
        {
            MovementQuantizer.TryPackPosition(agent.Position, out positionPacked);
            movementDirectionPacked = MovementQuantizer.PackVec2(agent.GetMovementDirection());
            lookDirectionPacked = MovementQuantizer.PackVec3(agent.LookDirection);
            inputVectorPacked = MovementQuantizer.PackVec2(agent.MovementInputVector);
            speedRaw = MovementQuantizer.EncodeSpeed(agent.GetRealGlobalVelocity().AsVec2.Length);
            MovementFlag = (uint)GetLocomotionMovementFlags(
                agent.MovementFlags);

            // The rider can be active while its mount is mid-teardown (e.g. right after a battle concludes):
            // reading the mount's native state (MovementInputVector, etc.) then access-violates. Only capture
            // the mount while it is itself active — mirrors the rider guard in AgentMovementHandler.PollMovement
            // and the horse.IsActive() check in SyncMountState.
            Agent mount = agent.MountAgent;
            if (mount != null && mount.IsActive())
            {
                MountData = new AgentMountData(
                    mount,
                    mountMovementId,
                    mountIdentityScopeId,
                    mountAgentId,
                    mountAction0TurnDirection: mountAction0TurnDirection,
                    mountAction0TurnActionIndex: mountAction0TurnActionIndex,
                    mountAction0TurnProgress: mountAction0TurnProgress,
                    mountAction0IsSyntheticTurn: mountAction0IsSyntheticTurn);
            }
            else
            {
                MountData = null;
            }
        }

        public AgentData(Agent agent, System.Guid mountAgentId)
            : this(agent, 0, null, mountAgentId)
        {
        }

        /// <summary>
        /// Builds the packet from explicit values rather than from a live agent.
        /// </summary>
        /// <remarks>
        /// For tests, which need to describe a movement update without a native agent behind it. It exists
        /// because the alternative they used - reflecting into auto-property backing fields - silently
        /// depended on this struct storing its state as plain properties, and broke the moment the wire form
        /// was quantised. Going through the same quantiser the real constructor uses also means a test
        /// observes exactly what a peer would receive, rather than a value no packet could ever carry.
        /// </remarks>
        internal AgentData(
            Vec3 position,
            Vec2 movementDirection,
            Vec3 lookDirection,
            Vec2 inputVector,
            float speed,
            AgentMountData mountData = null,
            uint movementFlag = 0)
        {
            MovementQuantizer.TryPackPosition(position, out positionPacked);
            movementDirectionPacked = MovementQuantizer.PackVec2(movementDirection);
            lookDirectionPacked = MovementQuantizer.PackVec3(lookDirection);
            inputVectorPacked = MovementQuantizer.PackVec2(inputVector);
            speedRaw = MovementQuantizer.EncodeSpeed(speed);
            MountData = mountData;
            MovementFlag = movementFlag;
        }

        public void Apply(Agent agent)
        {
            // if the player is dead, dont sync anything
            if (agent.Health <= 0)
            {
                return;
            }

            // NOTE: position is NOT applied here. It is reconciled per-frame by AgentPositionInterpolator (fed
            // this packet's Position by AgentMovementHandler), so the ease is decoupled from the packet cadence.
            // Everything below is per-packet state that drives the puppet's own walk + animation.

            ApplyContinuousState(agent);

            // NOTE: actions/animations are NOT applied here anymore. They are events, not continuous state, so
            // they are synced separately and on-change by AgentActionHandler (reliable-ordered), not polled with
            // movement. This keeps the movement packet purely continuous state.

            // Update mount
            if (agent.HasMount)
            {
                MountData?.ApplyMount(agent.MountAgent);
            }
        }

        internal void ApplyContinuousState(Agent agent)
        {
            ApplyMovementDirection(agent, MovementDirection);
            ApplyLookDirection(agent, LookDirection);
            ApplyMovementInput(agent, GetMovementInput(agent));
            ApplyLocomotionMovementFlags(
                agent,
                (Agent.MovementControlFlag)MovementFlag);
        }

        internal Vec2 GetMovementInput(Agent agent)
        {
            // The raw owner input is local-frame and unrepresentative for AI movement modes (native retreat
            // drives the owner with no input), so derive an on-foot puppet's throttle from ground speed.
            if (agent.HasMount)
                return InputVector;

            float maxSpeed = agent.GetMaximumForwardUnlimitedSpeed();
            float throttle = maxSpeed > 0f
                ? MBMath.ClampFloat(Speed / maxSpeed, 0f, 1f)
                : 0f;
            return InputVector.LengthSquared > 0.0001f
                ? InputVector.Normalized() * throttle
                : new Vec2(0f, throttle);
        }

        // Packed to 63 bits at 1 cm, with the top bit reserved to say the position is unusable. See
        // MovementQuantizer for why the contingency lives inside the value rather than in a second field:
        // a fallback Vec3 would make this struct BIGGER than it was before quantising, and dropping the
        // agent from the batch instead risks misaligning the parallel id and data arrays, which would hand
        // one agent another's position - the exact teleport this design exists to prevent.
        [ProtoMember(1, DataFormat = DataFormat.FixedSize)]
        private ulong positionPacked;

        /// <summary>
        /// The reported position, or <see cref="Vec3.Zero"/> when this packet carries none.
        /// </summary>
        /// <remarks>
        /// ALWAYS check <see cref="HasPosition"/> before feeding this to anything that moves an agent.
        /// Zero is a real place - the scene origin - so a caller that ignores the flag will quietly walk an
        /// agent to the middle of the map instead of leaving it where it was.
        /// </remarks>
        public Vec3 Position =>
            MovementQuantizer.TryUnpackPosition(positionPacked, out Vec3 position)
                ? position
                : Vec3.Zero;

        /// <summary>False when the sender could not represent this agent's position.</summary>
        /// <remarks>
        /// Only reachable if a position is non-finite or further than about 10 km from the scene origin.
        /// Neither should ever happen; the flag exists so that if one does, the receiver holds the last
        /// position it trusted instead of being told something false.
        /// </remarks>
        public bool HasPosition => !MovementQuantizer.IsPositionUnavailable(positionPacked);

        // The four members below are stored quantised and exposed as vectors, so callers and tests are
        // unchanged while the wire carries a third fewer bytes. See MovementQuantizer for the precision
        // argument: a step is ~0.002 degrees against a sender that will not transmit a change under 0.57.
        //
        // FixedSize is deliberate. A packed direction with negative components sets its high bits, so a
        // varint would cost MORE for an agent facing away from the origin than towards it - a wire whose
        // size depends on which way a soldier is looking is not one worth reasoning about.
        [ProtoMember(2, DataFormat = DataFormat.FixedSize)]
        private uint inputVectorPacked;
        [ProtoMember(3, DataFormat = DataFormat.FixedSize)]
        private ulong lookDirectionPacked;
        [ProtoMember(4, DataFormat = DataFormat.FixedSize)]
        private uint movementDirectionPacked;
        [ProtoMember(10)]
        private ushort speedRaw;

        public Vec2 InputVector => MovementQuantizer.UnpackVec2(inputVectorPacked);
        public Vec3 LookDirection => MovementQuantizer.UnpackVec3(lookDirectionPacked);
        public Vec2 MovementDirection => MovementQuantizer.UnpackVec2(movementDirectionPacked);
        // 5 was AgentEquipmentData — wield state moved to reliable on-change updates.
        // 6 was ActionData — actions moved to the event-driven AgentActionHandler.
        [ProtoMember(7)]
        public AgentMountData MountData { get; }
        /// <summary>The owner's real ground speed, m/s — drives the on-foot puppet's locomotion throttle.</summary>
        /// <summary>The owner's real ground speed, m/s - quantised to a millimetre per second.</summary>
        public float Speed => MovementQuantizer.DecodeSpeed(speedRaw);
        /// <summary>The owner's current translation and turn inputs.</summary>
        [ProtoMember(9)]
        public uint MovementFlag { get; }
    }
}
