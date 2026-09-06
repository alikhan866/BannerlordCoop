#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Common.Commands;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Inventory;
using TaleWorlds.Core;

namespace GameInterface.Services.GameDebug.Commands;

/// <summary>
/// [Debug] Drives the post-battle walk without a mouse, for the live rig: pick a game-menu option, finish the
/// members / prisoners screen, donate items on the loot screen for troop XP, and press Done. Written for M12
/// ("discarding items after a battle loses the troops' XP"), which needs the real screens to run unattended.
/// </summary>
internal static class InventoryWalkDebugCommands
{
    private static CoopCommandResult Succeeded(string output) => new CoopCommandResult(true, output);

    private static CoopCommandResult Failed(string output) => new CoopCommandResult(false, output, "command_failed");

    private static string F(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    // coop.debug.ui.menu_select
    /// <summary>Runs one option of the current game menu by index or id, as clicking it would.</summary>
    public sealed class MenuSelectCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "menu_select";

        public string Description => "Runs the current game menu's option by index or id string (see menu_options).";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("option", "Index into the menu's options, or the option id string.", isRequired: true),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            var menuContext = Campaign.Current?.CurrentMenuContext;
            var menu = menuContext?.GameMenu;
            if (menu == null) return Failed("No game menu is open.");
            int index = -1;
            var options = new List<GameMenuOption>(menu.MenuOptions);
            if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
            {
                index = options.FindIndex(o => string.Equals(o.IdString, args[0], StringComparison.OrdinalIgnoreCase));
                if (index < 0) return Failed("No option '" + args[0] + "' in menu " + menu.StringId + ".");
            }
            if (index < 0 || index >= options.Count) return Failed("Option index out of range (" + options.Count + " options).");
            var option = options[index];
            bool holds = menu.GetMenuOptionConditionsHold(Game.Current, menuContext, index);
            if (!holds) return Failed("Option [" + index + "] " + option.IdString + " is not available right now.");
            menu.RunMenuOptionConsequence(menuContext, index);
            return Succeeded("Ran option [" + index + "] " + option.IdString + " of menu " + menu.StringId);
        }
    }

    // coop.debug.ui.party_screen_done
    /// <summary>Presses Done on the open party screen (loot members / prisoners), or reports that none is open.</summary>
    public sealed class PartyScreenDoneCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "party_screen_done";

        public string Description => "Closes the open party screen with Done (not cancel).";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (PartyScreenHelper.GetActivePartyState() == null) return Failed("No party screen is open.");
            PartyScreenHelper.CloseScreen(false, false);
            return Succeeded("PARTY_SCREEN_DONE");
        }
    }

    // coop.debug.inventory.state
    /// <summary>What the open inventory screen holds: mode, both rosters' sizes, and the donation XP so far.</summary>
    public sealed class InventoryStateCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.inventory";

        public string Name => "state";

        public string Description => "Reports the open inventory screen: mode, roster sizes, donation XP accumulated, party screen open.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            var state = InventoryScreenHelper.GetActiveInventoryState();
            var logic = state?.InventoryLogic;
            bool partyScreen = PartyScreenHelper.GetActivePartyState() != null;
            if (logic == null)
                return Succeeded("INVENTORY open=false partyScreen=" + (partyScreen ? "true" : "false") +
                                 " menu=" + (Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId ?? "-"));
            var left = logic._rosters != null && logic._rosters.Length > 0 ? logic._rosters[0] : null;
            var right = logic._rosters != null && logic._rosters.Length > 1 ? logic._rosters[1] : null;
            int leftItems = 0;
            if (left != null) foreach (var element in left) leftItems += element.Amount;
            return Succeeded("INVENTORY open=true mode=" + state.InventoryMode +
                             " canGainXp=" + (logic.CanGainXpFromDiscarding ? "true" : "false") +
                             " donationXp=" + F(logic.XpGainFromDonations) +
                             " leftLines=" + (left?.Count ?? 0).ToString(CultureInfo.InvariantCulture) +
                             " leftItems=" + leftItems.ToString(CultureInfo.InvariantCulture) +
                             " rightLines=" + (right?.Count ?? 0).ToString(CultureInfo.InvariantCulture) +
                             " partyScreen=" + (partyScreen ? "true" : "false"));
        }
    }

    // coop.debug.inventory.donate_all
    /// <summary>
    /// Donates every donatable item on the loot (left) side for troop XP, the way clicking the donate action on
    /// each row would, and reports the XP the screen now shows. Optionally the player's own side too.
    /// </summary>
    public sealed class InventoryDonateAllCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.inventory";

        public string Name => "donate_all";

        public string Description => "Donates every donatable item on the loot side (add 'both' for the player's side too); reports the XP gained.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("sides", "loot (default) or both", isRequired: false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            var logic = InventoryScreenHelper.GetActiveInventoryState()?.InventoryLogic;
            if (logic == null) return Failed("No inventory screen is open.");
            bool both = args.Count > 0 && string.Equals(args[0], "both", StringComparison.OrdinalIgnoreCase);
            float before = logic.XpGainFromDonations;
            int donated = 0, skipped = 0;
            var sides = both
                ? new[] { InventoryLogic.InventorySide.OtherInventory, InventoryLogic.InventorySide.PlayerInventory }
                : new[] { InventoryLogic.InventorySide.OtherInventory };
            foreach (var side in sides)
            {
                int rosterIndex = side == InventoryLogic.InventorySide.OtherInventory ? 0 : 1;
                var roster = logic._rosters != null && logic._rosters.Length > rosterIndex ? logic._rosters[rosterIndex] : null;
                if (roster == null) continue;
                // Snapshot first: donating removes the element from the roster being walked.
                var elements = new List<ItemRosterElement>();
                foreach (var element in roster) elements.Add(element);
                foreach (var element in elements)
                {
                    if (element.EquipmentElement.Item == null || element.Amount <= 0) { skipped++; continue; }
                    if (!logic.CanDonateItem(element, side)) { skipped++; continue; }
                    logic.DonateItem(element);
                    donated++;
                }
            }
            return Succeeded("DONATED lines=" + donated.ToString(CultureInfo.InvariantCulture) +
                             " skipped=" + skipped.ToString(CultureInfo.InvariantCulture) +
                             " xpBefore=" + F(before) + " xpAfter=" + F(logic.XpGainFromDonations) +
                             " canGainXp=" + (logic.CanGainXpFromDiscarding ? "true" : "false"));
        }
    }

    // coop.debug.inventory.close
    /// <summary>Presses Done (or Cancel) on the open inventory screen.</summary>
    public sealed class InventoryCloseCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.inventory";

        public string Name => "close";

        public string Description => "Closes the open inventory screen with Done; 'cancel' closes without applying.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("mode", "done (default) or cancel", isRequired: false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            var logic = InventoryScreenHelper.GetActiveInventoryState()?.InventoryLogic;
            if (logic == null) return Failed("No inventory screen is open.");
            bool cancel = args.Count > 0 && string.Equals(args[0], "cancel", StringComparison.OrdinalIgnoreCase);
            float donationXp = logic.XpGainFromDonations;
            InventoryScreenHelper.CloseScreen(cancel);
            return Succeeded("INVENTORY_CLOSED cancel=" + (cancel ? "true" : "false") + " donationXp=" + F(donationXp));
        }
    }
}
#endif
