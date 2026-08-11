using Common.Logging;
using HarmonyLib;
using Helpers;
using Serilog;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;

namespace GameInterface.Services.MapEvents.Patches.Disable;

/// <summary>
/// Runs vanilla's "Capture the enemy" when it can survive, and closes the encounter cleanly when it cannot.
/// </summary>
/// <remarks>
/// This used to be an unconditional <c>=> false</c>. That was not a decision about prisoners - it was hiding a
/// crash. With it removed, clicking the option throws every time:
///
///     System.NullReferenceException
///        at Helpers.MenuHelper.EncounterCaptureTheEnemyOnConsequence(MenuCallbackArgs args)
///        at GameMenuOption.RunConsequence  ... at ButtonWidget.HandleClick
///
/// swallowed by ScreenManagerRobustnessPatches, so the click does nothing and the menu cannot be dismissed.
/// Because this consequence is also what walks the encounter to PlayerEncounterState.LootInventory, blocking
/// it is why the loot screen never appeared - which is in turn why battle loot had to be auto-credited.
/// Three symptoms, one cause.
///
/// The cause is ordering: vanilla needs a live PlayerMapEvent to capture from, and in co-op the SERVER
/// finalizes and tears the map event down as soon as the battle resolves - before the player has walked their
/// post-battle screens. Single-player keeps the event alive until the loot flow ends.
///
/// Fixing that ordering is the real work and is not attempted here. What this does is stop the option being a
/// trap: when the prerequisites vanilla dereferences are present, let vanilla run and the player gets the
/// proper capture and loot screens; when they are gone, close the encounter so the player returns to the map
/// instead of being stranded on a menu whose only option throws.
/// </remarks>
[HarmonyPatch(typeof(MenuHelper))]
internal class DisableEncounterCaptureTheEnemyOnConsequence
{
    private static readonly ILogger Logger = LogManager.GetLogger<DisableEncounterCaptureTheEnemyOnConsequence>();

    [HarmonyPatch(nameof(MenuHelper.EncounterCaptureTheEnemyOnConsequence))]
    [HarmonyPrefix]
    private static bool PrefixEncounterCaptureTheEnemyOnConsequence()
    {
        // The two references vanilla walks. Either being null is the NRE above.
        if (PlayerEncounter.Current != null && MapEvent.PlayerMapEvent != null)
        {
            Logger.Information("[Encounter] Capture the enemy: the map event is still live, running vanilla");
            return true;
        }

        Logger.Warning(
            "[Encounter] Capture the enemy taken with no live map event (encounter={HasEncounter}, " +
            "playerMapEvent={HasEvent}); closing the encounter instead of throwing",
            PlayerEncounter.Current != null,
            MapEvent.PlayerMapEvent != null);

        if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true);

        return false;
    }
}
