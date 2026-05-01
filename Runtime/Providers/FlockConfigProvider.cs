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
        private List<GamePatchSchema> _allPatchesCache;
        private readonly Dictionary<string, GamePatchSchema> _patchByIdCache = new Dictionary<string, GamePatchSchema>();
        private readonly Dictionary<string, List<GamePatchSchema>> _patchesBySchemaCache = new Dictionary<string, List<GamePatchSchema>>();
        private readonly Dictionary<SchemaTag, List<GameConfigSchema>> _gameConfigsByTagCache = new Dictionary<SchemaTag, List<GameConfigSchema>>();
        private readonly Dictionary<SchemaTag, List<GameConfigSchema>> _gameConfigsByVersionCache = new Dictionary<SchemaTag, List<GameConfigSchema>>();

        public FlockConfigProvider(FlockClient client) : base(client) { }

        /// <summary>
        /// Clears all cached configs and patches. Call this after a known server-side
        /// mutation if you need the next fetch to hit the backend.
        /// </summary>
        public void ClearCache()
        {
            _allPatchesCache = null;
            _patchByIdCache.Clear();
            _patchesBySchemaCache.Clear();
            _gameConfigsByTagCache.Clear();
            _gameConfigsByVersionCache.Clear();
        }

        public async Task<List<GamePatchSchema>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            if (_allPatchesCache != null) return _allPatchesCache;

            return await ExecuteAsync(async () =>
            {
                GenericResponse<List<GamePatchSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                    $"{Client.GetApiUrl()}/v1/game_patch", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                _allPatchesCache = response.Result;
                return response.Result;
            }, "Fetch game configs", cancellationToken);
        }

        public async Task<List<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
        {
            List<GamePatchSchema> configs = await GetAllAsync(cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        public async Task<GamePatchSchema> GetByIdAsync(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");
            if (_patchByIdCache.TryGetValue(configId, out GamePatchSchema cached)) return cached;

            return await ExecuteAsync(async () =>
            {
                GenericResponse<GamePatchSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GamePatchSchema>>(
                    $"{Client.GetApiUrl()}/v1/game_patch/{configId}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                _patchByIdCache[configId] = response.Result;
                return response.Result;
            }, $"Fetch config {configId}", cancellationToken);
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

            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/game_patch/config/{schemaId}";
                GenericResponse<List<GamePatchSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                _patchesBySchemaCache[schemaId] = response.Result;
                return response.Result;
            }, $"Fetch configs for schema {schemaId}", cancellationToken);
        }

        public async Task<List<T>> GetBySchemaAsync<T>(string schemaId, CancellationToken cancellationToken = default)
        {
            List<GamePatchSchema> configs = await GetBySchemaAsync(schemaId, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        public async Task<List<GameConfigSchema>> GetGameConfigsAsync(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            if (_gameConfigsByTagCache.TryGetValue(tag, out List<GameConfigSchema> cached)) return cached;

            return await ExecuteAsync(async () =>
            {
                string url = tag != SchemaTag.empty ? $"{Client.GetApiUrl()}/v1/game_config?tag={tag}" : $"{Client.GetApiUrl()}/v1/game_config";
                GenericResponse<List<GameConfigSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                _gameConfigsByTagCache[tag] = response.Result;
                return response.Result;
            }, "Fetch game configs", cancellationToken);
        }

        public async Task<List<T>> GetGameConfigsAsync<T>(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            List<GameConfigSchema> configs = await GetGameConfigsAsync(tag, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        public async Task<List<GameConfigSchema>> GetGameConfigsByVersionAsync(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            if (_gameConfigsByVersionCache.TryGetValue(tag, out List<GameConfigSchema> cached)) return cached;

            return await ExecuteAsync(async () =>
            {
                string url = tag != SchemaTag.empty ? $"{Client.GetApiUrl()}/v1/game_config/version?tag={tag}" : $"{Client.GetApiUrl()}/v1/game_config/version";
                GenericResponse<List<GameConfigSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                _gameConfigsByVersionCache[tag] = response.Result;
                return response.Result;
            }, "Fetch game configs by version", cancellationToken);
        }

        public async Task<List<T>> GetGameConfigsByVersionAsync<T>(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            List<GameConfigSchema> configs = await GetGameConfigsByVersionAsync(tag, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        public async Task<GameConfigSchema> GetPlayerFeaturesAsync(string playerId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerId, "Player ID");

            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/game_config/player/{playerId}/features";
                GenericResponse<GameConfigSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameConfigSchema>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch feature config for player {playerId}", cancellationToken);
        }

        public async Task<T> GetPlayerFeaturesAsync<T>(string playerId, CancellationToken cancellationToken = default)
        {
            GameConfigSchema config = await GetPlayerFeaturesAsync(playerId, cancellationToken);
            return config.GetDataAs<T>();
        }
    }
}
