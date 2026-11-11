using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
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

        // The configs as the signed-in player reads them, held for one sign-in only; a null entry means the game's read answers that config.
        private readonly Dictionary<string, GameConfigSchema> _playerConfigsById = new Dictionary<string, GameConfigSchema>();
        // The player's copies on disk, read once a sign-in (a null entry: none), so offline reads do not read the file each time.
        private readonly Dictionary<string, GameConfigSchema> _savedPlayerConfigsById = new Dictionary<string, GameConfigSchema>();
        // When a config's player read, which got no answer, may be sent again; until then the saved copy, else the game's read, answers at once.
        private readonly Dictionary<string, DateTime> _playerReadsPausedUntil = new Dictionary<string, DateTime>();
        private int _playerConfigsSignIn;
        // Counts ClearCache calls, so a player's read on its way when one ran keeps nothing.
        private int _clears;
        // The sign-in last warned that the server got no Game Version, so the warning comes once a sign-in.
        private int _warnedOfNoGameVersionSignIn = -1;

        private const string NoGameVersionWarning = "Config values are the game's, not the signed-in player's: the server has no Game Version to read them for, as the build sent none (or a proxy dropped it) and none is recorded for this player, so their segments' overrides and their own are left out. "
            + "Fix: resolve the Game Version in Flock > Settings > Configuration so the build sends it; the player then gets their own values from their next signed-in read.";

        // The tags of the configs the server resolves for a player; any other comes back as stored, without its patch.
        private static readonly string[] TagsResolvedForThePlayer = { nameof(SchemaTag.gameplay), nameof(SchemaTag.feature) };

        private const string SnapshotCategory = "config";

        /// <summary>How long a config's player read is not sent again after it got no answer.</summary>
        internal TimeSpan PlayerReadPauseAfterNoAnswer { get; set; } = TimeSpan.FromSeconds(30);

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
            _playerConfigsById.Clear();
            _savedPlayerConfigsById.Clear();
            _playerReadsPausedUntil.Clear();
            _clears++;
            DeleteSnapshotCategory(SnapshotCategory);
        }

        internal Task<List<GamePatchSchema>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            if (_allPatchesFetched)
                return Task.FromResult(new List<GamePatchSchema>(_patchesById.Values));
            if (_allPatchesFetchTask != null)
                return _allPatchesFetchTask;

            // Never pin a task that already finished — see the same guard in FlockAssetProvider.GetAllAsync.
            Task<List<GamePatchSchema>> fetch = FetchAllPatchesAsync(cancellationToken);
            _allPatchesFetchTask = fetch.IsCompleted ? null : fetch;
            return fetch;
        }

        private async Task<List<GamePatchSchema>> FetchAllPatchesAsync(CancellationToken cancellationToken)
        {
            try
            {
                List<GamePatchSchema> patches = await FetchWithSnapshotAsync(
                    SnapshotCategory, "game_patch_all", async () =>
                    {
                        GenericResponse<List<GamePatchSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                            $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GamePatch}", Client.GetBaseHeaders(), cancellationToken);
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
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GamePatchById(configId)}", Client.GetBaseHeaders(), cancellationToken);
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
                    string url = $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GamePatchByConfig(schemaId)}";
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

        /// <summary>Resolves a config's current values as <typeparamref name="T"/>: with a player signed in, as the server resolves them for that player (the newest patch, then the overrides of the segments they are in, then their own); with nobody signed in, the patch's data when a patch exists for this game version, otherwise the config's own data. Codegen entry point — consumers use the generated accessors.</summary>
        public async Task<T> GetByConfigIdAsync<T>(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");
            GameConfigSchema forThePlayer = await ReadForTheSignedInPlayerAsync(configId, cancellationToken);
            if (forThePlayer != null)
                return forThePlayer.GetDataAs<T>();

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
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GameConfigById(configId)}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, $"Fetch config {configId}", cancellationToken);

            // Keyed by the requested id (not config.Id) so a hit is guaranteed on the next call with this same configId.
            if (config != null)
                _gameConfigsById[configId] = config;
            return config;
        }

        // The config as the signed-in player reads it, held for that sign-in and on disk for that player; null with nobody signed in
        // or when the player's read cannot answer, so the game's read answers as it does for a player signed out.
        private async Task<GameConfigSchema> ReadForTheSignedInPlayerAsync(string configId, CancellationToken cancellationToken)
        {
            while (true)
            {
                int? signIn = SignInToActFor;
                string playerId = Client.CurrentPlayerId;
                if (signIn == null || string.IsNullOrEmpty(playerId))
                    return null;
                if (PlayerConfigsOf(signIn.Value).TryGetValue(configId, out GameConfigSchema held))
                    return held;

                string key = $"player_config_{configId}_{playerId}";
                GameConfigSchema saved = SavedPlayerConfig(configId, key);
                // No connection, or no answer a moment ago: the player's saved copy, else the game's read, at once.
                if (!IsServerReachable() || (_playerReadsPausedUntil.TryGetValue(configId, out DateTime pausedUntil) && DateTime.UtcNow < pausedUntil))
                    return saved;

                int clears = _clears;
                GameConfigSchema answer;
                try
                {
                    answer = await FetchForThePlayerAsync(configId, signIn.Value, saved != null, cancellationToken);
                }
                // The sign-in ended before a retry went out, or while the read was on its way: read again for whoever is signed in now.
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && signIn.Value != Client.SignInNumber)
                {
                    continue;
                }
                catch (FlockException) when (signIn.Value != Client.SignInNumber)
                {
                    continue;
                }
                catch (FlockException failed) when (failed is FlockNetworkException && !FlockNetworkException.IsPermanentStatus(failed.StatusCode))
                {
                    if (clears == _clears)
                        _playerReadsPausedUntil[configId] = DateTime.UtcNow + PlayerReadPauseAfterNoAnswer;
                    Client.Logger.LogDebug($"Config {configId}: the player's read got no answer, so {(saved != null ? "the player's saved copy" : "the game's read")} answers for now ({failed.StatusCode?.ToString() ?? "no answer"})");
                    return saved;
                }
                // A refusal, or a failure that will not change during this sign-in: the game's read answers for the rest of it.
                catch (FlockException refused)
                {
                    if (clears == _clears)
                        LeaveToTheGamesRead(signIn.Value, configId, key, IsNotForThePlayersRead(refused));
                    if (refused.ErrorCode == FlockErrorCode.GameConfigPlayerNoGameVersion && _warnedOfNoGameVersionSignIn != signIn.Value)
                    {
                        _warnedOfNoGameVersionSignIn = signIn.Value;
                        Client.Logger.LogWarning(NoGameVersionWarning);
                    }
                    else
                        Client.Logger.LogDebug($"Config {configId}: the player's read cannot answer it, so the game's read does for this sign-in ({refused.Code ?? refused.StatusCode?.ToString()})");
                    return null;
                }

                // Answered after its sign-in ended: kept nowhere, and read again for whoever is signed in now.
                if (signIn.Value != Client.SignInNumber)
                    continue;
                // Another config's values (a rename during the launch, another version's config of that name) are not this one's.
                bool forThisConfig = answer != null && answer.Id == configId;
                if (clears == _clears)
                {
                    if (forThisConfig)
                    {
                        PlayerConfigsOf(signIn.Value)[configId] = answer;
                        _savedPlayerConfigsById[configId] = answer;
                        WriteSnapshot(SnapshotCategory, key, answer);
                    }
                    else
                        LeaveToTheGamesRead(signIn.Value, configId, key, answer != null);
                }
                return forThisConfig ? answer : null;
            }
        }

        // Read by the config's name, which the game's read by id gives (kept, so asked once a launch); every try goes out as this sign-in or not at all.
        // Null for a config the server does not resolve for a player, which the game's read answers with its patch.
        private async Task<GameConfigSchema> FetchForThePlayerAsync(string configId, int signIn, bool hasSaved, CancellationToken cancellationToken)
        {
            GameConfigSchema config = await GetConfigByIdAsync(configId, cancellationToken);
            string name = config?.Name;
            if (string.IsNullOrEmpty(name) || Array.FindIndex(TagsResolvedForThePlayer, tag => string.Equals(tag, config.Tag, StringComparison.OrdinalIgnoreCase)) < 0)
                return null;

            return await ExecuteAsync(async () =>
            {
                GenericResponse<GameConfigSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameConfigSchema>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GameConfigForMeByName(name)}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch config {name} for the signed-in player", cancellationToken, maxRetriesOverride: hasSaved ? 0 : (int?)null, actsForSignIn: signIn);
        }

        // A refusal saying the player's read cannot answer this config (no such player, no version to read, no config by that name or id).
        private static bool IsNotForThePlayersRead(FlockException refused)
            => refused.ErrorCode == FlockErrorCode.GameConfigPlayerNotFound
               || refused.ErrorCode == FlockErrorCode.GameConfigPlayerNoGameVersion
               || refused.ErrorCode == FlockErrorCode.GameConfigConfigNotFound;

        // The game's read answers this config for the rest of the sign-in; a saved copy the server no longer stands behind is deleted.
        private void LeaveToTheGamesRead(int signIn, string configId, string key, bool deleteTheSavedCopy)
        {
            PlayerConfigsOf(signIn)[configId] = null;
            if (!deleteTheSavedCopy)
                return;
            _savedPlayerConfigsById[configId] = null;
            Client.SnapshotStore?.Delete(GetSnapshotScope(SnapshotCategory), key);
        }

        // The player's saved copy of a config, read from disk once a sign-in.
        private GameConfigSchema SavedPlayerConfig(string configId, string key)
        {
            if (_savedPlayerConfigsById.TryGetValue(configId, out GameConfigSchema saved))
                return saved;
            TryReadSnapshot(SnapshotCategory, key, out saved);
            _savedPlayerConfigsById[configId] = saved;
            return saved;
        }

        // The player's configs, emptied when the sign-in they were read for is no longer the one asked about.
        private Dictionary<string, GameConfigSchema> PlayerConfigsOf(int signIn)
        {
            if (_playerConfigsSignIn != signIn)
            {
                _playerConfigsById.Clear();
                _savedPlayerConfigsById.Clear();
                _playerReadsPausedUntil.Clear();
                _playerConfigsSignIn = signIn;
            }
            return _playerConfigsById;
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
                    string url = tag != SchemaTag.empty ? $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GameConfig}?tag={tag}" : $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GameConfig}";
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
                    string url = tag != SchemaTag.empty ? $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GameConfigVersion}?tag={tag}" : $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GameConfigVersion}";
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
                    string url = $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GameConfigByName(name)}";
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

        // A player's features are kept in memory for the run (_playerFeaturesByPlayer): the first read of a player fetches,
        // later reads of the same player answer from memory, and ClearCache() or a new client fetches again. Never written to
        // the offline snapshot, so a new launch always asks the server.
        internal async Task<GameConfigSchema> GetPlayerFeaturesAsync(string playerId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerId, "Player ID");
            if (_playerFeaturesByPlayer.TryGetValue(playerId, out GameConfigSchema cachedFeatures))
                return cachedFeatures;

            GameConfigSchema features = await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.GameConfigPlayerFeatures(playerId)}";
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
