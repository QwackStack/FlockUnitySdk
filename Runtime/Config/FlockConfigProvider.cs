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
        private readonly string _baseUrl;

        public FlockConfigProvider(FlockClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _baseUrl = $"{_client.GetApiUrl()}/game-config";
        }

        /// <summary>
        /// Gets all game configurations for the current game
        /// </summary>
        public async Task<List<GameConfig>> GetAllConfigAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching all game configurations for game: {_client.GameId}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<List<GameConfig>>>(
                        _baseUrl,
                        token,
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
            catch (OperationCanceledException)
            {
                _client.Logger.LogWarning("Get all game configurations operation was cancelled");
                throw;
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

        /// <summary>
        /// Gets a specific game configuration by ID
        /// </summary>
        public async Task<GameConfig> GetConfigByIdAsync(string configId, CancellationToken cancellationToken = default)
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

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<GameConfig>>(
                        $"{_baseUrl}/{configId}",
                        token,
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
            catch (OperationCanceledException)
            {
                _client.Logger.LogWarning($"Get game configuration {configId} operation was cancelled");
                throw;
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
    }
}
