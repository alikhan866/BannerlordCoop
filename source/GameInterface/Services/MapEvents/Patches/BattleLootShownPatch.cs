using Common;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem.Encounters;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Records that the post-battle walk actually put something in front of the player.
/// </summary>
/// <remarks>
/// The answer to a loot offer is derived from what is LEFT on the encounter's staged rosters. That reading is
/// correct only if the player was given the chance to empty them: "I declined everything" and "I was never
/// asked" leave identical evidence, and the second was being reported to the server as the first. A won siege
/// cost 191 item stacks, 47 members and 52 prisoners that way, with a single log line saying 0 claims.
///
/// These are the steps of vanilla's own walk, so this is true wherever the walk is driven from - the encounter
/// menu, a settlement menu, or our tick - rather than only where we happen to have put a driver.
///
/// <c>_stateHandled</c> is the test rather than "the step ran", because these steps also run to discover there
/// is nothing to do: DoCaptureHeroes with no captured lords simply advances the state. The flag is vanilla's
/// own record of "this step has opened something and is waiting for the player", which is exactly the question
/// being asked here.
/// </remarks>
[HarmonyPatch]
internal class BattleLootShownPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[]
        {
            "DoCaptureHeroes",
            "DoFreeOrCapturePrisonerHeroes",
            "DoLootMembersAndPrisonersOfParty",
            "DoLootInventory",
            "DoLootShips",
        })
        {
            var method = AccessTools.Method(typeof(PlayerEncounter), name);
            if (method != null) yield return method;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(PlayerEncounter __instance)
    {
        if (ModInformation.IsServer) return;
        if (!Loot.ClientBattleLootOffer.HasPending) return;

        if (__instance != null && __instance._stateHandled) Loot.ClientBattleLootOffer.MarkShown();
    }
}
