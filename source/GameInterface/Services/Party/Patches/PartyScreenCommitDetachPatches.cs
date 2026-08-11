using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Party.Patches;

/// <summary>
/// Stops the party screen's commit-time reset from detaching the heroes whose entries it clears.
/// </summary>
/// <remarks>
/// Closing the party screen runs <c>DoneLogic</c>, and the coop patch does NOT apply the roster changes
/// locally - the server is authoritative for those. It still calls <c>Reset(true)</c> though, and that
/// reaches <c>PartyScreenData.ResetUsing</c> -> <c>TroopRoster.Clear()</c> on rosters still bound to the
/// live party. Vanilla treats removing a HERO from a party roster as detaching that hero, so clearing
/// them cascades into the real campaign objects:
///
///   TroopRoster.Clear -> PartyBase.OnHeroRemoved -> Hero.PartyBelongedTo = null
///                     -> PartyComponent.ChangePartyLeader(null) -> LordPartyComponent._leader = null
///
/// The whole reset runs inside an <c>AllowedThread</c>, so the leader change takes the "apply locally,
/// publish nothing" branch: the client quietly loses the leader of the party it was just looking at while
/// the server keeps its own, with no error logged on either side. That is the divergence behind a lord
/// party that shows no leader on the client only - its size limit collapses to the base value and Army
/// Management stops listing it - and behind a companion who ends up belonging to no party at all.
/// Rejoining "fixed" it because the client rebuilt its state from the server.
///
/// Half of this was already known: <c>SetMoveModeHoldPatches</c> blocks <c>SetMoveModeHold</c> during the
/// same commit, which is the call <c>ChangePartyLeader(null)</c> makes two lines after nulling the leader.
/// The parked party was suppressed; the cause was left running. This blocks the cascade at its source
/// instead, which covers the leader, the hero's party membership and the move mode together.
/// </remarks>
[HarmonyPatch]
internal class PartyScreenCommitDetachPatches
{
    private static IEnumerable<MethodBase> TargetMethods() => new MethodBase[]
    {
        // internal (Hero heroObject, TroopRoster roster) - not reachable by name from here.
        AccessTools.Method(typeof(PartyBase), "OnHeroRemoved"),
    };

    /// <summary>
    /// Whether a hero removed from a roster should really be detached from their party.
    /// </summary>
    /// <remarks>
    /// Only false while the party screen is committing. Every other removal - a hero genuinely leaving,
    /// being captured, dying - still has to detach, so this must not become a blanket suppression.
    /// </remarks>
    internal static bool ShouldApplyHeroDetach(bool inPartyScreenCommit) => !inPartyScreenCommit;

    private static bool Prefix() => ShouldApplyHeroDetach(PartyScreenLogicPatches.InCommit);
}
