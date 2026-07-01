using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Providers
{
    public class FlockGameProvider : FlockProviderBase
    {
        private const string SnapshotCategory = "game";

        private GameSchema _game;
        private GameVersionSchema _gameVersion;
        private readonly Dictionary<string, GameVersionSchema> _gameVersionsByName = new Dictionary<string, GameVersionSchema>();

        public FlockGameProvider(FlockClient client) : base(client) { }

        public void ClearCache()
        {
            _game = null;
            _gameVersion = null;
            _gameVersionsByName.Clear();
            DeleteSnapshotCategory(SnapshotCategory);
        }

        public async Task<GameSchema> GetGameAsync(CancellationToken cancellationToken = default)
        {
            if (_game != null)
                return _game;

            _game = await FetchWithSnapshotAsync(
                SnapshotCategory, "game", async () =>
                {
                    string url = $"{Client.GetVersionedApiUrl()}/game";
                    GenericResponse<GameSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameSchema>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch game info", cancellationToken);

            return _game;
        }

        public async Task<GameVersionSchema> GetGameVersionAsync(CancellationToken cancellationToken = default)
        {
            if (_gameVersion != null)
                return _gameVersion;

            _gameVersion = await FetchWithSnapshotAsync(
                SnapshotCategory, "game_version", async () =>
                {
                    string url = $"{Client.GetVersionedApiUrl()}/game_version";
                    GenericResponse<GameVersionSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch game version", cancellationToken);

            return _gameVersion;
        }

        public async Task<GameVersionSchema> GetGameVersionByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(name, "Game Version Name");
            if (_gameVersionsByName.TryGetValue(name, out GameVersionSchema cached))
                return cached;

            // Uses the BootstrapScope snapshot namespace for by-name version lookups (populated lazily
            // on first call now that init no longer pre-resolves the version).
            GameVersionSchema version = await FetchAtScopeAsync(
                FlockSnapshotStore.BootstrapScope, $"{Client.GetApiUrl()}|{name}", async () =>
                {
                    string url = $"{Client.GetVersionedApiUrl()}/game_version/by-name/{System.Uri.EscapeDataString(name)}";
                    GenericResponse<GameVersionSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch game version By Name", cancellationToken);

            _gameVersionsByName[name] = version;
            return version;
        }
    }
}
