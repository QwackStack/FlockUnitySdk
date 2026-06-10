using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Interfaces;
using Flock.Models;

namespace Flock.Providers
{
    public class FlockConfigProvider : FlockProviderBase, IConfigProvider
    {
        private readonly Dictionary<string, GamePatchSchema> _patchesById = new Dictionary<string, GamePatchSchema>();
        private readonly Dictionary<string, List<GamePatchSchema>> _patchesBySchemaCache = new Dictionary<string, List<GamePatchSchema>>();
        private bool _allPatchesFetched;
        private Task<List<GamePatchSchema>> _allPatchesFetchTask;

        private readonly Dictionary<SchemaTag, List<GameConfigSchema>> _gameConfigsByTagCache = new Dictionary<SchemaTag, List<GameConfigSchema>>();
        private readonly Dictionary<SchemaTag, List<GameConfigSchema>> _gameConfigsByVersionCache = new Dictionary<SchemaTag, List<GameConfigSchema>>();
        private readonly Dictionary<string, GameConfigSchema> _playerFeaturesByPlayer = new Dictionary<string, GameConfigSchema>();

        private const string SnapshotCategory = "config";

        public FlockConfigProvider(FlockClient client) : base(client) { }

        /// <summary>
        /// Clears all cached configs and patches. Call this after a known server-side
        /// mutation if you need the next fetch to hit the backend.
        /// </summary>
        public void ClearCache()
        {
            _patchesById.Clear();
            _patchesBySchemaCache.Clear();
            _allPatchesFetched = false;
            _allPatchesFetchTask = null;
            _gameConfigsByTagCache.Clear();
            _gameConfigsByVersionCache.Clear();
            _playerFeaturesByPlayer.Clear();
            Client.SnapshotStore?.DeleteScope(GetSnapshotScope(SnapshotCategory));
        }

        public Task<List<GamePatchSchema>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            if (_allPatchesFetched)
                return Task.FromResult(new List<GamePatchSchema>(_patchesById.Values));
            if (_allPatchesFetchTask != null)
                return _allPatchesFetchTask;

            _allPatchesFetchTask = FetchAllPatchesAsync(cancellationToken);
            return _allPatchesFetchTask;
        }

        private async Task<List<GamePatchSchema>> FetchAllPatchesAsync(CancellationToken cancellationToken)
        {
            try
            {
                List<GamePatchSchema> patches = await FetchWithSnapshotAsync(
                    GetSnapshotScope(SnapshotCategory), "game_patch_all", async () =>
                    {
                        GenericResponse<List<GamePatchSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                            $"{Client.GetVersionedApiUrl()}/game_patch", Client.GetBaseHeaders(), cancellationToken);
                        ValidateResponse(response);
                        return response.Result;
                    }, "Fetch game configs", cancellationToken);

                foreach (GamePatchSchema p in patches)
                    IndexPatch(p);
                _allPatchesFetched = true;
                return patches;
            }
            finally
            {
                _allPatchesFetchTask = null;
            }
        }

        public async Task<List<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
        {
            List<GamePatchSchema> configs = await GetAllAsync(cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        public async Task<GamePatchSchema> GetByIdAsync(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");
            if (_patchesById.TryGetValue(configId, out GamePatchSchema cached)) return cached;

            GamePatchSchema patch = await FetchWithSnapshotAsync(
                GetSnapshotScope(SnapshotCategory), $"game_patch_{configId}", async () =>
                {
                    GenericResponse<GamePatchSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GamePatchSchema>>(
                        $"{Client.GetVersionedApiUrl()}/game_patch/{configId}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, $"Fetch config {configId}", cancellationToken);

            IndexPatch(patch);
            return patch;
        }

        public async Task<T> GetByIdAsync<T>(string configId, CancellationToken cancellationToken = default)
        {
            GamePatchSchema config = await GetByIdAsync(configId, cancellationToken);
            return config.GetDataAs<T>();
        }

        public async Task<List<GamePatchSchema>> GetBySchemaAsync(string schemaId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(schemaId, "Schema ID");
            if (_patchesBySchemaCache.TryGetValue(schemaId, out List<GamePatchSchema> cached)) return cached;

            List<GamePatchSchema> patches = await FetchWithSnapshotAsync(
                GetSnapshotScope(SnapshotCategory), $"game_patch_schema_{schemaId}", async () =>
                {
                    string url = $"{Client.GetVersionedApiUrl()}/game_patch/config/{schemaId}";
                    GenericResponse<List<GamePatchSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, $"Fetch configs for schema {schemaId}", cancellationToken);

            _patchesBySchemaCache[schemaId] = patches;
            foreach (GamePatchSchema p in patches)
                IndexPatch(p);
            return patches;
        }

        public async Task<List<T>> GetBySchemaAsync<T>(string schemaId, CancellationToken cancellationToken = default)
        {
            List<GamePatchSchema> configs = await GetBySchemaAsync(schemaId, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        /// <summary>
        /// Resolves the current patch for a game config and returns its data typed as <typeparamref name="T"/>.
        /// Returns <c>default</c> and logs a warning if the config has no patches yet.
        /// </summary>
        public async Task<T> GetByConfigIdAsync<T>(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");
            List<GamePatchSchema> patches = await GetBySchemaAsync(configId, cancellationToken);
            if (patches == null || patches.Count == 0)
            {
                Client.Logger.LogWarning($"No patches found for config {configId} — has a patch been created for it on the current game version?");
                return default;
            }
            return patches[0].GetDataAs<T>();
        }

        private void IndexPatch(GamePatchSchema patch)
        {
            if (patch == null || string.IsNullOrEmpty(patch.Id)) return;
            _patchesById[patch.Id] = patch;
        }

        public async Task<List<GameConfigSchema>> GetGameConfigsAsync(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            if (_gameConfigsByTagCache.TryGetValue(tag, out List<GameConfigSchema> cached)) return cached;

            List<GameConfigSchema> configs = await FetchWithSnapshotAsync(
                GetSnapshotScope(SnapshotCategory), $"game_config_tag_{tag}", async () =>
                {
                    string url = tag != SchemaTag.empty ? $"{Client.GetVersionedApiUrl()}/game_config?tag={tag}" : $"{Client.GetVersionedApiUrl()}/game_config";
                    GenericResponse<List<GameConfigSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch game configs", cancellationToken);

            _gameConfigsByTagCache[tag] = configs;
            return configs;
        }

        public async Task<List<T>> GetGameConfigsAsync<T>(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            List<GameConfigSchema> configs = await GetGameConfigsAsync(tag, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        public async Task<List<GameConfigSchema>> GetGameConfigsByVersionAsync(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            if (_gameConfigsByVersionCache.TryGetValue(tag, out List<GameConfigSchema> cached)) return cached;

            List<GameConfigSchema> configs = await FetchWithSnapshotAsync(
                GetSnapshotScope(SnapshotCategory), $"game_config_version_tag_{tag}", async () =>
                {
                    string url = tag != SchemaTag.empty ? $"{Client.GetVersionedApiUrl()}/game_config/version?tag={tag}" : $"{Client.GetVersionedApiUrl()}/game_config/version";
                    GenericResponse<List<GameConfigSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch game configs by version", cancellationToken);

            _gameConfigsByVersionCache[tag] = configs;
            return configs;
        }

        public async Task<List<T>> GetGameConfigsByVersionAsync<T>(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            List<GameConfigSchema> configs = await GetGameConfigsByVersionAsync(tag, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        public async Task<GameConfigSchema> GetPlayerFeaturesAsync(string playerId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerId, "Player ID");
            if (_playerFeaturesByPlayer.TryGetValue(playerId, out GameConfigSchema cachedFeatures))
                return cachedFeatures;

            GameConfigSchema features = await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetVersionedApiUrl()}/game_config/player/{playerId}/features";
                GenericResponse<GameConfigSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameConfigSchema>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch feature config for player {playerId}", cancellationToken);

            _playerFeaturesByPlayer[playerId] = features;
            return features;
        }

        public async Task<T> GetPlayerFeaturesAsync<T>(string playerId, CancellationToken cancellationToken = default)
        {
            GameConfigSchema config = await GetPlayerFeaturesAsync(playerId, cancellationToken);
            return config.GetDataAs<T>();
        }
    }
}
