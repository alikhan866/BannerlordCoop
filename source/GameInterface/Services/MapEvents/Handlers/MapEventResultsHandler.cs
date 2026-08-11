using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.MapEvents.Data;
using GameInterface.Services.MapEvents.Interfaces;
using GameInterface.Services.MapEvents.Messages.Leave;
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

                // Never let mirroring one player's loot stop another player being sent theirs. The send above
                // has already happened for this player; everything below is bookkeeping on our own copy, and a
                // failure in it must not abandon the loop over the remaining players.
                try
                {
                    MirrorAwardOnServer(player.MobilePartyId, mapEventId, mapEvent.WinningSide, playerSide,
                        playerMapEventPartyId, networkPlayerLootData);
                }
                catch (Exception e)
                {
                    Logger.Error(e, "[Loot] Could not mirror {MapEvent} onto the server's copy of {Party}; the client was still sent its results",
                        mapEventId, player.MobilePartyId);
                }
            }
        });
    }

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

        // Staging is not the same as awarding. The encounter grants these when it reaches LootInventory, and
        // concluding a coop battle closes every involved player's encounter - which can happen first, taking
        // the loot with it. Keep a copy so BattleLootRescuePatch can hand it over if the screen never comes.
        PendingBattleLoot.Remember(data.MapEventId, lootedItems, lootedMembers, lootedPrisoners);

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
    /// Applies a player's loot to the SERVER's copy of their party, without replicating it.
    /// </summary>
    /// <remarks>
    /// The server computed this loot, packed it and sent it - and then never gave it to its own copy of the
    /// party. Only the client applied it, so every battle left the two disagreeing about what that player owned:
    /// the client showed items, recovered troops and prisoners the server had no record of.
    ///
    /// Prisoners are where that surfaces, because prisoners are the one part of the loot the server is later
    /// asked to act on. <c>PrisonerSaleProcessor.Sell</c> validates a ransom against the SERVER's prison roster,
    /// so a player with a screen full of prisoners the server did not have could not ransom them, could not
    /// discard them, and got no feedback explaining why. Items and recovered members diverged just as silently;
    /// they simply had nothing that asked the server about them.
    ///
    /// Suppressed from replication on purpose. The client applies its own copy from the message it has just been
    /// sent, so broadcasting this write as well would hand it to them twice. <see cref="AllowedThread"/> is the
    /// existing way to say "apply this without telling anyone" - the same mechanism the client uses when it
    /// applies an authoritative change it received.
    ///
    /// Known gap: loot staged into a surviving encounter can still be declined at the loot screen, and a decline
    /// leaves the server holding what the player turned down. That screen rarely survives a co-op battle - it is
    /// why the direct-award and rescue paths exist at all - and the failure it leaves behind is far milder than
    /// the one being fixed here.
    /// </remarks>
    private void MirrorAwardOnServer(
        string playerMobilePartyId,
        string mapEventId,
        BattleSideEnum winningSide,
        BattleSideEnum playerSide,
        string playerMapEventPartyId,
        NetworkPlayerLootData networkPlayerLootData)
    {
        if (!WonTheBattle(winningSide, playerSide)) return;

        if (!objectManager.TryGetObject<MobileParty>(playerMobilePartyId, out var party) || party == null)
        {
            Logger.Warning("[Loot] No server-side party {Party} to mirror the results of {MapEvent} into",
                playerMobilePartyId, mapEventId);
            return;
        }

        mapEventResultsInterface.UnpackPlayerLootDataForParty(
            networkPlayerLootData,
            playerMapEventPartyId,
            out var lootedItems,
            out var lootedMembers,
            out var lootedPrisoners);

        using (new AllowedThread())
        {
            if (lootedItems != null) party.ItemRoster.Add(lootedItems);
            if (lootedMembers != null) party.MemberRoster.Add(lootedMembers);
            if (lootedPrisoners != null) party.PrisonRoster.Add(lootedPrisoners);
        }

        Logger.Information(
            "[Loot] Mirrored {MapEvent} onto the server's {Party}: {Items} item stack(s), {Members} recovered member(s), {Prisoners} prisoner(s)",
            mapEventId, SafePartyName(party), lootedItems?.Count ?? 0, lootedMembers?.Count ?? 0, lootedPrisoners?.Count ?? 0);
    }

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
