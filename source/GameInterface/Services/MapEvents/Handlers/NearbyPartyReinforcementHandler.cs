using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.PlayerCaptivityService.Messages;
using TaleWorlds.CampaignSystem.MapEvents;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>Scans for nearby AI when players join a battle and while its join window remains open.</summary>
internal sealed class NearbyPartyReinforcementHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly INearbyPartyReinforcer nearbyPartyReinforcer;

    public NearbyPartyReinforcementHandler(
        IMessageBroker messageBroker,
        INearbyPartyReinforcer nearbyPartyReinforcer)
    {
        this.messageBroker = messageBroker;
        this.nearbyPartyReinforcer = nearbyPartyReinforcer;

        messageBroker.Subscribe<PlayerJoinedBattle>(Handle_PlayerJoinedBattle);
        messageBroker.Subscribe<PartyRemovedFromMapEvent>(Handle_PartyRemovedFromMapEvent);
        messageBroker.Subscribe<CampaignTick>(Handle_CampaignTick);
        messageBroker.Subscribe<LiveBattleReinforcementTick>(Handle_LiveBattleReinforcementTick);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PlayerJoinedBattle>(Handle_PlayerJoinedBattle);
        messageBroker.Unsubscribe<PartyRemovedFromMapEvent>(Handle_PartyRemovedFromMapEvent);
        messageBroker.Unsubscribe<CampaignTick>(Handle_CampaignTick);
        messageBroker.Unsubscribe<LiveBattleReinforcementTick>(Handle_LiveBattleReinforcementTick);
    }

    private void Handle_PlayerJoinedBattle(MessagePayload<PlayerJoinedBattle> payload)
    {
        if (!ModInformation.IsServer)
            return;

        GameThread.RunSafe(() =>
        {
            if (payload.Who is not MapEvent mapEvent)
                return;

            using (AllowedThread.Suspend())
                nearbyPartyReinforcer.Reinforce(mapEvent);
        });
    }

    private void Handle_PartyRemovedFromMapEvent(MessagePayload<PartyRemovedFromMapEvent> payload)
    {
        if (!ModInformation.IsServer)
            return;

        GameThread.RunSafe(() =>
        {
            if (payload.Who is not MapEvent mapEvent)
                return;

            using (AllowedThread.Suspend())
                nearbyPartyReinforcer.RemoveReinforcementsIfNoPlayers(mapEvent, payload.What.RemovedParty);
        });
    }

    private void Handle_CampaignTick(MessagePayload<CampaignTick> payload)
    {
        if (!ModInformation.IsServer)
            return;

        GameThread.RunSafe(() =>
        {
            using (AllowedThread.Suspend())
                nearbyPartyReinforcer.ReinforceOpenPlayerBattles();
        });
    }

    /// <summary>
    /// The same sweep as <see cref="Handle_CampaignTick"/>, driven by real time instead of campaign time.
    /// </summary>
    /// <remarks>
    /// Campaign time stops for the duration of a co-op battle, so <see cref="CampaignTick"/> does not fire
    /// while one is being fought and the scheduled sweep can never come due. Without this the battle-start
    /// scan is the only one that ever runs, and a lord who was slightly too far away at the opening bell can
    /// never join - which is why nearby lords stood and watched.
    /// </remarks>
    private void Handle_LiveBattleReinforcementTick(MessagePayload<LiveBattleReinforcementTick> payload)
    {
        if (!ModInformation.IsServer)
            return;

        GameThread.RunSafe(() =>
        {
            using (AllowedThread.Suspend())
                nearbyPartyReinforcer.ReinforceOpenPlayerBattlesIgnoringCampaignClock();
        });
    }
}
