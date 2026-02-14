using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Exceptions;

namespace Flock.Services
{
    //TODO find a solution for paths , should all of them be hard coded in their features?
    public class FlockGamePatchProvider
    {
        private readonly FlockClient _client;

        public FlockGamePatchProvider(FlockClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<List<GamePatchSchema>> GetAllPatchesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug("Fetching all game patches");

                    var response = await HttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                        $"{_client.GetApiUrl()}/v1/game_patch",
                        _client.GetBaseHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Result.Count} game patches");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError("Failed to get game patches", ex);
                throw new FlockNetworkException("Failed to fetch game patches", ex);
            }
        }

        public async Task<GamePatchSchema> GetPatchByIdAsync(string patchId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(patchId))
            {
                throw new FlockValidationException("Patch ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching game patch: {patchId}");

                    var response = await HttpClient.GetAsync<GenericResponse<GamePatchSchema>>(
                        $"{_client.GetApiUrl()}/v1/game_patch/{patchId}",
                        _client.GetBaseHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched game patch: {patchId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get game patch {patchId}", ex);
                throw new FlockNetworkException($"Failed to fetch game patch {patchId}", ex);
            }
        }

        public async Task<List<GamePatchSchema>> GetPatchesByConfigIdAsync(string gameConfigId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(gameConfigId))
            {
                throw new FlockValidationException("Game config ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching game patches for config: {gameConfigId}");

                    var response = await HttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                        $"{_client.GetApiUrl()}/v1/game_patch/config/{gameConfigId}",
                        _client.GetBaseHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Result.Count} patches for config: {gameConfigId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get patches for config {gameConfigId}", ex);
                throw new FlockNetworkException($"Failed to fetch patches for config {gameConfigId}", ex);
            }
        }
    }
}
