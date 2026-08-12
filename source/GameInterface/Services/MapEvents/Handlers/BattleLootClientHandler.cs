using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.MapEvents.Loot;
using GameInterface.Services.MapEvents.Messages.Loot;
using GameInterface.Services.ObjectManager;
using Serilog;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>
/// The client half of the spoils transaction: hold the offer, answer it with what the player took.
/// </summary>
/// <remarks>
/// The answer is derived from what is LEFT on the encounter's staged rosters, never from the party. Vanilla's
/// loot screen removes from those staged rosters as the player takes things, and nothing else writes to them -
/// whereas the party is live, so a replicated update landing while the screen is open would be read as loot
/// the player chose.
/// </remarks>
internal class BattleLootClientHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleLootClientHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;

    public BattleLootClientHandler(IMessageBroker messageBroker, INetwork network, IObjectManager objectManager)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;

        messageBroker.Subscribe<NetworkBattleLootOffer>(Handle_Offer);
        messageBroker.Subscribe<NetworkBattleLootApplied>(Handle_Applied);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkBattleLootOffer>(Handle_Offer);
        messageBroker.Unsubscribe<NetworkBattleLootApplied>(Handle_Applied);
    }

    private void Handle_Offer(MessagePayload<NetworkBattleLootOffer> payload)
    {
        if (ModInformation.IsServer) return;

        var offer = payload.What.Offer;

        // An offer with nothing in it is answered here and now, and never becomes pending. Otherwise it
        // deadlocks: the finalize handler holds the teardown open while an offer is outstanding, the answer
        // is sent when the encounter finishes, and an empty offer opens no loot screen for the player to
        // click through - so nothing would ever finish the encounter that the answer was waiting on. This is
        // not hypothetical; a battle won by killing every enemy outright yields zero prisoners.
        if (offer.IsEmpty)
        {
            var partyId = string.Empty;
            var mainParty = MobileParty.MainParty;
            if (mainParty != null) objectManager.TryGetId(mainParty, out partyId);

            network.SendAll(new NetworkBattleLootResult(
                partyId, BattleLootSelection.FromRemaining(offer, null)));

            Logger.Information(
                "[Loot] Offer {Offer} for {MapEvent} was empty; answered immediately so the encounter can close",
                offer.OfferId, offer.MapEventId);
            return;
        }

        ClientBattleLootOffer.Remember(offer);

        Logger.Information(
            "[Loot] Offered {Lines} line(s) for {MapEvent} (offer {Offer})",
            offer.Lines?.Length ?? 0, offer.MapEventId, offer.OfferId);
    }

    private void Handle_Applied(MessagePayload<NetworkBattleLootApplied> payload)
    {
        if (ModInformation.IsServer) return;

        var data = payload.What;

        if (!data.Applied)
        {
            // Nothing was paid out. While the changeover is in progress the server answers "NotYetApplied"
            // and the old award paths are still what actually credit the player, so the local rosters are
            // already correct - overwriting them from an empty snapshot would take the loot away.
            Logger.Information(
                "[Loot] Offer {Offer} was not applied ({Refusal}); leaving the local rosters as they are",
                data.OfferId, data.Refusal);
            return;
        }

        // TODO (M6): apply data.Snapshot as absolute state once the server actually pays out. It is deliberately
        // NOT applied while AppliesResults is false, because the snapshot then describes a party the server has
        // credited by the old mirror rather than by this transaction, and the two are not the same thing yet.
        Logger.Information("[Loot] Offer {Offer} applied by the server", data.OfferId);
    }

    /// <summary>
    /// Answers the outstanding offer using whatever the player left on the staged rosters.
    /// </summary>
    /// <remarks>
    /// Called from the PlayerEncounter.Finish prefix, which is the moment those rosters hold exactly what was
    /// declined - the screen has run, and the encounter has not yet thrown them away.
    /// </remarks>
    public void AnswerOutstandingOffer(PlayerEncounter encounter)
    {
        if (ModInformation.IsServer) return;
        if (!ClientBattleLootOffer.TryTake(out var offer)) return;

        var partyId = string.Empty;
        var mainParty = MobileParty.MainParty;
        if (mainParty != null) objectManager.TryGetId(mainParty, out partyId);

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer,
            PackItems(encounter?.RosterToReceiveLootItems),
            PackTroops(encounter?.RosterToReceiveLootMembers),
            PackTroops(encounter?.RosterToReceiveLootPrisoners));

        var result = BattleLootSelection.FromRemaining(offer, remaining);

        network.SendAll(new NetworkBattleLootResult(partyId, result));

        Logger.Information(
            "[Loot] Answered offer {Offer} for {MapEvent} with {Claims} claim(s)",
            offer.OfferId, offer.MapEventId, result.Claims?.Length ?? 0);
    }

    private List<BattleLootItemStack> PackItems(ItemRoster roster)
    {
        var stacks = new List<BattleLootItemStack>();
        if (roster == null) return stacks;

        foreach (var element in roster)
        {
            var item = element.EquipmentElement.Item;
            if (item == null || element.Amount <= 0) continue;
            if (!objectManager.TryGetId(item, out var itemId)) continue;

            string modifierId = null;
            var modifier = element.EquipmentElement.ItemModifier;
            if (modifier != null) objectManager.TryGetId(modifier, out modifierId);

            stacks.Add(new BattleLootItemStack(itemId, modifierId, element.Amount));
        }

        return stacks;
    }

    private List<TroopRosters.Data.TroopRosterElementData> PackTroops(TroopRoster roster)
    {
        var packed = new List<TroopRosters.Data.TroopRosterElementData>();
        if (roster == null) return packed;

        for (int i = 0; i < roster.Count; i++)
        {
            var element = roster.GetElementCopyAtIndex(i);
            if (element.Character == null || element.Number <= 0) continue;
            if (!objectManager.TryGetId(element.Character, out var characterId)) continue;

            packed.Add(new TroopRosters.Data.TroopRosterElementData(
                characterId, element.Number, element.WoundedNumber, element.Xp));
        }

        return packed;
    }
}
