using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;
using Flock.Exceptions;

namespace Flock.Providers
{
    public class PlayerProvider : FlockProviderBase, IPlayerService
    {
        private readonly Dictionary<string, PlayerTemplateSchema> _templatesById = new Dictionary<string, PlayerTemplateSchema>();
        private readonly Dictionary<string, string> _templateIdByName = new Dictionary<string, string>();
        private bool _allTemplatesFetched;
        private Task<List<PlayerTemplateSchema>> _allTemplatesFetchTask;

        private readonly Dictionary<string, Dictionary<string, PlayerData>> _playerDataByPlayerCache = new Dictionary<string, Dictionary<string, PlayerData>>();
        private readonly Dictionary<string, Task<Dictionary<string, PlayerData>>> _playerDataFetchTasks = new Dictionary<string, Task<Dictionary<string, PlayerData>>>();

        private const string SnapshotCategory = "player_template";

        public PlayerProvider(FlockClient client) : base(client) { }

        /// <summary>
        /// Clears cached player templates and the per-player PlayerData snapshot used by
        /// <see cref="GetMyDataByTemplateAsync"/>. Call after a write or when switching player.
        /// </summary>
        public void ClearCache()
        {
            _templatesById.Clear();
            _templateIdByName.Clear();
            _allTemplatesFetched = false;
            _allTemplatesFetchTask = null;
            _playerDataByPlayerCache.Clear();
            _playerDataFetchTasks.Clear();
            Client.SnapshotStore?.DeleteScope(GetSnapshotScope(SnapshotCategory));
        }

        // Class B write-through: game commands hand their server-returned row here.
        internal void ApplyServerPlayerData(PlayerData data)
        {
            if (data == null || string.IsNullOrEmpty(data.PlayerId) || string.IsNullOrEmpty(data.PlayerTemplateId))
                return;

            if (_playerDataByPlayerCache.TryGetValue(data.PlayerId, out Dictionary<string, PlayerData> byTemplate))
                byTemplate[data.PlayerTemplateId] = data;
        }

        public async Task<PlayerData> GetDataByIdAsync(string playerDataId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");

            return await ExecuteAsync(async () =>
            {
                GenericResponse<PlayerData> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerData>>(
                    $"{Client.GetVersionedApiUrl()}/player_data/{playerDataId}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch player data {playerDataId}", cancellationToken);
        }

        public async Task<PaginatedResponse<PlayerData>> GetAllDataAsync(string playerId = null, int page = 1, int limit = 100, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetVersionedApiUrl()}/player_data?page={page}&limit={limit}";
                if (!string.IsNullOrEmpty(playerId))
                    url += $"&player_id={playerId}";

                return await FlockHttpClient.GetAsync<PaginatedResponse<PlayerData>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
            }, "Fetch player data list", cancellationToken);
        }

        public Task<List<PlayerTemplateSchema>> GetTemplatesAsync(CancellationToken cancellationToken = default)
        {
            if (_allTemplatesFetched)
                return Task.FromResult(new List<PlayerTemplateSchema>(_templatesById.Values));
            if (_allTemplatesFetchTask != null)
                return _allTemplatesFetchTask;

            _allTemplatesFetchTask = FetchAllTemplatesAsync(cancellationToken);
            return _allTemplatesFetchTask;
        }

        private async Task<List<PlayerTemplateSchema>> FetchAllTemplatesAsync(CancellationToken cancellationToken)
        {
            try
            {
                List<PlayerTemplateSchema> templates = await FetchWithSnapshotAsync(
                    GetSnapshotScope(SnapshotCategory), "all", async () =>
                    {
                        string url = $"{Client.GetVersionedApiUrl()}/player_template";
                        GenericResponse<List<PlayerTemplateSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<PlayerTemplateSchema>>>(
                            url, Client.GetBaseHeaders(), cancellationToken);
                        ValidateResponse(response);
                        return response.Result;
                    }, "Fetch player templates", cancellationToken);

                foreach (PlayerTemplateSchema t in templates)
                    IndexTemplate(t);
                _allTemplatesFetched = true;
                return templates;
            }
            finally
            {
                _allTemplatesFetchTask = null;
            }
        }

        public async Task<PlayerTemplateSchema> GetTemplateByIdAsync(string playerTemplateId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerTemplateId, "Player Template ID");
            if (_templatesById.TryGetValue(playerTemplateId, out PlayerTemplateSchema cached))
                return cached;

            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetVersionedApiUrl()}/player_template/{playerTemplateId}";
                GenericResponse<PlayerTemplateSchema> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerTemplateSchema>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                IndexTemplate(response.Result);
                return response.Result;
            }, $"Fetch player template {playerTemplateId}", cancellationToken);
        }

        public async Task<PlayerTemplateSchema> GetTemplateByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(name, "Player Template Name");
            if (_templateIdByName.TryGetValue(name, out string id) && _templatesById.TryGetValue(id, out PlayerTemplateSchema cached))
                return cached;

            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetVersionedApiUrl()}/player_template/by-name/{System.Uri.EscapeDataString(name)}";
                GenericResponse<PlayerTemplateSchema> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerTemplateSchema>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                IndexTemplate(response.Result);
                return response.Result;
            }, $"Fetch player template by name {name}", cancellationToken);
        }

        private void IndexTemplate(PlayerTemplateSchema t)
        {
            if (t == null || string.IsNullOrEmpty(t.Id)) return;
            _templatesById[t.Id] = t;
            if (!string.IsNullOrEmpty(t.Name))
                _templateIdByName[t.Name] = t.Id;
        }

        /// <summary>
        /// Resolves the current authenticated player's PlayerData for a given template,
        /// using <see cref="FlockClient.CurrentPlayerId"/>. Returns null if no row exists.
        /// First call paginates and caches all the player's PlayerData; subsequent
        /// calls (any template) are served from memory until <see cref="ClearCache"/>.
        /// </summary>
        public async Task<PlayerData> GetMyDataByTemplateAsync(string playerTemplateId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerTemplateId, "Player Template ID");
            string playerId = Client.CurrentPlayerId;
            RequireNotEmpty(playerId, "Current Player ID (sign in first)");

            Dictionary<string, PlayerData> byTemplate = await GetOrFetchByTemplateAsync(playerId, cancellationToken);
            if (byTemplate.TryGetValue(playerTemplateId, out PlayerData pd))
                return pd;

            Client.Logger.LogError($"No player data found for template {playerTemplateId} for player {playerId}");
            return null;
        }

        /// <summary>Finds the single player template carrying the given tag (e.g. "currency", "achievement").</summary>
        public async Task<PlayerTemplateSchema> GetTemplateByTagAsync(string tag, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(tag, "Template tag");
            List<PlayerTemplateSchema> templates = await GetTemplatesAsync(cancellationToken);
            foreach (PlayerTemplateSchema t in templates)
                if (t != null && !string.IsNullOrEmpty(t.Id) && string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    return t;
            throw new FlockValidationException($"No player template tagged '{tag}' for this game.");
        }

        /// <summary>Resolves the current player's data row for the single player template carrying the given tag (e.g. "currency", "achievement").</summary>
        public async Task<PlayerData> GetMyDataByTagAsync(string tag, CancellationToken cancellationToken = default)
        {
            PlayerTemplateSchema template = await GetTemplateByTagAsync(tag, cancellationToken);
            return await GetMyDataByTemplateAsync(template.Id, cancellationToken);
        }

        private Task<Dictionary<string, PlayerData>> GetOrFetchByTemplateAsync(string playerId, CancellationToken cancellationToken)
        {
            if (_playerDataByPlayerCache.TryGetValue(playerId, out Dictionary<string, PlayerData> cached))
                return Task.FromResult(cached);
            if (_playerDataFetchTasks.TryGetValue(playerId, out Task<Dictionary<string, PlayerData>> inFlight))
                return inFlight;

            Task<Dictionary<string, PlayerData>> task = FetchAndCacheAsync(playerId, cancellationToken);
            _playerDataFetchTasks[playerId] = task;
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
                _playerDataFetchTasks.Remove(playerId);
            }
        }

        private async Task<Dictionary<string, PlayerData>> FetchAllMyDataAsync(string playerId, CancellationToken cancellationToken)
        {
            Dictionary<string, PlayerData> byTemplate = new Dictionary<string, PlayerData>();
            const int pageSize = 100;
            int page = 1;
            while (true)
            {
                PaginatedResponse<PlayerData> response = await GetAllDataAsync(playerId, page, pageSize, cancellationToken);
                if (response?.Items == null || response.Items.Length == 0)
                {
                    Client.Logger.LogInfo($"No player data found for player {playerId}");
                    break;
                }
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
                string url = $"{Client.GetVersionedApiUrl()}/player_template/{playerTemplateId}/player-data";
                GenericResponse<List<PlayerData>> response = await FlockHttpClient.GetAsync<GenericResponse<List<PlayerData>>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch player data for template {playerTemplateId}", cancellationToken);
        }

        public async Task<PlayerBan> GetBanAsync(string playerId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerId, "Player ID");

            // Ban status is security state that can change server-side at any time — intentionally never cached; always fresh. Same rule as inventory.
            return await ExecuteAsync(async () =>
            {
                GenericResponse<PlayerBan> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerBan>>(
                    $"{Client.GetVersionedApiUrl()}/player-ban?player_id={playerId}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Get player ban", cancellationToken);
        }
    }
}
