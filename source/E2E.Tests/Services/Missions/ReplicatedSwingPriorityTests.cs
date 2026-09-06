using Missions.Agents.Packets;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// A replicated melee swing must be written over the puppet's own action priority.
/// </summary>
/// <remarks>
/// This is the rule that made remote attacks visible. Without it the engine arbitrates the write against
/// the puppet's current action, and the native controller - which has no attack input for that agent,
/// because the attack is happening on its owner's machine - immediately reclaims the channel. The call
/// reports success either way, which is why the bug survived so long: every delivery-side counter said the
/// wind-up had arrived and been applied, while the animation never appeared on screen.
///
/// Measured owner-versus-puppet on two clients sharing a clock, comparing the same swing at the same
/// instant:
///
///     wind-up rendered   1.5% -> 84.9%, 85.8%, 83.8%   (three consecutive runs)
///     release rendered  41.8% -> 91.7%, 90.6%, 90.2%
///
/// The scoping matters as much as the rule. Making every replicated action ignore priority would let a
/// stale swing sit on top of a death or a fall, so only melee swings and guard direction transitions are
/// allowed to override.
/// </remarks>
public class ReplicatedSwingPriorityTests
{
    [Fact]
    public void MeleeSwing_OverridesLocalPriority()
    {
        Assert.True(AgentActionData.ShouldIgnorePriority(
            forceGuardDirectionTransition: false,
            incomingIsMeleeSwing: true));
    }

    [Fact]
    public void GuardDirectionTransition_StillOverrides()
    {
        Assert.True(AgentActionData.ShouldIgnorePriority(
            forceGuardDirectionTransition: true,
            incomingIsMeleeSwing: false));
    }

    /// <summary>
    /// Anything that is neither keeps normal arbitration, so a death or fall is not overridden by a
    /// swing that arrived late.
    /// </summary>
    [Fact]
    public void OtherActions_KeepNormalArbitration()
    {
        Assert.False(AgentActionData.ShouldIgnorePriority(
            forceGuardDirectionTransition: false,
            incomingIsMeleeSwing: false));
    }

    /// <summary>
    /// The owner leaving a held wind-up (a feint) must take the puppet out of it too; the engine otherwise
    /// keeps the puppet winding up an attack that no longer exists (duel rig: set=-1 ok=0 refusals).
    /// </summary>
    [Fact]
    public void OwnerLeavingHeldWindup_Overrides()
    {
        Assert.True(AgentActionData.ShouldIgnorePriority(
            forceGuardDirectionTransition: false,
            incomingIsMeleeSwing: false,
            puppetHoldsReadyMelee: true));
        Assert.False(AgentActionData.ShouldIgnorePriority(
            forceGuardDirectionTransition: false,
            incomingIsMeleeSwing: false,
            puppetHoldsReadyMelee: false));
    }
}
