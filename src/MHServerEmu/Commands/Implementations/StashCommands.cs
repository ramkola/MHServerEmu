using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Entities.Options;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("stash")]
    [CommandGroupUserLevel(AccountUserLevel.Admin)]
    public class StashCommands : CommandGroup
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private readonly Dictionary<string, List<PrototypeId>> _categoryStashMap = new();

        private static readonly Dictionary<string, List<string>> AvatarNameVariations = new(StringComparer.OrdinalIgnoreCase)
        {
            { "DrDoom.prototype", new List<string> { "doctordoom", "drdoom", "doom" } },
            { "DrStrange.prototype", new List<string> { "doctorstrange", "drstrange" } },
            { "Spiderman.prototype", new List<string> { "spiderman", "spider-man" } },
            { "StarLord.prototype", new List<string> { "starlord", "star-lord" } },
            { "InvisiWoman.prototype", new List<string> { "invisiblewoman", "invisiwoman" } },
        };

        [Command("sort")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Sort(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            if (playerConnection?.Player == null) return "Invalid player connection";
            Player player = playerConnection.Player;

            string targetStashName = @params.Length > 0 ? string.Join(" ", @params) : null;

            List<PrototypeId> allStashRefs = ListPool<PrototypeId>.Instance.Get();
            if (!player.GetStashInventoryProtoRefs(allStashRefs, false, true))
            {
                ListPool<PrototypeId>.Instance.Return(allStashRefs);
                return "No stash tabs available";
            }

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
                        StashTabOptions options = GetStashTabOptions(player, stash);
                        string displayName = options?.DisplayName;

                        if (!string.IsNullOrEmpty(displayName))
                        {
                            stashNameMap[DecodeUrlString(displayName)] = stash.PrototypeDataRef;
                        }

                        string protoName = GameDatabase.GetPrototypeName(stash.PrototypeDataRef);
                        string generatedName = GenerateStashName(stash, protoName);
                        stashNameMap[DecodeUrlString(generatedName)] = stash.PrototypeDataRef;
                    }
                }

                if (stashNameMap.TryGetValue(targetStashName, out PrototypeId foundStashRef))
                {
                    ListPool<PrototypeId>.Instance.Return(allStashRefs);
                    stashesToSortInto = ListPool<PrototypeId>.Instance.Get();
                    stashesToSortInto.Add(foundStashRef);
                }
                else
                {
                    ListPool<PrototypeId>.Instance.Return(allStashRefs);
                    return $"Stash tab '{targetStashName}' not found.";
                }
            }

            ClearCategoryStashMap();
            int itemsMoved = SortItems(player, stashesToSortInto);

            ListPool<PrototypeId>.Instance.Return(stashesToSortInto);

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

            var entityManager = player.Game.EntityManager;
            List<ulong> allItemIds = ListPool<ulong>.Instance.Get();
            foreach (var entry in generalInventory)
            {
                allItemIds.Add(entry.Id);
            }

            HashSet<ulong> processedItemIds = HashSetPool<ulong>.Instance.Get();
            int itemsMoved = 0;

            // First Pass: Stackable Items
            foreach (ulong itemId in allItemIds)
            {
                Item item = entityManager.GetEntity<Item>(itemId);
                if (item == null || !item.CanStack() || item.IsEquipped) continue;

                if (TryMoveToMatchingStack(player, item, stashRefs))
                {
                    itemsMoved++;
                    processedItemIds.Add(itemId);
                }
            }

            // Item Collection & Sorting
            List<Item> itemsToSort = ListPool<Item>.Instance.Get();
            foreach (ulong itemId in allItemIds)
            {
                if (processedItemIds.Contains(itemId)) continue;

                Item item = entityManager.GetEntity<Item>(itemId);
                if (item != null && !item.IsEquipped)
                {
                    itemsToSort.Add(item);
                }
            }

            itemsToSort.Sort((a, b) =>
            {
                int categoryComparison = string.Compare(GetItemCategory(a), GetItemCategory(b), StringComparison.Ordinal);
                if (categoryComparison != 0) return categoryComparison;
                return string.Compare(GameDatabase.GetPrototypeName(a.PrototypeDataRef), GameDatabase.GetPrototypeName(b.PrototypeDataRef), StringComparison.Ordinal);
            });

            // Stash Categorization
            List<PrototypeId> avatarStashes = ListPool<PrototypeId>.Instance.Get();
            List<PrototypeId> craftingStashes = ListPool<PrototypeId>.Instance.Get();
            foreach (var stashRef in stashRefs)
            {
                var inventoryProto = GameDatabase.GetPrototype<InventoryPrototype>(stashRef);
                if (inventoryProto == null) continue;

                if (inventoryProto.Category == InventoryCategory.PlayerStashAvatarSpecific)
                    avatarStashes.Add(stashRef);
                else if (inventoryProto.Category == InventoryCategory.PlayerCraftingRecipes)
                    craftingStashes.Add(stashRef);
            }

            // Second Pass: Sorted Item Placement
            foreach (Item item in itemsToSort)
            {
                if (processedItemIds.Contains(item.Id)) continue;
                bool moved = false;

                if (avatarStashes.Count > 0)
                {
                    foreach (var avatarStashRef in avatarStashes)
                    {
                        Inventory stash = player.GetInventoryByRef(avatarStashRef);
                        if (stash == null) continue;
                        string stashProtoName = GameDatabase.GetPrototypeName(stash.PrototypeDataRef);
                        string avatarName = ExtractAvatarNameFromStash(stashProtoName);
                        if (!string.IsNullOrEmpty(avatarName) && IsItemSuitableForAvatar(item, avatarName))
                        {
                            if (TryMoveItemToStash(player, item, avatarStashRef, true))
                            {
                                moved = true;
                                break;
                            }
                        }
                    }
                }

                if (moved)
                {
                    itemsMoved++;
                    processedItemIds.Add(item.Id);
                    continue;
                }

                if (GetItemCategory(item) == "Crafting" && craftingStashes.Count > 0)
                {
                    foreach (var craftingStashRef in craftingStashes)
                    {
                        if (TryMoveItemToStash(player, item, craftingStashRef, false))
                        {
                            moved = true;
                            break;
                        }
                    }
                }

                if (moved)
                {
                    itemsMoved++;
                    processedItemIds.Add(item.Id);
                    continue;
                }

                if (TryMoveToCategoryStash(player, item, stashRefs))
                {
                    itemsMoved++;
                    processedItemIds.Add(item.Id);
                }
            }

            // Cleanup
            ListPool<ulong>.Instance.Return(allItemIds);
            HashSetPool<ulong>.Instance.Return(processedItemIds);
            ListPool<Item>.Instance.Return(itemsToSort);
            ListPool<PrototypeId>.Instance.Return(avatarStashes);
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
            string category = GetItemCategory(item);

            if (!_categoryStashMap.TryGetValue(category, out List<PrototypeId> categoryStashes))
            {
                categoryStashes = ListPool<PrototypeId>.Instance.Get();
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

        private void ClearCategoryStashMap()
        {
            foreach (var list in _categoryStashMap.Values)
            {
                ListPool<PrototypeId>.Instance.Return(list);
            }
            _categoryStashMap.Clear();
        }

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

        private string ExtractAvatarNameFromStash(string stashProtoName)
        {
            if (stashProtoName.Contains("PlayerStashForAvatar"))
            {
                int startIndex = stashProtoName.IndexOf("PlayerStashForAvatar") + "PlayerStashForAvatar".Length;
                if (startIndex < stashProtoName.Length)
                {
                    string avatarName = stashProtoName.Substring(startIndex);
                    avatarName = avatarName.Replace("/", "").Trim();
                    return avatarName;
                }
            }
            return string.Empty;
        }

        private bool IsItemSuitableForAvatar(Item item, string avatarName)
        {
            if (item == null || string.IsNullOrEmpty(avatarName))
                return false;

            string itemProtoName = GameDatabase.GetPrototypeName(item.PrototypeDataRef).ToLowerInvariant();

            if (AvatarNameVariations.TryGetValue(avatarName, out List<string> variations))
            {
                foreach (string variation in variations)
                {
                    if (itemProtoName.Contains(variation))
                    {
                        return true;
                    }
                }
            }

            string baseAvatarName = avatarName.Replace(".prototype", "").ToLowerInvariant();
            return itemProtoName.Contains(baseAvatarName);
        }

        #endregion
    }
}