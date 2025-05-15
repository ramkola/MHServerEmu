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
            PlayerConnection playerConnection = (PlayerConnection)client;
            if (playerConnection == null) return "Failed to get player connection.";

            Player player = playerConnection.Player;
            if (player == null) return "Failed to get player entity.";

            string option = @params.Length > 0 ? @params[0].ToLowerInvariant() : "stashes";
            
            if (option == "filters")
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
            PlayerConnection playerConnection = (PlayerConnection)client;
            if (playerConnection == null) return "Failed to get player connection.";

            Player player = playerConnection.Player;
            if (player == null) return "Failed to get player entity.";

            string filter = @params.Length > 0 ? @params[0].ToLowerInvariant() : "all";
            string targetStash = @params.Length > 1 ? string.Join(" ", @params.Skip(1)) : "";

            return SortInventoryWithFilter(player, filter, targetStash);
        }

        [Command("internal")]
        [CommandDescription("Sorts items within the general inventory.")]
        [CommandUsage("stash internal")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string SortInternal(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            if (playerConnection == null) return "Failed to get player connection.";

            Player player = playerConnection.Player;
            if (player == null) return "Failed to get player entity.";

            return SortInventoryInternal(player);
        }

        private string ListAvailableFilters()
        {
            StringBuilder sb = new StringBuilder("Available filters for autosort:\n");
            
            // List of available filters
            List<string> filters = new List<string>
            {
                "all - All items",
                "artifacts - Artifact items",
                "crafting - Crafting materials",
                "insignias - Team insignias",
                "legendaries - Legendary items",
                "rings - Ring items",
                "runes - Rune items",
                "relics - Relic items",
                "uniques - Unique items",
                "slot1 - Slot 1 items",
                "slot2 - Slot 2 items",
                "slot3 - Slot 3 items",
                "slot4 - Slot 4 items",
                "slot5 - Slot 5 items"
            };
            
            // Format the filters in a compact way
            foreach (string filter in filters)
            {
                sb.AppendLine(filter);
            }
            
            sb.AppendLine("\nUsage examples:");
            sb.AppendLine("!stash sort all General01");
            sb.AppendLine("!stash sort uniques Wolverine");
            sb.AppendLine("!stash sort slot1 Doctor Doom");
            
            return sb.ToString();
        }

        private string ListAvailableStashTabs(Player player)
        {
            List<PrototypeId> stashRefs = new List<PrototypeId>();
            if (!player.GetStashInventoryProtoRefs(stashRefs, false, true))
            {
                return "No stash tabs found.";
            }

            // Group stashes by category
            Dictionary<string, List<string>> stashGroups = new Dictionary<string, List<string>>();
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
                        
                        // Check if stash has free space
                        bool hasSpace = StashHasFreeSpace(stash);
                        
                        // Only add stashes with free space
                        if (hasSpace)
                        {
                            // Try to get the StashTabOptions for this stash
                            StashTabOptions options = GetStashTabOptions(player, stash);
                            string displayName = options?.DisplayName;
                            
                            // If no custom display name, generate one from the prototype
                            if (string.IsNullOrEmpty(displayName))
                            {
                                string protoName = GameDatabase.GetPrototypeName(stash.PrototypeDataRef);
                                displayName = GenerateStashName(stash, protoName);
                            }
                            
                            // Decode any URL-encoded characters
                            displayName = DecodeUrlString(displayName);
                            
                            // Determine the group this stash belongs to
                            string groupName;
                            if (stash.Category == InventoryCategory.PlayerStashGeneral)
                                groupName = "General";
                            else if (stash.Category == InventoryCategory.PlayerCraftingRecipes)
                                groupName = "Crafting";
                            else if (stash.Category == InventoryCategory.PlayerStashTeamUpGear)
                                groupName = "TeamUp";
                            else if (stash.Category == InventoryCategory.PlayerStashAvatarSpecific)
                                groupName = "Avatar";
                            else
                                groupName = "Other";
                            
                            // Add to the appropriate group
                            if (!stashGroups.ContainsKey(groupName))
                                stashGroups[groupName] = new List<string>();
                            
                            stashGroups[groupName].Add(displayName);
                        }
                    }
                }
            }
            
            // Build the output string with groups
            StringBuilder sb = new StringBuilder("Available stash tabs with free space:\n");
            
            // Order of groups to display
            string[] groupOrder = { "General", "Crafting", "TeamUp", "Avatar", "Other" };
            
            foreach (string group in groupOrder)
            {
                if (stashGroups.ContainsKey(group) && stashGroups[group].Count > 0)
                {
                    sb.AppendLine($"\n{group}: {string.Join(", ", stashGroups[group])}");
                }
            }
            
            int availableStashes = stashGroups.Values.Sum(list => list.Count);
            sb.AppendLine($"\nShowing {availableStashes} stash tabs with free space (out of {totalStashes} total).");
            sb.AppendLine("\nUsage: !stash sort [filter] [stash_name]");
            sb.AppendLine("Use '!stash list filters' to see available filters.");
            
            return sb.ToString();
        }

        private string SortInventoryWithFilter(Player player, string filter, string targetStashName = "")
        {
            // Decode the target stash name if provided
            if (!string.IsNullOrEmpty(targetStashName))
            {
                targetStashName = DecodeUrlString(targetStashName);
                //Logger.Debug($"SortInventoryWithFilter: Target stash name after decode: '{targetStashName}'");
            }

            Inventory generalInventory = player.GetInventory(InventoryConvenienceLabel.General);
            if (generalInventory == null) return "Failed to find general inventory.";

            List<Inventory> stashInventories = new List<Inventory>();
            List<PrototypeId> stashRefs = new List<PrototypeId>();

            if (!player.GetStashInventoryProtoRefs(stashRefs, false, true))
            {
                Logger.Debug("SortInventoryWithFilter: GetStashInventoryProtoRefs returned false or no stash inventories defined.");
            }

            // Collect valid stash inventories and their display names
            Dictionary<string, Inventory> stashNameMap = new Dictionary<string, Inventory>(StringComparer.OrdinalIgnoreCase);
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
                        // Check if stash has free space
                        if (!StashHasFreeSpace(stash))
                            continue;

                        // Try to get the StashTabOptions for this stash
                        StashTabOptions options = GetStashTabOptions(player, stash);
                        string displayName = options?.DisplayName;
                        
                        // If no custom display name, generate one from the prototype
                        if (string.IsNullOrEmpty(displayName))
                        {
                            string protoName = GameDatabase.GetPrototypeName(stash.PrototypeDataRef);
                            displayName = GenerateStashName(stash, protoName);
                        }
                        
                        // Decode any URL-encoded characters
                        displayName = DecodeUrlString(displayName);
                        
                        // Add both the display name and the generated name as keys
                        string generatedName = GenerateStashName(stash, GameDatabase.GetPrototypeName(stash.PrototypeDataRef));
                        generatedName = DecodeUrlString(generatedName);
                        
                        //Logger.Debug($"SortInventoryWithFilter: Adding stash - DisplayName: '{displayName}', GeneratedName: '{generatedName}'");
                        
                        stashNameMap[displayName] = stash;
                        if (generatedName != displayName)
                            stashNameMap[generatedName] = stash; // Allow lookup by both names
                        
                        stashInventories.Add(stash);
                    }
                }
            }

            if (stashInventories.Count == 0) return "No available stash tabs found to sort into.";

            // If a specific stash name was requested, validate and use only that stash
            Inventory targetStash = null;
            if (!string.IsNullOrEmpty(targetStashName))
            {
                if (stashNameMap.TryGetValue(targetStashName, out Inventory foundStash))
                {
                    targetStash = foundStash;
                    stashInventories.Clear();
                    stashInventories.Add(targetStash);
                    
                    // Get the actual display name for logging
                    StashTabOptions options = GetStashTabOptions(player, targetStash);
                    string actualName = options?.DisplayName ?? targetStashName;
                    //Logger.Debug($"SortInventoryWithFilter: Using specific stash tab '{actualName}'");
                }
                else
                {
                    //Logger.Debug($"SortInventoryWithFilter: Stash tab '{targetStashName}' not found in available stashes.");
                    return $"Stash tab '{targetStashName}' not found. Use '!stash list stashes' to see available stash tabs.";
                }
            }

            int itemsMoved = 0;
            List<Item> itemsToMove = new List<Item>();

            // Collect items from general inventory that match the filter
            foreach (var entry in generalInventory)
            {
                Item item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                if (item != null && !item.IsEquipped)
                {
                    string itemName = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
                    // Apply filter
                    if (filter != "all" && !MatchesFilter(item, filter))
                    {
                        //Logger.Debug($"SortInventoryWithFilter: Item '{itemName}' did not match filter '{filter}'");
                        continue;
                    }
                    //Logger.Debug($"SortInventoryWithFilter: Item '{itemName}' matched filter '{filter}'");
                    itemsToMove.Add(item);
                }
            }

            // Sort the list of items before attempting to move them to stash
            if (itemsToMove.Any())
            {
                itemsToMove = itemsToMove
                    .OrderBy(item => GameDatabase.GetPrototypeName(item.PrototypeDataRef)) // Primary sort: Item Name
                    .ToList();
                //Logger.Debug($"SortInventoryWithFilter: Sorted {itemsToMove.Count} items from general inventory before moving to stash.");
            }

            foreach (Item item in itemsToMove)
            {
                if (item == null) continue;
                string itemName = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
                
                Inventory selectedStash = null;
                uint targetSlot = Inventory.InvalidSlot;

                foreach (Inventory stash in stashInventories)
                {
                    // Skip avatar-specific stashes unless the item is suitable for that avatar
                    if (stash.Category == InventoryCategory.PlayerStashAvatarSpecific)
                    {
                        string stashProtoName = GameDatabase.GetPrototypeName(stash.PrototypeDataRef);
                        string avatarName = ExtractAvatarNameFromStash(stashProtoName);
                        
                        //Logger.Debug($"SortInventoryWithFilter: Checking if item '{itemName}' is suitable for avatar '{avatarName}'");
                        
                        if (!string.IsNullOrEmpty(avatarName) && !IsItemSuitableForAvatar(item, avatarName))
                        {
                            //Logger.Debug($"SortInventoryWithFilter: Item '{itemName}' not suitable for avatar '{avatarName}'");
                            continue; // Skip this avatar stash if the item doesn't match
                        }
                        //Logger.Debug($"SortInventoryWithFilter: Item '{itemName}' is suitable for avatar '{avatarName}'");
                    }
                    
                    if (stash.PassesContainmentFilter(item.PrototypeDataRef) == InventoryResult.Success)
                    {
                        InventoryResult canPlaceResult = item.CanChangeInventoryLocation(stash);
                        if (canPlaceResult == InventoryResult.Success)
                        {
                            targetSlot = stash.GetFreeSlot(item, true, true);
                            if (targetSlot != Inventory.InvalidSlot)
                            {
                                selectedStash = stash;
                                break;
                            }
                        }
                        else
                        {
                            Logger.Debug($"SortInventoryWithFilter: Cannot place item '{itemName}' in stash. Reason: {canPlaceResult}");
                        }
                    }
                    else
                    {
                        Logger.Debug($"SortInventoryWithFilter: Item '{itemName}' failed containment filter for stash");
                    }
                }

                if (selectedStash != null && targetSlot != Inventory.InvalidSlot)
                {
                    ulong? stackEntityId = null;
                    InventoryResult moveResult = Inventory.ChangeEntityInventoryLocation(item, selectedStash, targetSlot, ref stackEntityId, true);

                    if (moveResult == InventoryResult.Success)
                    {
                        itemsMoved++;
                        
                        // Get stash name for logging
                        StashTabOptions options = GetStashTabOptions(player, selectedStash);
                        string stashName = options?.DisplayName ?? GameDatabase.GetPrototypeName(selectedStash.PrototypeDataRef);
                        stashName = DecodeUrlString(stashName);
                        
                        //Logger.Debug($"SortInventoryWithFilter: Moved {itemName} to {stashName}, Slot: {targetSlot}" + 
                        //    (stackEntityId.HasValue && stackEntityId.Value != Entity.InvalidId ? $" (Stacked on {stackEntityId.Value})" : ""));
                    }
                    else
                    {
                        Logger.Warn($"SortInventoryWithFilter: Failed to move {itemName}. Reason: {moveResult}");
                    }
                }
                else
                {
                    Logger.Debug($"SortInventoryWithFilter: No suitable stash found for item '{itemName}'");
                }
            }

            if (filter == "all" && string.IsNullOrEmpty(targetStashName))
                return $"Auto-sort complete. Moved {itemsMoved} item(s) to stash tabs.";
            else if (!string.IsNullOrEmpty(targetStashName))
                return $"Auto-sort complete. Moved {itemsMoved} {filter} item(s) to stash tab '{targetStashName}'.";
            else
                return $"Auto-sort complete. Moved {itemsMoved} {filter} item(s) to stash tabs.";
        }


        private string SortInventoryInternal(Player player)
        {
            Inventory generalInventory = player.GetInventory(InventoryConvenienceLabel.General);
            if (generalInventory == null) return "Failed to find general inventory.";

            // --- Step 1: Consolidate Stacks (Basic Implementation) ---
            int stacksConsolidatedCount = 0;
            List<Item> itemsToCheck = new List<Item>();
            foreach (var entry in generalInventory)
            {
                Item item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                if (item != null && item.CanStack() && item.CurrentStackSize < item.Properties[PropertyEnum.InventoryStackSizeMax])
                {
                    itemsToCheck.Add(item);
                }
            }

            HashSet<ulong> processedItemIds = new HashSet<ulong>();
            foreach (Item item in itemsToCheck)
            {
                if (item == null || !item.InventoryLocation.IsValid || processedItemIds.Contains(item.Id)) continue;

                foreach (var entry in generalInventory)
                {
                    Item targetItem = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                    if (targetItem != null && targetItem.Id != item.Id && item.CanStackOnto(targetItem))
                    {
                        ulong? stackEntityId = targetItem.Id;
                        InventoryResult stackResult = Inventory.ChangeEntityInventoryLocation(item, generalInventory, entry.Slot, ref stackEntityId, true);

                        if (stackResult == InventoryResult.Success)
                        {
                            stacksConsolidatedCount++;
                            processedItemIds.Add(item.Id);
                            Logger.Debug($"AutoSort Internal: Stacked {GameDatabase.GetPrototypeName(item.PrototypeDataRef)} onto item ID {targetItem.Id}.");
                            break;
                        }
                    }
                }
            }
            //Logger.Debug($"AutoSort Internal: Finished stack consolidation attempt for {player.GetName()}. Consolidated: {stacksConsolidatedCount}");

            // --- Step 2: Compact Inventory (Fill Gaps & Sort by Name) ---
            Logger.Debug($"AutoSort Internal: Starting compaction for {player.GetName()}...");
            List<ulong> currentItemIds = new List<ulong>();
            foreach (var entry in generalInventory)
            {
                currentItemIds.Add(entry.Id);
            }

            List<Item> itemsToReAdd = new List<Item>();
            EntityManager entityManager = player.Game.EntityManager;

            foreach (ulong itemId in currentItemIds)
            {
                Item item = entityManager.GetEntity<Item>(itemId);
                if (item != null)
                {
                    if (item.ChangeInventoryLocation(null) == InventoryResult.Success)
                    {
                        itemsToReAdd.Add(item);
                    }
                    else
                    {
                        Logger.Warn($"AutoSort Internal Compaction: Failed to remove item {GameDatabase.GetPrototypeName(item.PrototypeDataRef)} (ID: {itemId}) for compaction.");
                    }
                }
            }

            // Sort the list of removed items by Prototype Name
            if (itemsToReAdd.Any())
            {
                itemsToReAdd = itemsToReAdd
                    .OrderBy(item => GameDatabase.GetPrototypeName(item.PrototypeDataRef))
                    .ToList();
                //Logger.Debug($"AutoSort Internal: Sorted {itemsToReAdd.Count} items for re-adding.");
            }

            // Re-add items sequentially from slot 0 in the new sorted order
            int itemsCompacted = 0;
            uint nextSlot = 0;
            foreach (Item item in itemsToReAdd)
            {
                uint targetSlot = FindNextAvailableSlot(generalInventory, nextSlot);

                if (targetSlot == Inventory.InvalidSlot)
                {
                    Logger.Error($"AutoSort Internal Compaction: No space left in general inventory to re-add {GameDatabase.GetPrototypeName(item.PrototypeDataRef)}. Item potentially lost!");
                    // Consider sending to error recovery or notifying player more explicitly
                    continue;
                }

                ulong? stackEntityId = null;
                InventoryResult addResult = Inventory.ChangeEntityInventoryLocation(item, generalInventory, targetSlot, ref stackEntityId, false); // allowStacking = false

                if (addResult == InventoryResult.Success)
                {
                    itemsCompacted++;
                    nextSlot = targetSlot + 1;
                }
                else
                {
                    Logger.Warn($"AutoSort Internal Compaction: Failed to re-add item {GameDatabase.GetPrototypeName(item.PrototypeDataRef)} to slot {targetSlot}. Reason: {addResult}");
                }
            }
            Logger.Debug($"AutoSort Internal: Finished compaction for {player.GetName()}. Items re-added: {itemsCompacted}");

            return $"Inventory sorted internally. Consolidated {stacksConsolidatedCount} stacks, re-positioned {itemsCompacted} items by type.";
        }

        #region Helper Methods

        // Helper method to check if a stash has free space
        private bool StashHasFreeSpace(Inventory stash)
        {
            if (stash == null) return false;
            
            // Check if the stash has at least one free slot
            uint freeSlot = stash.GetFreeSlot(null, false);
            return freeSlot != Inventory.InvalidSlot;
        }

        // Helper method to find the next available slot in an inventory
        private uint FindNextAvailableSlot(Inventory inventory, uint startSlot)
        {
            int capacity = inventory.GetCapacity();
            uint maxSlot = (capacity == int.MaxValue) ? 1000 : (uint)capacity; // Using a reasonable upper search limit

            for (uint slot = startSlot; slot < maxSlot; slot++)
            {
                // Check if the slot is physically free in the inventory's internal map
                if (inventory.GetEntityInSlot(slot) == Entity.InvalidId)
                {
                    // Also verify with the owner if this slot is generally usable
                    if (inventory.Owner == null || inventory.Owner.ValidateInventorySlot(inventory, slot))
                    {
                        return slot;
                    }
                }
            }
            Logger.Warn($"FindNextAvailableSlot: No free slot found in {GameDatabase.GetPrototypeName(inventory.PrototypeDataRef)} starting from {startSlot} up to {maxSlot}");
            return Inventory.InvalidSlot;
        }

        // Helper method to get StashTabOptions for a stash
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


        // Helper method to generate a stash name from prototype
        private string GenerateStashName(Inventory stash, string protoName)
        {
            // Handle avatar-specific stash tabs
            if (protoName.Contains("PlayerStashForAvatar"))
            {
                // Extract the avatar name from PlayerStashForAvatar{AvatarName}
                int startIndex = protoName.IndexOf("PlayerStashForAvatar") + "PlayerStashForAvatar".Length;
                if (startIndex < protoName.Length)
                {
                    string avatarName = protoName.Substring(startIndex);
                    // Clean up any remaining path parts or special characters
                    avatarName = avatarName.Replace("/", "").Trim();
                    return avatarName; // Return the avatar name directly
                }
            }
            
            // Handle crafting stash tabs
            if (protoName.Contains("PlayerStashCrafting"))
            {
                // Extract the number from PlayerStashCrafting{01-06}
                int startIndex = protoName.IndexOf("PlayerStashCrafting") + "PlayerStashCrafting".Length;
                if (startIndex < protoName.Length)
                {
                    string number = protoName.Substring(startIndex).Replace("/", "").Trim();
                    return $"Crafting{number}";
                }
                return "Crafting";
            }
            
            // Handle general stash tabs
            if (protoName.Contains("PlayerStashGeneral"))
            {
                // Special cases
                if (protoName.Contains("EternitySplinter"))
                {
                    return "GeneralEternity";
                }
                if (protoName.Contains("Anniversary"))
                {
                    return "GeneralAnniversary";
                }
                
                // Extract the number from PlayerStashGeneral{01-37}
                int startIndex = protoName.IndexOf("PlayerStashGeneral") + "PlayerStashGeneral".Length;
                if (startIndex < protoName.Length)
                {
                    string number = protoName.Substring(startIndex).Replace("/", "").Trim();
                    if (int.TryParse(number, out _)) // Verify it's a number
                    {
                        return $"General{number}";
                    }
                }
                return "General";
            }
            
            // Handle team-up stash tabs
            if (protoName.Contains("PlayerStashTeamUpGeneral"))
            {
                // Extract the number from PlayerStashTeamUpGeneral01 and PlayerStashTeamUpGeneral02
                int startIndex = protoName.IndexOf("PlayerStashTeamUpGeneral") + "PlayerStashTeamUpGeneral".Length;
                if (startIndex < protoName.Length)
                {
                    string number = protoName.Substring(startIndex).Replace("/", "").Trim();
                    return $"TeamUp{number}";
                }
                return "TeamUp";
            }
            
            // Default case - use inventory category
            switch (stash.Category)
            {
                case InventoryCategory.PlayerStashGeneral:
                    return "General";
                case InventoryCategory.PlayerStashAvatarSpecific:
                    return "Avatar";
                case InventoryCategory.PlayerStashTeamUpGear:
                    return "TeamUp";
                case InventoryCategory.PlayerCraftingRecipes:
                    return "Crafting";
                case InventoryCategory.PlayerGeneralExtra:
                    return "Extra";
                default:
                    return "Stash";
            }
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
            Logger.Debug($"Checking item suitability - Item: {itemProtoName}, Avatar: {avatarName}");
            
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
                        //Logger.Debug($"Item matches avatar variation: {variation}");
                        return true;
                    }
                }
            }
            
            // If no specific mapping, just check if the item name contains the avatar name without .prototype
            string baseAvatarName = avatarName.Replace(".prototype", "").ToLowerInvariant();
            return itemProtoName.Contains(baseAvatarName);
        }


        // Helper method to check if an item matches a filter
        private bool MatchesFilter(Item item, string filter)
        {
            if (item == null) return false;

            string itemProtoName = GameDatabase.GetPrototypeName(item.PrototypeDataRef).ToLowerInvariant();
            
            switch (filter.ToLowerInvariant())
            {
                case "artifacts":
                    return itemProtoName.Contains("artifact");
                
                case "crafting":
                    return itemProtoName.Contains("crafting") || 
                           itemProtoName.Contains("material") || 
                           itemProtoName.Contains("ingredient");
                
                case "insignias":
                    return itemProtoName.Contains("insignia");
                
                case "legendaries":
                    return itemProtoName.Contains("legendary");
                
                case "rings":
                    return itemProtoName.Contains("ring");
                
                case "runes":
                    return itemProtoName.Contains("rune");
                
                case "relics":
                    return itemProtoName.Contains("relic");
                
                case "uniques":
                    return itemProtoName.Contains("unique");
                
                case "slot1":
                    return itemProtoName.Contains("/o1");
                case "slot2":
                    return itemProtoName.Contains("/o2");
                case "slot3":
                    return itemProtoName.Contains("/o3");
                case "slot4":
                    return itemProtoName.Contains("/o4");
                case "slot5":
                    return itemProtoName.Contains("/o5"); 
                default:
                    // If the filter doesn't match any predefined category, check if the item name contains the filter
                    return itemProtoName.Contains(filter);
            }
        }

        // Helper method to decode URL-encoded strings
        private string DecodeUrlString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            
            // Replace %20 with spaces
            input = input.Replace("%20", " ");
            // Add other replacements if needed
            input = input.Replace("%2F", "/");
            input = input.Replace("%3A", ":");
            input = input.Replace("%2D", "-");

            return input;
        }

        #endregion
    }
}
