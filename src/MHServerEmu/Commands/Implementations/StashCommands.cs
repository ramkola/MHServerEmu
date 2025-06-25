using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Network;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Network;
using System.Collections.Generic;
using MHServerEmu.Games.Entities.Options;
using System.Reflection;
using System.Text;
using System.Linq;
using System;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("stash")]
    public class StashCommands : CommandGroup
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private readonly Dictionary<string, List<PrototypeId>> _categoryStashMap = new();

        [Command("sort")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Sort(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            if (playerConnection?.Player == null) return "Invalid player connection";
            Player player = playerConnection.Player;

            // Get the target stash name from parameters. It can contain spaces.
            string targetStashName = @params.Length > 0 ? string.Join(" ", @params) : null;

            // This list will hold all of the player's available stashes.
            List<PrototypeId> allStashRefs = ListPool<PrototypeId>.Instance.Get();
            if (!player.GetStashInventoryProtoRefs(allStashRefs, false, true))
            {
                ListPool<PrototypeId>.Instance.Return(allStashRefs);
                return "No stash tabs available";
            }

            // This list will be passed to the sorting logic. It will either contain all stashes,
            // or the single stash specified by the user.
            List<PrototypeId> stashesToSortInto = allStashRefs;

            if (!string.IsNullOrEmpty(targetStashName))
            {
                targetStashName = DecodeUrlString(targetStashName);
                Dictionary<string, PrototypeId> stashNameMap = new Dictionary<string, PrototypeId>(StringComparer.OrdinalIgnoreCase);

                foreach (PrototypeId stashRef in allStashRefs)
                {
                    Inventory stash = player.GetInventoryByRef(stashRef);
                    if (stash != null && (stash.Category == InventoryCategory.PlayerStashGeneral ||
                                          stash.Category == InventoryCategory.PlayerStashAvatarSpecific ||
                                          stash.Category == InventoryCategory.PlayerStashTeamUpGear ||
                                          stash.Category == InventoryCategory.PlayerGeneralExtra ||
                                          stash.Category == InventoryCategory.PlayerCraftingRecipes))
                    {
                        // Get custom display name from StashTabOptions
                        StashTabOptions options = GetStashTabOptions(player, stash);
                        string displayName = options?.DisplayName;

                        if (!string.IsNullOrEmpty(displayName))
                        {
                            stashNameMap[DecodeUrlString(displayName)] = stash.PrototypeDataRef;
                        }

                        // Get generated name from the prototype
                        string protoName = GameDatabase.GetPrototypeName(stash.PrototypeDataRef);
                        string generatedName = GenerateStashName(stash, protoName);

                        // Add the generated name to the map, allowing lookup by either name
                        stashNameMap[DecodeUrlString(generatedName)] = stash.PrototypeDataRef;
                    }
                }

                if (stashNameMap.TryGetValue(targetStashName, out PrototypeId foundStashRef))
                {
                    // Stash found. Create a new list containing only this stash.
                    stashesToSortInto = new List<PrototypeId> { foundStashRef };

                    // Return the original pooled list of all stashes since we are using a new list now.
                    ListPool<PrototypeId>.Instance.Return(allStashRefs);
                }
                else
                {
                    // Stash not found, return an error.
                    ListPool<PrototypeId>.Instance.Return(allStashRefs);
                    return $"Stash tab '{targetStashName}' not found.";
                }
            }

            _categoryStashMap.Clear();
            int itemsMoved = SortItems(player, stashesToSortInto);

            // If we used the original list of all stashes, we need to return it to the pool.
            // If we created a new list for a single stash, we do not return it as it wasn't from the pool.
            if (stashesToSortInto == allStashRefs)
            {
                ListPool<PrototypeId>.Instance.Return(stashesToSortInto);
            }

            // Return a message indicating what was done.
            if (!string.IsNullOrEmpty(targetStashName))
            {
                return $"Sorted {itemsMoved} items to stash tab '{targetStashName}'";
            }
            return $"Sorted {itemsMoved} items to stash tabs";
        }

        [Command("internal")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Internal(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            if (playerConnection?.Player == null) return "Invalid player connection";

            return CompactInventory(playerConnection.Player);
        }

        private int SortItems(Player player, List<PrototypeId> stashRefs)
        {
            Inventory generalInventory = player.GetInventory(InventoryConvenienceLabel.General);
            if (generalInventory == null) return 0;

            // Cache the EntityManager for performance
            var entityManager = player.Game.EntityManager;
            List<ulong> currentItems = ListPool<ulong>.Instance.Get();
            HashSet<ulong> processedItems = HashSetPool<ulong>.Instance.Get();
            int itemsMoved = 0;

            List<PrototypeId> avatarStashes = ListPool<PrototypeId>.Instance.Get();
            List<PrototypeId> generalStashes = ListPool<PrototypeId>.Instance.Get();
            List<PrototypeId> craftingStashes = ListPool<PrototypeId>.Instance.Get();

            foreach (var stashRef in stashRefs)
            {
                var inventoryProto = GameDatabase.GetPrototype<InventoryPrototype>(stashRef);
                if (inventoryProto == null) continue;

                if (inventoryProto.Category == InventoryCategory.PlayerStashAvatarSpecific)
                    avatarStashes.Add(stashRef);
                else if (inventoryProto.Category == InventoryCategory.PlayerStashGeneral)
                    generalStashes.Add(stashRef);
                else if (inventoryProto.Category == InventoryCategory.PlayerCraftingRecipes)
                    craftingStashes.Add(stashRef);
            }

            foreach (var entry in generalInventory)
                currentItems.Add(entry.Id);

            // First pass: Handle stackable items
            foreach (ulong itemId in currentItems)
            {
                if (processedItems.Contains(itemId)) continue;
                Item item = entityManager.GetEntity<Item>(itemId);
                if (item == null || !item.CanStack() || item.IsEquipped) continue;

                if (item.IsBoundToCharacter) continue;

                if (TryMoveToMatchingStack(player, item, stashRefs))
                {
                    itemsMoved++;
                    processedItems.Add(itemId);
                }
            }

            // Second pass: Handle avatar-bound and unique items
            foreach (ulong itemId in currentItems)
            {
                if (processedItems.Contains(itemId)) continue;
                Item item = entityManager.GetEntity<Item>(itemId);
                if (item == null || item.IsEquipped || item.CanStack()) continue; // Skip items already handled or stackable

                string itemName = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
                bool moved = false;

                // Try avatar-specific stashes first for any suitable item
                if (avatarStashes.Count > 0)
                {
                    foreach (var avatarStashRef in avatarStashes)
                    {
                        Inventory stash = player.GetInventoryByRef(avatarStashRef);
                        if (stash == null) continue;

                        // Get avatar name from stash and check if the item is suitable
                        string stashProtoName = GameDatabase.GetPrototypeName(stash.PrototypeDataRef);
                        string avatarName = ExtractAvatarNameFromStash(stashProtoName);

                        if (!string.IsNullOrEmpty(avatarName) && !IsItemSuitableForAvatar(item, avatarName))
                        {
                            continue; // This item doesn't belong in this hero's stash
                        }

                        if (TryMoveItemToStash(player, item, avatarStashRef, true))
                        {
                            Logger.Debug($"Successfully moved item '{itemName}' to matching avatar stash");
                            itemsMoved++;
                            processedItems.Add(itemId);
                            moved = true;
                            break;
                        }
                    }
                }

                // If not moved yet, try to find a suitable category stash
                if (!moved)
                {
                    if (TryMoveToCategoryStash(player, item, stashRefs))
                    {
                        itemsMoved++;
                        processedItems.Add(itemId);
                    }
                }
            }

            // Third pass: Handle crafting items
            if (craftingStashes.Count > 0)
            {
                foreach (ulong itemId in currentItems)
                {
                    if (processedItems.Contains(itemId)) continue;
                    Item item = entityManager.GetEntity<Item>(itemId);
                    if (item == null || item.IsEquipped) continue;
                    if (GetItemCategory(item) == "Crafting")
                    {
                        string itemName = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
                        bool moved = false;
                        foreach (var craftingStashRef in craftingStashes)
                        {
                            if (TryMoveItemToStash(player, item, craftingStashRef, false))
                            {
                                itemsMoved++;
                                processedItems.Add(itemId);
                                moved = true;
                                break;
                            }
                        }

                        if (!moved && generalStashes.Count > 0)
                        {
                            foreach (var generalStashRef in generalStashes)
                            {
                                if (TryMoveItemToStash(player, item, generalStashRef, false))
                                {
                                    itemsMoved++;
                                    processedItems.Add(itemId);
                                    moved = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Final pass: Categorize and move all remaining items
            foreach (ulong itemId in currentItems)
            {
                if (processedItems.Contains(itemId)) continue;
                Item item = entityManager.GetEntity<Item>(itemId);
                if (item == null || item.IsEquipped) continue;
                if (TryMoveToCategoryStash(player, item, stashRefs))
                {
                    itemsMoved++;
                }
            }

            // Return all lists to the pool to prevent memory leaks
            ListPool<ulong>.Instance.Return(currentItems);
            HashSetPool<ulong>.Instance.Return(processedItems);
            ListPool<PrototypeId>.Instance.Return(avatarStashes);
            ListPool<PrototypeId>.Instance.Return(generalStashes);
            ListPool<PrototypeId>.Instance.Return(craftingStashes);

            return itemsMoved;
        }

        private bool TryMoveToMatchingStack(Player player, Item item, List<PrototypeId> stashRefs)
        {
            var entityManager = player.Game.EntityManager;
            string itemName = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
            foreach (PrototypeId stashRef in stashRefs)
            {
                Inventory stash = player.GetInventoryByRef(stashRef);
                if (stash == null) continue;

                foreach (var entry in stash)
                {
                    Item targetItem = entityManager.GetEntity<Item>(entry.Id);
                    if (targetItem != null && item.CanStackOnto(targetItem))
                    {
                        bool needsBindingSkip = item.IsBoundToCharacter;
                        if (needsBindingSkip)
                            item.SetStatus(EntityStatus.SkipItemBindingCheck, true);

                        try
                        {
                            bool success = player.TryInventoryMove(item.Id, stash.OwnerId, stash.PrototypeDataRef, entry.Slot);
                            if (success)
                            {
                                Logger.Debug($"Stacked item '{itemName}' onto existing stack in stash '{GameDatabase.GetPrototypeName(stash.PrototypeDataRef)}'");
                                return true;
                            }
                            return false;
                        }
                        finally
                        {
                            if (needsBindingSkip)
                                item.SetStatus(EntityStatus.SkipItemBindingCheck, false);
                        }
                    }
                }
            }
            return false;
        }

        private bool TryMoveToCategoryStash(Player player, Item item, List<PrototypeId> stashRefs)
        {
            string itemName = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
            string category = GetItemCategory(item);

            if (!_categoryStashMap.TryGetValue(category, out List<PrototypeId> categoryStashes))
            {
                categoryStashes = new List<PrototypeId>();
                _categoryStashMap[category] = categoryStashes;
            }

            foreach (PrototypeId stashRef in categoryStashes)
            {
                if (TryMoveItemToStash(player, item, stashRef, item.IsBoundToCharacter))
                    return true;
            }

            foreach (PrototypeId stashRef in stashRefs)
            {
                if (categoryStashes.Contains(stashRef)) continue;

                Inventory stash = player.GetInventoryByRef(stashRef);
                if (stash == null || !IsStashEmpty(stash)) continue;

                categoryStashes.Add(stashRef);
                return TryMoveItemToStash(player, item, stashRef, item.IsBoundToCharacter);
            }

            foreach (PrototypeId stashRef in stashRefs)
            {
                if (TryMoveItemToStash(player, item, stashRef, item.IsBoundToCharacter))
                {
                    if (!categoryStashes.Contains(stashRef))
                        categoryStashes.Add(stashRef);
                    return true;
                }
            }

            return false;
        }

        private bool TryMoveItemToStash(Player player, Item item, PrototypeId stashRef, bool skipBindingCheck)
        {
            Inventory stash = player.GetInventoryByRef(stashRef);
            if (stash == null) return false;

            uint targetSlot = stash.GetFreeSlot(item, true, true);
            if (targetSlot == Inventory.InvalidSlot) return false;

            bool needsBindingSkip = skipBindingCheck && item.IsBoundToCharacter;

            if (needsBindingSkip)
                item.SetStatus(EntityStatus.SkipItemBindingCheck, true);

            try
            {
                return player.TryInventoryMove(item.Id, stash.OwnerId, stash.PrototypeDataRef, targetSlot);
            }
            finally
            {
                if (needsBindingSkip)
                    item.SetStatus(EntityStatus.SkipItemBindingCheck, false);
            }
        }

        private string GetItemCategory(Item item)
        {
            string itemProto = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
            if (itemProto.StartsWith("Entity/Items/"))
            {
                string[] parts = itemProto.Split('/');
                if (parts.Length >= 3)
                {
                    if (parts[2] == "Armor")
                        return itemProto.Contains("Unique") ? "Uniques" : "Gear";

                    switch (parts[2])
                    {
                        case "Crafting":
                        case "CurrencyItems":
                        case "Artifacts":
                        case "Insignias":
                        case "Legendaries":
                        case "Medals":
                        case "Pets":
                        case "Relics":
                        case "Rings":
                            return parts[2];
                    }
                }
            }
            return "Other";
        }

        private bool IsStashEmpty(Inventory stash)
        {
            foreach (var entry in stash)
                return false;
            return true;
        }

        private int GetStashItemCount(Inventory stash)
        {
            int count = 0;
            foreach (var entry in stash)
                count++;
            return count;
        }

        private string CompactInventory(Player player)
        {
            Inventory generalInventory = player.GetInventory(InventoryConvenienceLabel.General);
            if (generalInventory == null) return "No general inventory found";

            var entityManager = player.Game.EntityManager;
            List<ulong> currentItems = ListPool<ulong>.Instance.Get();
            int itemsCompacted = 0;

            foreach (var entry in generalInventory)
                currentItems.Add(entry.Id);

            uint nextSlot = 0;
            foreach (ulong itemId in currentItems)
            {
                Item item = entityManager.GetEntity<Item>(itemId);
                if (item == null || item.IsEquipped) continue;

                bool needsBindingSkip = item.IsBoundToCharacter;

                if (needsBindingSkip)
                    item.SetStatus(EntityStatus.SkipItemBindingCheck, true);

                try
                {
                    if (player.TryInventoryMove(itemId, generalInventory.OwnerId, generalInventory.PrototypeDataRef, nextSlot))
                    {
                        itemsCompacted++;
                        nextSlot++;
                    }
                }
                finally
                {
                    if (needsBindingSkip)
                        item.SetStatus(EntityStatus.SkipItemBindingCheck, false);
                }
            }

            ListPool<ulong>.Instance.Return(currentItems);
            return $"Compacted {itemsCompacted} items in inventory";
        }

        #region Helper Methods

        private static readonly FieldInfo StashTabOptionsField = typeof(Player).GetField("_stashTabOptionsDict",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private StashTabOptions GetStashTabOptions(Player player, Inventory stash)
        {
            if (StashTabOptionsField == null || player == null || stash == null)
                return null;

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
                int startIndex = protoName.IndexOf("PlayerStashForAvatar") + "PlayerStashForAvatar".Length;
                if (startIndex < protoName.Length)
                {
                    return protoName.Substring(startIndex).Replace("/", "").Trim();
                }
            }

            if (protoName.Contains("PlayerStashCrafting"))
            {
                int startIndex = protoName.IndexOf("PlayerStashCrafting") + "PlayerStashCrafting".Length;
                if (startIndex < protoName.Length)
                {
                    return $"Crafting{protoName.Substring(startIndex).Replace("/", "").Trim()}";
                }
                return "Crafting";
            }

            if (protoName.Contains("PlayerStashGeneral"))
            {
                if (protoName.Contains("EternitySplinter")) return "GeneralEternity";
                if (protoName.Contains("Anniversary")) return "GeneralAnniversary";

                int startIndex = protoName.IndexOf("PlayerStashGeneral") + "PlayerStashGeneral".Length;
                if (startIndex < protoName.Length)
                {
                    string number = protoName.Substring(startIndex).Replace("/", "").Trim();
                    if (int.TryParse(number, out _))
                    {
                        return $"General{number}";
                    }
                }
                return "General";
            }

            if (protoName.Contains("PlayerStashTeamUpGeneral"))
            {
                int startIndex = protoName.IndexOf("PlayerStashTeamUpGeneral") + "PlayerStashTeamUpGeneral".Length;
                if (startIndex < protoName.Length)
                {
                    return $"TeamUp{protoName.Substring(startIndex).Replace("/", "").Trim()}";
                }
                return "TeamUp";
            }

            return stash.Category.ToString();
        }

        private string DecodeUrlString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return input.Replace("%20", " ").Replace("%2F", "/").Replace("%3A", ":").Replace("%2D", "-");
        }

        // Helper method to extract avatar name from stash prototype name
        private string ExtractAvatarNameFromStash(string stashProtoName)
        {
            if (stashProtoName.Contains("PlayerStashForAvatar"))
            {
                int startIndex = stashProtoName.IndexOf("PlayerStashForAvatar") + "PlayerStashForAvatar".Length;
                if (startIndex < stashProtoName.Length)
                {
                    string avatarName = stashProtoName.Substring(startIndex);
                    // Clean up any remaining path parts or special characters
                    avatarName = avatarName.Replace("/", "").Trim();
                    return avatarName;
                }
            }
            return string.Empty;
        }

        // Helper method to check if an item is suitable for an avatar-specific stash
        private bool IsItemSuitableForAvatar(Item item, string avatarName)
        {
            if (item == null || string.IsNullOrEmpty(avatarName))
                return false;

            // Get the item prototype name
            string itemProtoName = GameDatabase.GetPrototypeName(item.PrototypeDataRef).ToLowerInvariant();

            // Create a mapping of avatar names to their possible variations
            Dictionary<string, List<string>> avatarVariations = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "DrDoom.prototype", new List<string> { "doctordoom", "drdoom", "doom" } },
                { "DrStrange.prototype", new List<string> { "doctorstrange", "drstrange" } },
                { "Spiderman.prototype", new List<string> { "spiderman", "spider-man" } },
                { "StarLord.prototype", new List<string> { "starlord", "star-lord" } },
                { "InvisiWoman.prototype", new List<string> { "invisiblewoman", "invisiwoman" } },
            };

            // Check if the avatar name exists in our mapping
            if (avatarVariations.TryGetValue(avatarName, out List<string> variations))
            {
                // Check if the item name contains any of the avatar variations
                foreach (string variation in variations)
                {
                    if (itemProtoName.Contains(variation))
                    {
                        return true;
                    }
                }
            }

            // If no specific mapping, just check if the item name contains the avatar name without .prototype
            string baseAvatarName = avatarName.Replace(".prototype", "").ToLowerInvariant();
            return itemProtoName.Contains(baseAvatarName);
        }

        #endregion
    }
}