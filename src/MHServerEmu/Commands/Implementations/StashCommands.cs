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

            List<PrototypeId> stashRefs = ListPool<PrototypeId>.Instance.Get();
            if (!playerConnection.Player.GetStashInventoryProtoRefs(stashRefs, false, true))
            {
                ListPool<PrototypeId>.Instance.Return(stashRefs);
                return "No stash tabs available";
            }

            _categoryStashMap.Clear();
            int itemsMoved = SortItems(playerConnection.Player, stashRefs);
            ListPool<PrototypeId>.Instance.Return(stashRefs);

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
                if (TryMoveToMatchingStack(player, item, stashRefs))
                {
                    itemsMoved++;
                    processedItems.Add(itemId);
                }
            }

            // Second pass: Handle avatar-bound items
            if (avatarStashes.Count > 0)
            {
                foreach (ulong itemId in currentItems)
                {
                    if (processedItems.Contains(itemId)) continue;
                    Item item = entityManager.GetEntity<Item>(itemId);
                    if (item == null || item.IsEquipped || !item.IsBoundToCharacter) continue;
                    foreach (var avatarStashRef in avatarStashes)
                    {
                        if (TryMoveItemToStash(player, item, avatarStashRef))
                        {
                            itemsMoved++;
                            processedItems.Add(itemId);
                            break;
                        }
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
                        foreach (var craftingStashRef in craftingStashes)
                        {
                            if (TryMoveItemToStash(player, item, craftingStashRef))
                            {
                                itemsMoved++;
                                processedItems.Add(itemId);
                                break;
                            }
                        }
                    }
                }
            }

            // Final pass: Categorize and move all remaining items into general stashes
            if (generalStashes.Count > 0)
            {
                foreach (ulong itemId in currentItems)
                {
                    if (processedItems.Contains(itemId)) continue;
                    Item item = entityManager.GetEntity<Item>(itemId);
                    if (item == null || item.IsEquipped) continue;
                    if (TryMoveToCategoryStash(player, item, generalStashes))
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
            // Cache the EntityManager for performance
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
                        item.SetStatus(EntityStatus.SkipItemBindingCheck, true);
                        bool success = player.TryInventoryMove(item.Id, stash.OwnerId, stash.PrototypeDataRef, entry.Slot);
                        item.SetStatus(EntityStatus.SkipItemBindingCheck, false);

                        if (success)
                        {
                            Logger.Debug($"Stacked item '{itemName}' onto existing stack in stash '{GameDatabase.GetPrototypeName(stash.PrototypeDataRef)}'");
                            return true;
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
            Logger.Debug($"Processing item '{itemName}' of category '{category}'");

            if (!_categoryStashMap.TryGetValue(category, out List<PrototypeId> categoryStashes))
            {
                categoryStashes = new List<PrototypeId>();
                _categoryStashMap[category] = categoryStashes;
            }

            // Try to get existing category stash
            foreach (PrototypeId stashRef in categoryStashes)
            {
                if (TryMoveItemToStash(player, item, stashRef))
                    return true;
            }

            // Find new empty stash for category
            foreach (PrototypeId stashRef in stashRefs)
            {
                if (categoryStashes.Contains(stashRef)) continue;

                Inventory stash = player.GetInventoryByRef(stashRef);
                if (stash == null || !IsStashEmpty(stash)) continue;

                categoryStashes.Add(stashRef);
                return TryMoveItemToStash(player, item, stashRef);
            }

            // Fall back to any available stash
            foreach (PrototypeId stashRef in stashRefs)
            {
                if (TryMoveItemToStash(player, item, stashRef))
                {
                    if (!categoryStashes.Contains(stashRef))
                        categoryStashes.Add(stashRef);
                    return true;
                }
            }

            return false;
        }

        private bool TryMoveItemToStash(Player player, Item item, PrototypeId stashRef)
        {
            Inventory stash = player.GetInventoryByRef(stashRef);
            if (stash == null) return false;

            uint targetSlot = stash.GetFreeSlot(item, true, true);
            if (targetSlot == Inventory.InvalidSlot) return false;

            item.SetStatus(EntityStatus.SkipItemBindingCheck, true);
            bool success = player.TryInventoryMove(item.Id, stash.OwnerId, stash.PrototypeDataRef, targetSlot);
            item.SetStatus(EntityStatus.SkipItemBindingCheck, false);

            return success;
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

            // Cache the EntityManager for performance
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

                item.SetStatus(EntityStatus.SkipItemBindingCheck, true);
                if (player.TryInventoryMove(itemId, generalInventory.OwnerId, generalInventory.PrototypeDataRef, nextSlot))
                {
                    itemsCompacted++;
                    nextSlot++;
                }
                item.SetStatus(EntityStatus.SkipItemBindingCheck, false);
            }

            ListPool<ulong>.Instance.Return(currentItems);
            return $"Compacted {itemsCompacted} items in inventory";
        }
    }
}