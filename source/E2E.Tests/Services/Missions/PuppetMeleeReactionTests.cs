using Missions.Agents.Combat;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Guards the rule that a node never rewrites the melee collision reaction of an agent it owns.
/// </summary>
/// <remarks>
/// The reaction handed back from Mission.MeleeHitCallback tells the engine what the weapon does next.
/// Bounced/Staggered/Stuck stop the swing; SlicedThrough lets it carry on into further collisions - and
/// local damage for a remote attacker is NOT suppressed, because AgentDamagePatch carries no patch category
/// and nothing calls PatchAllUncategorized on the Missions assembly, so it is never installed.
///
/// Rewriting the reaction for a LOCALLY OWNED attacker is therefore a live-fire bug: that node's own troops
/// keep swinging through blocks they should have bounced off, and land hits they should not have. It shows
/// up in a battle as one player's troops killing far more than the other's.
///
/// These are the cases that were shipped and tested in a live battle instead of here first.
/// </remarks>
public class PuppetMeleeReactionTests
{
    [Theory]
    [InlineData(MeleeCollisionReaction.Bounced)]
    [InlineData(MeleeCollisionReaction.Staggered)]
    [InlineData(MeleeCollisionReaction.Stuck)]
    [InlineData(MeleeCollisionReaction.SlicedThrough)]
    [InlineData(MeleeCollisionReaction.ContinueChecking)]
    public void LocallyOwnedAttacker_IsNeverRewritten_EvenWhenSuppressionIsOn(
        MeleeCollisionReaction reaction)
    {
        Assert.False(MeleeReactionPolicy.ShouldRewriteReaction(
            attackerIsLocallyControlled: true,
            reaction: reaction,
            suppressRemoteReaction: true));
    }

    [Theory]
    [InlineData(MeleeCollisionReaction.Bounced)]
    [InlineData(MeleeCollisionReaction.Staggered)]
    [InlineData(MeleeCollisionReaction.Stuck)]
    public void RemoteAttacker_InterruptingReaction_IsRewrittenOnlyWhenSuppressionIsOn(
        MeleeCollisionReaction reaction)
    {
        Assert.True(MeleeReactionPolicy.ShouldRewriteReaction(
            attackerIsLocallyControlled: false,
            reaction: reaction,
            suppressRemoteReaction: true));

        Assert.False(MeleeReactionPolicy.ShouldRewriteReaction(
            attackerIsLocallyControlled: false,
            reaction: reaction,
            suppressRemoteReaction: false));
    }

    [Theory]
    [InlineData(MeleeCollisionReaction.SlicedThrough)]
    [InlineData(MeleeCollisionReaction.ContinueChecking)]
    public void RemoteAttacker_NonInterruptingReaction_IsLeftAlone(
        MeleeCollisionReaction reaction)
    {
        Assert.False(MeleeReactionPolicy.ShouldRewriteReaction(
            attackerIsLocallyControlled: false,
            reaction: reaction,
            suppressRemoteReaction: true));
    }

    /// <summary>
    /// The shipped default. Suppression stays off until a mechanism is proven, so no reaction is rewritten
    /// on any node: measured live, Mission.MeleeHitCallback fired 21,633 times across two clients and not
    /// once with a remote attacker, so the rewrite had nothing to act on and only carried the damage risk.
    /// </summary>
    [Fact]
    public void SuppressionIsOffByDefault()
    {
        Assert.False(MeleeReactionPolicy.SuppressRemoteReaction);
    }
}
