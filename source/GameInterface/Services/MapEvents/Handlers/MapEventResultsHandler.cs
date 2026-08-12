using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.MapEvents.Data;
using GameInterface.Services.MapEvents.Interfaces;
using GameInterface.Services.MapEvents.Loot;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Messages.Loot;
using GameInterface.Services.TroopRosters.Data;
using LiteNetLib;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using GameInterface.Services.MapEventParties;
using GameInterface.Services.MapEventParties.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using System;
using Serilog;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Handlers;

internal class MapEventResultsHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<MapEventResultsHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IMapEventResultsInterface mapEventResultsInterface;
    private readonly IMapEventContributionBarrier contributionBarrier;
    private readonly IPlayerManager playerManager;

    public MapEventResultsHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IMapEventResultsInterface mapEventResultsInterface,
        IMapEventContributionBarrier contributionBarrier,
        IPlayerManager playerManager)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.mapEventResultsInterface = mapEventResultsInterface;
        this.contributionBarrier = contributionBarrier;
        this.playerManager = playerManager;

        messageBroker.Subscribe<CommitMapEventResults>(Handle_CommitMapEventResults);
        messageBroker.Subscribe<NetworkCommitMapEventResults>(Handle_NetworkCommitMapEventResults);
        messageBroker.Subscribe<MapEventContributionFlushRequested>(Handle_MapEventContributionFlushRequested);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<CommitMapEventResults>(Handle_CommitMapEventResults);
        messageBroker.Unsubscribe<NetworkCommitMapEventResults>(Handle_NetworkCommitMapEventResults);
        messageBroker.Unsubscribe<MapEventContributionFlushRequested>(Handle_MapEventContributionFlushRequested);
    }

    private void Handle_MapEventContributionFlushRequested(
        MessagePayload<MapEventContributionFlushRequested> payload)
    {
        if (ModInformation.IsClient) return;

        // Keep this inline so the publishing patch cannot continue into result or teardown before the flush.
        if (payload.What.MapEventParty != null)
            contributionBarrier.Flush(payload.What.MapEventParty);
        else
            contributionBarrier.Flush(payload.What.MapEvent);
    }

    private void Handle_CommitMapEventResults(MessagePayload<CommitMapEventResults> obj)
    {
        var mapEvent = obj.What.MapEvent;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetIdWithLogging(mapEvent, out var mapEventId)) return;

            contributionBarrier.Flush(mapEvent);
            mapEventResultsInterface.CalculateAndCommitMapEventResults(mapEvent, out NetworkPlayerLootData networkPlayerLootData);

            foreach (var player in playerManager.Players)
            {
                if (!playerManager.TryGetPeer(player.ControllerId, out var peer) ||
                    !TryGetPlayerMapEventParty(mapEvent, player.MobilePartyId, out var playerMapEventParty, out var playerSide) ||
                    !objectManager.TryGetIdWithLogging(playerMapEventParty, out var playerMapEventPartyId))
                {
                    continue;
                }

                network.Send(peer, new NetworkCommitMapEventResults(
                    mapEventId,
                    mapEvent.WinningSide,
                    playerSide,
                    playerMapEventPartyId,
                    networkPlayerLootData));

                // The send half of a send/receive pair. The results were reaching the wire - measured at two
                // packets and 13KB - while the client handler never ran once, and with logging on only one end
                // there was no way to tell a packet that never arrived from one that arrived and was dropped.
                Logger.Information("[Loot] Sent results of {MapEvent} to {Controller} (winner={Winner}, playerSide={Side}, party={Party})",
                    mapEventId, player.ControllerId, mapEvent.WinningSide, playerSide, playerMapEventPartyId);

                OfferLootToPlayer(peer, mapEventId, player.MobilePartyId, playerMapEventPartyId, networkPlayerLootData);

            }
        });
    }

    /// <summary>
    /// Registers what this player won as an OFFER and tells them about it.
    /// </summary>
    /// <remarks>
    /// Sent alongside the existing results packet rather than instead of it. The offer is the server's own
    /// record of what it is willing to hand over, which is the thing a client's answer is later checked
    /// against - without it the client's claim would be the only description of the loot, and a claim that
    /// describes itself can describe itself generously.
    ///
    /// This is now the ONLY way a player's battle spoils reach them. The server used to mirror the loot onto
    /// its own copy of the party here and the client used to rescue it locally; both are gone, because between
    /// them they credited the same spoils twice and moved heroes by roster copy - which left a hero owned by
    /// nobody and, in one live save, a doubled hero that broke battle reserve building outright.
    ///
    /// A failure here must never cost a player their results: the packet above has already gone, and this is
    /// bookkeeping on top of it.
    /// </remarks>
    private void OfferLootToPlayer(
        NetPeer peer,
        string mapEventId,
        string partyId,
        string mapEventPartyId,
        NetworkPlayerLootData loot)
    {
        try
        {
            var offer = BattleLootOfferBuilder.Build(
                Guid.NewGuid().ToString("N"),
                mapEventId,
                partyId,
                PackItems(loot, mapEventPartyId),
                PackTroops(loot.LootedMembers, mapEventPartyId),
                PackTroops(loot.LootedPrisoners, mapEventPartyId),
                IsHeroId);

            // An offer from an earlier wave of the same siege must not still be answerable once this one
            // exists, or a late reply to the old one would be honoured against the new battle.
            BattleLootOfferRegistry.Shared.ForgetMapEvent(mapEventId);
            BattleLootOfferRegistry.Shared.Register(offer, BattleLootTransactionHandler.NowSeconds());

            network.Send(peer, new NetworkBattleLootOffer(offer));

            // Counts, not just line counts. A line is one KIND of thing, so "21 lines" says nothing about
            // whether a haul is plausible for the army that produced it - a stack of forty arrows and a
            // single sword are both one line. These totals are what you compare against the battle.
            int items = 0, members = 0, prisoners = 0, heroes = 0;
            foreach (var line in offer.Lines)
            {
                switch (line.Kind)
                {
                    case BattleLootLineKind.Item: items += line.Count; break;
                    case BattleLootLineKind.Member: members += line.Count; break;
                    case BattleLootLineKind.Prisoner: prisoners += line.Count; break;
                }

                if (line.IsHero) heroes++;
            }

            Logger.Information(
                "[Loot] Offered {Lines} line(s) of {MapEvent} to party {Party} as offer {Offer}: " +
                "{Items} item(s), {Members} member(s), {Prisoners} prisoner(s), {Heroes} hero(es)",
                offer.Lines.Length, mapEventId, partyId, offer.OfferId, items, members, prisoners, heroes);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[Loot] Could not offer the results of {MapEvent} to party {Party}", mapEventId, partyId);
        }
    }

    private IEnumerable<BattleLootItemStack> PackItems(NetworkPlayerLootData loot, string mapEventPartyId)
    {
        var stacks = new List<BattleLootItemStack>();
        if (loot.LootedItems == null ||
            !loot.LootedItems.TryGetValue(mapEventPartyId, out var elements) ||
            elements == null)
        {
            return stacks;
        }

        foreach (var element in elements)
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

    private static IEnumerable<TroopRosterElementData> PackTroops(
        Dictionary<string, TroopRosterData> byParty,
        string mapEventPartyId)
    {
        if (byParty == null || !byParty.TryGetValue(mapEventPartyId, out var roster) || roster.Data == null)
            return new TroopRosterElementData[0];

        return roster.Data;
    }

    /// <summary>Whether a character id names a hero, which decides how its line may be claimed and applied.</summary>
    private bool IsHeroId(string characterId)
        => objectManager.TryGetObject<CharacterObject>(characterId, out var character)
           && character != null
           && character.IsHero;

    private void Handle_NetworkCommitMapEventResults(MessagePayload<NetworkCommitMapEventResults> obj)
    {
        var data = obj.What;

        // The receive half. Logged BEFORE the game-thread hop and before any decision, because the question
        // this answers is simply "did this arrive at all". Every rejection below already logs its reason, yet a
        // whole battle's results produced no line of any kind on either client - which can only mean the
        // handler never ran, and that is a different problem from any of the ones the rejections describe.
        Logger.Information("[Loot] Received results for {MapEvent} (winner={Winner}, mySide={Side}, myParty={Party})",
            data.MapEventId, data.WinningSide, data.PlayerSide, data.PlayerMapEventPartyId);

        GameThread.RunSafe(() =>
        {
            if ((data.PlayerSide != BattleSideEnum.Attacker && data.PlayerSide != BattleSideEnum.Defender) ||
                string.IsNullOrEmpty(data.PlayerMapEventPartyId))
            {
                Logger.Warning("[Loot] Results for {MapEvent} name no side or party for this player (side={Side}, party={Party}); nothing to award",
                    data.MapEventId, data.PlayerSide, data.PlayerMapEventPartyId);
                return;
            }

            // The loot itself is keyed by MAP EVENT PARTY id, so it is unpacked before anything is allowed to
            // turn us back. It used to sit behind the map-event and encounter checks below, which meant that
            // losing either of those lost the loot with it.
            mapEventResultsInterface.UnpackPlayerLootDataForParty(
                data.PlayerLootData,
                data.PlayerMapEventPartyId,
                out var lootedItems,
                out var lootedMembers,
                out var lootedPrisoners);

            // The counts, not just the fact of arrival. A whole battle's results can arrive, stage without
            // complaint and still award nothing, because staging an EMPTY roster succeeds exactly like staging
            // a full one - and the loot screen is then skipped for having nothing to show. That is what one
            // player saw while the other collected everything, and nothing in the log distinguished the two.
            Logger.Information("[Loot] Unpacked for {Party}: {Items} item stack(s), {Members} member(s), {Prisoners} prisoner(s)",
                data.PlayerMapEventPartyId, lootedItems?.Count ?? 0, lootedMembers?.Count ?? 0, lootedPrisoners?.Count ?? 0);

            if (TryStageIntoEncounter(data, lootedItems, lootedMembers, lootedPrisoners)) return;

            // The encounter could not take it. Award it directly rather than dropping it: the staging path
            // exists to route loot through the encounter's own screen, not to decide whether the player is
            // entitled to it, and the server has already decided that.
            AwardDirectly(data, lootedItems, lootedMembers, lootedPrisoners);
        });
    }

    /// <summary>
    /// Stages loot into the local encounter, the way a single-player victory does. False if it cannot.
    /// </summary>
    /// <remarks>
    /// Only the encounter of a client whose own party fought this battle: an uninvolved client (no open
    /// encounter, or one for something unrelated - a town visit, a conversation) must not have its encounter
    /// state touched by another battle's results.
    ///
    /// Matched through LocalBattleLookup rather than PlayerEncounter.Battle alone: a player who fought from
    /// inside a besieged settlement holds the battle only through EncounteredBattle, and testing Battle skipped
    /// them entirely - so a defender who won a siege in their own castle had no loot or prisoners staged while
    /// an ally who reinforced from outside collected the lot.
    /// </remarks>
    private bool TryStageIntoEncounter(
        NetworkCommitMapEventResults data, ItemRoster lootedItems, TroopRoster lootedMembers, TroopRoster lootedPrisoners)
    {
        var playerEncounter = PlayerEncounter.Current;
        if (playerEncounter == null)
        {
            Logger.Warning("[Loot] No local encounter to stage the results of {MapEvent} into", data.MapEventId);
            return false;
        }

        if (!objectManager.TryGetObject<MapEvent>(data.MapEventId, out var mapEvent) ||
            !LocalBattleLookup.MatchesEncounter(mapEvent))
        {
            Logger.Warning("[Loot] The local encounter does not match {MapEvent} (mapEventResolved={Resolved})",
                data.MapEventId, mapEvent != null);
            return false;
        }

        // Set the encounter state ahead to start at applying results when a winning player leaves the battle.
        // CaptureHeroes is the first EncounterState that doesn't rely on the MapEvent, which is already
        // destroyed when a player leaves a battle.
        playerEncounter.EncounterState = data.WinningSide == data.PlayerSide
            ? PlayerEncounterState.CaptureHeroes
            : PlayerEncounterState.End;

        using (new AllowedThread())
        {
            playerEncounter.RosterToReceiveLootItems.Add(lootedItems);
            playerEncounter.RosterToReceiveLootMembers.Add(lootedMembers);
            playerEncounter.RosterToReceiveLootPrisoners.Add(lootedPrisoners);
        }


        return true;
    }

    /// <summary>
    /// Puts the loot straight into the player's party when the encounter cannot carry it.
    /// </summary>
    /// <remarks>
    /// This exists because the loot was being lost, silently and completely. The server computes it, packs it
    /// and sends it - measured at 13KB across two packets, one per player - and the receiving handler then
    /// dropped the lot behind an unlogged early return because <c>PlayerEncounter.Current</c> was gone.
    ///
    /// It is gone for a good reason, and one that will happen again: concluding a battle finalizes the map
    /// event and closes every involved player's encounter, and the results are sent around that same moment.
    /// Whoever loses the race gets nothing. Both players did.
    ///
    /// So the encounter is treated as the preferred route rather than the only one. Awarding directly skips
    /// the loot SCREEN, which is a real loss of presentation - but the alternative on this path is no loot at
    /// all, and the entitlement was already decided by the server.
    ///
    /// Only a winner is awarded. A defeated player's losses are handled by the defeat path, and running this
    /// for them would hand them the enemy's spoils.
    /// </remarks>
    /// <summary>
    /// Whether a player's side won, and so is owed the spoils.
    /// </summary>
    /// <remarks>
    /// One rule, consulted by both the client that awards the loot and the server that mirrors it. They cannot
    /// be allowed to disagree: whichever side awards without the other is exactly the divergence this whole
    /// mirror exists to close, and "winner" is the only condition either of them tests.
    ///
    /// BattleSideEnum has a None, and None == None would otherwise read as a win for a player the results name
    /// no side for.
    /// </remarks>
    internal static bool WonTheBattle(BattleSideEnum winningSide, BattleSideEnum playerSide)
        => playerSide == winningSide &&
           (playerSide == BattleSideEnum.Attacker || playerSide == BattleSideEnum.Defender);


    /// <summary>
    /// A party's name for a log line, or a placeholder when reading it would throw.
    /// </summary>
    /// <remarks>
    /// <c>MobileParty.Name</c> composes a TextObject from campaign state and can throw on a party that is
    /// mid-construction or otherwise incomplete - and it is read only to write a log line.
    ///
    /// That made it a genuinely bad place to fail. This runs inside the per-player loop that sends every
    /// participant their results, so an exception here does not just lose a log: it abandons the loop, and every
    /// player after the one being logged is never sent their loot at all. The award itself has already been
    /// applied by this point, so the throw would also leave the server's rosters ahead of what the clients were
    /// told. Caught by the E2E suite rather than in play, which is the only reason it is not another silent
    /// loot-loss report.
    /// </remarks>
    private static string SafePartyName(MobileParty party)
    {
        try { return party?.Name?.ToString() ?? "<unnamed party>"; }
        catch { return "<unreadable party>"; }
    }

    private void AwardDirectly(
        NetworkCommitMapEventResults data, ItemRoster lootedItems, TroopRoster lootedMembers, TroopRoster lootedPrisoners)
    {
        if (!WonTheBattle(data.WinningSide, data.PlayerSide))
        {
            Logger.Information("[Loot] {MapEvent} was not won by this player; nothing to award directly", data.MapEventId);
            return;
        }

        var party = MobileParty.MainParty;
        if (party == null)
        {
            Logger.Warning("[Loot] No local party to award the results of {MapEvent} to", data.MapEventId);
            return;
        }

        using (new AllowedThread())
        {
            if (lootedItems != null) party.ItemRoster.Add(lootedItems);
            if (lootedMembers != null) party.MemberRoster.Add(lootedMembers);
            if (lootedPrisoners != null) party.PrisonRoster.Add(lootedPrisoners);
        }

        Logger.Information(
            "[Loot] Awarded {MapEvent} directly to {Party} (no usable encounter): {Items} item stack(s), {Members} recovered member(s), {Prisoners} prisoner(s)",
            data.MapEventId, SafePartyName(party), lootedItems?.Count ?? 0, lootedMembers?.Count ?? 0, lootedPrisoners?.Count ?? 0);
    }

    private bool TryGetPlayerMapEventParty(
        MapEvent mapEvent,
        string playerMobilePartyId,
        out MapEventParty playerMapEventParty,
        out BattleSideEnum playerSide)
    {
        playerMapEventParty = null;
        playerSide = BattleSideEnum.None;

        if (TryGetPlayerMapEventParty(mapEvent.AttackerSide, playerMobilePartyId, out playerMapEventParty))
        {
            playerSide = BattleSideEnum.Attacker;
            return true;
        }

        if (TryGetPlayerMapEventParty(mapEvent.DefenderSide, playerMobilePartyId, out playerMapEventParty))
        {
            playerSide = BattleSideEnum.Defender;
            return true;
        }

        return false;
    }

    private bool TryGetPlayerMapEventParty(
        MapEventSide mapEventSide,
        string playerMobilePartyId,
        out MapEventParty playerMapEventParty)
    {
        foreach (var mapEventParty in mapEventSide.Parties)
        {
            var mobileParty = mapEventParty.Party?.MobileParty;
            if (mobileParty == null ||
                !objectManager.TryGetId(mobileParty, out var mobilePartyId) ||
                mobilePartyId != playerMobilePartyId)
            {
                continue;
            }

            playerMapEventParty = mapEventParty;
            return true;
        }

        playerMapEventParty = null;
        return false;
    }
}
