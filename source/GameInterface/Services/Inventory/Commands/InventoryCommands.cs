using Autofac;
using Common;
using Common.Logging;
using GameInterface.Services.ObjectManager;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Inventory.Commands;

internal class InventoryCommands
{
    private static readonly ILogger Logger = LogManager.GetLogger<InventoryCommands>();

    /// <summary>
    /// Attempts to get the ObjectManager
    /// </summary>
    private static bool TryGetObjectManager(out IObjectManager objectManager)
    {
        objectManager = null;
        if (ContainerProvider.TryGetContainer(out var container) == false) return false;

        return container.TryResolve(out objectManager);
    }

    /// <summary>
    /// View item ids in player inventories
    /// </summary>
    [CommandLineArgumentFunction("itemids", "coop.debug.inventory")]
    public static string ViewItemIdsCommand(List<string> strings)
    {
        if (strings.Count == 0)
        {
            return "Hero name argument required.";
        }

        StringBuilder stringBuilder = new StringBuilder();
        foreach (var hero in Hero.AllAliveHeroes)
        {
            if (hero.Name.ToString() == strings[0])
            {
                stringBuilder.AppendLine(hero.Name.ToString());
                foreach (var rosterElement in hero.PartyBelongedTo.ItemRoster)
                {
                    stringBuilder.AppendLine(rosterElement.EquipmentElement.Item.StringId + ": " + rosterElement._amount);
                }
            }
        }

        string result = stringBuilder.ToString();
        if (result.Length > 0)
        {
            return result;
        }
        return "Hero not found.";
    }

    /// <summary>
    /// View item values in player inventories
    /// </summary>
    [CommandLineArgumentFunction("itemvalues", "coop.debug.inventory")]
    public static string ViewItemValuesCommand(List<string> strings)
    {
        if (strings.Count == 0)
        {
            return "Hero name argument required.";
        }

        StringBuilder stringBuilder = new StringBuilder();
        foreach (var hero in Hero.AllAliveHeroes)
        {
            if (hero.Name.ToString() == strings[0])
            {
                stringBuilder.AppendLine(hero.Name.ToString());
                foreach (var rosterElement in hero.PartyBelongedTo.ItemRoster)
                {
                    stringBuilder.AppendLine(rosterElement.EquipmentElement.Item.StringId + ": " + rosterElement.EquipmentElement.Item.Value + "(" + rosterElement.EquipmentElement.ItemValue + ")");
                }
            }
        }

        string result = stringBuilder.ToString();
        if (result.Length > 0)
        {
            return result;
        }
        return "Hero not found.";
    }

    /// <summary>
    /// Output equipment ids of battle, civilian and stealth equipment for a specific hero
    /// </summary>
    [CommandLineArgumentFunction("heroequipment", "coop.debug.inventory")]
    public static string HeroEquipmentCommand(List<string> strings)
    {
        if (strings.Count == 0)
        {
            return "Hero name argument required.";
        }

        StringBuilder stringBuilder = new StringBuilder();
        foreach (var hero in Hero.AllAliveHeroes)
        {
            if (hero.Name.ToString() == strings[0])
            {
                stringBuilder.AppendLine(hero.Name.ToString());

                stringBuilder.AppendLine("BattleEquipment");
                foreach (var equipmentElement in hero.BattleEquipment._itemSlots) stringBuilder.AppendLine(equipmentElement.Item?.StringId ?? "[EMPTY]");

                stringBuilder.AppendLine("CivilianEquipment");
                foreach (var equipmentElement in hero.CivilianEquipment._itemSlots) stringBuilder.AppendLine(equipmentElement.Item?.StringId ?? "[EMPTY]");

                stringBuilder.AppendLine("StealthEquipment");
                foreach (var equipmentElement in hero.StealthEquipment._itemSlots) stringBuilder.AppendLine(equipmentElement.Item?.StringId ?? "[EMPTY]");
            }
        }

        string result = stringBuilder.ToString();
        if (result.Length > 0)
        {
            return result;
        }
        return "Hero not found.";
    }

    /// <summary>
    /// Give debug animals to a hero with a given name
    /// </summary>
    [CommandLineArgumentFunction("giveanimals", "coop.debug.inventory")]
    public static string GiveAnimalsCommand(List<string> strings)
    {
        if (ModInformation.IsClient) return "Command can only be run on the server.";

        if (strings.Count == 0)
        {
            return "Hero name argument required.";
        }

        if (TryGetObjectManager(out var objectManager) == false)
        {
            return "Unable to resolve ObjectManager.";
        }

        StringBuilder stringBuilder = new StringBuilder();
        foreach (var hero in Hero.AllAliveHeroes)
        {
            if (hero.Name.ToString() == strings[0])
            {
                var itemsToAdd = new Dictionary<string, int>()
                {
                    { "sheep", 5 },
                    { "cow", 5 },
                    { "hog", 5 },
                    { "goose", 5 },
                    { "chicken", 5 }
                };

                foreach (var itemId in itemsToAdd.Keys)
                {
                    if (!objectManager.TryGetObject(itemId, out ItemObject itemObject))
                    {
                        stringBuilder.AppendLine("Failed to retrieve object for ItemObject id: " + itemId);
                    }
                    else
                    {
                        hero.PartyBelongedTo.ItemRoster.AddToCounts(itemObject, itemsToAdd[itemId]);
                    }
                }

                stringBuilder.AppendLine(strings[0] + " was given animals.");
            }
        }

        string result = stringBuilder.ToString();
        if (result.Length > 0)
        {
            return result;
        }
        return "Hero not found.";
    }

    /// <summary>
    /// Give war horses to a hero with a given name
    /// </summary>
    [CommandLineArgumentFunction("givewarhorses", "coop.debug.inventory")]
    public static string GiveWarhorsesCommand(List<string> strings)
    {
        if (ModInformation.IsClient) return "Command can only be run on the server.";

        if (strings.Count == 0) return "Hero name argument required.";
        
        if (TryGetObjectManager(out var objectManager) == false) return "Unable to resolve ObjectManager.";

        StringBuilder stringBuilder = new StringBuilder();
        foreach (var hero in Hero.AllAliveHeroes)
        {
            if (hero.Name.ToString() == strings[0])
            {
                var itemsToAdd = new Dictionary<string, int>()
                {
                    { "t2_empire_horse", 3 },
                    { "t2_khuzait_horse", 2 }
                };

                foreach (var itemId in itemsToAdd.Keys)
                {
                    if (!objectManager.TryGetObject(itemId, out ItemObject itemObject))
                    {
                        stringBuilder.AppendLine("Failed to retrieve object for ItemObject id: " + itemId);
                    }
                    else
                    {
                        hero.PartyBelongedTo.ItemRoster.AddToCounts(itemObject, itemsToAdd[itemId]);
                    }
                }

                stringBuilder.AppendLine(strings[0] + " was given war horses.");
            }
        }

        string result = stringBuilder.ToString();
        if (result.Length > 0)
        {
            return result;
        }
        return "Hero not found.";
    }

    /// <summary>
    /// Gives a named item, in a given amount, to a hero's party.
    /// </summary>
    /// <remarks>
    /// The general form of giveanimals/givewarhorses, which hand out a fixed basket and so cannot answer
    /// "this party needs food, now" - the case this was written for.
    ///
    /// Targets by object-manager id BEFORE name, unlike its two neighbours, because a name is not unique:
    /// a hosted campaign holds both the unused vanilla main_hero slot and the player's own Hero_Player,
    /// and they share the player's name. The name-only lookup walks the list in order, reaches the slot
    /// that has no party first, and throws on PartyBelongedTo - which is exactly what
    /// coop.debug.inventory.itemids does today.
    ///
    /// Server only: ItemRoster.AddToCounts publishes from the server postfix, so the client's roster
    /// follows. The same call on a client is allowed but never published, so it would show up locally and
    /// then be wiped by the next sync.
    /// </remarks>
    [CommandLineArgumentFunction("giveitem", "coop.debug.inventory")]
    public static string GiveItemCommand(List<string> strings)
    {
        if (ModInformation.IsClient) return "Command can only be run on the server.";

        if (strings.Count < 3 ||
            !int.TryParse(strings[strings.Count - 1], out var count) ||
            count < 1)
        {
            return "Usage: coop.debug.inventory.giveitem <hero id or name> <item id> <count> " +
                   "(i.e. coop.debug.inventory.giveitem Hero_Player grain 600)";
        }

        if (TryGetObjectManager(out var objectManager) == false) return "Unable to resolve ObjectManager.";

        var itemId = strings[strings.Count - 2];
        if (!objectManager.TryGetObject(itemId, out ItemObject item))
        {
            return "No item with id '" + itemId + "'. Ids come from coop.debug.itemrosters.export.";
        }

        // Everything before the item id is the hero, so a multi-word name survives the console's space split.
        var target = string.Join(" ", strings.Take(strings.Count - 2));

        if (!objectManager.TryGetObject(target, out Hero hero) || hero == null)
        {
            var byName = Hero.AllAliveHeroes
                .Where(h => h.Name?.ToString() == target && h.PartyBelongedTo != null)
                .ToList();

            if (byName.Count == 0) return "No hero '" + target + "' with a party found.";
            if (byName.Count > 1)
            {
                return "'" + target + "' matches " + byName.Count + " heroes with a party; pass an id " +
                       "instead (coop.debug.hero.list " + target + ").";
            }

            hero = byName[0];
        }

        var party = hero.PartyBelongedTo;
        if (party == null) return hero.Name + " has no party to give items to.";

        party.ItemRoster.AddToCounts(item, count);

        return "Gave " + count + "x " + item.Name + " to " + hero.Name + "'s party (" + party.StringId + ").";
    }
}
