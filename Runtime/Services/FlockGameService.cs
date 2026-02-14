using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Exceptions;

namespace Flock.Services
{
    public class FlockGameService
    {
        private readonly FlockClient _client;

        public FlockGameService(FlockClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        // GET /v1/game
        public async Task<GameSchema> GetGameAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug("Fetching game info");

                    var response = await HttpClient.GetAsync<GenericResponse<GameSchema>>(
                        $"{_client.GetApiUrl()}/v1/game",
                        _client.GetBaseHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched game: {response.Result.Name}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError("Failed to get game info", ex);
                throw new FlockNetworkException("Failed to fetch game info", ex);
            }
        }

        // GET /v1/game_version
        public async Task<GameVersionSchema> GetGameVersionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug("Fetching game version info");

                    var response = await HttpClient.GetAsync<GenericResponse<GameVersionSchema>>(
                        $"{_client.GetApiUrl()}/v1/game_version",
                        _client.GetBaseHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched game version: {response.Result.Name}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError("Failed to get game version info", ex);
                throw new FlockNetworkException("Failed to fetch game version info", ex);
            }
        }
    }
}
