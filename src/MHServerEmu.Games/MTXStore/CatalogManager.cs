using Gazillion;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.MTXStore.Catalogs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MHServerEmu.Games.MTXStore
{
    public class CatalogManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly string BillingDataDirectory = Path.Combine(FileHelper.DataDirectory, "Billing");

        private Catalog _catalog;

        public static CatalogManager Instance { get; } = new();

        private CatalogManager() { }

        public bool Initialize()
        {
            if (_catalog != null)
                return true;

            var config = ConfigManager.Instance.GetConfig<BillingConfig>();

            _catalog = FileHelper.DeserializeJson<Catalog>(Path.Combine(BillingDataDirectory, "Catalog.json"));

            // Apply a patch to the catalog if it's enabled and there's one
            if (config.ApplyCatalogPatch)
            {
                string patchPath = Path.Combine(BillingDataDirectory, "CatalogPatch.json");
                if (File.Exists(patchPath))
                {
                    CatalogEntry[] catalogPatch = FileHelper.DeserializeJson<CatalogEntry[]>(patchPath);
                    _catalog.ApplyPatch(catalogPatch);
                }
            }

            // Override store urls if enabled
            if (config.OverrideStoreUrls)
            {
                _catalog.Urls[0].StoreHomePageUrl = config.StoreHomePageUrl;
                _catalog.Urls[0].StoreBannerPageUrls[0].Url = config.StoreHomeBannerPageUrl;
                _catalog.Urls[0].StoreBannerPageUrls[1].Url = config.StoreHeroesBannerPageUrl;
                _catalog.Urls[0].StoreBannerPageUrls[2].Url = config.StoreCostumesBannerPageUrl;
                _catalog.Urls[0].StoreBannerPageUrls[3].Url = config.StoreBoostsBannerPageUrl;
                _catalog.Urls[0].StoreBannerPageUrls[4].Url = config.StoreChestsBannerPageUrl;
                _catalog.Urls[0].StoreBannerPageUrls[5].Url = config.StoreSpecialsBannerPageUrl;
                _catalog.Urls[0].StoreRealMoneyUrl = config.StoreRealMoneyUrl;
            }

            Logger.Info($"Initialized store catalog with {_catalog.Entries.Length} entries");

            return true;
        }

        #region Message Handling

        public bool OnGetCatalog(Player player, NetMessageGetCatalog getCatalog)
        {
            // Bail out if the client already has an up to date catalog
            if (getCatalog.TimestampSeconds == _catalog.TimestampSeconds && getCatalog.TimestampMicroseconds == _catalog.TimestampMicroseconds)
                return true;

            // Send the current catalog
            player.SendMessage(_catalog.ToNetMessageCatalogItems(false));
            return true;
        }

        public bool OnGetCurrencyBalance(Player player)
        {
            player.SendMessage(NetMessageGetCurrencyBalanceResponse.CreateBuilder()
                .SetCurrencyBalance(player.GazillioniteBalance)
                .Build());

            return true;
        }

        public bool OnBuyItemFromCatalog(Player player, NetMessageBuyItemFromCatalog buyItemFromCatalog)
        {
            long skuId = buyItemFromCatalog.SkuId;
            BuyItemResultErrorCodes result = BuyItem(player, skuId);
            SendBuyItemResponse(player, result, skuId);
            return true;
        }

        #endregion

        private BuyItemResultErrorCodes BuyItem(Player player, long skuId)
        {
            // --- 1. Validation ---
            if (!player.HasFinishedTutorial())
                return BuyItemResultErrorCodes.BUY_RESULT_ERROR_UNKNOWN;

            CatalogEntry entry = _catalog.GetEntry(skuId);
            if (entry == null || (entry.GuidItems.Length == 0 && entry.AdditionalGuidItems.Length == 0))
                return BuyItemResultErrorCodes.BUY_RESULT_ERROR_UNKNOWN;

            // Bundles don't work properly yet, so disable them for now
            if (entry.Type?.Name == "Bundle")
                return BuyItemResultErrorCodes.BUY_RESULT_ERROR_UNKNOWN;

            if (entry.LocalizedEntries.IsNullOrEmpty())
                return BuyItemResultErrorCodes.BUY_RESULT_ERROR_UNKNOWN;

            LocalizedCatalogEntry localizedEntry = entry.LocalizedEntries[0];
            long itemPrice = localizedEntry.ItemPrice;
            long balance = player.GazillioniteBalance;

            if (itemPrice > balance)
                return BuyItemResultErrorCodes.BUY_RESULT_ERROR_INSUFFICIENT_BALANCE;

            // --- 2. Collect All Items and Quantities ---
            var itemsToGrant = new Dictionary<PrototypeId, int>();
            Action<CatalogGuidEntry> collectItem = (guidItem) => {
                var protoId = (PrototypeId)guidItem.ItemPrototypeRuntimeIdForClient;
                if (itemsToGrant.ContainsKey(protoId))
                    itemsToGrant[protoId] += guidItem.Quantity;
                else
                    itemsToGrant.Add(protoId, guidItem.Quantity);
            };

            foreach (var guidItem in entry.GuidItems) collectItem(guidItem);
            foreach (var guidItem in entry.AdditionalGuidItems) collectItem(guidItem);

            // --- 3. Fulfill Purchase ---
            foreach (var (protoId, quantity) in itemsToGrant)
            {
                Prototype catalogItemProto = protoId.As<Prototype>();
                if (catalogItemProto == null)
                {
                    Logger.Warn($"BuyItem(): Could not find prototype for ID {protoId} in SkuId {skuId}");
                    return BuyItemResultErrorCodes.BUY_RESULT_ERROR_UNKNOWN;
                }

                BuyItemResultErrorCodes fulfillmentResult = BuyItemResultErrorCodes.BUY_RESULT_ERROR_UNKNOWN;

                switch (catalogItemProto)
                {
                    case ItemPrototype itemProto:
                        bool allGranted = true;
                        for (int i = 0; i < quantity; i++)
                        {
                            if (!player.Game.LootManager.GiveItem(itemProto.DataRef, LootContext.CashShop, player))
                            {
                                Logger.Warn($"BuyItem(): Failed to grant item {itemProto.DataRef.GetName()} (iteration {i + 1}/{quantity}) for SkuId {skuId}.");
                                allGranted = false;
                                break;
                            }
                        }
                        if (allGranted)
                            fulfillmentResult = BuyItemResultErrorCodes.BUY_RESULT_ERROR_SUCCESS;
                        break;

                    case PlayerStashInventoryPrototype siProto:
                        if (player.UnlockInventory(siProto.DataRef) || player.IsInventoryUnlocked(siProto.DataRef))
                            fulfillmentResult = BuyItemResultErrorCodes.BUY_RESULT_ERROR_SUCCESS;
                        break;

                    case AvatarPrototype avatarProto:
                        if (player.HasAvatarFullyUnlocked(avatarProto.DataRef))
                            fulfillmentResult = BuyItemResultErrorCodes.BUY_RESULT_ERROR_ALREADY_HAVE_AVATAR;
                        else if (player.UnlockAvatar(avatarProto.DataRef, true))
                            fulfillmentResult = BuyItemResultErrorCodes.BUY_RESULT_ERROR_SUCCESS;
                        break;

                    case AgentTeamUpPrototype teamUpProto:
                        if (player.IsTeamUpAgentUnlocked(teamUpProto.DataRef))
                            fulfillmentResult = BuyItemResultErrorCodes.BUY_RESULT_ERROR_ALREADY_HAVE_AVATAR;
                        else if (player.UnlockTeamUpAgent(teamUpProto.DataRef, true))
                            fulfillmentResult = BuyItemResultErrorCodes.BUY_RESULT_ERROR_SUCCESS;
                        break;

                    case PowerSpecPrototype powerSpecProto:
                        if (player.UnlockPowerSpecIndex(powerSpecProto.Index))
                            fulfillmentResult = BuyItemResultErrorCodes.BUY_RESULT_ERROR_SUCCESS;
                        break;

                    default:
                        Logger.Warn($"BuyItem(): Unimplemented catalog item type {catalogItemProto.GetType().Name} for {catalogItemProto}", LogCategory.MTXStore);
                        break;
                }

                if (fulfillmentResult != BuyItemResultErrorCodes.BUY_RESULT_ERROR_SUCCESS)
                {
                    Logger.Warn($"BuyItem(): Failed to fulfill item {catalogItemProto.DataRef.GetName()} (Quantity: {quantity}) for SkuId {skuId}. Error: {fulfillmentResult}. Halting transaction.");
                    return fulfillmentResult;
                }
            }

            // --- 4. Finalization ---
            balance = Math.Max(balance - itemPrice, 0);
            player.GazillioniteBalance = balance;
            var grantedItemsString = string.Join(", ", itemsToGrant.Select(kvp => {
                return $"{kvp.Key.GetName() ?? "Unknown"} x{kvp.Value}";
            }));
            Logger.Trace($"OnBuyItemFromCatalog(): Player [{player}] purchased [skuId={skuId}, itemPrice={itemPrice}]. Granted: [{grantedItemsString}]. New Balance={balance}", LogCategory.MTXStore);

            return BuyItemResultErrorCodes.BUY_RESULT_ERROR_SUCCESS;
        }

        private void SendBuyItemResponse(Player player, BuyItemResultErrorCodes errorCode, long skuId)
        {
            player.SendMessage(NetMessageBuyItemFromCatalogResponse.CreateBuilder()
                .SetDidSucceed(errorCode == BuyItemResultErrorCodes.BUY_RESULT_ERROR_SUCCESS)
                .SetCurrentCurrencyBalance(player.GazillioniteBalance)
                .SetErrorcode(errorCode)
                .SetSkuId(skuId)
                .Build());
        }
    }
}