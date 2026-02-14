using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;
using Flock.Exceptions;

namespace Flock.Config
{
    public class FlockConfigProvider : IConfigProvider
    {
        private readonly FlockClient _client;

        public FlockConfigProvider(FlockClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        // GET /v1/game_config
        public async Task<List<GameConfigSchema>> GetAllConfigsAsync(string tag = null, CancellationToken cancellationToken = default)
        {
            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug("Fetching all game configurations");

                    var url = $"{_client.GetApiUrl()}/v1/game_config";
                    if (!string.IsNullOrEmpty(tag))
                    {
                        url += $"?tag={Uri.EscapeDataString(tag)}";
                    }

                    var response = await HttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                        url,
                        _client.GetBaseHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Result.Count} game configurations");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError("Failed to get all game configurations", ex);
                throw new FlockNetworkException("Failed to fetch game configurations", ex);
            }
        }

        // GET /v1/game_config/version
        public async Task<List<GameConfigSchema>> GetConfigsByVersionAsync(string tag = null, CancellationToken cancellationToken = default)
        {
            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug("Fetching game configurations by version");

                    var url = $"{_client.GetApiUrl()}/v1/game_config/version";
                    if (!string.IsNullOrEmpty(tag))
                    {
                        url += $"?tag={Uri.EscapeDataString(tag)}";
                    }

                    var response = await HttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                        url,
                        _client.GetBaseHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Result.Count} game configurations by version");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError("Failed to get game configurations by version", ex);
                throw new FlockNetworkException("Failed to fetch game configurations by version", ex);
            }
        }

        // GET /v1/game_config/{game_config_id}
        public async Task<GameConfigSchema> GetConfigByIdAsync(string configId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(configId))
            {
                throw new FlockValidationException("Config ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching game configuration: {configId}");

                    var response = await HttpClient.GetAsync<GenericResponse<GameConfigSchema>>(
                        $"{_client.GetApiUrl()}/v1/game_config/{configId}",
                        _client.GetBaseHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched game configuration: {configId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get game configuration {configId}", ex);
                throw new FlockNetworkException($"Failed to fetch game configuration {configId}", ex);
            }
        }

        // GET /v1/game_config/{game_config_id}/patches
        public async Task<List<GamePatchSchema>> GetConfigPatchesAsync(string configId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(configId))
            {
                throw new FlockValidationException("Config ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching patches for game configuration: {configId}");

                    var response = await HttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                        $"{_client.GetApiUrl()}/v1/game_config/{configId}/patches",
                        _client.GetBaseHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Result.Count} patches for config: {configId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get patches for config {configId}", ex);
                throw new FlockNetworkException($"Failed to fetch patches for config {configId}", ex);
            }
        }
    }
}
