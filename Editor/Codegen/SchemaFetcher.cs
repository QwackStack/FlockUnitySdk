using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;

namespace Flock.Editor.Codegen
{
    internal static class SchemaFetcher
    {
        // Throws on any resolve/fetch failure so a transient outage or bad key aborts the sync
        // before the emitters wipe and rewrite — never returns a partial or empty snapshot.
        public static async Task<FlockSchemaSnapshot> FetchAsync(FlockConfigAsset config)
        {
            string baseUrl = (config.apiUrl ?? "").TrimEnd('/');
            Dictionary<string, string> bootstrapHeaders = new Dictionary<string, string>
            {
                { "X-Flock-API-Key", config.ApiKey }
            };

            string gameVersionId = await ResolveGameVersionIdAsync(baseUrl, config.gameVersion, bootstrapHeaders);

            Dictionary<string, string> headers = new Dictionary<string, string>(bootstrapHeaders)
            {
                ["X-Game-Version-ID"] = gameVersionId
            };

            FlockSchemaSnapshot snapshot = new FlockSchemaSnapshot
            {
                GameVersionId = gameVersionId,
                FetchedAt = DateTime.UtcNow,
            };

            snapshot.PlayerTemplates = await GetList<PlayerTemplateSchema>($"{baseUrl}/{FlockClient.ApiVersion}/player_template", headers);
            snapshot.GameConfigs     = await GetList<GameConfigSchema>($"{baseUrl}/{FlockClient.ApiVersion}/game_config/version", headers);
            snapshot.Shops           = await GetShopsWithItems(baseUrl, headers);

            return snapshot;
        }

        private static async Task<string> ResolveGameVersionIdAsync(string baseUrl, string gameVersion, Dictionary<string, string> headers)
        {
            if (string.IsNullOrEmpty(gameVersion))
                throw new InvalidOperationException("Game Version is empty in FlockConfigAsset.");

            string url = $"{baseUrl}/{FlockClient.ApiVersion}/game_version/by-name/{Uri.EscapeDataString(gameVersion)}";
            GenericResponse<GameVersionSchema> response;
            try
            {
                response = await FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(url, headers);
            }
            catch (Exception ex)
            {
                throw new FlockException($"GET {url} failed: {ex.GetType().Name}: {ex.Message}", ex);
            }

            if (response?.Error != null && !string.IsNullOrEmpty(response.Error.Code))
                throw new FlockException($"{url} returned error code '{response.Error.Code}'.");

            string id = response?.Result?.Id;
            if (string.IsNullOrEmpty(id))
                throw new FlockException($"Could not resolve GameVersionId for game version '{gameVersion}' from {url}.");
            return id;
        }

        // A server error code or a network failure both throw so a failed sync never reaches the
        // emitters. A legitimately empty Result (no error code) is allowed through as 0 items.
        private static async Task<List<T>> GetList<T>(string url, Dictionary<string, string> headers)
        {
            GenericResponse<List<T>> response;
            try
            {
                response = await FlockHttpClient.GetAsync<GenericResponse<List<T>>>(url, headers);
            }
            catch (Exception ex)
            {
                throw new FlockException($"GET {url} failed: {ex.GetType().Name}: {ex.Message}", ex);
            }

            if (response?.Error != null && !string.IsNullOrEmpty(response.Error.Code))
                throw new FlockException($"{url} returned error code '{response.Error.Code}'.");

            return response?.Result ?? new List<T>();
        }

        // Shops are paginated and their items aren't guaranteed embedded in the list, so page the
        // list then backfill items per shop. Any failure throws so a bad sync never reaches the emitters.
        private static async Task<List<Shop>> GetShopsWithItems(string baseUrl, Dictionary<string, string> headers)
        {
            List<Shop> shops = new List<Shop>();
            int page = 1;
            const int limit = 100;
            while (true)
            {
                string url = $"{baseUrl}/{FlockClient.ApiVersion}/shop?page={page}&limit={limit}";
                PaginatedResponse<Shop> response;
                try
                {
                    response = await FlockHttpClient.GetAsync<PaginatedResponse<Shop>>(url, headers);
                }
                catch (Exception ex)
                {
                    throw new FlockException($"GET {url} failed: {ex.GetType().Name}: {ex.Message}", ex);
                }

                if (response?.Items == null || response.Items.Length == 0)
                    break;
                shops.AddRange(response.Items);
                if (response.Items.Length < limit)
                    break;
                page++;
            }

            foreach (Shop shop in shops)
            {
                if (shop == null || string.IsNullOrEmpty(shop.Id)) continue;
                if (shop.ShopItems != null && shop.ShopItems.Count > 0) continue;
                shop.ShopItems = await GetList<ShopItem>($"{baseUrl}/{FlockClient.ApiVersion}/shop_item/shop/{shop.Id}", headers);
            }
            return shops;
        }
    }
}
