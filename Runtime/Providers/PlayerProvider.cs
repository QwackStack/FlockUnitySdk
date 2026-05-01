using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;

namespace Flock.Providers
{
    public class PlayerProvider : FlockProviderBase, IPlayerService
    {
        private List<PlayerTemplateSchema> _allTemplatesCache;
        private readonly Dictionary<string, PlayerTemplateSchema> _templateByIdCache = new Dictionary<string, PlayerTemplateSchema>();
        private readonly Dictionary<string, PlayerTemplateSchema> _templateByNameCache = new Dictionary<string, PlayerTemplateSchema>();
        private readonly Dictionary<string, Dictionary<string, PlayerData>> _playerDataByPlayerCache = new Dictionary<string, Dictionary<string, PlayerData>>();
        private readonly Dictionary<string, Task<Dictionary<string, PlayerData>>> _inFlightPlayerDataFetches = new Dictionary<string, Task<Dictionary<string, PlayerData>>>();

        public PlayerProvider(FlockClient client) : base(client) { }

        /// <summary>
        /// Clears cached player templates and the per-player PlayerData snapshot used by
        /// <see cref="GetMyDataByTemplateAsync"/>. Call after a write or when switching player.
        /// </summary>
        public void ClearCache()
        {
            _allTemplatesCache = null;
            _templateByIdCache.Clear();
            _templateByNameCache.Clear();
            _playerDataByPlayerCache.Clear();
            _inFlightPlayerDataFetches.Clear();
        }

        public async Task<PlayerData> GetDataByIdAsync(string playerDataId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");

            return await ExecuteAsync(async () =>
            {
                GenericResponse<PlayerData> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerData>>(
                    $"{Client.GetApiUrl()}/v1/player_data/{playerDataId}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch player data {playerDataId}", cancellationToken);
        }

        public async Task<PaginatedResponse<PlayerData>> GetAllDataAsync( string playerId = null,int page = 1, int limit = 100, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/player_data?page={page}&limit={limit}";
                if (!string.IsNullOrEmpty(playerId))
                    url += $"&player_id={playerId}";

                return await FlockHttpClient.GetAsync<PaginatedResponse<PlayerData>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
            }, "Fetch player data list", cancellationToken);
        }

        public async Task<List<PlayerTemplateSchema>> GetTemplatesAsync(CancellationToken cancellationToken = default)
        {
            if (_allTemplatesCache != null) return _allTemplatesCache;

            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/player_template";
                GenericResponse<List<PlayerTemplateSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<PlayerTemplateSchema>>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                _allTemplatesCache = response.Result;
                return response.Result;
            }, "Fetch player templates", cancellationToken);
        }

        public async Task<PlayerTemplateSchema> GetTemplateByIdAsync(string playerTemplateId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerTemplateId, "Player Template ID");
            if (_templateByIdCache.TryGetValue(playerTemplateId, out PlayerTemplateSchema cached)) return cached;

            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/player_template/{playerTemplateId}";
                GenericResponse<PlayerTemplateSchema> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerTemplateSchema>>(url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                _templateByIdCache[playerTemplateId] = response.Result;
                return response.Result;
            }, $"Fetch player template {playerTemplateId}", cancellationToken);
        }

        public async Task<PlayerTemplateSchema> GetTemplateByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(name, "Player Template Name");
            if (_templateByNameCache.TryGetValue(name, out PlayerTemplateSchema cached)) return cached;

            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/player_template/by-name/{name}";
                GenericResponse<PlayerTemplateSchema> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerTemplateSchema>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                _templateByNameCache[name] = response.Result;
                return response.Result;
            }, $"Fetch player template by name {name}", cancellationToken);
        }

        /// <summary>
        /// Resolves the current authenticated player's PlayerData for a given template,
        /// using <see cref="FlockClient.CurrentPlayerId"/>. Returns null if no row exists.
        /// First call paginates and caches all of the player's PlayerData; subsequent
        /// calls (any template) are served from memory until <see cref="ClearCache"/>.
        /// </summary>
        public async Task<PlayerData> GetMyDataByTemplateAsync(string playerTemplateId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerTemplateId, "Player Template ID");
            string playerId = Client.CurrentPlayerId;
            RequireNotEmpty(playerId, "Current Player ID (sign in first)");

            Dictionary<string, PlayerData> byTemplate = await GetOrFetchByTemplateAsync(playerId, cancellationToken);
            return byTemplate.TryGetValue(playerTemplateId, out PlayerData pd) ? pd : null;
        }

        private Task<Dictionary<string, PlayerData>> GetOrFetchByTemplateAsync(string playerId, CancellationToken cancellationToken)
        {
            if (_playerDataByPlayerCache.TryGetValue(playerId, out Dictionary<string, PlayerData> cached))
                return Task.FromResult(cached);
            if (_inFlightPlayerDataFetches.TryGetValue(playerId, out Task<Dictionary<string, PlayerData>> inFlight))
                return inFlight;

            Task<Dictionary<string, PlayerData>> task = FetchAndCacheAsync(playerId, cancellationToken);
            _inFlightPlayerDataFetches[playerId] = task;
            return task;
        }

        private async Task<Dictionary<string, PlayerData>> FetchAndCacheAsync(string playerId, CancellationToken cancellationToken)
        {
            try
            {
                Dictionary<string, PlayerData> byTemplate = await FetchAllMyDataAsync(playerId, cancellationToken);
                _playerDataByPlayerCache[playerId] = byTemplate;
                return byTemplate;
            }
            finally
            {
                _inFlightPlayerDataFetches.Remove(playerId);
            }
        }

        private async Task<Dictionary<string, PlayerData>> FetchAllMyDataAsync(string playerId, CancellationToken cancellationToken)
        {
            var byTemplate = new Dictionary<string, PlayerData>();
            const int pageSize = 100;
            int page = 1;
            while (true)
            {
                PaginatedResponse<PlayerData> response = await GetAllDataAsync(playerId, page, pageSize, cancellationToken);
                if (response?.Items == null || response.Items.Length == 0) break;
                foreach (PlayerData pd in response.Items)
                {
                    if (!string.IsNullOrEmpty(pd.PlayerTemplateId))
                        byTemplate[pd.PlayerTemplateId] = pd;
                }
                if (response.Items.Length < pageSize) break;
                page++;
            }
            return byTemplate;
        }

        public async Task<List<PlayerData>> GetTemplatePlayerDataAsync(string playerTemplateId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerTemplateId, "Player Template ID");
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/player_template/{playerTemplateId}/player-data";
                GenericResponse<List<PlayerData>> response = await FlockHttpClient.GetAsync<GenericResponse<List<PlayerData>>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch player data for template {playerTemplateId}", cancellationToken);
        }
    }
}
