using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MobileParties.Messages.Lifetime;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.MobileParties.Patches;

/// <summary>
/// Patches for lifecycle of <see cref="MobileParty"/> objects.
/// </summary>
[HarmonyPatch(typeof(MobileParty))]
internal class PartyLifetimePatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<PartyLifetimePatches>();

    [HarmonyPatch(MethodType.Constructor)]
    [HarmonyPostfix]
    private static void PostfixCtor(MobileParty __instance)
    {
        Logger.Debug("MobileParty created: {StringId}", __instance.StringId);
    }
}

[HarmonyPatch(typeof(DestroyPartyAction))]
internal class DestroyPartyActionPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<DestroyPartyActionPatch>();
    [HarmonyPatch(nameof(DestroyPartyAction.Apply))]
    [HarmonyPrefix]
    internal static bool PrefixApply(PartyBase destroyerParty, MobileParty destroyedParty)
    {
        // Checked before the skip-patches guard so player parties stay protected even when a
        // destroy runs nested inside another action's AllowedThread scope.
        if (IsProtectedPlayerParty(destroyedParty)) return false;

        if (CallOriginalPolicy.IsOriginalAllowed()) return true;

        if (ModInformation.IsClient)
        {
            // A player destroying a party directly (e.g. recruiting surrendering bandits via
            // dialogue) only ever happens on the conversing client, and the local destroy alone
            // leaves the party alive on every other peer (a zombie). Forward it so the server
            // destroys the party authoritatively and the destruction replicates to the other peers.
            if (destroyerParty == MobileParty.MainParty?.Party)
            {
                MessageBroker.Instance.Publish(null, new DestroyPartyRequested(destroyerParty, destroyedParty));
            }
            else
            {
                Logger.Error("Client attempted to apply DestroyPartyAction for party {partyName}, {StringId}", destroyedParty.Name, destroyedParty.StringId);
            }

            // A server-managed party may only die via the replicated destroy (the receive path
            // applies under AllowedThread and never reaches here). Vanilla menu-init cleanup —
            // e.g. the town menu destroying an empty garrison — would otherwise run the full local
            // removal and leave a half-dead party (null CurrentSettlement, IsGarrison still set)
            // that crashes anything walking it, like the siege spawn's morale checks. A party the
            // server never registered (e.g. quest-spawned) still destroys locally as before.
            //
            // The client only destroys locally when it can PROVE the server does not own the party.
            // "Not registered" used to be taken as that proof, but it is indistinguishable from
            // "not registered yet": during a join the object manager may be unresolvable or still
            // filling, so vanilla cleanup silently deleted parties the server still had. The joiner
            // then held fewer parties than the baseline and the join could never converge - measured
            // live as baseline=1538 / client=1537, the missing party being the garrison of the very
            // castle the joining player was sitting in, because that client is the only one that
            // runs that settlement's menu-init.
            //
            // Denying wrongly costs a party that lingers until the next sync corrects it.
            // Allowing wrongly costs a party the server still has, and a join that never completes.
            if (!ContainerProvider.TryResolve<IObjectManager>(out var objectManager))
            {
                Logger.Warning(
                    "Blocked a local destroy of {StringId}: the object manager is unavailable, so server ownership cannot be ruled out",
                    destroyedParty?.StringId);
                return false;
            }

            if (IsSettlementOwnedParty(destroyedParty))
            {
                Logger.Warning(
                    "Blocked a local destroy of settlement-owned party {StringId}; only the server may destroy it",
                    destroyedParty?.StringId);
                return false;
            }

            if (objectManager.TryGetId(destroyedParty, out _))
            {
                return false;
            }

            return true;
        }

        MessageBroker.Instance.Publish(null, new DestroyPartyApplied(destroyerParty, destroyedParty));
        return true;
    }

    /// <summary>
    /// Never destroy a party owned by a connected player. On the server a remote player's party
    /// is NOT MobileParty.MainParty, so vanilla's main-party guard does not protect it; a lost/
    /// finalized MapEvent (MapEventSide.HandleMapEventEndForPartyInternal) would call
    /// DestroyPartyAction.Apply on MobileParty_Player and remove it from the object manager.
    /// The party then no longer resolves and a subsequent settlement encounter activates an
    /// empty/unregistered menu -> null GameMenu NRE in MenuContext.HandleStates.
    /// Blocking here also prevents publishing DestroyPartyApplied, so clients keep the party too.
    /// </summary>
    /// <summary>
    /// A party that belongs to a settlement - a garrison or a militia - and is therefore created and
    /// owned by the server, never locally by a client.
    /// </summary>
    /// <remarks>
    /// These are the parties vanilla cleans up on menu init, and the ones a joining client is most
    /// likely to hold in a half-registered state, so they need to be safe even when registration
    /// cannot answer the question. Quest-spawned client-local parties are neither.
    /// </remarks>
    private static bool IsSettlementOwnedParty(MobileParty party)
        => party != null && (party.IsGarrison || party.IsMilitia);

    private static bool IsProtectedPlayerParty(MobileParty destroyedParty)
    {
        if (destroyedParty == null || !destroyedParty.IsPlayerParty()) return false;

        Logger.Warning("Blocked DestroyPartyAction for player party {partyName}, {StringId}", destroyedParty.Name, destroyedParty.StringId);
        return true;
    }

    [HarmonyPatch(nameof(DestroyPartyAction.ApplyForDisbanding))]
    [HarmonyPrefix]
    private static bool PrefixApplyForDisbanding(MobileParty disbandedParty, Settlement relatedSettlement)
    {
        if (IsProtectedPlayerParty(disbandedParty)) return false;

        if (CallOriginalPolicy.IsOriginalAllowed()) return true;

        if (ModInformation.IsClient)
        {
            Logger.Error("Client attempted to apply DestroyPartyAction for disbanding party {partyName}, {StringId}", disbandedParty.Name, disbandedParty.StringId);
            return true;
        }

        MessageBroker.Instance.Publish(null, new PartyDisbanded(disbandedParty, relatedSettlement));
        return true;
    }
}
