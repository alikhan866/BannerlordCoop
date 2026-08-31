using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents.Handlers;

public class HitRewardHandler : IHandler
{
    private const string UpgradedTroopsScoreboardRefreshChannel = "UpgradedTroopsScoreboardRefreshChannel";

    /// <summary>
    /// How often scoreboard upgrades are allowed onto the wire.
    /// </summary>
    /// <remarks>
    /// 200ms rather than the 25ms network poll. Coalescing alone could not help here: the flush runs every
    /// poll, so the channel emitted ~700 messages per 10 seconds across ~400 flushes however well each one
    /// merged. At 200ms that ceiling becomes 50 per 10 seconds, with every upgrade in the window arriving
    /// together in one message. The cost is a scoreboard that settles up to a fifth of a second late, which
    /// is invisible; the gain is paid straight back into the reliable queue that was dropping players.
    /// </remarks>
    private static readonly TimeSpan ScoreboardFlushInterval = TimeSpan.FromMilliseconds(200);

    private static readonly ILogger Logger = LogManager.GetLogger<HitRewardHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly ISendCoalescer coalescer;

    public HitRewardHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        ISendCoalescer coalescer = null)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.coalescer = coalescer;

        // The scoreboard is a readout, not gameplay: nobody can see that a kill tally settled a fifth of a
        // second late, and holding it back lets a whole flush window's upgrades ride in one message instead
        // of ~2. This channel was the single largest contributor to the reliable-queue backlog that reached
        // 12,926 messages and dropped a player, so its message rate is what has to come down.
        coalescer?.SetChannelInterval(UpgradedTroopsScoreboardRefreshChannel, ScoreboardFlushInterval);

        messageBroker.Subscribe<TrackTroopForUpgrades>(Handle_TrackTroopForUpgrades);
        messageBroker.Subscribe<NetworkTrackTroopForUpgrades>(Handle_NetworkTrackTroopForUpgrades);

        messageBroker.Subscribe<BattleHitReward>(Handle_BattleHitReward);
        messageBroker.Subscribe<NetworkBattleHitReward>(Handle_NetworkBattleHitReward);

        messageBroker.Subscribe<CheckUpgradeAfterAgentRemoved>(Handle_CheckUpgradeAfterAgentRemoved);
        messageBroker.Subscribe<NetworkCheckUpgradeAfterAgentRemoved>(Handle_NetworkCheckUpgradeAfterAgentRemoved);

        messageBroker.Subscribe<NetworkUpdateScoreboardAfterUpgradesBatch>(Handle_NetworkUpdateScoreboardAfterUpgrades);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<TrackTroopForUpgrades>(Handle_TrackTroopForUpgrades);
        messageBroker.Unsubscribe<NetworkTrackTroopForUpgrades>(Handle_NetworkTrackTroopForUpgrades);

        messageBroker.Unsubscribe<BattleHitReward>(Handle_BattleHitReward);
        messageBroker.Unsubscribe<NetworkBattleHitReward>(Handle_NetworkBattleHitReward);

        messageBroker.Unsubscribe<CheckUpgradeAfterAgentRemoved>(Handle_CheckUpgradeAfterAgentRemoved);
        messageBroker.Unsubscribe<NetworkCheckUpgradeAfterAgentRemoved>(Handle_NetworkCheckUpgradeAfterAgentRemoved);

        messageBroker.Unsubscribe<NetworkUpdateScoreboardAfterUpgradesBatch>(Handle_NetworkUpdateScoreboardAfterUpgrades);
    }

    private void Handle_TrackTroopForUpgrades(MessagePayload<TrackTroopForUpgrades> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            network.SendAll(new NetworkTrackTroopForUpgrades(data.MapEventPartyId, data.CharacterId));
        });
    }

    private void Handle_NetworkTrackTroopForUpgrades(MessagePayload<NetworkTrackTroopForUpgrades> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<MapEventParty>(data.MapEventPartyId, out var mapEventParty)) return;
            if (!objectManager.TryGetObjectWithLogging<CharacterObject>(data.CharacterId, out var character)) return;

            mapEventParty.Party.MapEvent?.TroopUpgradeTracker.AddTrackedTroop(mapEventParty.Party, character);
        });
    }

    private void Handle_BattleHitReward(MessagePayload<BattleHitReward> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetIdWithLogging(data.MapEvent, out var mapEventId)) return;
            if (!objectManager.TryGetIdWithLogging(data.AffectedCharacter, out var affectedCharacterId)) return;
            if (!objectManager.TryGetIdWithLogging(data.AffectorCharacter, out var affectorCharacterId)) return;

            string captainId = null;
            if (data.Captain != null && !objectManager.TryGetIdWithLogging(data.Captain, out captainId)) return;

            string heroId = null;
            if (data.Hero != null && !objectManager.TryGetIdWithLogging(data.Hero, out heroId)) return;

            if (!objectManager.TryGetIdWithLogging(data.AffectorParty, out var affectorPartyId)) return;

            var message = new NetworkBattleHitReward(
                mapEventId,
                affectedCharacterId,
                affectorCharacterId,
                captainId,
                heroId,
                data.AffectedAgentSide,
                data.AffectorAgentSide,
                data.IsAgentMounted,
                data.LastSpeedBonus,
                data.LastShotDifficulty,
                data.IsSiegeEngineHit,
                data.LastAttackerWeapon,
                data.AttackType,
                data.HitpointRatio,
                data.DamageAmount,
                affectorPartyId,
                data.IsSneakAttack,
                data.AffectedAgentHealth,
                data.IsAffectorUnderCommand);

            network.SendAll(message);
        });
    }

    private void Handle_NetworkBattleHitReward(MessagePayload<NetworkBattleHitReward> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<MapEvent>(data.MapEventId, out var mapEvent)) return;

            if (!objectManager.TryGetObjectWithLogging<CharacterObject>(data.AffectedCharacterId, out var affectedCharacter)) return;
            if (!objectManager.TryGetObjectWithLogging<CharacterObject>(data.AffectorCharacterId, out var affectorCharacter)) return;

            Hero captain = null;
            if (data.CaptainId != null && !objectManager.TryGetObjectWithLogging<Hero>(data.CaptainId, out captain)) return;

            Hero hero = null;
            if (data.HeroId != null && !objectManager.TryGetObjectWithLogging<Hero>(data.HeroId, out hero)) return;

            if (!objectManager.TryGetObjectWithLogging<PartyBase>(data.AffectorPartyId, out var affectorParty)) return;

            bool isTeamKill = data.AffectedAgentSide == data.AffectorAgentSide;
            bool isHorseCharge = data.IsAgentMounted && data.AttackType == AgentAttackType.Collision;

            SkillLevelingManager.OnCombatHit(
                affectorCharacter,
                affectedCharacter,
                captain?.CharacterObject,
                hero,
                data.LastSpeedBonus,
                data.LastShotDifficulty,
                data.LastAttackerWeapon,
                data.HitpointRatio,
                CombatXpModel.MissionTypeEnum.Battle,
                data.IsAgentMounted,
                isTeamKill,
                data.IsAffectorUnderCommand,
                data.DamageAmount,
                data.AffectedAgentHealth < 1f,
                data.IsSiegeEngineHit,
                isHorseCharge,
                data.IsSneakAttack);

            int upgradedCount = 0;

            // Applies changes to the TroopUpgradeTracker
            upgradedCount = mapEvent.TroopUpgradeTracker.CheckUpgradedCount(affectorParty, affectorCharacter);

            // Update scoreboard for clients
            EnqueueScoreboardUpgrade(
                data.MapEventId, data.AffectorPartyId, data.AffectorCharacterId, data.AffectorAgentSide, upgradedCount);
        });
    }

    private void Handle_CheckUpgradeAfterAgentRemoved(MessagePayload<CheckUpgradeAfterAgentRemoved> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetIdWithLogging(data.MapEvent, out var mapEventId)) return;
            if (!objectManager.TryGetIdWithLogging(data.Party, out var partyId)) return;
            if (!objectManager.TryGetIdWithLogging(data.CharacterObject, out var characterObjectId)) return;

            var message = new NetworkCheckUpgradeAfterAgentRemoved(
                mapEventId,
                partyId,
                characterObjectId,
                data.Side);

            network.SendAll(message);
        });
    }

    private void Handle_NetworkCheckUpgradeAfterAgentRemoved(MessagePayload<NetworkCheckUpgradeAfterAgentRemoved> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<MapEvent>(data.MapEventId, out var mapEvent)) return;
            if (!objectManager.TryGetObjectWithLogging<PartyBase>(data.PartyId, out var party)) return;
            if (!objectManager.TryGetObjectWithLogging<CharacterObject>(data.CharacterObjectId, out var character)) return;

            // Applies changes to the TroopUpgradeTracker
            var upgradedCount = mapEvent.TroopUpgradeTracker.CheckUpgradedCount(party, character);

            // Update scoreboard for clients
            EnqueueScoreboardUpgrade(
                data.MapEventId, data.PartyId, data.CharacterObjectId, data.Side, upgradedCount);
        });
    }

    /// <summary>
    /// [Server] Queue one scoreboard upgrade for the batch belonging to <paramref name="mapEventId"/>.
    /// </summary>
    /// <remarks>
    /// Coalesced by MAP EVENT, with the entries keyed by party+troop-type inside the batch. Keying the
    /// coalescer itself by party+troop-type — as this did before — deduplicated each pair perfectly but
    /// still cost one message per pair per flush, because <c>SendCoalescer.Flush</c> sends each key
    /// separately. In a live siege that came to 21,004 messages and 2.03 MB, the largest recurring
    /// consumer on the server pipe, most of it the same map event id and party id written over and over.
    /// </remarks>
    private void EnqueueScoreboardUpgrade(
        string mapEventId,
        string partyId,
        string characterId,
        BattleSideEnum side,
        int upgradedCount)
    {
        if (string.IsNullOrEmpty(mapEventId)) return;

        var key = new CoalesceKey(UpgradedTroopsScoreboardRefreshChannel, mapEventId);
        var entry = new ScoreboardUpgradeEntry(characterId, partyId, side, upgradedCount);

        coalescer.Enqueue(key, new KeyedBatchPayload<ScoreboardUpgradeEntry>(
            partyId + characterId,
            entry,
            entries => new NetworkUpdateScoreboardAfterUpgradesBatch(
                mapEventId,
                entries.ToArray())));
    }

    private void Handle_NetworkUpdateScoreboardAfterUpgrades(MessagePayload<NetworkUpdateScoreboardAfterUpgradesBatch> obj)
    {
        var data = obj.What;
        if (data.Entries == null || data.Entries.Length == 0) return;

        GameThread.RunSafe(() =>
        {
            var mission = Mission.Current;
            if (mission == null) return;

            if (!objectManager.TryGetObjectWithLogging<MapEvent>(data.MapEventId, out var mapEvent)) return;

            // Skip update if the client is not in this map event. Checked once for the whole batch, and
            // before any per-entry lookup: entries for a battle this client is not watching cost nothing.
            if (MapEvent.PlayerMapEvent != mapEvent) return;

            BattleObserverMissionLogic battleObserverMissionLogic = mission.GetMissionBehavior<BattleObserverMissionLogic>();
            if ((battleObserverMissionLogic?.BattleObserver) == null) return;

            TroopUpgradeTracker troopUpgradeTracker = mapEvent.TroopUpgradeTracker;

            foreach (var entry in data.Entries)
            {
                ApplyScoreboardUpgrade(battleObserverMissionLogic, troopUpgradeTracker, entry);
            }
        });
    }

    /// <summary>[Client, game thread] Apply one entry of a scoreboard batch. A missing character or party
    /// skips only its own entry, so one unresolvable id cannot discard the rest of the batch.</summary>
    private void ApplyScoreboardUpgrade(
        BattleObserverMissionLogic battleObserverMissionLogic,
        TroopUpgradeTracker troopUpgradeTracker,
        ScoreboardUpgradeEntry entry)
    {
        if (!objectManager.TryGetObjectWithLogging<CharacterObject>(entry.AffectorCharacterId, out var affectorCharacter)) return;
        if (!objectManager.TryGetObjectWithLogging<PartyBase>(entry.AffectorPartyId, out var affectorParty)) return;

        if (affectorCharacter.IsHero)
        {
            Hero heroObject = affectorCharacter.HeroObject;
            foreach (SkillObject skill in troopUpgradeTracker.CheckSkillUpgrades(heroObject))
            {
                battleObserverMissionLogic.BattleObserver.HeroSkillIncreased(
                    entry.AffectorAgentSide, affectorParty, affectorCharacter, skill);
            }

            return;
        }

        if (entry.UpgradedCount != 0)
        {
            battleObserverMissionLogic.BattleObserver.TroopNumberChanged(
                entry.AffectorAgentSide, affectorParty, affectorCharacter, 0, 0, 0, 0, 0, entry.UpgradedCount);
        }
    }
}
