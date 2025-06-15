using Gazillion;
using MHServerEmu.Billing.Catalogs;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Network;
using MHServerEmu.Frontend;
using MHServerEmu.Games;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.MTXStore;
using MHServerEmu.Games.Network;
using MHServerEmu.PlayerManagement;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MHServerEmu.Billing
{
    public class BillingService : IGameService
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly string BillingDataDirectory = Path.Combine(FileHelper.DataDirectory, "Billing");

        private readonly Catalog _catalog;

        public BillingService()
        {
            var config = ConfigManager.Instance.GetConfig<BillingConfig>();
            _catalog = FileHelper.DeserializeJson<Catalog>(Path.Combine(BillingDataDirectory, "Catalog.json"));

            if (config.ApplyCatalogPatch)
            {
                string patchPath = Path.Combine(BillingDataDirectory, "CatalogPatch.json");
                if (File.Exists(patchPath))
                {
                    CatalogEntry[] catalogPatch = FileHelper.DeserializeJson<CatalogEntry[]>(patchPath);
                    _catalog.ApplyPatch(catalogPatch);
                }
            }

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
        }

        #region IGameService Implementation

        public void Run() { }
        public void Shutdown() { }

        public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage
        {
            switch (message)
            {
                case GameServiceProtocol.RouteMessage routeMailboxMessage:
                    OnRouteMailboxMessage(routeMailboxMessage);
                    break;
                default:
                    Logger.Warn($"ReceiveServiceMessage(): Unhandled service message type {typeof(T).Name}");
                    break;
            }
        }

        public string GetStatus() => $"Catalog Entries: {_catalog.Entries.Length}";

        private void OnRouteMailboxMessage(in GameServiceProtocol.RouteMessage routeMailboxMessage)
        {
            FrontendClient client = (FrontendClient)routeMailboxMessage.Client;
            PlayerManagerService playerManager = ServerManager.Instance.GetGameService(ServerType.PlayerManager) as PlayerManagerService;
            Game game = playerManager.GetGameByPlayer(client);
            if (game == null) return;
            PlayerConnection playerConnection = game.NetworkManager.GetNetClient(client);
            if (playerConnection == null) return;
            Player player = playerConnection.Player;

            switch ((ClientToGameServerMessage)routeMailboxMessage.Message.Id)
            {
                case ClientToGameServerMessage.NetMessageGetCatalog: OnGetCatalog(player, routeMailboxMessage.Message); break;
                case ClientToGameServerMessage.NetMessageGetCurrencyBalance: OnGetCurrencyBalance(player, routeMailboxMessage.Message); break;
                case ClientToGameServerMessage.NetMessageBuyItemFromCatalog: OnBuyItemFromCatalog(player, routeMailboxMessage.Message); break;
                default: Logger.Warn($"Handle(): Unhandled {(ClientToGameServerMessage)routeMailboxMessage.Message.Id} [{routeMailboxMessage.Message.Id}]"); break;
            }
        }

        #endregion

        private bool OnGetCatalog(Player player, MailboxMessage message)
        {
            var getCatalog = message.As<NetMessageGetCatalog>();
            if (getCatalog == null) return Logger.WarnReturn(false, "OnGetCatalog(): Failed to retrieve message");

            if (getCatalog.TimestampSeconds == _catalog.TimestampSeconds && getCatalog.TimestampMicroseconds == _catalog.TimestampMicroseconds)
                return true;

            player.SendMessage(_catalog.ToNetMessageCatalogItems(false));
            return true;
        }

        private void OnGetCurrencyBalance(Player player, MailboxMessage message)
        {
            player.SendMessage(NetMessageGetCurrencyBalanceResponse.CreateBuilder()
                .SetCurrencyBalance(player.GazillioniteBalance)
                .Build());
        }

        private bool OnBuyItemFromCatalog(Player player, MailboxMessage message)
        {
            var buyItemFromCatalog = message.As<NetMessageBuyItemFromCatalog>();
            if (buyItemFromCatalog == null) return Logger.WarnReturn(false, "OnBuyItemFromCatalog(): Failed to retrieve message");

            long skuId = buyItemFromCatalog.SkuId;
            BuyItemResultErrorCodes result = BuyItem(player, skuId);
            SendBuyItemResponse(player, result, skuId);
            return true;
        }

        private BuyItemResultErrorCodes BuyItem(Player player, long skuId)
        {
            // --- 1. Validation ---
            if (!player.HasFinishedTutorial())
                return BuyItemResultErrorCodes.BUY_RESULT_ERROR_UNKNOWN;

            CatalogEntry entry = _catalog.GetEntry(skuId);
            if (entry == null || (entry.GuidItems.Length == 0 && entry.AdditionalGuidItems.Length == 0))
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