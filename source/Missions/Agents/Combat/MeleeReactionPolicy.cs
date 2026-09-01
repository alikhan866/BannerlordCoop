using TaleWorlds.MountAndBlade;

namespace Missions.Agents.Combat;

/// <summary>
/// Whether a node may rewrite the melee collision reaction the engine picked for an attacker.
/// </summary>
/// <remarks>
/// The reaction returned from Mission.MeleeHitCallback tells the engine what the weapon does next.
/// Bounced/Staggered/Stuck stop the swing; SlicedThrough lets it carry on into further collisions - and
/// local damage for a remote attacker is NOT suppressed, because AgentDamagePatch carries no patch category
/// and nothing calls PatchAllUncategorized on the Missions assembly, so it is never installed.
///
/// Rewriting the reaction of a LOCALLY OWNED attacker is therefore a live-fire bug: that node's own troops
/// swing through blocks they should have bounced off and land hits they should not have, which shows up in
/// a battle as one player's troops out-killing the other's.
///
/// No patch calls this today. A Postfix on Mission.MeleeHitCallback was built to act on it and then removed:
/// measured live it fired 21,633 times across two clients with puppetCalls=0 - the callback never once ran
/// with a remote attacker, so the local engine does not resolve a puppet swing's collisions and the recoil
/// that truncates puppet swings does not originate there. The rule is kept because it is the one that was
/// broken, and because anything that reaches for this hook again needs it.
/// </remarks>
internal static class MeleeReactionPolicy
{
    /// <summary>Off until a mechanism is proven; overriding a reaction changes local hit resolution.</summary>
    internal static bool SuppressRemoteReaction { get; set; }

    internal static bool ShouldRewriteReaction(
        bool attackerIsLocallyControlled,
        MeleeCollisionReaction reaction,
        bool suppressRemoteReaction)
    {
        // A locally owned attacker is this node's own business and must never be touched.
        if (attackerIsLocallyControlled) return false;
        if (!suppressRemoteReaction) return false;
        return reaction == MeleeCollisionReaction.Bounced
            || reaction == MeleeCollisionReaction.Staggered
            || reaction == MeleeCollisionReaction.Stuck;
    }
}
