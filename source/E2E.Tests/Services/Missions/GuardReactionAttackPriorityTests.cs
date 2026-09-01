using Missions.Agents.Packets;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// An incoming attack must not be discarded to keep a parry or block flinch on screen.
/// </summary>
/// <remarks>
/// <para>
/// Preserving a guard reaction is deliberate: without it, a flinch gets overwritten before it is visible. But it
/// was preserving against EVERYTHING, attacks included. Measured on a live client, 992 incoming melee swings were
/// refused this way in a few minutes of fighting - every one by this predicate - and the swing then never
/// rendered: no animation, no sound, damage from nowhere. A side-by-side recording of the same agent showed the
/// client missing 71% of the owner's swing time.
/// </para>
/// <para>
/// Hosts never saw it because a locally simulated agent never goes through the remote apply path at all, which is
/// exactly the asymmetry that was reported from the start.
/// </para>
/// </remarks>
public class GuardReactionAttackPriorityTests
{
    [Fact]
    public void AnIncomingAttackIsNeverDiscardedToKeepAFlinch()
    {
        Assert.False(AgentActionData.ShouldSuppressForGuardReaction(
            incomingIsMeleeSwing: true,
            guardReactionWouldPreserve: true));
    }

    /// <summary>The behaviour that must survive: a flinch is still protected from ordinary guard traffic.</summary>
    [Fact]
    public void AFlinchIsStillPreservedAgainstNonAttacks()
    {
        Assert.True(AgentActionData.ShouldSuppressForGuardReaction(
            incomingIsMeleeSwing: false,
            guardReactionWouldPreserve: true));
    }

    [Fact]
    public void NothingIsSuppressedWhenThereIsNoFlinchToPreserve()
    {
        Assert.False(AgentActionData.ShouldSuppressForGuardReaction(false, false));
        Assert.False(AgentActionData.ShouldSuppressForGuardReaction(true, false));
    }

    /// <summary>
    /// Only real swings get priority. Parries, blocks, reloads and drawn bows all sit inside the engine's
    /// attack-type range, and treating them as attacks is what made several earlier measurements meaningless.
    /// </summary>
    [Theory]
    [InlineData(Agent.ActionCodeType.ReadyMelee, true)]
    [InlineData(Agent.ActionCodeType.ReleaseMelee, true)]
    [InlineData(Agent.ActionCodeType.ParriedMelee, false)]
    [InlineData(Agent.ActionCodeType.BlockedMelee, false)]
    [InlineData(Agent.ActionCodeType.ReadyRanged, false)]
    [InlineData(Agent.ActionCodeType.Reload, false)]
    [InlineData(Agent.ActionCodeType.Guard, false)]
    [InlineData(Agent.ActionCodeType.Idle, false)]
    public void OnlyRealMeleeSwingsCountAsAttacks(Agent.ActionCodeType actionType, bool expected)
    {
        Assert.Equal(expected, AgentActionData.IsMeleeSwingType(actionType));
    }
}
