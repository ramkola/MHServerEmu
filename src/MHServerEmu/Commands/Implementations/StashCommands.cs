using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Properties;
using System.Text;
using MHServerEmu.Games.Entities.Options;
using System.Reflection;
using System.Collections.Generic;
using System;
using MHServerEmu.Core.Memory;
using System.Linq;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("stash")]
    [CommandGroupDescription("Commands for managing stash tabs.")]
    public class StashCommands : CommandGroup
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        [Command("list")]
        [CommandDescription("Lists available stash tabs and filters for autosort.")]
        [CommandUsage("stash list [stashes|filters]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string ListOptions(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = client as PlayerConnection;
            if (playerConnection == null) return "Failed to get player connection.";

            Player player = playerConnection.Player;
            if (player == null) return "Failed to get player entity.";

            string option = @params.Length > 0 ? @params[0] : "stashes";

            if (string.Equals(option, "filters", StringComparison.OrdinalIgnoreCase))
            {
                return ListAvailableFilters();
            }
            else // default to stashes
            {
                return ListAvailableStashTabs(player);
            }
        }

        [Command("sort")]
        [CommandDescription("Automatically sorts items to stash tabs. Usage: '!stash sort' (all items) or '!stash sort [filter] [stash_name]' (specific items to specific stash).")]
        [CommandUsage("stash sort [filter] [stash_name]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string AutoSort(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = client as PlayerConnection;
            if (playerConnection == null) return "Failed to get player connection.";

            Player player = playerConnection.Player;
            if (player == null) return "Failed to get player entity.";

            string filter = @params.Length > 0 ? @params[0] : "all";
            string targetStashName = string.Empty;
            if (@params.Length > 1)
            {
                var sb = new StringBuilder();
                for (int i = 1; i < @params.Length; i++)
                {
                    sb.Append(@params[i]);
                    if (i < @params.Length - 1) sb.Append(" ");
                }
                targetStashName = sb.ToString();
            }

            return SortInventoryWithFilter(player, filter, targetStashName);
        }

        [Command("internal")]
        [CommandDescription("Sorts items within the general inventory.")]
        [CommandUsage("stash internal")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string SortInternal(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = client as PlayerConnection;
            if (playerConnection == null) return "Failed to get player connection.";

            Player player = playerConnection.Player;
            if (player == null) return "Failed to get player entity.";

            return SortInventoryInternal(player);
        }

        [Command("sortbylevel")]
        [CommandDescription("Sorts a stash tab by item level. If no name is provided, sorts all tabs.")]
        [CommandUsage("stash sortbylevel [\"<stash_name>\"]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string SortByLevel(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = client as PlayerConnection;
            if (playerConnection == null) return "Failed to get player connection.";

            Player player = playerConnection.Player;
            if (player == null) return "Failed to get player entity.";

            var inventoriesToSort = ListPool<Inventory>.Instance.Get();
            int stashesModified = 0;

            try
            {
                var stashNameMap = GetStashNameMap(player);

                if (@params.Length > 0)
                {
                    if (stashNameMap.TryGetValue(@params[0], out Inventory inventory))
                    {
                        inventoriesToSort.Add(inventory);
                    }
                    else
                    {
                        return $"Stash tab '{@params[0]}' not found. Use '!stash list' to see available tab names.";
                    }
                }
                else
                {
                    foreach (var inv in stashNameMap.Values)
                    {
                        if (!inventoriesToSort.Contains(inv))
                        {
                            inventoriesToSort.Add(inv);
                        }
                    }
                }

                if (inventoriesToSort.Count == 0) return "No available stash tabs to sort.";

                foreach (var inventory in inventoriesToSort)
                {
                    var itemsInStash = ListPool<Item>.Instance.Get();
                    try
                    {
                        foreach (var entry in inventory)
                        {
                            var item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                            if (item != null) itemsInStash.Add(item);
                        }

                        if (itemsInStash.Count <= 1) continue;

                        itemsInStash.Sort((a, b) =>
                        {
                            PropertyValue propA = a.Properties[(PropertyId)PropertyEnum.ItemLevel];
                            PropertyValue propB = b.Properties[(PropertyId)PropertyEnum.ItemLevel];
                            return ((float)propA).CompareTo((float)propB);
                        });

                        var originalItems = ListPool<Item>.Instance.Get();
                        try
                        {
                            foreach (var entry in inventory)
                            {
                                var item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                                if (item != null) originalItems.Add(item);
                            }

                            bool isAlreadySorted = true;
                            if (originalItems.Count == itemsInStash.Count)
                            {
                                for (int i = 0; i < originalItems.Count; i++)
                                {
                                    if (originalItems[i]?.Id != itemsInStash[i]?.Id)
                                    {
                                        isAlreadySorted = false;
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                isAlreadySorted = false;
                            }
                            if (isAlreadySorted) continue;
                        }
                        finally
                        {
                            ListPool<Item>.Instance.Return(originalItems);
                        }

                        stashesModified++;

                        var tempHolding = player.GetInventory(InventoryConvenienceLabel.General);
                        var itemsToReAdd = ListPool<Item>.Instance.Get();
                        itemsToReAdd.AddRange(itemsInStash);
                        try
                        {
                            foreach (var item in itemsToReAdd)
                            {
                                if (!player.TryInventoryMove(item.Id, player.Id, tempHolding.PrototypeDataRef, Inventory.InvalidSlot))
                                {
                                    Logger.Error($"SortByLevel: Failed to move item '{GameDatabase.GetPrototypeName(item.PrototypeDataRef)}' to temporary holding.");
                                }
                            }

                            foreach (var item in itemsToReAdd)
                            {
                                if (!player.TryInventoryMove(item.Id, player.Id, inventory.PrototypeDataRef, Inventory.InvalidSlot))
                                {
                                    Logger.Error($"SortByLevel: Failed to move item '{GameDatabase.GetPrototypeName(item.PrototypeDataRef)}' back to stash. It remains in your general inventory.");
                                }
                            }
                        }
                        finally
                        {
                            ListPool<Item>.Instance.Return(itemsToReAdd);
                        }
                    }
                    finally
                    {
                        ListPool<Item>.Instance.Return(itemsInStash);
                    }
                }
            }
            finally
            {
                ListPool<Inventory>.Instance.Return(inventoriesToSort);
            }

            if (stashesModified > 0) return $"Successfully sorted {stashesModified} stash tab(s) by level.";
            return "All specified stash tabs are already sorted by level.";
        }

        #region Helper Methods

        private Dictionary<string, Inventory> GetStashNameMap(Player player)
        {
            var stashNameMap = new Dictionary<string, Inventory>(StringComparer.OrdinalIgnoreCase);
            var stashRefs = ListPool<PrototypeId>.Instance.Get();
            try
            {
                if (!player.GetStashInventoryProtoRefs(stashRefs, false, true))
                {
                    return stashNameMap;
                }

                foreach (var stashRef in stashRefs)
                {
                    var stash = player.GetInventoryByRef(stashRef);
                    if (stash != null && StashHasFreeSpace(stash))
                    {
                        var options = GetStashTabOptions(player, stash);
                        string displayName = options?.DisplayName;

                        if (string.IsNullOrEmpty(displayName))
                        {
                            string protoName = GameDatabase.GetPrototypeName(stash.PrototypeDataRef);
                            displayName = GenerateStashName(stash, protoName);
                        }

                        displayName = DecodeUrlString(displayName);
                        if (!stashNameMap.ContainsKey(displayName))
                        {
                            stashNameMap[displayName] = stash;
                        }
                    }
                }
            }
            finally
            {
                ListPool<PrototypeId>.Instance.Return(stashRefs);
            }
            return stashNameMap;
        }

        private string ListAvailableFilters()
        {
            StringBuilder sb = new StringBuilder("Available filters for autosort:\n");
            var filters = new List<string>
            {
                "all - All items", "artifacts - Artifact items", "crafting - Crafting materials",
                "insignias - Team insignias", "legendaries - Legendary items", "rings - Ring items",
                "runes - Rune items", "relics - Relic items", "uniques - Unique items",
                "slot1 - Slot 1 items", "slot2 - Slot 2 items", "slot3 - Slot 3 items",
                "slot4 - Slot 4 items", "slot5 - Slot 5 items"
            };
            foreach (string filter in filters)
            {
                sb.AppendLine(filter);
            }
            sb.AppendLine("\nUsage examples:");
            sb.AppendLine("!stash sort all General01");
            sb.AppendLine("!stash sort uniques Wolverine");
            sb.AppendLine("!stash sort slot1 \"Doctor Doom\"");
            return sb.ToString();
        }

        private string ListAvailableStashTabs(Player player)
        {
            var stashRefs = ListPool<PrototypeId>.Instance.Get();
            try
            {
                if (!player.GetStashInventoryProtoRefs(stashRefs, false, true))
                {
                    return "No stash tabs found.";
                }

                var stashGroups = new Dictionary<string, List<string>>();
                int totalStashes = 0;

                foreach (PrototypeId stashRef in stashRefs)
                {
                    Inventory stash = player.GetInventoryByRef(stashRef);
                    if (stash != null)
                    {
                        if (stash.Category == InventoryCategory.PlayerStashGeneral ||
                            stash.Category == InventoryCategory.PlayerStashAvatarSpecific ||
                            stash.Category == InventoryCategory.PlayerStashTeamUpGear ||
                            stash.Category == InventoryCategory.PlayerGeneralExtra ||
                            stash.Category == InventoryCategory.PlayerCraftingRecipes)
                        {
                            totalStashes++;
                            if (StashHasFreeSpace(stash))
                            {
                                StashTabOptions options = GetStashTabOptions(player, stash);
                                string displayName = options?.DisplayName;

                                if (string.IsNullOrEmpty(displayName))
                                {
                                    displayName = GenerateStashName(stash, GameDatabase.GetPrototypeName(stash.PrototypeDataRef));
                                }

                                displayName = DecodeUrlString(displayName);

                                string groupName = stash.Category switch
                                {
                                    InventoryCategory.PlayerStashGeneral => "General",
                                    InventoryCategory.PlayerCraftingRecipes => "Crafting",
                                    InventoryCategory.PlayerStashTeamUpGear => "TeamUp",
                                    InventoryCategory.PlayerStashAvatarSpecific => "Avatar",
                                    _ => "Other",
                                };

                                if (!stashGroups.ContainsKey(groupName))
                                    stashGroups[groupName] = new List<string>();
                                stashGroups[groupName].Add(displayName);
                            }
                        }
                    }
                }

                StringBuilder sb = new StringBuilder("Available stash tabs with free space:\n");
                string[] groupOrder = { "General", "Crafting", "TeamUp", "Avatar", "Other" };

                foreach (string group in groupOrder)
                {
                    if (stashGroups.TryGetValue(group, out List<string> groupList) && groupList.Count > 0)
                    {
                        sb.AppendLine($"\n{group}: \"{string.Join("\", \"", groupList)}\"");
                    }
                }

                int availableStashes = stashGroups.Values.Sum(list => list.Count);
                sb.AppendLine($"\nShowing {availableStashes} stash tabs with free space (out of {totalStashes} total).");
                sb.AppendLine("\nUsage: !stash sort [filter] [\"stash_name\"]");
                sb.AppendLine("Use '!stash list filters' to see available filters.");

                return sb.ToString();
            }
            finally
            {
                ListPool<PrototypeId>.Instance.Return(stashRefs);
            }
        }

        private string SortInventoryWithFilter(Player player, string filter, string targetStashName = "")
        {
            if (!string.IsNullOrEmpty(targetStashName))
            {
                targetStashName = DecodeUrlString(targetStashName);
            }

            Inventory generalInventory = player.GetInventory(InventoryConvenienceLabel.General);
            if (generalInventory == null) return "Failed to find general inventory.";

            var stashInventories = ListPool<Inventory>.Instance.Get();
            var itemsToMove = ListPool<Item>.Instance.Get();
            try
            {
                var stashNameMap = GetStashNameMap(player);
                if (!string.IsNullOrEmpty(targetStashName))
                {
                    if (stashNameMap.TryGetValue(targetStashName, out Inventory foundStash))
                    {
                        stashInventories.Add(foundStash);
                    }
                    else
                    {
                        return $"Stash tab '{targetStashName}' not found. Use '!stash list stashes' to see available stash tabs.";
                    }
                }
                else
                {
                    foreach (var inv in stashNameMap.Values)
                    {
                        if (!stashInventories.Contains(inv)) stashInventories.Add(inv);
                    }
                }

                if (stashInventories.Count == 0) return "No available stash tabs found to sort into.";

                foreach (var entry in generalInventory)
                {
                    Item item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                    if (item != null && !item.IsEquipped && (string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase) || MatchesFilter(item, filter)))
                    {
                        itemsToMove.Add(item);
                    }
                }

                itemsToMove.Sort((a, b) => string.Compare(GameDatabase.GetPrototypeName(a.PrototypeDataRef), GameDatabase.GetPrototypeName(b.PrototypeDataRef), StringComparison.Ordinal));

                int itemsMoved = 0;
                foreach (Item item in itemsToMove)
                {
                    foreach (Inventory stash in stashInventories)
                    {
                        if (player.TryInventoryMove(item.Id, player.Id, stash.PrototypeDataRef, Inventory.InvalidSlot))
                        {
                            itemsMoved++;
                            break;
                        }
                    }
                }

                if (string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(targetStashName))
                    return $"Auto-sort complete. Moved {itemsMoved} item(s) to stash tabs.";
                else if (!string.IsNullOrEmpty(targetStashName))
                    return $"Auto-sort complete. Moved {itemsMoved} {filter} item(s) to stash tab '{targetStashName}'.";
                else
                    return $"Auto-sort complete. Moved {itemsMoved} {filter} item(s) to stash tabs.";
            }
            finally
            {
                ListPool<Inventory>.Instance.Return(stashInventories);
                ListPool<Item>.Instance.Return(itemsToMove);
            }
        }

        private string SortInventoryInternal(Player player)
        {
            Inventory generalInventory = player.GetInventory(InventoryConvenienceLabel.General);
            if (generalInventory == null) return "Failed to find general inventory.";

            int stacksConsolidatedCount = 0;
            var itemsToCheck = ListPool<Item>.Instance.Get();
            var processedItemIds = new HashSet<ulong>();
            try
            {
                foreach (var entry in generalInventory)
                {
                    Item item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                    if (item != null && item.CanStack() && item.CurrentStackSize < (int)item.Properties[(PropertyId)PropertyEnum.InventoryStackSizeMax])
                    {
                        itemsToCheck.Add(item);
                    }
                }

                foreach (Item item in itemsToCheck)
                {
                    if (item == null || !item.InventoryLocation.IsValid || processedItemIds.Contains(item.Id)) continue;
                    foreach (var entry in generalInventory)
                    {
                        Item targetItem = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                        if (targetItem != null && targetItem.Id != item.Id && item.CanStackOnto(targetItem))
                        {
                            if (player.TryInventoryMove(item.Id, player.Id, generalInventory.PrototypeDataRef, entry.Slot))
                            {
                                stacksConsolidatedCount++;
                                processedItemIds.Add(item.Id);
                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                ListPool<Item>.Instance.Return(itemsToCheck);
            }

            var itemsToReAdd = ListPool<Item>.Instance.Get();
            try
            {
                foreach (var entry in generalInventory)
                {
                    var item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                    if (item != null) itemsToReAdd.Add(item);
                }

                itemsToReAdd.Sort((a, b) => string.Compare(GameDatabase.GetPrototypeName(a.PrototypeDataRef), GameDatabase.GetPrototypeName(b.PrototypeDataRef), StringComparison.Ordinal));

                foreach (var item in itemsToReAdd)
                {
                    player.TryInventoryMove(item.Id, player.Id, default, 0);
                }

                int itemsCompacted = 0;
                foreach (var item in itemsToReAdd)
                {
                    if (player.TryInventoryMove(item.Id, player.Id, generalInventory.PrototypeDataRef, (uint)itemsCompacted))
                    {
                        itemsCompacted++;
                    }
                }
                return $"Inventory sorted internally. Consolidated {stacksConsolidatedCount} stacks, re-positioned {itemsCompacted} items by type.";
            }
            finally
            {
                ListPool<Item>.Instance.Return(itemsToReAdd);
            }
        }

        private bool StashHasFreeSpace(Inventory stash)
        {
            if (stash == null) return false;
            return stash.GetFreeSlot(null, false) != Inventory.InvalidSlot;
        }

        private uint FindNextAvailableSlot(Inventory inventory, uint startSlot)
        {
            int capacity = inventory.GetCapacity();
            uint maxSlot = (capacity == int.MaxValue) ? 1000 : (uint)capacity;
            for (uint slot = startSlot; slot < maxSlot; slot++)
            {
                if (inventory.GetEntityInSlot(slot) == Entity.InvalidId && (inventory.Owner == null || inventory.Owner.ValidateInventorySlot(inventory, slot)))
                {
                    return slot;
                }
            }
            Logger.Warn($"FindNextAvailableSlot: No free slot found in {GameDatabase.GetPrototypeName(inventory.PrototypeDataRef)} starting from {startSlot} up to {maxSlot}");
            return Inventory.InvalidSlot;
        }

        private static readonly FieldInfo StashTabOptionsField = typeof(Player).GetField("_stashTabOptionsDict", BindingFlags.NonPublic | BindingFlags.Instance);

        private StashTabOptions GetStashTabOptions(Player player, Inventory stash)
        {
            if (StashTabOptionsField == null || player == null || stash == null) return null;
            var stashTabOptionsDict = StashTabOptionsField.GetValue(player) as Dictionary<PrototypeId, StashTabOptions>;
            if (stashTabOptionsDict?.TryGetValue(stash.PrototypeDataRef, out StashTabOptions options) == true)
            {
                if (!string.IsNullOrEmpty(options.DisplayName))
                {
                    options.DisplayName = DecodeUrlString(options.DisplayName);
                }
                return options;
            }
            return null;
        }

        private string GenerateStashName(Inventory stash, string protoName)
        {
            if (protoName.Contains("PlayerStashForAvatar"))
            {
                return protoName.Substring(protoName.IndexOf("PlayerStashForAvatar") + "PlayerStashForAvatar".Length).Replace("/", "").Trim();
            }
            if (protoName.Contains("PlayerStashCrafting"))
            {
                return $"Crafting{protoName.Substring(protoName.IndexOf("PlayerStashCrafting") + "PlayerStashCrafting".Length).Replace("/", "").Trim()}";
            }
            if (protoName.Contains("PlayerStashGeneral"))
            {
                if (protoName.Contains("EternitySplinter")) return "GeneralEternity";
                if (protoName.Contains("Anniversary")) return "GeneralAnniversary";
                string number = protoName.Substring(protoName.IndexOf("PlayerStashGeneral") + "PlayerStashGeneral".Length).Replace("/", "").Trim();
                if (int.TryParse(number, out int _)) return $"General{number}";
                return "General";
            }
            if (protoName.Contains("PlayerStashTeamUpGeneral"))
            {
                return $"TeamUp{protoName.Substring(protoName.IndexOf("PlayerStashTeamUpGeneral") + "PlayerStashTeamUpGeneral".Length).Replace("/", "").Trim()}";
            }
            return stash.Category switch
            {
                InventoryCategory.PlayerStashGeneral => "General",
                InventoryCategory.PlayerStashAvatarSpecific => "Avatar",
                InventoryCategory.PlayerStashTeamUpGear => "TeamUp",
                InventoryCategory.PlayerCraftingRecipes => "Crafting",
                InventoryCategory.PlayerGeneralExtra => "Extra",
                _ => "Stash",
            };
        }

        private string ExtractAvatarNameFromStash(string stashProtoName)
        {
            if (stashProtoName.Contains("PlayerStashForAvatar"))
            {
                int startIndex = stashProtoName.IndexOf("PlayerStashForAvatar") + "PlayerStashForAvatar".Length;
                if (startIndex < stashProtoName.Length)
                {
                    return stashProtoName.Substring(startIndex).Replace("/", "").Trim();
                }
            }
            return string.Empty;
        }

        private bool IsItemSuitableForAvatar(Item item, string avatarName)
        {
            if (item == null || string.IsNullOrEmpty(avatarName)) return false;
            string itemProtoName = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
            return itemProtoName.Contains(avatarName, StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesFilter(Item item, string filter)
        {
            if (item == null) return false;
            string itemProtoName = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
            return filter.ToLower() switch
            {
                "artifacts" => itemProtoName.Contains("artifact", StringComparison.OrdinalIgnoreCase),
                "crafting" => itemProtoName.Contains("crafting", StringComparison.OrdinalIgnoreCase) || itemProtoName.Contains("material", StringComparison.OrdinalIgnoreCase) || itemProtoName.Contains("ingredient", StringComparison.OrdinalIgnoreCase),
                "insignias" => itemProtoName.Contains("insignia", StringComparison.OrdinalIgnoreCase),
                "legendaries" => itemProtoName.Contains("legendary", StringComparison.OrdinalIgnoreCase),
                "rings" => itemProtoName.Contains("ring", StringComparison.OrdinalIgnoreCase),
                "runes" => itemProtoName.Contains("rune", StringComparison.OrdinalIgnoreCase),
                "relics" => itemProtoName.Contains("relic", StringComparison.OrdinalIgnoreCase),
                "uniques" => itemProtoName.Contains("unique", StringComparison.OrdinalIgnoreCase),
                "slot1" => itemProtoName.Contains("/o1", StringComparison.OrdinalIgnoreCase),
                "slot2" => itemProtoName.Contains("/o2", StringComparison.OrdinalIgnoreCase),
                "slot3" => itemProtoName.Contains("/o3", StringComparison.OrdinalIgnoreCase),
                "slot4" => itemProtoName.Contains("/o4", StringComparison.OrdinalIgnoreCase),
                "slot5" => itemProtoName.Contains("/o5", StringComparison.OrdinalIgnoreCase),
                _ => itemProtoName.Contains(filter, StringComparison.OrdinalIgnoreCase),
            };
        }

        private string DecodeUrlString(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return input.Replace("%20", " ").Replace("%2F", "/").Replace("%3A", ":").Replace("%2D", "-");
        }

        #endregion
    }
}