using System;
using System.Collections.Generic;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.Inventory.Handlers;
using GameInterface.Services.Inventory.Interfaces;
using GameInterface.Services.Inventory.Messages;
using GameInterface.Services.MapEvents.Loot;
using GameInterface.Services.ObjectManager;
using Moq;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using Xunit;

namespace E2E.Tests.Services.Inventory;

/// <summary>
/// M12: donating loot for troop XP paid nothing. The current game tracks donations in
/// <c>InventoryLogic.XpGainFromDonations</c> and applies that one number in <c>DoneLogic</c>; the coop Done prefix
/// replaces DoneLogic and did not carry it, and the server recomputed XP from the items LEFT on the loot pile,
/// which never contain a donated item (donating removes it). The number now travels in <c>CompleteTrade</c>, and
/// the server grants it capped to what the party could actually have donated, priced by the discard model that
/// holds the Steward perk gates.
/// </summary>
public class TradeDonationXpTests
{
    private static CompleteTrade Trade(string partyId, float donationXp, bool canGainXp = true) => new CompleteTrade(
        fromItemRosterId: null, isFromItemRosterNull: true, toItemRosterId: "roster", fromItemRosterData: Array.Empty<ItemRosterElement>(),
        toItemRosterData: Array.Empty<ItemRosterElement>(), characterIdEquipmentsData: new Dictionary<string, EquipmentData[]>(),
        isTrading: false, canGainXpFromDiscarding: canGainXp, isManagingWarehouse: false, heroId: "hero", initialHeroId: "hero",
        totalAmount: 0, merchantGold: 0, ownerPartyId: partyId, currentMobilePartyId: null, isSettlementComponentNull: true,
        currentSettlementComponentId: null, boughtItems: Array.Empty<(ItemRosterElementData, int)>(),
        soldItems: Array.Empty<(ItemRosterElementData, int)>(), donationXp: donationXp);

    private static (TradeHandler handler, Mock<IDefaultItemDiscardModelInterface> model) Handler(IDictionary<string, ItemObject> items, int xpPerItem)
    {
        var objectManager = new Mock<IObjectManager>();
        foreach (var pair in items)
        {
            var item = pair.Value;
            objectManager.Setup(m => m.TryGetObject<ItemObject>(pair.Key, out item)).Returns(true);
        }
        var model = new Mock<IDefaultItemDiscardModelInterface>();
        model.Setup(m => m.GetXpBonusForDiscardingItem(It.IsAny<MobileParty>(), It.IsAny<ItemObject>(), It.IsAny<int>()))
            .Returns((MobileParty party, ItemObject item, int amount) => xpPerItem * amount);
        var handler = new TradeHandler(
            new Mock<IInventoryLogicInterface>().Object,
            new Mock<IMessageBroker>().Object,
            objectManager.Object,
            new Mock<INetwork>().Object,
            model.Object);
        return (handler, model);
    }

    private static void Offer(string partyId, params (string itemId, int count)[] lines)
    {
        var offerLines = new List<BattleLootOfferLine>();
        foreach (var (itemId, count) in lines)
            offerLines.Add(new BattleLootOfferLine(BattleLootLineKind.Item, itemId, null, count));
        BattleLootOfferRegistry.Shared.Register(
            new BattleLootOffer(Guid.NewGuid().ToString("N"), "MapEvent_donation_test", partyId, offerLines),
            DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void DonationXpIsGranted_UpToWhatTheOfferedLootAndTheCarriedItemsAreWorth()
    {
        BattleLootOfferRegistry.Shared.Clear();
        var sword = new ItemObject("donation_test_sword");
        var helmet = new ItemObject("donation_test_helmet");
        var (handler, _) = Handler(new Dictionary<string, ItemObject> { ["item_sword"] = sword, ["item_helmet"] = helmet }, xpPerItem: 100);
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        Offer("party_A", ("item_sword", 3));
        var carried = new ItemRoster();
        carried.AddToCounts(new EquipmentElement(helmet), 2);

        // 3 offered swords + 2 carried helmets, 100 each: the party could have donated 500.
        Assert.Equal(400f, handler.CapDonationXp(Trade("party_A", 400f), party, carried));
        Assert.Equal(500f, handler.CapDonationXp(Trade("party_A", 900f), party, carried));
    }

    [Fact]
    public void WithoutThePerks_TheDiscardModelPricesEverythingAtZero_AndNothingIsGranted()
    {
        BattleLootOfferRegistry.Shared.Clear();
        var sword = new ItemObject("donation_test_sword_noperk");
        var (handler, _) = Handler(new Dictionary<string, ItemObject> { ["item_sword"] = sword }, xpPerItem: 0);
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        Offer("party_B", ("item_sword", 5));
        Assert.Equal(0f, handler.CapDonationXp(Trade("party_B", 300f), party, new ItemRoster()));
    }

    [Fact]
    public void NoOfferAndNothingCarried_MeansNothingCanHaveBeenDonated()
    {
        BattleLootOfferRegistry.Shared.Clear();
        var (handler, _) = Handler(new Dictionary<string, ItemObject>(), xpPerItem: 100);
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        Assert.Equal(0f, handler.CapDonationXp(Trade("party_C", 250f), party, new ItemRoster()));
    }

    [Fact]
    public void AScreenThatCannotDonate_GrantsNothing_WhateverTheClientClaims()
    {
        BattleLootOfferRegistry.Shared.Clear();
        var sword = new ItemObject("donation_test_sword_trade");
        var (handler, _) = Handler(new Dictionary<string, ItemObject> { ["item_sword"] = sword }, xpPerItem: 100);
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        Offer("party_D", ("item_sword", 5));
        Assert.Equal(0f, handler.CapDonationXp(Trade("party_D", 300f, canGainXp: false), party, new ItemRoster()));
    }
}
