using Common;
using Common.Logging;
using Common.Messaging;
using Common.Util;
using GameInterface.Services.Entity;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MobileParties.Interfaces;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.MobilePartyAIs;
using GameInterface.Services.ObjectManager;
using static GameInterface.Services.ObjectManager.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties.Handlers;

/// <summary>
/// Handles synchronization of the <see cref="MobilePartyAi"/>'s behavior on the campaign map, which includes
/// target positions and target entities used for updating movement.
/// </summary>
/// <remarks>
/// Important note: <see cref="MobilePartyAi"/> is also present in player-controlled parties, where it is 
/// responsible for pathfinding and movement.
/// </remarks>
/// <seealso cref="AiBehavior"/>
internal class MobilePartyBehaviorHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<MobilePartyBehaviorHandler>();

    private readonly Dictionary<string, PartyBehaviorUpdateData> latestPredictions = new Dictionary<string, PartyBehaviorUpdateData>();
    private readonly IMessageBroker messageBroker;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly IMobilePartyInterface mobilePartyInterface;
    private readonly IObjectManager objectManager;
    private readonly IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot;
    private readonly PartyPositionCorrection positionCorrection = new PartyPositionCorrection();

    public MobilePartyBehaviorHandler(
        IMessageBroker messageBroker,
        IControllerIdProvider controllerIdProvider,
        IMobilePartyInterface mobilePartyInterface,
        IObjectManager objectManager,
        IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot)
    {
        this.messageBroker = messageBroker;
        this.controllerIdProvider = controllerIdProvider;
        this.mobilePartyInterface = mobilePartyInterface;
        this.objectManager = objectManager;
        this.mobilePartyBehaviorSnapshot = mobilePartyBehaviorSnapshot;

        messageBroker.Subscribe<PartyBehaviorChangeAttempted>(Handle_PartyBehaviorChanged);
        messageBroker.Subscribe<UpdatePartyBehavior>(Handle_UpdatePartyBehavior);
        messageBroker.Subscribe<CampaignReady>(Handle_CampaignReady);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PartyBehaviorChangeAttempted>(Handle_PartyBehaviorChanged);
        messageBroker.Unsubscribe<UpdatePartyBehavior>(Handle_UpdatePartyBehavior);
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
    }

    /// <summary>
    /// Starts the drift sweep once a campaign exists to sweep.
    /// </summary>
    /// <remarks>
    /// Registered here rather than in the constructor because the handler is built by the container before any
    /// campaign is loaded, and <c>CampaignEvents</c> has nothing to attach to until then.
    /// </remarks>
    private void Handle_CampaignReady(MessagePayload<CampaignReady> obj)
    {
        // Server only: the correction re-states what the authority believes, so a client emitting them would
        // simply be arguing with the server about parties it does not own.
        if (!ModInformation.IsServer) return;

        CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, CorrectDriftedPartyPositions);
    }

    /// <summary>
    /// Re-states the position of parties that have travelled far enough for the clients' copies to have
    /// drifted - see <see cref="PartyPositionCorrection"/> for why this is needed at all.
    /// </summary>
    /// <remarks>
    /// Hourly rather than per-frame: a campaign hour is the coarsest cadence that still bounds the error to
    /// something a player would not read as a jump, and it costs one pass over the party list per hour instead
    /// of per frame. The pass is budgeted, so the cost of a fast-forward is bounded too.
    ///
    /// Failures are swallowed deliberately. This runs on the campaign tick, where an escaping exception stops
    /// the whole tick; a missed correction is a party that jumps once, which is the bug this reduces, not a
    /// reason to take the campaign down with it.
    /// </remarks>
    private void CorrectDriftedPartyPositions()
    {
        try
        {
            var parties = Campaign.Current?.CampaignObjectManager?.MobileParties;
            if (parties == null) return;

            foreach (var party in positionCorrection.SelectPartiesToCorrect(parties))
                PublishForcedPosition(party);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Party position drift correction failed; the campaign continues uncorrected");
        }
    }

    public void Handle_PartyBehaviorChanged(MessagePayload<PartyBehaviorChangeAttempted> obj)
    {
        var party = obj.What.Party;

        // TEMP INSTRUMENTATION for the "cannot move for the first N seconds after joining" bug.
        // This is the gate every player order passes through, and when it drops one it does so silently -
        // no send, no error, nothing in any log - which is why polling party STATE never explained it.
        // Logs both that an order arrived and whether it survived, so a dead window can be told apart from
        // a window where the order never reached here at all.
        if (ModInformation.IsClient && party != null && party.IsPlayerParty())
        {
            Logger.Warning(
                "[MoveProbe] order for {Party}: controlled={Controlled} mainParty={IsMain} settlement={Settlement} active={Active}",
                party.StringId,
                party.IsControlledByThisInstance(),
                ReferenceEquals(party, MobileParty.MainParty),
                party.CurrentSettlement?.Name?.ToString() ?? "<none>",
                party.IsActive);
        }

        if (ModInformation.IsClient && !party.IsControlledByThisInstance())
        {
            if (party != null && party.IsPlayerParty())
            {
                Logger.Warning(
                    "[MoveProbe] DROPPED order for {Party}: this client does not (yet) hold control",
                    party.StringId);
            }

            return;
        }

        if (!mobilePartyBehaviorSnapshot.TryCreate(
                party,
                out PartyBehaviorUpdateData data))
            return;

        data.ForcePosition = obj.What.ForcePosition;
        data.IsCurrentlyAtSea = obj.What.IsCurrentlyAtSea;
        data.ResetMovementToHold = obj.What.ResetMovementToHold;

        if (ModInformation.IsClient)
        {
            data.OriginControllerId = controllerIdProvider.ControllerId;
            latestPredictions[data.MobilePartyId] = data;
            messageBroker.Publish(this, new ControlledPartyBehaviorUpdated(data));
            return;
        }

        messageBroker.Publish(this, new PartyBehaviorUpdated(ref data));
    }

    public void Handle_UpdatePartyBehavior(MessagePayload<UpdatePartyBehavior> obj)
    {
        var data = obj.What.BehaviorUpdateData;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging(data.MobilePartyId, out MobileParty party))
                return;

            bool isSelfEcho = ModInformation.IsClient &&
                party.IsControlledByThisInstance() &&
                !string.IsNullOrEmpty(data.OriginControllerId) &&
                string.Equals(data.OriginControllerId, controllerIdProvider.ControllerId, StringComparison.Ordinal);

            // Reapply the newest local command if an authoritative update arrived before its echo.
            if (!TrySelectBehaviorUpdate(isSelfEcho, latestPredictions, ref data))
                return;

            List<MobileParty> attachedParties = null;
            if (ModInformation.IsServer && data.ForcePosition)
            {
                attachedParties = ApplyServerForcedPosition(party, data.PartyPosition, data.IsCurrentlyAtSea);
                if (data.ResetMovementToHold)
                {
                    party.SetMoveModeHold();
                    party.ResetNavigationToHold();
                }
            }

            IInteractablePoint interactablePoint = null;
            // AllowedThread keeps outbound movement patches quiet while the complete snapshot is replayed.
            using (new AllowedThread())
            {
                if ((!ModInformation.IsServer || !data.ResetMovementToHold) &&
                    !mobilePartyBehaviorSnapshot.TryApply(
                        party,
                        data,
                        out interactablePoint))
                    return;

                if (ModInformation.IsClient && data.ForcePosition)
                    ApplyForcedPosition(party, data.PartyPosition, data.IsCurrentlyAtSea);

                if (ModInformation.IsClient && data.ResetMovementToHold)
                {
                    party.SetMoveModeHold();
                    party.ResetNavigationToHold();
                }

                if (MobilePartyAiConfig.DEBUG)
                {
                    Logger.Debug(
                        "Setting AI behavior. PartyId: {PartyId}, Behavior: {Behavior}, TargetParty: {TargetParty}, BestTargetPoint: {BestTargetPoint}",
                        data.MobilePartyId,
                        data.NewAiBehavior,
                        interactablePoint,
                        data.BestTargetPoint);
                }

                if (ModInformation.IsClient)
                {
                    // Moving parties already simulate the replicated target, so an in-flight snapshot is stale.
                    if (!data.ForcePosition && ShouldApplyAuthoritativePosition(
                            isSelfEcho,
                            data.ForcePosition,
                            party.PartyMoveMode == MoveModeType.Hold,
                            party.Position,
                            data.PartyPosition))
                        party.Position = data.PartyPosition;
                }
            }

            if (ModInformation.IsServer)
                PublishAuthoritativeBehavior(party, data);

            if (attachedParties != null)
            {
                foreach (var attachedParty in attachedParties)
                    PublishForcedPosition(attachedParty);
            }
        });
    }

    private static void ApplyForcedPosition(MobileParty party, CampaignVec2 position, bool isCurrentlyAtSea)
    {
        party.Position = position;

        if (party.IsCurrentlyAtSea != isCurrentlyAtSea)
            party.ChangeIsCurrentlyAtSeaCheat();
    }

    private static List<MobileParty> ApplyServerForcedPosition(
        MobileParty party,
        CampaignVec2 position,
        bool isCurrentlyAtSea)
    {
        ApplyForcedPosition(party, position, isCurrentlyAtSea);

        if (party.Army == null)
            return null;

        List<MobileParty> attachedParties = null;
        foreach (var attachedParty in party.Army.LeaderParty.AttachedParties)
        {
            if (attachedParty == party)
                continue;

            attachedParty.Position = position;
            attachedParties ??= new List<MobileParty>();
            attachedParties.Add(attachedParty);
        }

        return attachedParties;
    }

    private void PublishForcedPosition(MobileParty party)
    {
        if (!mobilePartyBehaviorSnapshot.TryCreate(
                party,
                out PartyBehaviorUpdateData data))
            return;

        data.ForcePosition = true;
        messageBroker.Publish(this, new PartyBehaviorUpdated(ref data));
    }

    private void PublishAuthoritativeBehavior(MobileParty party, PartyBehaviorUpdateData request)
    {
        if (!mobilePartyBehaviorSnapshot.TryCreate(
                party,
                out PartyBehaviorUpdateData authoritativeData))
            return;

        authoritativeData.OriginControllerId = request.OriginControllerId;
        authoritativeData.ForcePosition = request.ForcePosition;
        authoritativeData.ResetMovementToHold = request.ResetMovementToHold;
        messageBroker.Publish(this, new PartyBehaviorUpdated(ref authoritativeData));
    }

    internal static bool TrySelectBehaviorUpdate(
        bool isSelfEcho,
        IReadOnlyDictionary<string, PartyBehaviorUpdateData> latestPredictions,
        ref PartyBehaviorUpdateData data)
    {
        if (!isSelfEcho)
            return true;

        var partyId = Compact(data.MobilePartyId, typeof(MobileParty));
        return latestPredictions.TryGetValue(partyId, out data);
    }

    /// <summary>
    /// Whether the client should take the server's word for where a party is.
    /// </summary>
    /// <remarks>
    /// A moving party is normally left to the client's own simulation - it is already walking the same
    /// replicated target, so an in-flight snapshot is a frame or two stale and adopting it only jitters.
    ///
    /// What that reasoning lacked was a CEILING. Nothing bounded how far the two simulations could part
    /// company, and since a position only travels inside a behaviour update - which
    /// <see cref="MobilePartyAIs.Patches.PartyBehaviorPatch"/> publishes only when the behaviour CHANGES - a
    /// party marching under one unchanged order was corrected by nothing at all for the whole journey. The
    /// debt was then settled in a single assignment the moment it held or was forced, which is what
    /// "lords teleport across the map to join an army" actually was.
    ///
    /// So a distance term, at the same threshold the server re-states positions on
    /// (<see cref="PartyPositionCorrection.CorrectionDistance"/>): below it the snapshot is merely stale and is
    /// still ignored, above it the client is not slightly behind, it is somewhere else.
    /// </remarks>
    internal static bool ShouldApplyAuthoritativePosition(
        bool isSelfEcho,
        bool forcePosition,
        bool isHolding,
        CampaignVec2 currentPosition,
        CampaignVec2 authoritativePosition)
    {
        return !isSelfEcho &&
            (forcePosition
             || isHolding
             || currentPosition.IsOnLand != authoritativePosition.IsOnLand
             || currentPosition.DistanceSquared(authoritativePosition) >= PartyPositionCorrection.CorrectionDistanceSquared);
    }
}
