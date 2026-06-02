using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Providers
{
    public enum PurchaseStatus
    {
        //Status will change once validation/flow changes
        Started,
        Purchased
    }
    public enum TransactionType
    {
        //Status will change once validation/flow changes
        Purchase
    }
    public class FlockShopProvider : FlockProviderBase
    {
        public FlockShopProvider(FlockClient client) : base(client) { }

        public async Task<PaginatedResponse<Shop>> GetAllAsync(
            int page = 1, int limit = 100, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                return await FlockHttpClient.GetAsync<PaginatedResponse<Shop>>(
                    $"{Client.GetVersionedApiUrl()}/shop?page={page}&limit={limit}", Client.GetBaseHeaders(), cancellationToken);
            }, "Fetch shops", cancellationToken);
        }

        public async Task<Shop> GetByIdAsync(
            string shopId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopId, "Shop ID");

            return await ExecuteAsync(async () => await FlockHttpClient.GetAsync<Shop>(
                $"{Client.GetVersionedApiUrl()}/shop/{shopId}", Client.GetBaseHeaders(), cancellationToken), "Fetch shop", cancellationToken);
        }

        public async Task<ShopItem> GetItemAsync(
            string shopItemId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");

            return await ExecuteAsync(async () =>
            {
                return await FlockHttpClient.GetAsync<ShopItem>(
                    $"{Client.GetVersionedApiUrl()}/shop_item/{shopItemId}", Client.GetBaseHeaders(), cancellationToken);
            }, "Fetch shop item", cancellationToken);
        }

        public async Task<List<ShopItem>> GetItemsByShopAsync(
            string shopId, string patchId = null, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopId, "Shop ID");

            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetVersionedApiUrl()}/shop_item/shop/{shopId}{(!string.IsNullOrEmpty(patchId) ? $"?patch_id={patchId}" : "")}";

                GenericResponse<List<ShopItem>> response = await FlockHttpClient.GetAsync<GenericResponse<List<ShopItem>>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch shop items", cancellationToken);
        }

        public async Task<PlayerInventory> PurchaseAsync(
            string shopItemId, string playerId,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");
            RequireNotEmpty(playerId, "Player ID");

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

            PlayerInventory result = await ExecuteAsync(async () =>
            {
                ShopTransactionRequest request = new ShopTransactionRequest
                {
                    ShopItemId = shopItemId,
                    PlayerId = playerId
                };

                return await FlockHttpClient.PostAsync<PlayerInventory>(
                    $"{Client.GetVersionedApiUrl()}/shop/transaction", request, Client.GetBaseHeaders(), cancellationToken);
            }, "Purchase shop item", cancellationToken);

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
            string playerId, int page = 1, int limit = 100,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerId, "Player ID");

            return await ExecuteAsync(async () =>
            {
                return await FlockHttpClient.GetAsync<PaginatedResponse<PlayerInventory>>(
                    $"{Client.GetVersionedApiUrl()}/player_inventory/player/{playerId}?page={page}&limit={limit}",
                    Client.GetBaseHeaders(), cancellationToken);
            }, "Get player inventory", cancellationToken);
        }
    }
}
