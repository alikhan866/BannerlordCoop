using Common.Logging;
using GameInterface.Services.MapEvents.TroopSupply;
using HarmonyLib;
using SandBox.View.Map;
using Serilog;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Drops every trace of a previous battle when a campaign is loaded.
/// </summary>
/// <remarks>
/// The coop battle stack keeps three pieces of PROCESS-GLOBAL state, because the Harmony patches that read them
/// are static and cannot resolve DI services: <see cref="BattleSpawnGate"/> (is a coop battle active, and which),
/// <see cref="CoopTroopSupplierRegistry"/> (each side's troop reserve) and <see cref="PendingBattleLoot"/> (loot
/// staged but not yet handed over). All three are set up when a battle STARTS and torn down when it ENDS - and
/// ending is the half that is not guaranteed. Abandon a battle to the main menu and the mission behaviour that
/// releases them never runs, so they outlive the campaign that created them.
///
/// What survives is not inert. The spawn gate left engaged makes <c>MapEventPartyPatches</c> keep suppressing
/// vanilla troop supply, while <c>BattleTroopSupplierInjectionPatch</c> keys the coop supplier to a battle that
/// no longer exists - so the next battle is fed by neither path and starts with no troops. The deployment
/// patches gate on that same flag, which is why the Ready button goes with it. The reserve registry is worse
/// than stale: its keys are map event ids, which are per-campaign counters that restart on load, so a new
/// battle can be handed a dead battle's reserve purely because the counter reached the same number again.
///
/// <c>MapScreen.OnInitialize</c> is the campaign coming up, which no battle can legitimately outlive - so this
/// resets rather than repairs. It is deliberately separate from the state a live battle owns: nothing here
/// interrupts a battle in progress, because a map screen is not initialising while one is running.
/// </remarks>
[HarmonyPatch(typeof(MapScreen), nameof(MapScreen.OnInitialize))]
internal class BattleStateResetOnCampaignLoadPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleStateResetOnCampaignLoadPatch>();

    [HarmonyPostfix]
    private static void Postfix() => ResetBattleState();

    /// <summary>
    /// Returns the process-global battle state to "no battle". Split out so it can be asserted without a
    /// MapScreen, and because all three pieces have to go together - releasing the gate while a dead reserve
    /// stayed buffered would just move the failure.
    /// </summary>
    internal static void ResetBattleState()
    {
        // Only worth a line when something actually survived; a clean load is the normal case and says nothing.
        if (BattleSpawnGate.IsCoopBattleActive)
        {
            Logger.Warning(
                "[BattleSync] A campaign loaded while the spawn gate still held {MapEventId} - a battle was left without ending. Releasing it, otherwise the next battle spawns no troops and offers no Ready button",
                BattleSpawnGate.ActiveMapEventId);
        }

        BattleSpawnGate.EndBattle();
        CoopTroopSupplierRegistry.ClearAll();

        // An offer belongs to a battle in the world that was just replaced. Answering it after a load would
        // aim line indices at spoils from a campaign that no longer exists - and on the server side the
        // offer is gone with the reload anyway, so the answer could only ever be refused.
        Loot.ClientBattleLootOffer.Clear();
        Loot.BattleLootOfferRegistry.Shared.Clear();
    }
}
