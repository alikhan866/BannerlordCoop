using Common;
using Common.Logging;
using Serilog;
using System.Linq;
using TaleWorlds.CampaignSystem;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MobileParties.Messages.Behavior;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.MobileParties.Patches
{
    [HarmonyPatch(typeof(EnterSettlementAction))]
    internal class EnterSettlementActionPatches
    {
        private static readonly ILogger Logger = LogManager.GetLogger<EnterSettlementActionPatches>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnterSettlementAction.ApplyForParty))]
        private static bool ApplyForPartyPrefix(ref MobileParty mobileParty, ref Settlement settlement)
        {
            // Idempotency first, and it must stay first: SettlementInterface.PartyEnterSettlement
            // applies entries under an AllowedThread, so letting an allowed original past this check
            // makes a party already inside re-enter.
            if (mobileParty == null || mobileParty.CurrentSettlement == settlement) return false;

            // An explicitly allowed original then wins over the map-event veto below. Without this,
            // a caller that deliberately opened an AllowedThread - the server applying an
            // authoritative entry, or a client completing a break-in - was silently dropped whenever
            // the party was attached to a map-event side, which is the normal case during a siege.
            if (CallOriginalPolicy.IsOriginalAllowed()) return true;

            if (mobileParty.Party?.MapEventSide != null) return false;

            var message = new PartyEnterSettlementAttempted(settlement, mobileParty);
            MessageBroker.Instance.Publish(mobileParty, message);

            return ModInformation.IsServer;
        }

        /// <summary>
        /// Brings a PLAYER army's attached parties into the settlement with their leader.
        /// </summary>
        /// <remarks>
        /// Vanilla does this in EnterSettlementAction.ApplyInternal, but only for the local main party:
        ///
        ///     if (mobileParty == MobileParty.MainParty &amp;&amp; MobileParty.MainParty.Army != null
        ///         &amp;&amp; MobileParty.MainParty.Army.LeaderParty == MobileParty.MainParty)
        ///         foreach (var attached in ...AttachedParties) ApplyForParty(attached, settlement);
        ///
        /// On a coop server MobileParty.MainParty is the dummy campaign hero, which has no party at all,
        /// so that loop never runs for EITHER player's army and the lords riding with it are never entered
        /// into the settlement.
        ///
        /// The visible symptom is prisoners. PartiesSellPrisonerCampaignBehavior sells a party's prisoners
        /// from OnSettlementEntered - an entry EVENT - so a lord who is never entered never sells, and the
        /// prisoners sit there until the army disbands and the lord walks into a town under its own AI.
        /// Recruitment appears to work in the same situation only because it runs from HourlyTickParty,
        /// which merely asks 'am I inside a settlement', and so never needed the event at all.
        /// </remarks>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(EnterSettlementAction.ApplyForParty))]
        private static void ApplyForPartyPostfix(
            MobileParty mobileParty,
            Settlement settlement,
            bool __runOriginal)
        {
            // The prefix above refuses plenty of entries - already inside, map-event side, client. If the
            // leader did not actually enter, its army must not be dragged in behind it.
            if (!__runOriginal) return;
            if (!ModInformation.IsServer) return;
            if (mobileParty == null || settlement == null) return;

            Army army = mobileParty.Army;
            if (army == null || army.LeaderParty != mobileParty) return;

            // Scoped to player armies, which is what vanilla's MainParty check meant. Widening it to AI
            // armies would change AI prisoner economics across the whole map, which is not this bug.
            if (!mobileParty.IsPlayerParty()) return;

            // Copy first: ApplyForParty can call Army.AddPartyToMergedParties, which mutates
            // AttachedParties while it is being enumerated.
            var attached = mobileParty.AttachedParties.ToList();
            int entered = 0, announced = 0;
            foreach (MobileParty attachedParty in attached)
            {
                if (attachedParty == null || attachedParty == mobileParty) continue;

                if (attachedParty.CurrentSettlement != settlement)
                {
                    // Not placed inside yet - a normal entry does the positioning and raises the events.
                    EnterSettlementAction.ApplyForParty(attachedParty, settlement);
                    entered++;
                    continue;
                }

                // Already standing inside, because MobileParty's CurrentSettlement setter pushes the
                // value down to every attached party:
                //
                //     foreach (MobileParty attachedParty in _attachedParties)
                //         attachedParty.CurrentSettlement = value;
                //
                // That places them in the settlement and raises NOTHING. Vanilla makes up for it by
                // calling ApplyForParty on each attached party, which does raise the events - but only
                // for MobileParty.MainParty, which on a coop server is a dummy hero with no party.
                //
                // So the positioning already happened and only the announcement is missing. Raise the
                // same three events ApplyInternal would have, rather than forcing a second entry: the
                // party is where it should be, and re-entering it would fight the idempotency guard in
                // the prefix above, which exists for good reasons of its own.
                CampaignEventDispatcher.Instance.OnBeforeSettlementEntered(
                    attachedParty, settlement, attachedParty.LeaderHero);
                CampaignEventDispatcher.Instance.OnSettlementEntered(
                    attachedParty, settlement, attachedParty.LeaderHero);
                CampaignEventDispatcher.Instance.OnAfterSettlementEntered(
                    attachedParty, settlement, attachedParty.LeaderHero);
                announced++;
            }

            Logger.Verbose(
                "[ArmyEnter] leader={Leader} settlement={Settlement} attached={Attached} entered={Entered} announced={Announced}",
                mobileParty.StringId, settlement.StringId, attached.Count, entered, announced);
        }
    }
}
