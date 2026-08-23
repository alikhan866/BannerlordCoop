using Common.Logging;
using Common.Network;
using Coop.Core.Server.Services.MobileParties.Messages;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.Players;
using GameInterface.Services.Time.Interfaces;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace Coop.Core.Server.Services.MobileParties;

public interface IJoinCampaignBaselineSender
{
    void Send(NetPeer peer);
}

internal sealed class JoinCampaignBaselineSender : IJoinCampaignBaselineSender
{
    private static readonly ILogger Logger = LogManager.GetLogger<JoinCampaignBaselineSender>();

    private readonly INetwork network;
    private readonly IMapTimeTrackerInterface mapTimeTrackerInterface;
    private readonly IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot;
    private readonly ITimeControlInterface timeControlInterface;
    private readonly IPlayerPartyTroopXpBaselineProvider troopXpBaselineProvider;

    public JoinCampaignBaselineSender(
        INetwork network,
        IMapTimeTrackerInterface mapTimeTrackerInterface,
        IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot,
        ITimeControlInterface timeControlInterface,
        IPlayerPartyTroopXpBaselineProvider troopXpBaselineProvider)
    {
        this.network = network;
        this.mapTimeTrackerInterface = mapTimeTrackerInterface;
        this.mobilePartyBehaviorSnapshot = mobilePartyBehaviorSnapshot;
        this.timeControlInterface = timeControlInterface;
        this.troopXpBaselineProvider = troopXpBaselineProvider;
    }

    public void Send(NetPeer peer)
    {
        var campaignObjectManager = Campaign.Current?.CampaignObjectManager;
        var parties = campaignObjectManager?.MobileParties;
        var settlements = campaignObjectManager?.Settlements;
        if (parties == null ||
            settlements == null ||
            !mapTimeTrackerInterface.TryGetCurrentTicks(out long serverTicks))
        {
            throw new InvalidOperationException("Cannot capture a join baseline without a loaded campaign");
        }

        var activeParties = new List<MobileParty>(parties.Count);
        for (int i = 0; i < parties.Count; i++)
        {
            MobileParty party = parties[i];
            if (party?.IsActive == true) activeParties.Add(party);
        }

        var liveParties = new HashSet<MobileParty>(activeParties);
        var liveSettlements = new HashSet<Settlement>(settlements);
        var partyStates = new MobilePartyJoinState[activeParties.Count];
        TroopRosterXpBaseline[] troopXpBaselines = Array.Empty<TroopRosterXpBaseline>();
        bool isComplete = true;
        for (int i = 0; i < activeParties.Count; i++)
        {
            MobileParty party = activeParties[i];
            if (!mobilePartyBehaviorSnapshot.TryCreateJoinState(
                party,
                liveParties,
                liveSettlements,
                out MobilePartyJoinState state,
                out string failure))
            {
                Logger.Warning(
                    "Could not capture a complete join baseline for party {Party}: {Failure}",
                    party?.StringId,
                    failure);
                isComplete = false;
                break;
            }

            PartyBehaviorUpdateData behavior = state.Behavior;
            behavior.ForcePosition = true;

            // A party a PLAYER controls is driven by that player, never by an AI behaviour. Sending one
            // makes the joiner's own party re-path to wherever the server last had it heading and quietly
            // undo every order given. Seen live: a player who rejoined while parked inside a castle came
            // back with GoToSettlement aimed at that castle and could not leave until a later routine sync
            // happened to deliver Hold - which is why the party became movable only after a wait.
            //
            // Decided HERE rather than on the receiving client: the client's own answer to "do I control
            // this party" depends on player registration, which during a join is not populated yet, so the
            // guard silently evaluates false exactly when it is needed. The server always knows.
            //
            // NOT fixed here yet. Two attempts failed and both are recorded so they are not retried:
            //
            //   1. ResetMovementToHold = true - that flag is serialized and asserted in three test
            //      assertions but NO production code reads it, so it changes nothing at all.
            //   2. Forcing PartyMoveMode = Hold and nulling the target ids - this REGRESSED the join.
            //      A party that is inside a settlement legitimately carries TargetSettlementId, and
            //      stripping it fails the joining client's reference validation, so the baseline is
            //      rejected, retried, and the peer is eventually dropped by the no-progress cap.
            //
            // The player party must stop arriving AI-driven, but not by mutilating the behaviour data the
            // client validates against. The next thing to look at is ApplyBehavior on the receiving side,
            // which is where a controlled party could be given the position without the AI target.

            state.Behavior = behavior;
            partyStates[i] = state;
        }

        if (isComplete && !troopXpBaselineProvider.TryCapture(peer, out troopXpBaselines))
        {
            Logger.Warning("Could not capture the joining player's troop XP baseline");
            isComplete = false;
        }

        if (isComplete == false)
        {
            partyStates = Array.Empty<MobilePartyJoinState>();
            troopXpBaselines = Array.Empty<TroopRosterXpBaseline>();
        }

        network.SendImmediate(
            peer,
            new NetworkJoinCampaignBaseline(
                serverTicks,
                timeControlInterface.GetTimeControl(),
                partyStates,
                isComplete,
                troopXpBaselines));
    }
}
