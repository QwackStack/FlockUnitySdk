using System.Collections.Generic;
using System.ComponentModel;
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
        // every patch/config lives here exactly once, keyed by id.
        private readonly Dictionary<string, GamePatchSchema> _patchesById = new Dictionary<string, GamePatchSchema>();
        private readonly Dictionary<string, GameConfigSchema> _gameConfigsById = new Dictionary<string, GameConfigSchema>();

       
        private readonly Dictionary<string, List<string>> _patchIdsBySchema = new Dictionary<string, List<string>>();
        private bool _allPatchesFetched;
        private Task<List<GamePatchSchema>> _allPatchesFetchTask;

        private readonly Dictionary<SchemaTag, List<string>> _configIdsByTag = new Dictionary<SchemaTag, List<string>>();
        private readonly Dictionary<SchemaTag, List<string>> _configIdsByVersionTag = new Dictionary<SchemaTag, List<string>>();
        private readonly Dictionary<string, string> _configIdByName = new Dictionary<string, string>();

        // Per-player resolved features — not the same object as a plain config lookup, so it isn't folded into _gameConfigsById.
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
            _patchIdsBySchema.Clear();
            _allPatchesFetched = false;
            _allPatchesFetchTask = null;
            _gameConfigsById.Clear();
            _configIdsByTag.Clear();
            _configIdsByVersionTag.Clear();
            _configIdByName.Clear();
            _playerFeaturesByPlayer.Clear();
            DeleteSnapshotCategory(SnapshotCategory);
        }

        internal Task<List<GamePatchSchema>> GetAllAsync(CancellationToken cancellationToken = default)
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
                    SnapshotCategory, "game_patch_all", async () =>
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

        internal async Task<List<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
        {
            List<GamePatchSchema> configs = await GetAllAsync(cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        internal async Task<GamePatchSchema> GetByIdAsync(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");
            if (_patchesById.TryGetValue(configId, out GamePatchSchema cached)) return cached;

            GamePatchSchema patch = await FetchWithSnapshotAsync(SnapshotCategory, $"game_patch_{configId}", async () =>
                {
                    GenericResponse<GamePatchSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GamePatchSchema>>(
                        $"{Client.GetVersionedApiUrl()}/game_patch/{configId}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, $"Fetch config {configId}", cancellationToken);

            IndexPatch(patch);
            return patch;
        }

        internal async Task<T> GetByIdAsync<T>(string configId, CancellationToken cancellationToken = default)
        {
            GamePatchSchema config = await GetByIdAsync(configId, cancellationToken);
            return config.GetDataAs<T>();
        }

        internal async Task<List<GamePatchSchema>> GetBySchemaAsync(string schemaId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(schemaId, "Schema ID");
            if (_patchIdsBySchema.TryGetValue(schemaId, out List<string> cachedIds)) return ResolvePatches(cachedIds);

            List<GamePatchSchema> patches = await FetchWithSnapshotAsync(
                SnapshotCategory, $"game_patch_schema_{schemaId}", async () =>
                {
                    string url = $"{Client.GetVersionedApiUrl()}/game_patch/config/{schemaId}";
                    GenericResponse<List<GamePatchSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, $"Fetch configs for schema {schemaId}", cancellationToken);

            List<string> ids = new List<string>(patches.Count);
            foreach (GamePatchSchema p in patches)
            {
                IndexPatch(p);
                if (!string.IsNullOrEmpty(p.Id)) ids.Add(p.Id);
            }
            _patchIdsBySchema[schemaId] = ids;
            return patches;
        }

        internal async Task<List<T>> GetBySchemaAsync<T>(string schemaId, CancellationToken cancellationToken = default)
        {
            List<GamePatchSchema> configs = await GetBySchemaAsync(schemaId, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        /// <summary>Resolves a config's current values as <typeparamref name="T"/>: the patch's data when a patch exists for this game version, otherwise the config's own data. Codegen entry point — consumers use the generated accessors.</summary>
        public async Task<T> GetByConfigIdAsync<T>(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");
            List<GamePatchSchema> patches = await GetBySchemaAsync(configId, cancellationToken);
            if (patches != null && patches.Count > 0)
                return patches[0].GetDataAs<T>();

            // No patch on this game version — fall back to the config's own base data instead of returning empty defaults.
            GameConfigSchema config = await GetConfigByIdAsync(configId, cancellationToken);
            return config != null ? config.GetDataAs<T>() : default;
        }

        // Single config by id (game_config/{id}); the no-patch fallback for GetByConfigIdAsync<T>.
        internal async Task<GameConfigSchema> GetConfigByIdAsync(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");
            if (_gameConfigsById.TryGetValue(configId, out GameConfigSchema cached)) return cached;

            GameConfigSchema config = await FetchWithSnapshotAsync(
                SnapshotCategory, $"game_config_{configId}", async () =>
                {
                    GenericResponse<GameConfigSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameConfigSchema>>(
                        $"{Client.GetVersionedApiUrl()}/game_config/{configId}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, $"Fetch config {configId}", cancellationToken);

            // Keyed by the requested id (not config.Id) so a hit is guaranteed on the next call with this same configId.
            if (config != null)
                _gameConfigsById[configId] = config;
            return config;
        }

        private void IndexPatch(GamePatchSchema patch)
        {
            if (patch == null || string.IsNullOrEmpty(patch.Id)) return;
            _patchesById[patch.Id] = patch;
        }

        private List<GamePatchSchema> ResolvePatches(List<string> ids)
        {
            List<GamePatchSchema> result = new List<GamePatchSchema>(ids.Count);
            foreach (string id in ids)
                if (_patchesById.TryGetValue(id, out GamePatchSchema patch))
                    result.Add(patch);
            return result;
        }

        private void IndexConfig(GameConfigSchema config)
        {
            if (config == null || string.IsNullOrEmpty(config.Id)) return;
            _gameConfigsById[config.Id] = config;
        }

        private List<GameConfigSchema> ResolveConfigs(List<string> ids)
        {
            List<GameConfigSchema> result = new List<GameConfigSchema>(ids.Count);
            foreach (string id in ids)
                if (_gameConfigsById.TryGetValue(id, out GameConfigSchema config))
                    result.Add(config);
            return result;
        }

        internal async Task<List<GameConfigSchema>> GetGameConfigsAsync(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            if (_configIdsByTag.TryGetValue(tag, out List<string> cachedIds)) return ResolveConfigs(cachedIds);

            List<GameConfigSchema> configs = await FetchWithSnapshotAsync(
                SnapshotCategory, $"game_config_tag_{tag}", async () =>
                {
                    string url = tag != SchemaTag.empty ? $"{Client.GetVersionedApiUrl()}/game_config?tag={tag}" : $"{Client.GetVersionedApiUrl()}/game_config";
                    GenericResponse<List<GameConfigSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch game configs", cancellationToken);

            List<string> ids = new List<string>(configs.Count);
            foreach (GameConfigSchema c in configs)
            {
                IndexConfig(c);
                if (!string.IsNullOrEmpty(c.Id)) ids.Add(c.Id);
            }
            _configIdsByTag[tag] = ids;
            return configs;
        }

        internal async Task<List<T>> GetGameConfigsAsync<T>(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            List<GameConfigSchema> configs = await GetGameConfigsAsync(tag, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        internal async Task<List<GameConfigSchema>> GetGameConfigsByVersionAsync(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            if (_configIdsByVersionTag.TryGetValue(tag, out List<string> cachedIds)) return ResolveConfigs(cachedIds);

            List<GameConfigSchema> configs = await FetchWithSnapshotAsync(
                SnapshotCategory, $"game_config_version_tag_{tag}", async () =>
                {
                    string url = tag != SchemaTag.empty ? $"{Client.GetVersionedApiUrl()}/game_config/version?tag={tag}" : $"{Client.GetVersionedApiUrl()}/game_config/version";
                    GenericResponse<List<GameConfigSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch game configs by version", cancellationToken);

            List<string> ids = new List<string>(configs.Count);
            foreach (GameConfigSchema c in configs)
            {
                IndexConfig(c);
                if (!string.IsNullOrEmpty(c.Id)) ids.Add(c.Id);
            }
            _configIdsByVersionTag[tag] = ids;
            return configs;
        }

        internal async Task<List<T>> GetGameConfigsByVersionAsync<T>(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            List<GameConfigSchema> configs = await GetGameConfigsByVersionAsync(tag, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        internal async Task<GameConfigSchema> GetGameConfigByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(name, "Game Config Name");
            if (_configIdByName.TryGetValue(name, out string cachedId) && _gameConfigsById.TryGetValue(cachedId, out GameConfigSchema cached))
                return cached;

            GameConfigSchema config = await FetchWithSnapshotAsync(
                SnapshotCategory, $"game_config_name_{name}", async () =>
                {
                    string url = $"{Client.GetVersionedApiUrl()}/game_config/by-name/{System.Uri.EscapeDataString(name)}";
                    GenericResponse<GameConfigSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameConfigSchema>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, $"Fetch game config by name {name}", cancellationToken);

            IndexConfig(config);
            if (config != null && !string.IsNullOrEmpty(config.Id))
                _configIdByName[name] = config.Id;
            return config;
        }

        internal async Task<GameConfigSchema> GetPlayerFeaturesAsync(string playerId, CancellationToken cancellationToken = default)
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

        internal async Task<T> GetPlayerFeaturesAsync<T>(string playerId, CancellationToken cancellationToken = default)
        {
            GameConfigSchema config = await GetPlayerFeaturesAsync(playerId, cancellationToken);
            return config.GetDataAs<T>();
        }
    }
}
