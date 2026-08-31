using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.MapEventParties.Messages;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEventParties.Handlers;

internal class MapEventPartyHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<MapEventPartyHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;

    /// <summary>Keeps unchanged rosters off the wire; see <see cref="RosterBroadcastGate"/>.</summary>
    private readonly RosterBroadcastGate rosterBroadcastGate = new RosterBroadcastGate();

    private const double RosterReportIntervalSeconds = 60d;
    private DateTime lastRosterReportUtc = DateTime.MinValue;

    public MapEventPartyHandler(IMessageBroker messageBroker, INetwork network, IObjectManager objectManager)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;

        messageBroker.Subscribe<OnTroopKilledAttempted>(Handle_OnTroopKilledAttempted);
        messageBroker.Subscribe<NetworkTroopKilled>(Handle_NetworkTroopKilled);

        messageBroker.Subscribe<OnTroopWoundedAttempted>(Handle_OnTroopWoundedAttempted);
        messageBroker.Subscribe<NetworkTroopWounded>(Handle_NetworkTroopWounded);

        messageBroker.Subscribe<OnTroopScoreHitAttempted>(Handle_OnTroopScoreHitAttempted);
        messageBroker.Subscribe<NetworkTroopScoreHit>(Handle_NetworkTroopScoreHit);

        // Client
        messageBroker.Subscribe<RequestMapEventPartyUpdate>(Handle_RequestMapEventPartyUpdate);
        messageBroker.Subscribe<NetworkRequestMapEventPartyUpdate>(Handle_NetworkRequestMapEventPartyUpdate);

        // Server
        messageBroker.Subscribe<MapEventPartyUpdated>(Handle_MapEventPartyUpdated);
        messageBroker.Subscribe<NetworkUpdateMapEventParty>(Handle_NetworkUpdateMapEventParty);
    }



    public void Dispose()
    {
        messageBroker.Unsubscribe<OnTroopKilledAttempted>(Handle_OnTroopKilledAttempted);
        messageBroker.Unsubscribe<NetworkTroopKilled>(Handle_NetworkTroopKilled);

        messageBroker.Unsubscribe<OnTroopWoundedAttempted>(Handle_OnTroopWoundedAttempted);
        messageBroker.Unsubscribe<NetworkTroopWounded>(Handle_NetworkTroopWounded);

        messageBroker.Unsubscribe<OnTroopScoreHitAttempted>(Handle_OnTroopScoreHitAttempted);
        messageBroker.Unsubscribe<NetworkTroopScoreHit>(Handle_NetworkTroopScoreHit);
        messageBroker.Unsubscribe<RequestMapEventPartyUpdate>(Handle_RequestMapEventPartyUpdate);
        messageBroker.Unsubscribe<NetworkRequestMapEventPartyUpdate>(Handle_NetworkRequestMapEventPartyUpdate);
        messageBroker.Unsubscribe<MapEventPartyUpdated>(Handle_MapEventPartyUpdated);
        messageBroker.Unsubscribe<NetworkUpdateMapEventParty>(Handle_NetworkUpdateMapEventParty);
    }

    private void Handle_RequestMapEventPartyUpdate(MessagePayload<RequestMapEventPartyUpdate> payload)
    {
        if (!objectManager.TryGetIdWithLogging(payload.What.MapEventParty, out var mapEventPartyId))
            return;

        network.SendAll(new NetworkRequestMapEventPartyUpdate(mapEventPartyId));
    }

    private void Handle_NetworkRequestMapEventPartyUpdate(MessagePayload<NetworkRequestMapEventPartyUpdate> payload)
    {
        var obj = payload.What;

        GameThread.Run(() =>
        {
            try
            {
                if (!objectManager.TryGetObjectWithLogging<MapEventParty>(obj.MapEventPartyId, out var mapEventParty))
                    return;

                mapEventParty.Update();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to apply {Message}", nameof(NetworkRequestMapEventPartyUpdate));
            }
        });
    }

    private void Handle_MapEventPartyUpdated(MessagePayload<MapEventPartyUpdated> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.MapEventParty, out var mapEventPartyId))
            return;

        var flattenedTroops = FlattenedTroopSerializer.Serialize(obj.Roster, objectManager);

        // Vanilla re-runs MapEventParty.Update for EVERY party on both sides once per simulation round, so
        // this fires for rosters in which nothing changed - measured at 600 packets / 3.2 MB in ten seconds,
        // enough to put 5.3 MB into an 8 MB reliable buffer and take a 1 ms peer to 80 ms of queueing.
        // The receiver replaces the roster wholesale, so an identical snapshot is a no-op and skipping it is
        // indistinguishable from sending it. See RosterBroadcastGate for the keyframe that bounds staleness.
        var now = DateTime.UtcNow;
        if (!rosterBroadcastGate.ShouldBroadcast(mapEventPartyId, flattenedTroops, now))
            return;

        ReportRosterBroadcastSavings(now);

        var message = new NetworkUpdateMapEventParty(
            mapEventPartyId,
            FlattenedTroopPayload.Compress(flattenedTroops));
        network.SendAll(message);
    }

    /// <summary>
    /// Says once a minute how much of this traffic the gate is removing.
    /// </summary>
    /// <remarks>
    /// The saving is the whole point of the gate and is invisible from the outside - a battle that runs well
    /// looks identical to one that never had the problem. Printing sent-versus-suppressed makes it possible to
    /// answer "did that help" from a log after the fact, rather than by reproducing the battle.
    /// </remarks>
    private void ReportRosterBroadcastSavings(DateTime nowUtc)
    {
        if ((nowUtc - lastRosterReportUtc).TotalSeconds < RosterReportIntervalSeconds) return;
        lastRosterReportUtc = nowUtc;

        long sent = rosterBroadcastGate.Sent;
        long suppressed = rosterBroadcastGate.Suppressed;
        long total = sent + suppressed;
        if (total <= 0) return;

        long rosters = FlattenedTroopPayload.TotalPayloads;
        long rawBytes = FlattenedTroopPayload.TotalRawBytes;
        long wireBytes = FlattenedTroopPayload.TotalCompressedBytes;

        // The average matters more than the total here: the batcher refuses anything at or above 1200
        // bytes, so an average that sits under it is the evidence that these rosters are sharing
        // datagrams again instead of fragmenting into several apiece.
        Logger.Information(
            "[RosterTraffic] map-event roster broadcasts: {Sent} sent, {Suppressed} suppressed ({Percent:F1}% removed) | payloads: {Rosters} rosters, {RawBytes} -> {WireBytes} bytes ({Saved:F1}% smaller, avg {AverageBytes} bytes/roster)",
            sent,
            suppressed,
            100d * suppressed / total,
            rosters,
            rawBytes,
            wireBytes,
            rawBytes > 0 ? 100d * (rawBytes - wireBytes) / rawBytes : 0d,
            rosters > 0 ? wireBytes / rosters : 0L);
    }

    /// <summary>
    /// Applies a roster snapshot, decoding it BEFORE the game thread is asked to do anything.
    /// </summary>
    /// <remarks>
    /// Inflating the payload touches no game state - only bytes and protobuf - so it is deliberately kept
    /// outside <see cref="GameThread.Run"/>. Rebuilding the roster itself cannot be: it resolves every troop
    /// through the object manager and writes to a live map-event party. Splitting them keeps the frame
    /// thread doing only the part that genuinely belongs to it, which matters because the client applying
    /// these updates is the machine already struggling in a large battle.
    ///
    /// A payload that will not decode is dropped here, before any game work is scheduled. The roster then
    /// keeps its previous value rather than being replaced by a plausible-looking empty one, and the next
    /// snapshot - or the gate keyframe - corrects it.
    /// </remarks>
    private void Handle_NetworkUpdateMapEventParty(MessagePayload<NetworkUpdateMapEventParty> payload)
    {
        var obj = payload.What;

        FlattenedTroop[] troops;
        try
        {
            troops = FlattenedTroopPayload.Decompress(obj.CompressedTroops);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to decode {Message}", nameof(NetworkUpdateMapEventParty));
            return;
        }

        GameThread.Run(() =>
        {
            try
            {
                if (!objectManager.TryGetObjectWithLogging<MapEventParty>(obj.MapEventPartyId, out var mapEventParty))
                    return;

                mapEventParty._roster = FlattenedTroopSerializer.Deserialize(troops, objectManager);

                messageBroker.Publish(this, new MapEventTroopsUpdated(mapEventParty));
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to apply {Message}", nameof(NetworkUpdateMapEventParty));
            }
        });
    }

    private void Handle_OnTroopKilledAttempted(MessagePayload<OnTroopKilledAttempted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.MapEventParty, out var mapEventPartyId))
            return;

        var message = new NetworkTroopKilled(mapEventPartyId, obj.TroopSeed);

        network.SendAll(message);
    }

    private void Handle_NetworkTroopKilled(MessagePayload<NetworkTroopKilled> payload)
    {
        var obj = payload.What;

        GameThread.Run(() =>
        {
            try
            {
                if (!objectManager.TryGetObjectWithLogging(obj.MapEventPartyId, out MapEventParty mapEventParty))
                    return;

                var troopDescriptor = new UniqueTroopDescriptor(obj.TroopSeed);

                if (ModInformation.IsServer)
                {
                    mapEventParty.OnTroopKilled(troopDescriptor);
                }
                else
                {
                    // Only the scoreboard tally; Party.MemberRoster arrives separately.
                    using (new AllowedThread())
                    {
                        mapEventParty.Troops.OnTroopKilled(troopDescriptor);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error handling NetworkTroopKilled message for MapEventParty with ID {MapEventPartyId}", obj.MapEventPartyId);
            }
        });
    }

    private void Handle_OnTroopWoundedAttempted(MessagePayload<OnTroopWoundedAttempted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.MapEventParty, out var mapEventPartyId))
            return;

        var message = new NetworkTroopWounded(mapEventPartyId, obj.TroopSeed);

        network.SendAll(message);
    }

    private void Handle_NetworkTroopWounded(MessagePayload<NetworkTroopWounded> payload)
    {
        var obj = payload.What;

        GameThread.Run(() =>
        {
            try
            {
                if (!objectManager.TryGetObjectWithLogging(obj.MapEventPartyId, out MapEventParty mapEventParty))
                    return;

                var troopDescriptor = new UniqueTroopDescriptor(obj.TroopSeed);

                if (ModInformation.IsServer)
                {
                    mapEventParty.OnTroopWounded(troopDescriptor);
                }
                else
                {
                    // Only the scoreboard tally; Party.MemberRoster arrives separately.
                    using (new AllowedThread())
                    {
                        mapEventParty.Troops.OnTroopWounded(troopDescriptor);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error handling NetworkTroopWounded message for MapEventParty with ID {MapEventPartyId}", obj.MapEventPartyId);
            }
        });
    }

    private void Handle_OnTroopScoreHitAttempted(MessagePayload<OnTroopScoreHitAttempted> payload)
    {
        var obj = payload.What;

        if (ModInformation.IsServer) return;

        if (!objectManager.TryGetIdWithLogging(obj.MapEventParty, out var mapEventPartyId))
            return;

        if (!objectManager.TryGetIdWithLogging(obj.AttackingTroop, out var attackingTroopId))
            return;

        if (!objectManager.TryGetIdWithLogging(obj.AttackedTroop, out var attackedTroopId))
            return;

        network.SendAll(new NetworkTroopScoreHit(
            mapEventPartyId,
            attackingTroopId,
            attackedTroopId,
            obj.Damage,
            obj.IsFatal,
            obj.IsSimulatedHit));
    }

    private void Handle_NetworkTroopScoreHit(MessagePayload<NetworkTroopScoreHit> payload)
    {
        // Server-authoritative: clients receive the resulting contribution through the
        // MapEventParty._contributionToBattle autosync and the roster xp through the roster sync.
        if (ModInformation.IsClient) return;

        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging(obj.MapEventPartyId, out MapEventParty mapEventParty))
                return;

            if (!objectManager.TryGetObjectWithLogging(obj.AttackingTroopId, out CharacterObject attackingTroop))
                return;

            if (!objectManager.TryGetObjectWithLogging(obj.AttackedTroopId, out CharacterObject attackedTroop))
                return;

            ApplyTroopScoreHit(
                mapEventParty,
                attackingTroop,
                attackedTroop,
                obj.Damage,
                obj.IsFatal,
                obj.IsSimulatedHit);
        }, context: nameof(Handle_NetworkTroopScoreHit));
    }

    private void ApplyTroopScoreHit(
        MapEventParty mapEventParty,
        CharacterObject attackingTroop,
        CharacterObject attackedTroop,
        int damage,
        bool isFatal,
        bool isSimulatedHit)
    {
        var roster = mapEventParty.Troops;
        UniqueTroopDescriptor? fallbackDescriptor = null;
        if (roster != null)
        {
            foreach (var element in roster)
            {
                if (element.Troop != attackingTroop) continue;
                fallbackDescriptor ??= element.Descriptor;
                if (element.IsKilled || element.IsWounded || element.IsRouted) continue;

                // The attacker's weapon is not carried over the wire; native simulation also passes null.
                mapEventParty.OnTroopScoreHit(
                    element.Descriptor,
                    attackedTroop,
                    damage,
                    isFatal,
                    isTeamKill: false,
                    null,
                    isSimulatedHit);
                return;
            }
        }

        if (fallbackDescriptor.HasValue)
        {
            // The score message may arrive after the matching attacker became a casualty.
            mapEventParty.OnTroopScoreHit(
                fallbackDescriptor.Value,
                attackedTroop,
                damage,
                isFatal,
                isTeamKill: false,
                null,
                isSimulatedHit);
            return;
        }

        Logger.Warning(
            "Score hit for {AttackingTroop} dropped: no matching troop in party {Party}'s current roster",
            attackingTroop.StringId,
            mapEventParty.Party?.Id);
    }
}
