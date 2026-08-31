using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.MapEvents.Extensions;
using GameInterface.Services.MapEvents.Logging;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.MapEvents.Patches;
using GameInterface.Services.MapEvents.TroopSupply;
using GameInterface.Services.MapEvents.UI;
using GameInterface.Services.MapEventSides.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Threading;
using TaleWorlds.CampaignSystem;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>
/// Owns the live battle-mission start flow (split out of <see cref="BattleHandler"/>). On the server it answers the
/// mission-mode <see cref="NetworkBattleStartRequest"/>: gate it against <see cref="ServerBattleModeArbiter"/>, apply
/// the attack's hostile consequences, make the sides mission-ready, reply, send the mission start to participants
/// (<see cref="NetworkStartAttackMission"/>), and claim the mission mode on every client
/// (<see cref="NetworkBattleModeSet"/>). Eligible clients in the map event open the coop field-battle mission.
/// </summary>
internal class BattleMissionStartHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleMissionStartHandler>();

    // Exclusive upper bound for the terrain seed, preserving the range of the original
    // client-side MBRandom.RandomInt(10000) roll this replaces.
    private const int MaxTerrainSeed = 10000;
    private const int SiegeMissionOpenRetrySeconds = 30;

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly INetwork network;
    private readonly IMapEventLogger mapEventLogger;
    private readonly IBattleMissionInitializerResolver missionInitializerResolver;
    private readonly IBattleTroopReserveBuilder reserveBuilder;
    private static long attackMissionStartSequence;

    // Server-side: the complete mission initializer chosen once per map event and reused for late entrants.
    private readonly ConcurrentDictionary<string, MissionInitializerRecord> mapEventMissionInitializers =
        new ConcurrentDictionary<string, MissionInitializerRecord>();
    private readonly Random terrainSeedRandom = new Random();

    // Server-side: the siege mission inputs (wall level, wall HPs, engine lists) snapshotted once per map event,
    // so a joiner entering mid-assault loads the same scene as the first entrant even though the campaign-side
    // container keeps syncing. Evicted with the terrain seed when the event finalizes.
    private readonly ConcurrentDictionary<string, NetworkStartSiegeMission> siegeMissionSnapshots = new ConcurrentDictionary<string, NetworkStartSiegeMission>();

    /// <summary>
    /// Battles held at the starting line while their players choose how their troops deploy.
    /// </summary>
    /// <summary>
    /// Controllers already sent into each battle's mission, so a later barrier never waits on them.
    /// </summary>
    /// <remarks>
    /// A late joiner opens a SECOND preference barrier for a battle that is already running, and the roster
    /// was built from every participant - including the players currently fighting in it. Their clients are
    /// in a mission, not on the map, so no inquiry can be shown and no answer can come back; the barrier
    /// then waited out its full timeout while the joiner sat behind a "Waiting for other players" panel
    /// naming someone who had been in the battle for half a minute. Asking a player who is already deployed
    /// is meaningless anyway: their troops are on the field and the choice only shapes future waves, which
    /// they keep from their existing preference.
    /// </remarks>
    private readonly ConcurrentDictionary<string, HashSet<string>> missionStartedControllers =
        new ConcurrentDictionary<string, HashSet<string>>();

    private readonly ConcurrentDictionary<string, HeldMissionStart> heldMissionStarts =
        new ConcurrentDictionary<string, HeldMissionStart>();

    /// <summary>
    /// How long a battle waits for an answer before starting anyway.
    /// </summary>
    /// <remarks>
    /// A battle that never starts is a far worse failure than one that starts on whatever a player last chose,
    /// and the preference is client-local and always has a value - a timeout leaves it unasked, never invalid.
    /// </remarks>
    private static readonly TimeSpan TroopPreferenceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A mission start that is built and ready, waiting only on its players to choose.</summary>
    private sealed class HeldMissionStart : IDisposable
    {
        public TroopPreferenceBarrier Barrier;
        public IReadOnlyList<MissionParticipant> Participants;
        public IMessage MissionStartMessage;
        public System.Threading.Timer Expiry;

        public void Dispose() => Expiry?.Dispose();
    }

    public BattleMissionStartHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        INetwork network,
        IMapEventLogger mapEventLogger,
        IBattleMissionInitializerResolver missionInitializerResolver,
        IBattleTroopReserveBuilder reserveBuilder)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.network = network;
        this.mapEventLogger = mapEventLogger;
        this.missionInitializerResolver = missionInitializerResolver;
        this.reserveBuilder = reserveBuilder;

        messageBroker.Subscribe<NetworkBattleStartRequest>(Handle_NetworkBattleStartRequest);
        messageBroker.Subscribe<NetworkStartAttackMission>(Handle_NetworkStartAttackMission);
        messageBroker.Subscribe<NetworkStartSiegeMission>(Handle_NetworkStartSiegeMission);
        messageBroker.Subscribe<MapEventFinalized>(Handle_MapEventFinalized);
        messageBroker.Subscribe<NetworkTroopPreferenceChosen>(Handle_NetworkTroopPreferenceChosen);
        messageBroker.Subscribe<NetworkTroopPreferenceRequest>(Handle_NetworkTroopPreferenceRequest);
        messageBroker.Subscribe<NetworkTroopPreferenceProgress>(Handle_NetworkTroopPreferenceProgress);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkBattleStartRequest>(Handle_NetworkBattleStartRequest);
        messageBroker.Unsubscribe<NetworkStartAttackMission>(Handle_NetworkStartAttackMission);
        messageBroker.Unsubscribe<NetworkStartSiegeMission>(Handle_NetworkStartSiegeMission);
        messageBroker.Unsubscribe<MapEventFinalized>(Handle_MapEventFinalized);
        messageBroker.Unsubscribe<NetworkTroopPreferenceChosen>(Handle_NetworkTroopPreferenceChosen);
        messageBroker.Unsubscribe<NetworkTroopPreferenceRequest>(Handle_NetworkTroopPreferenceRequest);
        messageBroker.Unsubscribe<NetworkTroopPreferenceProgress>(Handle_NetworkTroopPreferenceProgress);

        foreach (var held in heldMissionStarts.Values) held.Dispose();
        heldMissionStarts.Clear();
    }

    /// <summary>The battle ended — drop its cached mission inputs (server-side; a no-op on a client's empty maps).</summary>
    private void Handle_MapEventFinalized(MessagePayload<MapEventFinalized> payload)
    {
        if (objectManager.TryGetId(payload.What.MapEvent, out var mapEventId))
        {
            mapEventMissionInitializers.TryRemove(mapEventId, out _);
            siegeMissionSnapshots.TryRemove(mapEventId, out _);
            missionStartedControllers.TryRemove(mapEventId, out _);
            if (heldMissionStarts.TryRemove(mapEventId, out var held)) held.Dispose();
        }
    }

    /// <summary>[Server] Handle a battle-start request for the live-mission mode: gate it, make the sides
    /// mission-ready, send the mission start to participants, and reply. Other modes are ignored here.</summary>
    private void Handle_NetworkBattleStartRequest(MessagePayload<NetworkBattleStartRequest> payload)
    {
        if (ModInformation.IsClient)
            return;

        if (payload.What.Mode != (int)BattleStartMode.Mission)
            return;

        if (!(payload.Who is NetPeer requester))
        {
            Logger.Error("Received {Message} with no originating peer", nameof(NetworkBattleStartRequest));
            return;
        }

        // _sides is game state the main-thread tick also touches; mutating it from the
        // network thread races the tick. Make the sides mission-ready on the main thread.
        // Re-resolve the event at drain time: it may have finalized between this request
        // arriving and the queued action running, in which case a captured reference would
        // point at a torn-down event.
        GameThread.RunSafe(() =>
        {
            var operation = "resolve map event";
            var isNewMissionClaim = false;
            var startAccepted = false;

            try
            {
                if (!objectManager.TryGetObject(payload.What.MapEventId, out MapEvent mapEvent))
                    return;

                operation = "validate requesting participant";
                if (!TryGetRequestingParticipant(requester, payload.What, mapEvent, out var attackerMobileParty))
                {
                    Logger.Warning("Rejecting attack mission start for map event {MapEventId}: requester is not an authoritative participant",
                        payload.What.MapEventId);
                    network.Send(requester, new NetworkBattleStartReply(payload.What.RequestId, false));
                    return;
                }

                operation = "validate hostile action mode";
                if (mapEvent.IsUnsupportedMultiPlayerHostileAction())
                {
                    Logger.Warning("Rejecting attack mission start for map event {MapEventId}: this hostile action does not support multiple player parties", payload.What.MapEventId);
                    network.Send(requester, new NetworkBattleStartReply(payload.What.RequestId, false));
                    return;
                }

                operation = "validate naval battle";
                if (mapEvent.IsNavalMapEvent)
                {
                    Logger.Warning("Rejecting attack mission start for map event {MapEventId}: naval battles are disabled", payload.What.MapEventId);
                    network.Send(requester, new NetworkBattleStartReply(payload.What.RequestId, false));
                    return;
                }

                // The lords-hall stage is not supported: CurrentSiegeState never advances past OnTheWalls in
                // co-op (SiegeMissionEndPatches), so this only trips on a save that carried the state in.
                // Rejected before the arbiter claim so the event stays open for auto-resolve.
                operation = "validate siege stage";
                if (mapEvent.IsSiegeAssault && mapEvent.MapEventSettlement?.CurrentSiegeState == Settlement.SiegeState.InTheLordsHall)
                {
                    Logger.Error("Rejecting siege mission for {MapEventId}: lords-hall stage is not supported", payload.What.MapEventId);
                    network.Send(requester, new NetworkBattleStartReply(payload.What.RequestId, false));
                    return;
                }

                // Server-authoritative mode gate: accept the live mission only if no auto-resolve simulation already
                // owns this event. On reject, don't make the sides mission-ready or reply — the requesting client
                // waits for NetworkStartAttackMission to open the mission, so it simply stays at the encounter menu.
                operation = "claim mission mode";
                if (!ServerBattleModeArbiter.TryClaimMission(payload.What.MapEventId, out isNewMissionClaim))
                {
                    mapEventLogger.DebugMapEvent(mapEvent, "Rejecting attack mission: an auto-resolve simulation is already underway for this event");
                    network.Send(requester, new NetworkBattleStartReply(payload.What.RequestId, false));
                    return;
                }

                mapEventLogger.DebugMapEvent(mapEvent, "Handling network attack mission attempted for map event. Making sides mission-ready and replying with mission start");

                // Apply the diplomatic consequences of the client's attack (war / relation)
                // authoritatively before the mission opens, reproducing the hostile-action head of
                // vanilla EncounterAttackConsequence that neither the client nor the server runs.
                operation = "apply attack hostile-action consequences";
                ApplyClientAttackHostileConsequences(mapEvent, attackerMobileParty.Party);

                if (isNewMissionClaim)
                {
                    operation = "remove wounded non-initiating players";
                    if (!RemoveWoundedNonInitiatorParties(
                            payload.What.MapEventId,
                            mapEvent,
                            payload.What.AttackerPartyId))
                    {
                        ServerBattleModeArbiter.Release(payload.What.MapEventId);
                        network.Send(requester, new NetworkBattleStartReply(payload.What.RequestId, false));
                        return;
                    }
                }

                operation = "make map event sides mission-ready";
                reserveBuilder.PrepareMissionReserves(mapEvent, attackerMobileParty);

                IMessage missionStartMessage;
                if (mapEvent.IsSiegeAssault || mapEvent.IsSiegeAmbush)
                {
                    operation = "build siege mission snapshot";
                    var snapshot = siegeMissionSnapshots.GetOrAdd(payload.What.MapEventId, _ => BuildSiegeMissionSnapshot(payload.What.MapEventId, mapEvent));
                    // Wounded non-initiators were removed above; the client-side eligibility check remains a fallback.
                    missionStartMessage = new NetworkStartSiegeMission(
                        snapshot.MapEventId,
                        snapshot.WallLevel,
                        snapshot.WallHitPointRatios,
                        snapshot.AttackerEngines,
                        snapshot.DefenderEngines,
                        payload.What.AttackerPartyId,
                        snapshot.SettlementId,
                        snapshot.IsSallyOut);
                }
                else
                {
                    operation = "build attack mission snapshot";
                    MissionInitializerRecord missionInitializer = GetOrCreateMissionInitializerSnapshot(
                        payload.What.MapEventId,
                        () => missionInitializerResolver.Create(
                            mapEvent,
                            RollTerrainSeed(),
                            GetAtmosphereOnCampaign(mapEvent)));
                    missionStartMessage = new NetworkStartAttackMission(
                        payload.What.MapEventId, missionInitializer,
                        payload.What.AttackerPartyId);
                }

                operation = "snapshot mission participants";
                var participants = GetMissionParticipants(mapEvent);

                operation = "reserve mission participants";
                ReserveMissionParticipants(payload.What.MapEventId, participants);

                // Reply first so the requesting client's blocked consequence unblocks before the mission-open
                // message arrives — the mission then opens off the menu-consequence stack, as in the pre-coordinator
                // flow, rather than re-entrantly during the blocking wait.
                operation = "send battle start reply";
                network.Send(requester, new NetworkBattleStartReply(payload.What.RequestId, true));
                startAccepted = true;

                // Hold the battle here, not later. At this line no client has a mission open and no battle
                // traffic is flowing, so waiting for everyone to choose costs nothing and lets nobody run
                // ahead. Asking a single client AFTER the battle had started for the others is what broke
                // mission loading before - it came back to hundreds of queued deaths and a mission that could
                // no longer initialise.
                operation = "hold for troop preference";
                if (!TryHoldForTroopPreference(payload.What.MapEventId, participants, missionStartMessage))
                {
                    operation = "send mission start";
                    SendMissionStart(participants, missionStartMessage);
                }

                // Claim the event for the mission mode on every client, so one still sitting at the encounter menu
                // greys out the auto-resolve option — a map event is fought as a live mission XOR an auto-resolve,
                // never both (see BattleModeEncounterOptionsPatch / BattleModeRegistry).
                operation = "send battle mode";
                network.SendAll(new NetworkBattleModeSet(payload.What.MapEventId, (int)BattleStartMode.Mission));
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to {Operation} for {Message}", operation, nameof(NetworkBattleStartRequest));
                if (!startAccepted)
                {
                    if (isNewMissionClaim)
                        ServerBattleModeArbiter.Release(payload.What.MapEventId);

                    network.Send(requester, new NetworkBattleStartReply(payload.What.RequestId, false));
                }
            }
        }, context: nameof(Handle_NetworkBattleStartRequest));
    }

    private bool TryGetRequestingParticipant(
        NetPeer requester,
        NetworkBattleStartRequest request,
        MapEvent mapEvent,
        out MobileParty party)
    {
        party = null;
        if (requester == null ||
            !playerManager.TryGetPlayer(requester, out var player) ||
            !string.Equals(player.MobilePartyId, request.AttackerPartyId, StringComparison.Ordinal) ||
            !objectManager.TryGetObject(player.MobilePartyId, out party))
        {
            return false;
        }

        return mapEvent.FindMapEventParty(party.Party) != null;
    }

    private IReadOnlyList<MissionParticipant> GetMissionParticipants(MapEvent mapEvent)
    {
        var participants = new List<MissionParticipant>();
        foreach (var player in playerManager.Players)
        {
            if (!playerManager.TryGetPeer(player.ControllerId, out var peer) ||
                !objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var party) ||
                mapEvent.FindMapEventParty(party.Party, out var side) is not MapEventParty mapEventParty ||
                !objectManager.TryGetIdWithLogging(side, out var sideId) ||
                !objectManager.TryGetIdWithLogging(mapEventParty, out var mapEventPartyId) ||
                !objectManager.TryGetIdWithLogging(party.Party, out var partyId))
            {
                continue;
            }

            participants.Add(new MissionParticipant(
                player.ControllerId,
                peer,
                sideId,
                mapEventPartyId,
                partyId));
        }

        return participants;
    }

    private void ReserveMissionParticipants(string mapEventId, IReadOnlyList<MissionParticipant> participants)
    {
        foreach (var participant in participants)
        {
            messageBroker.Publish(participant.Peer,
                new BattleJoinAccepted(mapEventId, participant.ControllerId, Guid.NewGuid()));
        }
    }

    /// <summary>
    /// Asks every player in this battle how their troops should deploy, and holds the start until they answer.
    /// </summary>
    /// <returns>True if the battle is now held; false if it should start immediately.</returns>
    /// <remarks>
    /// Only acknowledgements are collected. What each player chose stays on their own client and is read when
    /// that client allocates its troops, so there is no preference state on the wire to drift.
    /// </remarks>
    private bool TryHoldForTroopPreference(
        string mapEventId,
        IReadOnlyList<MissionParticipant> participants,
        IMessage missionStartMessage)
    {
        missionStartedControllers.TryGetValue(mapEventId, out var alreadyDeployed);

        var heroIds = new List<string>();
        foreach (var participant in participants)
        {
            // Already in the mission: their client cannot show an inquiry and cannot answer, so including
            // them guarantees the barrier runs to its timeout and strands everyone who did answer.
            if (alreadyDeployed != null && alreadyDeployed.Contains(participant.ControllerId)) continue;

            if (playerManager.TryGetPlayer(participant.ControllerId, out var player) &&
                !string.IsNullOrEmpty(player.HeroId))
            {
                heroIds.Add(player.HeroId);
            }
        }

        // Nobody to ask: a solo battle, or one whose players cannot be identified, starts exactly as it did
        // before this existed. Waiting on someone we cannot name or notify would hang the battle for 30s.
        if (heroIds.Count == 0) return false;

        var held = new HeldMissionStart
        {
            Barrier = new TroopPreferenceBarrier(mapEventId, heroIds, DateTime.UtcNow),
            Participants = participants,
            MissionStartMessage = missionStartMessage,
        };

        if (!heldMissionStarts.TryAdd(mapEventId, held))
        {
            // Already held - a re-requested start. Let the existing hold own it rather than racing a second
            // timer and a second SendMissionStart against it.
            held.Dispose();
            return true;
        }

        var roster = heroIds.ToArray();
        foreach (var participant in participants)
        {
            // Deployed players are not on the roster, so they must not be prompted either - an inquiry
            // raised over a running mission is the modal that cannot be dismissed.
            if (alreadyDeployed != null && alreadyDeployed.Contains(participant.ControllerId)) continue;
            network.Send(participant.Peer, new NetworkTroopPreferenceRequest(mapEventId, roster));
        }

        held.Expiry = new System.Threading.Timer(
            _ => GameThread.RunSafe(() => ReleaseHold(mapEventId, "timed out"), context: nameof(ReleaseHold)),
            null,
            TroopPreferenceTimeout,
            Timeout.InfiniteTimeSpan);

        Logger.Information("[TroopPreference] holding {MapEventId} while {Count} player(s) choose", mapEventId, roster.Length);
        return true;
    }

    private void Handle_NetworkTroopPreferenceChosen(MessagePayload<NetworkTroopPreferenceChosen> payload)
    {
        if (!ModInformation.IsServer) return;

        var mapEventId = payload.What.MapEventId;
        if (!heldMissionStarts.TryGetValue(mapEventId, out var held)) return;

        // Identity comes from the peer, never from the message - otherwise one client could answer for another
        // and start a battle the rest were still choosing for.
        if (payload.Who is not NetPeer peer || !playerManager.TryGetPlayer(peer, out var player)) return;

        bool complete = held.Barrier.Record(player.HeroId);
        Logger.Information("[TroopPreference] {MapEventId}: {Hero} chose; still waiting on [{Outstanding}]",
            mapEventId, player.HeroId, string.Join(", ", held.Barrier.Outstanding));

        if (complete)
        {
            ReleaseHold(mapEventId, "everyone chose");
            return;
        }

        BroadcastPreferenceProgress(held, held.Barrier.Outstanding.ToArray());
    }

    private void BroadcastPreferenceProgress(HeldMissionStart held, string[] outstanding)
    {
        foreach (var participant in held.Participants)
            network.Send(participant.Peer, new NetworkTroopPreferenceProgress(held.Barrier.MapEventId, outstanding));
    }

    /// <summary>Starts a held battle. Removes the hold first, so it can only fire once.</summary>
    private void ReleaseHold(string mapEventId, string reason)
    {
        if (!heldMissionStarts.TryRemove(mapEventId, out var held)) return;

        using (held)
        {
            Logger.Information("[TroopPreference] starting {MapEventId}: {Reason}{Unanswered}",
                mapEventId,
                reason,
                held.Barrier.Outstanding.Count == 0
                    ? string.Empty
                    : $" (never answered: {string.Join(", ", held.Barrier.Outstanding)})");

            // An empty list is what tells a client the wait is over and its waiting panel can go, whether the
            // battle started because everyone chose or because it ran out of patience.
            BroadcastPreferenceProgress(held, Array.Empty<string>());
            SendMissionStart(held.Participants, held.MissionStartMessage);
        }
    }

    // ---- client side of the barrier -------------------------------------------------------------------

    /// <summary>The battle this client has already answered for, so progress updates know to show the wait.</summary>
    private string answeredPreferenceForMapEventId;

    /// <summary>
    /// The battle whose deployment-preference inquiry is currently on screen, or null.
    /// </summary>
    /// <remarks>
    /// Tracked so the inquiry can be taken down if the battle starts without this player answering. The
    /// server gives up after <see cref="TroopPreferenceTimeout"/> and starts anyway, but nothing used to
    /// close the dialog: it stayed up over the running mission, modal and unanswerable, because the choice
    /// it was waiting for no longer existed. Dismissing the WAIT overlay was not enough - that is a
    /// different panel, shown only after a player has already chosen.
    /// </remarks>
    private string preferenceInquiryMapEventId;

    /// <summary>
    /// The server is holding a battle open for this player to choose how their troops deploy.
    /// </summary>
    /// <remarks>
    /// Safe to block on: no mission exists yet on any client, so nothing runs ahead while the dialog is up.
    /// That is the whole difference from the earlier attempt, which asked one client after the battle had
    /// already started for everyone else.
    /// </remarks>
    private void Handle_NetworkTroopPreferenceRequest(MessagePayload<NetworkTroopPreferenceRequest> payload)
    {
        if (ModInformation.IsServer) return;

        var mapEventId = payload.What.MapEventId;
        var roster = payload.What.ParticipantHeroIds ?? Array.Empty<string>();

        // A driven client has nobody to press a button and PopupCapture suppresses inquiries there, so it must
        // answer immediately - otherwise every headless battle waits out the full timeout.
        if (ModInformation.IsHeadless)
        {
            AnswerTroopPreference(mapEventId, LocalTroopDeploymentPreference.Current, roster);
            return;
        }

        GameThread.RunSafe(() =>
        {
            bool mineIsCurrent = LocalTroopDeploymentPreference.Current == TroopDeploymentPreference.MyTroopsFirst;
            preferenceInquiryMapEventId = mapEventId;
            InformationManager.ShowInquiry(new InquiryData(
                "Deployment Preference",
                "Which of your troops should fill this battle first?\n\n" +
                "Prefer My Troops - your own party deploys and reinforces first; the garrison, the militia " +
                "and other lords only once your party has nothing left to send." + "\n\n" +
                "Prefer All Party Troops - every party you field shares each wave in proportion to what it " +
                "has left." + "\n\n" +
                $"Currently: {(mineIsCurrent ? "Prefer My Troops" : "Prefer All Party Troops")}. This is " +
                "yours alone - it changes which of your troops arrive, never how many.",
                isAffirmativeOptionShown: true,
                isNegativeOptionShown: true,
                affirmativeText: "Prefer My Troops",
                negativeText: "Prefer All Party Troops",
                affirmativeAction: () => AnswerTroopPreference(mapEventId, TroopDeploymentPreference.MyTroopsFirst, roster),
                negativeAction: () => AnswerTroopPreference(mapEventId, TroopDeploymentPreference.AllPartyTroops, roster)));
        }, context: nameof(Handle_NetworkTroopPreferenceRequest));
    }

    private void AnswerTroopPreference(string mapEventId, TroopDeploymentPreference choice, string[] roster)
    {
        LocalTroopDeploymentPreference.Current = choice;
        answeredPreferenceForMapEventId = mapEventId;
        preferenceInquiryMapEventId = null;
        network.SendAll(new NetworkTroopPreferenceChosen(mapEventId));

        Logger.Information("[TroopPreference] {MapEventId}: chose {Choice}; waiting for the others", mapEventId, choice);

        // Show the wait immediately rather than waiting for the first progress message, so the moment between
        // clicking and the server answering is not a blank map with nothing happening.
        var others = DescribeHeroes(roster.Where(id => !IsLocalHero(id)));
        if (!string.IsNullOrEmpty(others)) TroopPreferenceWaitOverlay.ShowWaitingFor(others);
    }

    private void Handle_NetworkTroopPreferenceProgress(MessagePayload<NetworkTroopPreferenceProgress> payload)
    {
        if (ModInformation.IsServer) return;

        var outstanding = (payload.What.OutstandingHeroIds ?? Array.Empty<string>())
            .Where(id => !IsLocalHero(id))
            .ToArray();

        GameThread.RunSafe(() =>
        {
            // Empty means the wait is over - either everyone chose or the server ran out of patience. Either
            // way the mission start is on its way and the notice must go.
            if (outstanding.Length == 0 ||
                !string.Equals(answeredPreferenceForMapEventId, payload.What.MapEventId, StringComparison.Ordinal))
            {
                if (outstanding.Length == 0) answeredPreferenceForMapEventId = null;
                DismissPreferenceInquiry(payload.What.MapEventId);
                TroopPreferenceWaitOverlay.HideIfShown();
                return;
            }

            TroopPreferenceWaitOverlay.ShowWaitingFor(DescribeHeroes(outstanding));
        }, context: nameof(Handle_NetworkTroopPreferenceProgress));
    }

    /// <summary>
    /// Closes the deployment-preference inquiry if it is still on screen for this battle.
    /// </summary>
    /// <remarks>
    /// Scoped to the battle it was raised for, so a progress message about some other map event cannot
    /// close a dialog the player is still reading. Leaving the local preference untouched is deliberate:
    /// the player never chose, so whatever they had stays in force.
    /// </remarks>
    private void DismissPreferenceInquiry(string mapEventId)
    {
        if (preferenceInquiryMapEventId == null) return;
        if (!string.Equals(preferenceInquiryMapEventId, mapEventId, StringComparison.Ordinal)) return;

        preferenceInquiryMapEventId = null;
        Logger.Information(
            "[TroopPreference] {MapEventId}: battle started without an answer here; closing the prompt",
            mapEventId);
        InformationManager.HideInquiry();
    }

    private bool IsLocalHero(string heroId) =>
        !string.IsNullOrEmpty(heroId) &&
        objectManager.TryGetId(Hero.MainHero, out var mine) &&
        string.Equals(mine, heroId, StringComparison.Ordinal);

    /// <summary>Turns hero ids into readable names, falling back to the id so a waiting list is never blank.</summary>
    private string DescribeHeroes(IEnumerable<string> heroIds)
    {
        var names = new List<string>();
        foreach (var heroId in heroIds)
        {
            names.Add(objectManager.TryGetObject<Hero>(heroId, out var hero) && hero?.Name != null
                ? hero.Name.ToString()
                : heroId);
        }
        return string.Join(", ", names);
    }

    private void SendMissionStart(IReadOnlyList<MissionParticipant> participants, IMessage message)
    {
        if (message is NetworkStartAttackMission attackMission)
        {
            Logger.Information(
                "[BattleMissionLifecycle] Sending attack mission start: mapEvent={MapEventId} initiatingParty={InitiatingPartyId} participantCount={ParticipantCount}",
                attackMission.MapEventId,
                attackMission.InitiatingPartyId,
                participants.Count);
        }

        // Pattern matching, not `as`: both start messages are structs. Siege included, because a late
        // joiner opens a fresh barrier for a running siege exactly as it does for a field battle.
        string startedMapEventId =
            message is NetworkStartAttackMission attackStart ? attackStart.MapEventId
            : message is NetworkStartSiegeMission siegeStart ? siegeStart.MapEventId
            : null;

        foreach (var participant in participants)
        {
            // Do not start a mission for someone already in it.
            //
            // A late joiner re-runs this whole path for a battle that is already running, and the
            // participant list is everyone, not just the newcomer. Re-sending the start to a client that is
            // mid-battle tore it out of its mission and dropped it back on the encounter menu, which then
            // read as the two players' battles diverging: one fought on, the other sat at "You have
            // encountered..." with the fight continuing without them.
            if (startedMapEventId != null)
            {
                var deployed = missionStartedControllers.GetOrAdd(
                    startedMapEventId, _ => new HashSet<string>(StringComparer.Ordinal));

                bool alreadyStarted;
                lock (deployed) alreadyStarted = !deployed.Add(participant.ControllerId);

                if (alreadyStarted)
                {
                    Logger.Information(
                        "[BattleMissionLifecycle] {MapEventId}: {ControllerId} is already in this mission; " +
                        "not re-sending its start",
                        startedMapEventId,
                        participant.ControllerId);
                    continue;
                }
            }

            // Replay the recipient's authoritative membership first. The ordered channel and
            // idempotent client attachment make it present before the mission-start guard runs.
            network.Send(participant.Peer, new NetworkAddBattleParty(
                participant.MapEventSideId,
                participant.MapEventPartyId,
                participant.PartyId));
            network.Send(participant.Peer, message);
        }
    }

    private sealed class MissionParticipant
    {
        public string ControllerId { get; }
        public NetPeer Peer { get; }
        public string MapEventSideId { get; }
        public string MapEventPartyId { get; }
        public string PartyId { get; }

        public MissionParticipant(
            string controllerId,
            NetPeer peer,
            string mapEventSideId,
            string mapEventPartyId,
            string partyId)
        {
            ControllerId = controllerId;
            Peer = peer;
            MapEventSideId = mapEventSideId;
            MapEventPartyId = mapEventPartyId;
            PartyId = partyId;
        }
    }

    internal MissionInitializerRecord GetOrCreateMissionInitializerSnapshot(
        string mapEventId,
        Func<MissionInitializerRecord> create)
    {
        return mapEventMissionInitializers.GetOrAdd(mapEventId, _ => create());
    }

    private static AtmosphereInfo GetAtmosphereOnCampaign(MapEvent mapEvent)
    {
        var weatherModel = Campaign.Current?.Models?.MapWeatherModel;
        if (weatherModel == null)
            return default;

        try
        {
            return weatherModel.GetAtmosphereModel(mapEvent.Position);
        }
        catch (Exception e)
        {
            Logger.Warning(e, "Failed to read campaign atmosphere for map event; using default atmosphere");
            return default;
        }
    }

    /// <summary>
    /// When a client attacks a not-already-hostile party, declares war on the target faction and
    /// applies the player-hostility relation penalty against its leader - the war block of vanilla
    /// MenuHelper.EncounterAttackConsequence (BeHostileAction.ApplyEncounterHostileAction), which
    /// neither the client (it defers to the server) nor the dedicated server (it never opens the
    /// encounter menu) runs.
    /// </summary>
    private static void ApplyClientAttackHostileConsequences(MapEvent mapEvent, PartyBase attackerParty)
    {
        MapEventHostileActionConsequences.Apply(mapEvent, attackerParty, "attack");
    }

    private bool RemoveWoundedNonInitiatorParties(
        string mapEventId,
        MapEvent mapEvent,
        string initiatingPartyId)
    {
        foreach (var player in playerManager.Players)
        {
            if (string.Equals(player.MobilePartyId, initiatingPartyId, StringComparison.Ordinal) ||
                !objectManager.TryGetObject<Hero>(player.HeroId, out var hero) ||
                !hero.IsWounded ||
                !objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var mobileParty) ||
                mapEvent.FindMapEventParty(mobileParty.Party) == null ||
                !objectManager.TryGetId(mobileParty.Party, out var partyId))
                continue;

            bool leaveSiege = mapEvent.IsSiegeAssault && mobileParty.Party.Side == BattleSideEnum.Attacker;
            mobileParty.Party.MapEventSide = null;
            messageBroker.Publish(this, new BattleJoinCancelled(mapEventId, player.ControllerId));

            if (mapEvent.IsFinalized)
                return false;

            // Preserve the client's PlayerSiege reference until its explicit cleanup runs.
            network.SendAll(new NetworkPartyLeftBattle(partyId, leaveSiege));

            if (leaveSiege && mobileParty.BesiegerCamp != null)
                mobileParty.BesiegerCamp = null;
        }

        return true;
    }

    internal bool RemoveWoundedNonInitiatorParties(MapEvent mapEvent, string initiatingPartyId)
    {
        if (!objectManager.TryGetIdWithLogging(mapEvent, out var mapEventId))
            return false;

        return RemoveWoundedNonInitiatorParties(mapEventId, mapEvent, initiatingPartyId);
    }

    private int RollTerrainSeed()
    {
        // This runs on the network thread, so it avoids MBRandom, which mutates the
        // game's shared main-thread RNG state. System.Random is not thread-safe, so
        // the shared instance is guarded.
        lock (terrainSeedRandom)
        {
            return terrainSeedRandom.Next(MaxTerrainSeed);
        }
    }

    private void Handle_NetworkStartAttackMission(MessagePayload<NetworkStartAttackMission> payload)
    {
        // Opening a mission pushes a screen, and ScreenManager only tolerates screen
        // changes from the main thread; doing it from the network thread races its
        // layer lists and crashes the game.
        var message = payload.What;
        long sequence = Interlocked.Increment(ref attackMissionStartSequence);
        Logger.Information(
            "[BattleMissionLifecycle] Received attack mission start: sequence={Sequence} mapEvent={MapEventId} initiatingParty={InitiatingPartyId}",
            sequence,
            message.MapEventId,
            message.InitiatingPartyId);
        GameThread.RunSafe(
            () => ShowLoadingScreenAndQueueAttackMission(message, sequence),
            context: nameof(Handle_NetworkStartAttackMission));
    }

    private void ShowLoadingScreenAndQueueAttackMission(NetworkStartAttackMission message, long sequence)
    {
        if (!TryGetValidBattle(nameof(NetworkStartAttackMission), message.MapEventId, out _))
        {
            LogAttackMissionLifecycle("rejected before queue", sequence, message.MapEventId);
            return;
        }

        LoadingWindow.EnableGlobalLoadingWindow();
        LogAttackMissionLifecycle("queued", sequence, message.MapEventId);

        // MissionState enables the loading window only after building every mission behavior.
        // Defer that work one frame so the window is rendered before setup can stall the map.
        GameThread.EnqueueSafe(() =>
        {
            LogAttackMissionLifecycle("executing queued open", sequence, message.MapEventId);
            OpenAttackMission(message.MapEventId, message.MissionInitializer,
                message.InitiatingPartyId, sequence);

            if (MissionState.Current == null)
                LoadingWindow.DisableGlobalLoadingWindow();
        }, context: nameof(Handle_NetworkStartAttackMission));
    }

    /// <summary>[Server] Snapshot the mission-defining siege inputs for one map event.</summary>
    private NetworkStartSiegeMission BuildSiegeMissionSnapshot(string mapEventId, MapEvent mapEvent)
    {
        var settlement = mapEvent.MapEventSettlement;
        if (settlement == null || settlement.SiegeEvent == null)
            throw new InvalidOperationException($"Siege map event {mapEventId} has no active settlement siege");
        if (!objectManager.TryGetIdWithLogging(settlement, out var settlementId))
            throw new InvalidOperationException($"Siege settlement for {mapEventId} is not registered");

        var siegeEvent = settlement.SiegeEvent;
        int wallLevel = settlement.Town.GetWallLevel();

        var attackerEngines = SiegeEngineStateConverter.ToEngineStates(siegeEvent.GetPreparedAndActiveSiegeEngines(siegeEvent.GetSiegeEventSide(BattleSideEnum.Attacker)));
        var defenderEngines = SiegeEngineStateConverter.ToEngineStates(siegeEvent.GetPreparedAndActiveSiegeEngines(siegeEvent.GetSiegeEventSide(BattleSideEnum.Defender)));

        return new NetworkStartSiegeMission(mapEventId, wallLevel,
            settlement.SettlementWallSectionHitPointsRatioList.ToArray(), attackerEngines, defenderEngines,
            initiatingPartyId: null, settlementId: settlementId, isSallyOut: mapEvent.IsSiegeAmbush);
    }

    private void Handle_NetworkStartSiegeMission(MessagePayload<NetworkStartSiegeMission> payload)
    {
        var message = payload.What;
        var retryDeadline = DateTime.UtcNow.AddSeconds(SiegeMissionOpenRetrySeconds);
        GameThread.RunSafe(() => OpenSiegeMission(message, retryDeadline),
            context: nameof(Handle_NetworkStartSiegeMission));
    }

    private void OpenSiegeMission(NetworkStartSiegeMission payload, DateTime retryDeadline)
    {
        bool spawnGateEngaged = false;
        try
        {
            if (!TryGetValidBattle(nameof(NetworkStartSiegeMission), payload.MapEventId, out var battle))
                return;

            objectManager.TryGetId(MobileParty.MainParty, out var localPartyId);
            if (!ShouldOpenBattleMission(Hero.MainHero?.IsWounded == true, localPartyId,
                    payload.InitiatingPartyId))
            {
                Logger.Information("Not opening {Message}: the local player is wounded and another player started the battle", nameof(NetworkStartSiegeMission));
                return;
            }

            var settlementResolution = ResolveMissionSettlement(battle, payload.SettlementId, out var settlement);
            if (settlementResolution == MissionSettlementResolution.Retry)
            {
                if (DateTime.UtcNow >= retryDeadline)
                {
                    Logger.Error("Timed out waiting for settlement {SettlementId} for siege map event {MapEventId}",
                        payload.SettlementId, payload.MapEventId);
                    return;
                }

                GameThread.EnqueueSafe(() => OpenSiegeMission(payload, retryDeadline),
                    context: nameof(Handle_NetworkStartSiegeMission));
                return;
            }
            if (settlementResolution == MissionSettlementResolution.Rejected)
                return;

            // The scene is the fixed settlement scene keyed by wall level — no terrain seed on the siege
            // path. Mirrors vanilla CreateSandBoxMissionInitializerRecord; atmosphere is client-local,
            // same tolerance as the field path.
            string sceneName = settlement.LocationComplex.GetLocationWithId("center").GetSceneName(payload.WallLevel);
            var rec = new MissionInitializerRecord(sceneName)
            {
                SceneLevels = Campaign.Current.Models.LocationModel.GetUpgradeLevelTag(payload.WallLevel) + " siege",
                DamageToFriendsMultiplier = Campaign.Current.Models.DifficultyModel.GetPlayerTroopsReceivedDamageMultiplier(),
                DamageFromPlayerToFriendsMultiplier = Campaign.Current.Models.DifficultyModel.GetPlayerTroopsReceivedDamageMultiplier(),
                PlayingInCampaignMode = true,
                AtmosphereOnCampaign = Campaign.Current.Models.MapWeatherModel.GetAtmosphereModel(MobileParty.MainParty.Position),
                TerrainType = (int)Campaign.Current.MapSceneWrapper.GetFaceTerrainType(MobileParty.MainParty.CurrentNavigationFace),
                DecalAtlasGroup = 3,
            };

            var attackerWeapons = SiegeEngineStateConverter.ToMissionWeapons(payload.AttackerEngines);
            var defenderWeapons = SiegeEngineStateConverter.ToMissionWeapons(payload.DefenderEngines);

            if (BattleSpawnConfig.Enabled)
            {
                BattleSpawnGate.BeginBattle(payload.MapEventId);
                spawnGateEngaged = true;
                Logger.Information("[BattleSync] Engaged spawn gate in OpenSiegeMission: mapEvent={MapEventId}", payload.MapEventId);
            }

            // No native fallback: SandBoxMissions.OpenSiegeMissionWithDeployment would size to whole-side
            // counts and never attach the coop behaviors, so a missing launcher is a hard error.
            if (ContainerProvider.TryResolve(out ICoopSiegeBattleLauncher siegeLauncher))
            {
                var mission = siegeLauncher.OpenCoopSiegeBattle(rec, payload.WallHitPointRatios,
                    attackerWeapons, defenderWeapons, payload.IsSallyOut);
                if (mission != null)
                    spawnGateEngaged = false; // the attached mission lifecycle owns EndBattle from here
                else
                    Logger.Error("[BattleSync] Coop siege launcher returned no mission");
            }
            else
            {
                Logger.Error("[BattleSync] ICoopSiegeBattleLauncher unavailable; cannot open the siege mission");
            }
        }
        catch (Exception e)
        {
            // GameThread runs queued actions unguarded, so a throw from here
            // would escape into the game's main tick and crash it.
            Logger.Error(e, "Failed to open the siege mission for {Message}", nameof(NetworkStartSiegeMission));
        }
        finally
        {
            UnwindSpawnGateAfterFailedOpen(spawnGateEngaged);
        }
    }

    private MissionSettlementResolution ResolveMissionSettlement(
        MapEvent battle,
        string settlementId,
        out Settlement settlement)
    {
        settlement = battle.MapEventSettlement;
        if (settlement != null)
        {
            if (string.IsNullOrEmpty(settlementId))
                return MissionSettlementResolution.Resolved;

            if (!objectManager.TryGetId(settlement, out var currentSettlementId) ||
                !string.Equals(currentSettlementId, settlementId, StringComparison.Ordinal))
            {
                Logger.Error("Received {Message} for settlement {SettlementId}, but the map event is bound to a different settlement",
                    nameof(NetworkStartSiegeMission), settlementId);
                return MissionSettlementResolution.Rejected;
            }

            return MissionSettlementResolution.Resolved;
        }

        if (string.IsNullOrEmpty(settlementId))
        {
            Logger.Error("Received {Message} without a settlement id while the battle settlement is missing",
                nameof(NetworkStartSiegeMission));
            return MissionSettlementResolution.Rejected;
        }

        if (!objectManager.TryGetObject(settlementId, out settlement))
            return MissionSettlementResolution.Retry;

        using (new AllowedThread())
            battle.MapEventSettlement = settlement;
        return MissionSettlementResolution.Resolved;
    }

    private enum MissionSettlementResolution
    {
        Resolved,
        Retry,
        Rejected,
    }

    /// <summary>[Client] Re-validates everything a mission open depends on: the battle can end (or
    /// another mission can open) between the server round-trip and the queued open running. The
    /// MissionState check covers a second start queued in the same frame.</summary>
    private bool TryGetValidBattle(string messageName, string expectedMapEventId, out MapEvent battle)
    {
        battle = null;
        if (Campaign.Current == null)
        {
            Logger.Warning("Received {Message} but the campaign was not loaded, not opening the mission", messageName);
            return false;
        }

        battle = MobileParty.MainParty?.MapEvent;
        if (battle == null)
        {
            Logger.Warning("Received {Message} but the main party is no longer in a map event, not opening the mission", messageName);
            return false;
        }

        if (!MatchesMapEventId(objectManager, battle, expectedMapEventId))
        {
            Logger.Warning("Received {Message} for map event {MapEventId}, but the local player is not in that battle; not opening the mission", messageName, expectedMapEventId);
            return false;
        }

        if (battle.FindMapEventParty(PartyBase.MainParty) == null)
        {
            Logger.Warning("Received {Message} for map event {MapEventId}, but the main party has no authoritative side membership; not opening the mission",
                messageName, expectedMapEventId);
            return false;
        }

        if (MissionState.Current != null)
        {
            Logger.Warning("Received {Message} but a mission is already open, not opening the mission", messageName);
            return false;
        }

        return true;
    }

    internal static bool MatchesMapEventId(IObjectManager objectManager, MapEvent battle, string expectedMapEventId)
    {
        return objectManager != null
            && battle != null
            && objectManager.TryGetId(battle, out var actualMapEventId)
            && string.Equals(actualMapEventId, expectedMapEventId, StringComparison.Ordinal);
    }

    private void OpenAttackMission(string mapEventId, MissionInitializerRecord missionInitializer,
        string initiatingPartyId, long sequence)
    {
        bool spawnGateEngaged = false;
        try
        {
            if (!TryGetValidBattle(nameof(NetworkStartAttackMission), mapEventId, out var battle))
            {
                LogAttackMissionLifecycle("rejected at open", sequence, mapEventId);
                return;
            }

            if (battle.IsNavalMapEvent)
            {
                Logger.Warning("Received {Message} for naval map event {MapEventId}, but naval battles are disabled", nameof(NetworkStartAttackMission), mapEventId);
                return;
            }

            objectManager.TryGetId(MobileParty.MainParty, out var localPartyId);
            if (!ShouldOpenBattleMission(Hero.MainHero?.IsWounded == true, localPartyId, initiatingPartyId))
            {
                Logger.Information("Not opening {Message}: the local player is wounded and another player started the battle", nameof(NetworkStartAttackMission));
                LogAttackMissionLifecycle("rejected wounded non-initiator", sequence, mapEventId);
                return;
            }

            LogAttackMissionLifecycle("opening", sequence, mapEventId);
            InitializePlayerEncounter(battle);

            // Engage the spawn gate BEFORE OpenBattleMission builds the mission — the deployment controller
            // spawns the initial wave during mission setup (inside OpenBattleMission), earlier than the
            // CoopBattleController attach. The gate only marks "a coop battle is active" for the spawn patches;
            // who fields which troops is decided by the server-fed reserves (CoopTroopSupplier).
            if (BattleSpawnConfig.Enabled)
            {
                BattleSpawnGate.BeginBattle(mapEventId);
                spawnGateEngaged = true;
                Logger.Information("[BattleSync] Engaged spawn gate in OpenAttackMission: mapEvent={MapEventId}", mapEventId);
            }

            // Coop opens a custom battle mission (per-client troop suppliers) instead of the native one; the
            // launcher lives in Missions and is resolved from the container. There is deliberately no native
            // fallback: the same unavailable container would prevent BattleMissionEntryPatch from attaching the
            // lifecycle that owns EndBattle, while the already-engaged spawn patches could corrupt native setup.
            if (ContainerProvider.TryResolve(out ICoopFieldBattleLauncher battleLauncher))
            {
                var mission = battleLauncher.OpenCoopFieldBattle(missionInitializer);
                if (mission != null)
                {
                    spawnGateEngaged = false; // the attached mission lifecycle owns EndBattle from here
                    MissionStateFinalizeDiagnosticsPatch.RecordCorrelation(mission, sequence, mapEventId);
                    Logger.Information(
                        "[BattleMissionLifecycle] Attack mission opened: sequence={Sequence} mapEvent={MapEventId} scene={Scene} missionStatePresent={MissionStatePresent} missionPresent={MissionPresent}",
                        sequence,
                        mapEventId,
                        mission.SceneName,
                        MissionState.Current != null,
                        Mission.Current != null);
                }
                else
                {
                    Logger.Error("[BattleSync] Coop field-battle launcher returned no mission");
                    LogAttackMissionLifecycle("launcher returned no mission", sequence, mapEventId);
                }
            }
            else
            {
                Logger.Error("[BattleSync] ICoopFieldBattleLauncher unavailable; cannot safely open the field battle mission");
                LogAttackMissionLifecycle("launcher unavailable", sequence, mapEventId);
            }
        }
        catch (Exception e)
        {
            // GameThread runs queued actions unguarded, so a throw from here
            // would escape into the game's main tick and crash it.
            Logger.Error(e,
                "[BattleMissionLifecycle] Failed to open attack mission: sequence={Sequence} mapEvent={MapEventId} message={Message}",
                sequence,
                mapEventId,
                nameof(NetworkStartAttackMission));
        }
        finally
        {
            UnwindSpawnGateAfterFailedOpen(spawnGateEngaged);
        }
    }

    private void LogAttackMissionLifecycle(string stage, long sequence, string mapEventId)
    {
        Logger.Information(
            "[BattleMissionLifecycle] Attack mission {Stage}: sequence={Sequence} mapEvent={MapEventId} missionStatePresent={MissionStatePresent} missionPresent={MissionPresent}",
            stage,
            sequence,
            mapEventId,
            MissionState.Current != null,
            Mission.Current != null);
    }

    internal static void InitializePlayerEncounter(MapEvent battle)
    {
        if (PlayerEncounter.Battle == battle)
            return;

        PlayerEncounter.Start();
        var encounter = PlayerEncounter.Current;
        var playerSide = battle.PlayerSide;
        var opponentSide = playerSide.GetOppositeSide();
        var opponentParty = playerSide == BattleSideEnum.Attacker
            ? battle.DefenderSide.LeaderParty
            : battle.AttackerSide.LeaderParty;

        // The server already assigned map-event membership; restore this client's joined-battle context.
        encounter._attackerParty = playerSide == BattleSideEnum.Attacker ? PartyBase.MainParty : opponentParty;
        encounter._defenderParty = playerSide == BattleSideEnum.Defender ? PartyBase.MainParty : opponentParty;
        encounter._encounteredParty = opponentParty;
        encounter._mapEvent = battle;
        encounter.PlayerSide = playerSide;
        encounter.OpponentSide = opponentSide;
        encounter.EncounterSettlementAux = battle.MapEventSettlement;
        encounter.PlayerPartyInitialStrength = PartyBase.MainParty.CalculateCurrentStrength();
        encounter.IsJoinedBattle = true;
    }

    internal static void UnwindSpawnGateAfterFailedOpen(bool spawnGateEngaged)
    {
        if (spawnGateEngaged) BattleSpawnGate.EndBattle();
    }

    internal static bool ShouldOpenBattleMission(bool isPlayerWounded, string localPartyId, string initiatingPartyId)
    {
        return !isPlayerWounded ||
               (localPartyId != null && string.Equals(localPartyId, initiatingPartyId, StringComparison.Ordinal));
    }
}
