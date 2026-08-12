using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;

namespace GameInterface.Services.UI.Patches;

[HarmonyPatch(typeof(GameMenu))]
internal class RemoveWaitOption
{
    private static readonly ILogger Logger = LogManager.GetLogger<RemoveWaitOption>();

    [HarmonyPatch(nameof(GameMenu.SwitchToMenu))]
    static bool Prefix(string menuId)
    {
        MenuContext currentMenuContext = Campaign.Current.CurrentMenuContext;
        if (currentMenuContext == null)
        {
            // This replaces vanilla wholesale, so with no menu context the switch is simply DROPPED - the
            // caller believes it moved the player and nothing happened. Vanilla at least asserts here. Said
            // out loud because a silently dropped menu switch is indistinguishable, from the player's side,
            // from a menu option that does nothing.
            Logger.Warning("Menu switch to '{MenuId}' was dropped: there is no current menu context", menuId);
            return false;
        }

        if (currentMenuContext != null)
        {
            try
            {
                currentMenuContext.SwitchToMenu(menuId);
                if (currentMenuContext.GameMenu.IsWaitMenu)
                {
                    currentMenuContext.GameMenu.StartWait();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to switch to menu {menuId}");
                GameMenu.ExitToLast();
            }
        }

        return false;
    }
}
