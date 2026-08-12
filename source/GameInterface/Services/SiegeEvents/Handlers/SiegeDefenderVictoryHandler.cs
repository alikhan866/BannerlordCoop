using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.MapEvents.Loot;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Patches;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PlayerCaptivityService.Messages;
using GameInterface.Services.SiegeEvents.Interfaces;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.SiegeEvents.Handlers;

/// <summary>
/// Client-side deferred siege-defeated transition for a winning inside defender. On a defender assault victory
/// the server replicates the SiegeEvent/MapEvent teardown, which bypasses vanilla's local siege-end routing, so
/// the winner would otherwise fall through to the settlement arrival menu. The server sends
/// <see cref="NetworkPromptSiegeDefenderVictory"/> after the finalize; this parks the transition and runs it on
/// the next CampaignTick once the mission has fully popped, mirroring <see cref="SiegeCaptureTransitionRetryHandler"/>.
/// </summary>
internal class SiegeDefenderVictoryHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<SiegeDefenderVictoryHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly ISiegeEventInterface siegeEventInterface;

    /// <summary>
    /// How long to wait for the post-battle walk to put its first screen up before giving up on it.
    /// </summary>
    /// <remarks>
    /// Only covers the walk STARTING. Once it has shown the player something the wait becomes open-ended,
    /// because from then on the delay is the player reading a loot screen, and hurrying that along would take
    /// the screens away mid-decision - the very thing this exists to stop.
    /// </remarks>
    private const int TicksToWaitForSpoilsToOpen = 600;

    /// <summary>
    /// The outer limit on the whole thing, however well it is going.
    /// </summary>
    /// <remarks>
    /// Generous, because the player may genuinely be reading a loot screen - but not unbounded. A walk that
    /// opens one screen and then stalls would otherwise hold the victory menu back forever, leaving the player
    /// on a map with nothing to click, which is a worse failure than a menu arriving late.
    /// </remarks>
    private const int TicksToWaitForThePlayer = 36000;

    // Game-thread only (armed inside a GameThread closure, drained on CampaignTick).
    private Settlement pendingSettlement;
    private bool walkStarted;
    private int ticksWaitedForSpoils;

    public SiegeDefenderVictoryHandler(IMessageBroker messageBroker, IObjectManager objectManager, ISiegeEventInterface siegeEventInterface)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.siegeEventInterface = siegeEventInterface;

        messageBroker.Subscribe<NetworkPromptSiegeDefenderVictory>(Handle_NetworkPromptSiegeDefenderVictory);
        messageBroker.Subscribe<CampaignTick>(Handle_CampaignTick);
    }

    private void Handle_NetworkPromptSiegeDefenderVictory(MessagePayload<NetworkPromptSiegeDefenderVictory> payload)
    {
        if (ModInformation.IsServer) return;
        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            // Only the winning defender whose party the server named transitions; the attacker ignores it.
            if (!objectManager.TryGetId(MobileParty.MainParty?.Party, out var localPartyId)) return;
            if (Array.IndexOf(obj.DefenderPartyIds ?? Array.Empty<string>(), localPartyId) < 0) return;
            if (!objectManager.TryGetObjectWithLogging<Settlement>(obj.SettlementId, out var settlement)) return;

            pendingSettlement = settlement;
            walkStarted = false;
            ticksWaitedForSpoils = 0;
        });
    }

    private void Handle_CampaignTick(MessagePayload<CampaignTick> payload)
    {
        if (ModInformation.IsServer || pendingSettlement == null) return;
        // The mission screen is still up (or popping): re-establishing PlayerEncounter now is unsafe. Wait.
        if (MissionState.Current != null) return;

        if (SpoilsStillOwedToThePlayer()) return;

        var settlement = pendingSettlement;
        pendingSettlement = null;
        walkStarted = false;
        ticksWaitedForSpoils = 0;
        siegeEventInterface.PromptSiegeDefenderVictory(settlement);
    }

    /// <summary>
    /// Holds the victory menu back until the player has been through their spoils.
    /// </summary>
    /// <remarks>
    /// <see cref="ISiegeEventInterface.PromptSiegeDefenderVictory"/> finishes the PlayerEncounter in order to
    /// put its own menu up, and finishing the encounter is what walks the player through captured lords, the
    /// troop and prisoner screen and the loot. Doing it here, four seconds after the results land, is why a won
    /// siege defence produced no screens at all: measured live, 186 item stacks, 50 members and 54 prisoners
    /// staged and then closed out from under the player.
    ///
    /// So the encounter is left alone while spoils are owed, and the walk is started once - the same walk that
    /// runs after any other battle, not a second one written for sieges. When it reaches its end it finishes
    /// the encounter itself, the offer is answered, and this proceeds on the following tick.
    ///
    /// Bounded on purpose. If the walk never opens anything the menu still arrives, a little late, rather than
    /// the player being stranded on a map with no way forward - and the loot is not lost either way, because an
    /// offer nobody was shown is paid out rather than forfeited.
    /// </remarks>
    private bool SpoilsStillOwedToThePlayer()
    {
        if (!ClientBattleLootOffer.HasPending) return false;

        // No encounter left to walk them through - nothing to wait for.
        if (PlayerEncounter.Current == null) return false;

        if (!walkStarted)
        {
            Logger.Information(
                "[Loot] Holding the siege victory menu back: the player still has spoils to go through");

            PostBattleWalkAutoAdvancePatch.StartWalk();
            walkStarted = true;
        }

        ticksWaitedForSpoils++;

        // Something is in front of the player now, so the only thing left is how long they take over it -
        // bounded only so a stalled walk cannot strand them.
        if (ClientBattleLootOffer.WasShown)
        {
            if (ticksWaitedForSpoils <= TicksToWaitForThePlayer) return true;

            Logger.Warning(
                "[Loot] The post-battle walk at {Settlement} opened something and then stopped making progress; " +
                "showing the siege victory menu rather than leaving the player with nothing to click",
                pendingSettlement?.StringId);

            return false;
        }

        if (ticksWaitedForSpoils <= TicksToWaitForSpoilsToOpen) return true;

        Logger.Warning(
            "[Loot] The post-battle walk never opened anything for the siege victory at {Settlement}; " +
            "showing the victory menu anyway. The offer is settled by the unshown-offer path, so the spoils " +
            "are paid rather than lost - but the screens did not run and that is still a bug",
            pendingSettlement?.StringId);

        return false;
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkPromptSiegeDefenderVictory>(Handle_NetworkPromptSiegeDefenderVictory);
        messageBroker.Unsubscribe<CampaignTick>(Handle_CampaignTick);
        pendingSettlement = null;
    }
}
