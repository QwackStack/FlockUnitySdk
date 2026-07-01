using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Providers
{
    public enum PurchaseStatus
    {
        Started,
        Purchased,
        Failed
    }
    public enum TransactionType
    {
        //Status will change once validation/flow changes
        Purchase
    }
    public class FlockShopProvider : FlockProviderBase
    {
        private const string SnapshotCategory = "shop";

        // Full paginated response cache — page metadata (total/page/limit) isn't part of the Shop entity, so it isn't folded into _shopsById.
        private readonly Dictionary<string, PaginatedResponse<Shop>> _shopPages = new Dictionary<string, PaginatedResponse<Shop>>();

        // Canonical stores — every shop/item lives here exactly once, keyed by id.
        private readonly Dictionary<string, Shop> _shopsById = new Dictionary<string, Shop>();
        private readonly Dictionary<string, ShopItem> _itemsById = new Dictionary<string, ShopItem>();

        // Indexes into the canonical stores above — id (or id-list) per query, not duplicate objects.
        private readonly Dictionary<string, string> _shopIdByName = new Dictionary<string, string>();
        private readonly Dictionary<string, List<string>> _itemIdsByShop = new Dictionary<string, List<string>>();

        public FlockShopProvider(FlockClient client) : base(client) { }

        public void ClearCache()
        {
            _shopPages.Clear();
            _shopsById.Clear();
            _shopIdByName.Clear();
            _itemsById.Clear();
            _itemIdsByShop.Clear();
            DeleteSnapshotCategory(SnapshotCategory);
        }

        public async Task<PaginatedResponse<Shop>> GetAllAsync(
            int page = 1, int limit = 100, CancellationToken cancellationToken = default)
        {
            string pageKey = $"all_p{page}_l{limit}";
            if (_shopPages.TryGetValue(pageKey, out PaginatedResponse<Shop> cached))
                return cached;

            PaginatedResponse<Shop> shops = await FetchWithSnapshotAsync(
                SnapshotCategory, pageKey, async () =>
                {
                    return await FlockHttpClient.GetAsync<PaginatedResponse<Shop>>(
                        $"{Client.GetVersionedApiUrl()}/shop?page={page}&limit={limit}", Client.GetBaseHeaders(), cancellationToken);
                }, "Fetch shops", cancellationToken);

            if (shops != null)
            {
                _shopPages[pageKey] = shops;
                if (shops.Items != null)
                {
                    foreach (Shop shop in shops.Items)
                        IndexShop(shop);
                }
            }
            return shops;
        }

        public async Task<Shop> GetByIdAsync(
            string shopId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopId, "Shop ID");
            if (_shopsById.TryGetValue(shopId, out Shop cached))
                return cached;

            Shop shop = await FetchWithSnapshotAsync(
                SnapshotCategory, $"shop_{shopId}",
                async () => await FlockHttpClient.GetAsync<Shop>(
                    $"{Client.GetVersionedApiUrl()}/shop/{shopId}", Client.GetBaseHeaders(), cancellationToken),
                "Fetch shop", cancellationToken);

            // Keyed by the requested id (not shop.Id) so a hit is guaranteed on the next call with this same shopId.
            if (shop != null)
                _shopsById[shopId] = shop;
            return shop;
        }

        public async Task<Shop> GetByNameAsync(
            string name, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(name, "Shop Name");
            if (_shopIdByName.TryGetValue(name, out string cachedId) && _shopsById.TryGetValue(cachedId, out Shop cached))
                return cached;

            Shop shop = await FetchWithSnapshotAsync(
                SnapshotCategory, $"shop_name_{name}",
                async () => await FlockHttpClient.GetAsync<Shop>(
                    $"{Client.GetVersionedApiUrl()}/shop/by-name/{System.Uri.EscapeDataString(name)}", Client.GetBaseHeaders(), cancellationToken),
                "Fetch shop by name", cancellationToken);

            IndexShop(shop);
            if (shop != null && !string.IsNullOrEmpty(shop.Id))
                _shopIdByName[name] = shop.Id;
            return shop;
        }

        private void IndexShop(Shop shop)
        {
            if (shop == null || string.IsNullOrEmpty(shop.Id)) return;
            _shopsById[shop.Id] = shop;
        }

        public async Task<ShopItem> GetItemAsync(
            string shopItemId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");
            if (_itemsById.TryGetValue(shopItemId, out ShopItem cached))
                return cached;

            ShopItem item = await FetchWithSnapshotAsync(
                SnapshotCategory, $"item_{shopItemId}", async () =>
                {
                    return await FlockHttpClient.GetAsync<ShopItem>(
                        $"{Client.GetVersionedApiUrl()}/shop_item/{shopItemId}", Client.GetBaseHeaders(), cancellationToken);
                }, "Fetch shop item", cancellationToken);

            if (item != null)
                _itemsById[shopItemId] = item;
            return item;
        }

        public async Task<List<ShopItem>> GetItemsByShopAsync(
            string shopId, string patchId = null, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopId, "Shop ID");

            string cacheKey = $"items_shop_{shopId}_{(string.IsNullOrEmpty(patchId) ? "current" : patchId)}";
            if (_itemIdsByShop.TryGetValue(cacheKey, out List<string> cachedIds))
                return ResolveItems(cachedIds);

            List<ShopItem> items = await FetchWithSnapshotAsync(
                SnapshotCategory, cacheKey, async () =>
                {
                    string url = $"{Client.GetVersionedApiUrl()}/shop_item/shop/{shopId}{(!string.IsNullOrEmpty(patchId) ? $"?patch_id={patchId}" : "")}";

                    GenericResponse<List<ShopItem>> response = await FlockHttpClient.GetAsync<GenericResponse<List<ShopItem>>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch shop items", cancellationToken);

            List<string> ids = new List<string>(items.Count);
            foreach (ShopItem item in items)
            {
                if (item != null && !string.IsNullOrEmpty(item.Id))
                {
                    _itemsById[item.Id] = item;
                    ids.Add(item.Id);
                }
            }
            _itemIdsByShop[cacheKey] = ids;
            return ResolveItems(ids);
        }

        private List<ShopItem> ResolveItems(List<string> ids)
        {
            List<ShopItem> result = new List<ShopItem>(ids.Count);
            foreach (string id in ids)
                if (_itemsById.TryGetValue(id, out ShopItem item))
                    result.Add(item);
            return result;
        }

        public async Task<PlayerInventory> PurchaseAsync(
            string shopItemId, string playerId = null,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");
            // Default to the signed-in player; callers no longer need to pass CurrentPlayerId.
            if (string.IsNullOrEmpty(playerId))
                playerId = Client.CurrentPlayerId;
            RequireNotEmpty(playerId, "Player ID (sign in first)");

            ShopItem shopItem = await GetItemAsync(shopItemId, cancellationToken);
            try
            {
                await Client.Analytics.RecordTransactionAsync(
                    new AnalyticsTransactionRequest
                    {
                        Amount = shopItem.Price,
                        CurrencyCode = shopItem.Currency,
                        ShopItemId = shopItemId,
                        TransactionType = nameof(TransactionType.Purchase),
                        Status = nameof(PurchaseStatus.Started)
                    }, cancellationToken);
            }
            catch
            {
                Client.Logger.LogWarning("Failed to record purchase analytics");
            }

            // Purchase is non-idempotent: an ambiguous failure may mean the charge already cleared, so surface it rather than re-send (double-charge). Only 408/429 (not processed) retry.
            PlayerInventory result;
            try
            {
                result = await ExecuteAsync(async () =>
                {
                    ShopTransactionRequest request = new ShopTransactionRequest
                    {
                        ShopItemId = shopItemId,
                        PlayerId = playerId
                    };

                    return await FlockHttpClient.PostAsync<PlayerInventory>(
                        $"{Client.GetVersionedApiUrl()}/shop/transaction", request, Client.GetBaseHeaders(), cancellationToken);
                }, "Purchase shop item", cancellationToken, idempotent: false);
            }
            catch
            {
                try
                {
                    await Client.Analytics.RecordTransactionAsync(
                        new AnalyticsTransactionRequest
                        {
                            Amount = shopItem.Price,
                            CurrencyCode = shopItem.Currency,
                            ShopItemId = shopItemId,
                            TransactionType = nameof(TransactionType.Purchase),
                            Status = nameof(PurchaseStatus.Failed)
                        }, cancellationToken);
                }
                catch
                {
                    Client.Logger.LogWarning("Failed to record failed-purchase analytics");
                }
                throw;
            }

            if (Client.Analytics != null && shopItem != null)
            {
                try
                {
                    await Client.Analytics.RecordTransactionAsync(
                        new AnalyticsTransactionRequest
                        {
                            Amount = shopItem.Price,
                            CurrencyCode = shopItem.Currency,
                            ShopItemId = shopItemId,
                            TransactionType = nameof(TransactionType.Purchase),
                            Status = nameof(PurchaseStatus.Purchased)
                        }, cancellationToken);
                }
                catch
                {
                    Client.Logger.LogWarning("Failed to record purchase analytics");
                }
            }

            return result;
        }

        public async Task<PaginatedResponse<PlayerInventory>> GetPlayerInventoryAsync(
            string playerId = null, int page = 1, int limit = 100,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(playerId))
                playerId = Client.CurrentPlayerId;
            RequireNotEmpty(playerId, "Player ID (sign in first)");

            // Inventory changes on every purchase — intentionally never cached (no in-memory dict / snapshot); always fresh. Same rule as bans.
            return await ExecuteAsync(async () =>
            {
                return await FlockHttpClient.GetAsync<PaginatedResponse<PlayerInventory>>(
                    $"{Client.GetVersionedApiUrl()}/player_inventory/player/{playerId}?page={page}&limit={limit}",
                    Client.GetBaseHeaders(), cancellationToken);
            }, "Get player inventory", cancellationToken);
        }
    }
}
