using Common;
using GameInterface.Services.SiegeEvents.Interfaces;
using HarmonyLib;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.SiegeEvents.Patches;

/// <summary>
/// Backstop for the siege-assault entry race. The vanilla siege tick switches a besieger's client to the
/// encounter menu the instant MainParty.MapEvent flips to the replicated assault, which can beat
/// NetworkPromptSiegeAssault by a frame or two. Without a PlayerEncounter, vanilla game_menu_encounter_on_init
/// runs Init on not-yet-synced sides and NREs; bounce back to the wait menu until the prompt establishes the
/// encounter, then let vanilla render it. Scoped to Current == null + IsSiegeAssault so it never touches the
/// field/defender/settlement flows, which reach this menu with a PlayerEncounter already set.
/// </summary>
[HarmonyPatch(typeof(EncounterGameMenuBehavior))]
internal class EncounterAssaultInitGuardPatch
{
    [HarmonyPatch("game_menu_encounter_on_init")]
    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (ModInformation.IsServer) return true;
        if (PlayerEncounter.Current != null) return true;

        if (MobileParty.MainParty?.MapEvent?.IsSiegeAssault == true)
        {
            // Bouncing only ever covered the frame or two before the assault prompt lands. That prompt is a
            // ONE-SHOT broadcast sent when the assault begins, so a party seated after it - a relief force
            // that arrives mid-assault, or any client whose server restarted while one was in progress -
            // never receives one and bounces here forever: seated in the battle, counted in its side's
            // strength, with "Leave..." as the only option on the menu. If the server has already seated us,
            // adopt the replicated event and let vanilla render the menu properly. With no seat this is
            // still the original race, and bouncing is still the right answer.
            if (ContainerProvider.TryResolve<ISiegeEventInterface>(out var siegeEventInterface) &&
                siegeEventInterface.TryAdoptReplicatedAssaultEncounter())
            {
                return true;
            }

            GameMenu.ExitToLast();
            return false;
        }

        return true;
    }
}
