using Common;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.GameDebug.Commands;

/// <summary>
/// C12 - look a menu option up and call its consequence.
/// </summary>
/// <remarks>
/// THIS WAS RECORDED AS BLOCKED, AND THE PREMISE WAS WRONG
/// The earlier finding was that a headless client shows MenuContext with no current state and GameMenu with no
/// items, and the conclusion drawn was that activating menus headlessly would require changing how menus
/// initialise for EVERY client, rendered ones included. That conclusion does not survive looking at where the
/// types actually live.
///
/// MenuContext and GameMenu are both in TaleWorlds.CampaignSystem, not in a UI assembly.
/// <c>GameMenu.ActivateGameMenu(string)</c> is public and static, <c>Campaign.Current.CurrentMenuContext</c> is
/// a public property, and <c>MenuContext.InvokeConsequence(int)</c> is public. The renderer attaches through
/// <c>IMenuContextHandler</c>, whose entire surface is notification - OnMenuCreate, OnMenuActivate, sounds,
/// background mesh names. Menu LOGIC does not depend on it.
///
/// So the menu was empty because nothing ever called activation on a headless client, not because it could not
/// be called. Nothing here changes shared initialisation; it calls the same public entry point the game does.
///
/// CONDITIONS ARE CHECKED BEFORE INVOKING
/// A consequence whose condition does not hold is an option the player could not have picked. Running it anyway
/// would let a scenario reach states no player can reach and then report the resulting mess as a coop fault -
/// the rig manufacturing its own findings, which is the failure mode this whole plan keeps guarding against.
/// </remarks>
public class MenuOptionDebugCommand
{
    [CommandLineArgumentFunction("state", "coop.debug.menu")]
    public static string State(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.menu.state";
        if (Campaign.Current == null) return "MENU_STATE campaign=false";

        var context = Campaign.Current.CurrentMenuContext;
        var mapState = GameStateManager.Current?.ActiveState as MapState;
        if (context?.GameMenu == null)
            return $"MENU_STATE active=false atMenu={Lower(mapState?.AtMenu ?? false)} " +
                   $"mapState={(mapState != null).ToString().ToLowerInvariant()}";

        var menu = context.GameMenu;
        var result = new StringBuilder();
        result.AppendLine(
            $"MENU_STATE active=true menu={context.StringId ?? "none"} " +
            $"atMenu={Lower(mapState?.AtMenu ?? false)} options={menu.MenuItemAmount}");

        for (var index = 0; index < menu.MenuItemAmount; index++)
        {
            // ConditionsHold is what separates an option a player could pick from one merely present in the
            // list, and it is the difference between a meaningful selection and a silent no-op.
            var holds = menu.GetMenuOptionConditionsHold(Game.Current, context, index);
            result.AppendLine(
                $"index={index}|id={menu.GetMenuOptionIdString(index)}|" +
                $"conditionsHold={Lower(holds)}|isLeave={Lower(menu.GetMenuOptionIsLeave(index))}|" +
                $"text={menu.GetMenuOptionText(index)}");
        }

        return result.ToString();
    }

    [CommandLineArgumentFunction("activate", "coop.debug.menu")]
    public static string Activate(List<string> args)
    {
        if (args.Count != 1) return "Usage: coop.debug.menu.activate <menuId>";
        if (Campaign.Current == null) return "No campaign is loaded.";

        GameMenu.ActivateGameMenu(args[0]);

        var context = Campaign.Current.CurrentMenuContext;
        return $"MENU_ACTIVATE requested={args[0]} now={context?.StringId ?? "none"} " +
               $"options={context?.GameMenu?.MenuItemAmount ?? 0}";
    }

    [CommandLineArgumentFunction("invoke_index", "coop.debug.menu")]
    public static string InvokeIndex(List<string> args)
    {
        if (args.Count != 1 ||
            !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            return "Usage: coop.debug.menu.invoke_index <0-based index>";
        if (!TryGetMenu(out var context, out var menu, out var error)) return error;
        if (index >= menu.MenuItemAmount)
            return $"Index {index} is out of range - the menu has {menu.MenuItemAmount} option(s).";

        return Invoke(context, menu, index, $"index={index}");
    }

    [CommandLineArgumentFunction("invoke_id", "coop.debug.menu")]
    public static string InvokeId(List<string> args)
    {
        if (args.Count != 1) return "Usage: coop.debug.menu.invoke_id <optionId>";
        if (!TryGetMenu(out var context, out var menu, out var error)) return error;

        var available = new List<string>();
        for (var index = 0; index < menu.MenuItemAmount; index++)
        {
            var id = menu.GetMenuOptionIdString(index);
            available.Add(id);
            if (id == args[0]) return Invoke(context, menu, index, $"id={args[0]}");
        }

        return $"Option '{args[0]}' is not in this menu. Available: {string.Join(",", available)}";
    }

    private static string Invoke(MenuContext context, GameMenu menu, int index, string how)
    {
        var id = menu.GetMenuOptionIdString(index);
        if (!menu.GetMenuOptionConditionsHold(Game.Current, context, index))
            return $"Option '{id}' does not meet its conditions, so a player could not pick it either.";

        var before = context.StringId;
        context.InvokeConsequence(index);
        var after = Campaign.Current.CurrentMenuContext;

        return $"MENU_INVOKED {how} id={id} menuBefore={before} " +
               $"menuAfter={after?.StringId ?? "none"} " +
               $"options={after?.GameMenu?.MenuItemAmount ?? 0}";
    }

    private static bool TryGetMenu(out MenuContext context, out GameMenu menu, out string error)
    {
        error = null;
        menu = null;
        context = Campaign.Current?.CurrentMenuContext;

        if (context?.GameMenu == null)
        {
            error = "No menu is active. Open one with coop.debug.menu.activate <menuId> first.";
            return false;
        }

        menu = context.GameMenu;
        return true;
    }

    private static string Lower(bool value) => value.ToString().ToLowerInvariant();
}
