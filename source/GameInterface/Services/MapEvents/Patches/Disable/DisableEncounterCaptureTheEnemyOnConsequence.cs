using Common.Logging;
using HarmonyLib;
using Helpers;
using Serilog;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;

namespace GameInterface.Services.MapEvents.Patches.Disable;

/// <summary>
/// Runs "Capture the enemy" without the one line of it that needs a live map event.
/// </summary>
/// <remarks>
/// Vanilla's consequence is two statements:
///
///     MapEvent.PlayerMapEvent.SetOverrideWinner(MapEvent.PlayerMapEvent.PlayerSide);
///     PlayerEncounter.Update();
///
/// Only the first needs the map event, and in co-op the SERVER decides the winner and tears the event down
/// as soon as the battle resolves - so that line throws:
///
///     System.NullReferenceException
///        at Helpers.MenuHelper.EncounterCaptureTheEnemyOnConsequence(MenuCallbackArgs args)
///        at GameMenuOption.RunConsequence  ... at ButtonWidget.HandleClick
///
/// swallowed by ScreenManagerRobustnessPatches, so the click did nothing and the menu could not be dismissed.
/// Because the second statement is what walks the encounter through its post-battle screens, losing the first
/// one to an exception is also why the capture and loot screens never appeared - which is in turn why battle
/// loot had to be auto-credited. Three symptoms, one line.
///
/// So: skip the winner, keep the walk. The winner is not in question - the server already decided it, and
/// what it decided is what put results on the staged rosters in the first place.
///
/// <see cref="PlayerEncounter.Update"/> is itself patched (PlayerEncounterPatches) to run our copy of
/// vanilla's post-battle loop when no map event is left: CaptureHeroes, FreeHeroes, LootParty, LootInventory,
/// End - repeating until a step opens something the player has to answer.
///
/// An earlier revision of this file walked those steps by hand instead, and got it wrong in a way a player
/// sees immediately: defeated LORDS turned up in the loot list to be picked over like recruits. The reason is
/// that <c>DoCaptureHeroes</c> is not merely the step that talks to them - it is the step that REMOVES them
/// from <c>RosterToReceiveLootPrisoners</c> (it fills <c>_capturedHeroes</c> from that roster on first call).
/// Skipping it leaves every captured lord sitting in the loot. Same for <c>DoFreeOrCapturePrisonerHeroes</c>
/// and <c>RosterToReceiveLootMembers</c>. Hence: call the loop, never its steps.
/// </remarks>
[HarmonyPatch(typeof(MenuHelper))]
internal class DisableEncounterCaptureTheEnemyOnConsequence
{
    private static readonly ILogger Logger = LogManager.GetLogger<DisableEncounterCaptureTheEnemyOnConsequence>();

    [HarmonyPatch(nameof(MenuHelper.EncounterCaptureTheEnemyOnConsequence))]
    [HarmonyPrefix]
    private static bool PrefixEncounterCaptureTheEnemyOnConsequence()
    {
        if (MapEvent.PlayerMapEvent != null)
        {
            Logger.Information("[Encounter] Capture the enemy: the map event is still live, running vanilla");
            return true;
        }

        if (PlayerEncounter.Current == null)
        {
            Logger.Warning("[Encounter] Capture the enemy taken with no encounter at all; nothing to advance");
            return false;
        }

        Logger.Information("[Encounter] Capture the enemy: no live map event, running the post-battle walk");

        // Runs the first step and carries the rest, so this is the only click the player makes. It does the
        // PlayerEncounter.Update() itself - the walk has to record where each step drove from, and only the
        // thing doing the driving knows that.
        PostBattleWalkAutoAdvancePatch.StartWalk();

        return false;
    }
}
