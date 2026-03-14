using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Services
{
    public class FlockShopProvider : FlockProviderBase
    {
        public FlockShopProvider(FlockClient client) : base(client) { }

        public async Task<PaginatedResponse<Shop>> GetAllAsync(
            int page = 1, int limit = 100, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                var url = new StringBuilder().Append(Client.GetApiUrl())
                    .Append("/v1/shop")
                    .Append("?page=").Append(page)
                    .Append("&limit=").Append(limit)
                    .ToString();

                return await FlockHttpClient.GetAsync<PaginatedResponse<Shop>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
            }, "Fetch shops", cancellationToken);
        }

        public async Task<Shop> GetByIdAsync(
            string shopId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopId, "Shop ID");

            return await ExecuteAsync(async () =>
            {
                var url = new StringBuilder().Append(Client.GetApiUrl())
                    .Append("/v1/shop/")
                    .Append(shopId)
                    .ToString();

                return await FlockHttpClient.GetAsync<Shop>(
                    url, Client.GetBaseHeaders(), cancellationToken);
            }, "Fetch shop", cancellationToken);
        }

        public async Task<ShopItem> GetItemAsync(
            string shopItemId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");

            return await ExecuteAsync(async () =>
            {
                var url = new StringBuilder().Append(Client.GetApiUrl())
                    .Append("/v1/shop_item/")
                    .Append(shopItemId)
                    .ToString();

                return await FlockHttpClient.GetAsync<ShopItem>(
                    url, Client.GetBaseHeaders(), cancellationToken);
            }, "Fetch shop item", cancellationToken);
        }

        public async Task<List<ShopItem>> GetItemsByShopAsync(
            string shopId, string patchId = null, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopId, "Shop ID");

            return await ExecuteAsync(async () =>
            {
                var url = new StringBuilder().Append(Client.GetApiUrl())
                    .Append("/v1/shop_item/shop/")
                    .Append(shopId);

                if (!string.IsNullOrEmpty(patchId))
                    url.Append("?patch_id=").Append(patchId);

                var response = await FlockHttpClient.GetAsync<GenericResponse<List<ShopItem>>>(
                    url.ToString(), Client.GetBaseHeaders(), cancellationToken);
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

            return await ExecuteAsync(async () =>
            {
                var request = new ShopTransactionRequest
                {
                    ShopItemId = shopItemId,
                    PlayerId = playerId
                };

                return await FlockHttpClient.PostAsync<PlayerInventory>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/shop/transaction")
                        .ToString(), request, Client.GetBaseHeaders(), cancellationToken);
            }, "Purchase shop item", cancellationToken);
        }

        public async Task<PaginatedResponse<PlayerInventory>> GetPlayerInventoryAsync(
            string playerId, int page = 1, int limit = 100,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerId, "Player ID");

            return await ExecuteAsync(async () =>
            {
                var url = new StringBuilder().Append(Client.GetApiUrl())
                    .Append("/v1/player_inventory/player/")
                    .Append(playerId)
                    .Append("?page=").Append(page)
                    .Append("&limit=").Append(limit)
                    .ToString();

                return await FlockHttpClient.GetAsync<PaginatedResponse<PlayerInventory>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
            }, "Get player inventory", cancellationToken);
        }
    }
}
