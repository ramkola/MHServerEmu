using System.Buffers.Text;
using System.Diagnostics;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Serialization;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Properties;
using System;
using System.Collections.Generic; // Needed for List
using System.Linq; // Needed for ToList
using static MHServerEmu.Games.Entities.Inventories.Inventory;
using System.Text;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("item")]
    [CommandGroupDescription("Commands for managing items.")]
    public class ItemCommands : CommandGroup
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        [Command("drop")]
        [CommandDescription("Creates and drops the specified item from the current avatar.")]
        [CommandUsage("item drop [pattern] [count]")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Drop(string[] @params, NetClient client)
        {
            PrototypeId itemProtoRef = CommandHelper.FindPrototype(HardcodedBlueprints.Item, @params[0], client);
            if (itemProtoRef == PrototypeId.Invalid) return string.Empty;

            if (@params.Length == 1 || int.TryParse(@params[1], out int count) == false)
                count = 1;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;
            Avatar avatar = player.CurrentAvatar;

            LootManager lootManager = playerConnection.Game.LootManager;

            for (int i = 0; i < count; i++)
            {
                lootManager.SpawnItem(itemProtoRef, LootContext.Drop, player, avatar);
                Logger.Debug($"DropItem(): {itemProtoRef.GetName()} from {avatar}");
            }

            return string.Empty;
        }

        [Command("give")]
        [CommandDescription("Creates and gives the specified item to the current player.")]
        [CommandUsage("item give [pattern] [count]")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Give(string[] @params, NetClient client)
        {
            PrototypeId itemProtoRef = CommandHelper.FindPrototype(HardcodedBlueprints.Item, @params[0], client);
            if (itemProtoRef == PrototypeId.Invalid) return string.Empty;

            if (@params.Length == 1 || int.TryParse(@params[1], out int count) == false)
                count = 1;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            LootManager lootGenerator = playerConnection.Game.LootManager;

            for (int i = 0; i < count; i++)
                lootGenerator.GiveItem(itemProtoRef, LootContext.Drop, player);
            Logger.Debug($"GiveItem(): {itemProtoRef.GetName()}[{count}] to {player}");

            return string.Empty;
        }

        [Command("destroyindestructible")]
        [CommandDescription("Destroys indestructible items contained in the player's general inventory.")]
        [CommandUsage("item destroyindestructible")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string DestroyIndestructible(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;
            Inventory general = player.GetInventory(InventoryConvenienceLabel.General);

            List<Item> indestructibleItemList = new();
            foreach (var entry in general)
            {
                Item item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                if (item == null) continue;

                if (item.ItemPrototype.CanBeDestroyed == false)
                    indestructibleItemList.Add(item);
            }

            foreach (Item item in indestructibleItemList)
                item.Destroy();

            return $"Destroyed {indestructibleItemList.Count} indestructible items.";
        }

        [Command("roll")]
        [CommandDescription("Rolls the specified loot table.")]
        [CommandUsage("item roll [pattern]")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string RollLootTable(string[] @params, NetClient client)
        {
            PrototypeId lootTableProtoRef = CommandHelper.FindPrototype(HardcodedBlueprints.LootTable, @params[0], client);
            if (lootTableProtoRef == PrototypeId.Invalid) return string.Empty;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            player.Game.LootManager.TestLootTable(lootTableProtoRef, player);

            return $"Finished rolling {lootTableProtoRef.GetName()}, see the server console for results.";
        }

        [Command("rollall")]
        [CommandDescription("Rolls all loot tables.")]
        [CommandUsage("item rollall")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string RollAllLootTables(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            int numLootTables = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();

            foreach (PrototypeId lootTableProtoRef in DataDirectory.Instance.IteratePrototypesInHierarchy<LootTablePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                player.Game.LootManager.TestLootTable(lootTableProtoRef, player);
                numLootTables++;
            }

            stopwatch.Stop();

            return $"Finished rolling {numLootTables} loot tables in {stopwatch.Elapsed.TotalMilliseconds} ms, see the server console for results.";
        }

        [Command("creditchest")]
        [CommandDescription("Converts credits to a specified number of sellable chest items. Each chest costs 500k.")]
        [CommandUsage("item creditchest [count]")] // Optional count
        [CommandInvokerType(CommandInvokerType.Client)]
        public string CreditChest(string[] @params, NetClient client)
        {
            const PrototypeId CreditItemProtoRef = (PrototypeId)13983056721138685632; // Entity/Items/Crafting/Ingredients/CreditItem500k.prototype
            const int CreditItemPrice = 500000; // Cost per chest

            int requestedChests = 1; // Default to 1 chest

            if (@params.Length > 0)
            {
                if (!int.TryParse(@params[0], out requestedChests) || requestedChests <= 0)
                {
                    return "Invalid count specified. Please provide a positive number or omit for 1 chest.";
                }
            }

            PlayerConnection playerConnection = (PlayerConnection)client;
            if (playerConnection == null)
            {
                Logger.Error("CreditChest: PlayerConnection is null.");
                return "Error: Could not establish player connection.";
            }
            Player player = playerConnection.Player;
            if (player == null)
            {
                Logger.Error("CreditChest: Player entity is null.");
                return "Error: Could not retrieve player information.";
            }

            CurrencyPrototype creditsProto = GameDatabase.CurrencyGlobalsPrototype.CreditsPrototype;
            if (creditsProto == null)
            {
                Logger.Error("CreditChest: CreditsPrototype is null in CurrencyGlobalsPrototype. Cannot proceed.");
                return "Error: Server configuration issue with credits definition.";
            }
            PropertyId creditsProperty = new(PropertyEnum.Currency, creditsProto.DataRef);

            int chestsCreated = 0;
            long totalCreditsSpent = 0;

            for (int i = 0; i < requestedChests; i++)
            {
                PropertyValue currentCreditsPropVal = player.Properties[creditsProperty];
                long currentCredits = 0;

                // Attempt to cast/convert PropertyValue to long.
                // If PropertyValue is a default struct or doesn't hold a long, this will fail.
                try
                {
                    currentCredits = (long)currentCreditsPropVal;
                }
                catch (Exception ex) // Catch potential conversion errors (InvalidCastException or others)
                {
                    Logger.Error($"CreditChest: Could not convert credits PropertyValue to long for player {player.GetName()}. Value: '{currentCreditsPropVal}'. Error: {ex.Message}. Assuming 0 credits.");
                    currentCredits = 0;
                }

                if (currentCredits < CreditItemPrice)
                {
                    if (chestsCreated > 0)
                    {
                        return $"Created {chestsCreated} chest(s). Not enough credits for more (needed {CreditItemPrice:N0}, have {currentCredits:N0}).";
                    }
                    return $"You need at least {CreditItemPrice:N0} credits to create a chest. You have {currentCredits:N0}.";
                }

                player.Properties.AdjustProperty(-CreditItemPrice, creditsProperty);
                totalCreditsSpent += CreditItemPrice;

                player.Game.LootManager.GiveItem(CreditItemProtoRef, LootContext.CashShop, player);
                chestsCreated++;
                Logger.Trace($"CreditChest(): {player.GetName()} created chest #{i + 1}. Credits deducted: {CreditItemPrice}.");
            }

            PropertyValue finalCreditsPropVal = player.Properties[creditsProperty];
            long finalCredits = 0;
            try
            {
                finalCredits = (long)finalCreditsPropVal;
            }
            catch (Exception ex)
            {
                Logger.Error($"CreditChest: Could not convert final credits PropertyValue to long for player {player.GetName()}. Value: '{finalCreditsPropVal}'. Error: {ex.Message}. Assuming 0 credits for final display.");
                finalCredits = 0;
            }

            if (chestsCreated > 0)
            {
                return $"Successfully created {chestsCreated} Credit Chest(s). Total credits spent: {totalCreditsSpent:N0}. (Credits remaining: {finalCredits:N0})";
            }
            else
            {
                return "No credit chests were created (or an error occurred).";
            }
        }
        [Command("search")]
        [CommandDescription("Searches for an item in your stash tabs and tells you which tab it's in.")]
        [CommandUsage("item search [item name pattern]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string SearchItemInStashes(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            if (playerConnection == null) return "Failed to get player connection.";

            Player player = playerConnection.Player;
            if (player == null) return "Failed to get player entity.";

            string searchPattern = @params[0];
            if (string.IsNullOrWhiteSpace(searchPattern))
            {
                return "Please specify an item name pattern to search for.";
            }

            StringBuilder resultsBuilder = new StringBuilder();
            bool foundItemsOverall = false;

            List<PrototypeId> stashProtoRefs = new List<PrototypeId>();
            // Get all unlocked stash tabs (the 'true' for getUnlocked means only unlocked tabs)
            player.GetStashInventoryProtoRefs(stashProtoRefs, false, true);

            if (!stashProtoRefs.Any())
            {
                return "You have no unlocked stash tabs to search.";
            }

            EntityManager entityManager = player.Game.EntityManager;

            foreach (PrototypeId stashProtoRef in stashProtoRefs)
            {
                Inventory stash = player.GetInventoryByRef(stashProtoRef);
                if (stash == null) continue;

                // Get stash display name.
                // For custom names, the Player class would need a public getter for its _stashTabOptionsDict.
                // As _stashTabOptionsDict is private, we'll use the stash inventory's prototype name.
                // If you modify Player.cs to expose a method like `TryGetStashTabDisplayName(PrototypeId, out string)`,
                // you could use it here for more user-friendly tab names.
                string stashDisplayName = GameDatabase.GetPrototypeName(stash.PrototypeDataRef);

                List<string> foundItemsInThisStashList = new List<string>();

                foreach (var invEntry in stash) // Inventory.Enumerator.Entry
                {
                    Item item = entityManager.GetEntity<Item>(invEntry.Id);
                    if (item == null) continue;

                    string currentItemName = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
                    // Case-insensitive partial match
                    if (currentItemName.IndexOf(searchPattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string suffix = item.CurrentStackSize > 1 ? $" (x{item.CurrentStackSize})" : "";
                        foundItemsInThisStashList.Add($"- {currentItemName}{suffix}");
                    }
                }

                if (foundItemsInThisStashList.Any())
                {
                    if (foundItemsOverall) // Add a newline separator if this isn't the first stash with results
                    {
                        resultsBuilder.AppendLine();
                    }
                    resultsBuilder.AppendLine($"In Stash '{stashDisplayName}':");
                    foreach (string itemNameEntry in foundItemsInThisStashList)
                    {
                        resultsBuilder.AppendLine(itemNameEntry);
                    }
                    foundItemsOverall = true;
                }
            }

            if (foundItemsOverall)
            {
                return $"Search results for '{searchPattern}':\n{resultsBuilder.ToString()}";
            }
            else
            {
                return $"No items found matching '{searchPattern}' in your stashes.";
            }
        }
       
    }
}
