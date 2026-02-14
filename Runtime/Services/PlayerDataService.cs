using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;
using Flock.Exceptions;

namespace Flock.Services
{
    public class PlayerDataService : IPlayerDataService
    {
        private readonly FlockClient _client;
        private readonly string _baseUrl;

        public PlayerDataService(FlockClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _baseUrl = $"{_client.GetApiUrl()}/player-data";
        }

        public async Task<PlayerData> GetPlayerDataAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug("Fetching player data for current player");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<PlayerData>>(
                        _baseUrl,
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug("Successfully fetched player data");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError("Failed to get player data", ex);
                throw new FlockNetworkException("Failed to fetch player data", ex);
            }
        }

        public async Task<PlayerData> UpdatePlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default)
        {
            if (data == null)
            {
                throw new FlockValidationException("Player data cannot be null");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug("Updating player data for current player");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.PutAsync<GenericResponse<PlayerData>>(
                        _baseUrl,
                        data,
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogInfo("Successfully updated player data");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError("Failed to update player data", ex);
                throw new FlockNetworkException("Failed to update player data", ex);
            }
        }

        public async Task<PlayerData> CreateAsync(string playerId, Dictionary<string, object> data, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                throw new FlockValidationException("Player ID cannot be null or empty");
            }

            if (data == null)
            {
                throw new FlockValidationException("Data cannot be null");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Creating player data for player: {playerId}");

                    var request = new PlayerDataRequest
                    {
                        GameId = _client.GameId,
                        PlayerId = playerId,
                        Data = data
                    };

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.PostAsync<GenericResponse<PlayerData>>(
                        _baseUrl,
                        request,
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogInfo($"Successfully created player data for player: {playerId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to create player data for {playerId}", ex);
                throw new FlockNetworkException($"Failed to create player data for {playerId}", ex);
            }
        }

        public async Task<PlayerData> GetByIdAsync(string playerDataId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(playerDataId))
            {
                throw new FlockValidationException("Player data ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching player data by ID: {playerDataId}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<PlayerData>>(
                        $"{_baseUrl}/{playerDataId}",
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched player data: {playerDataId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get player data {playerDataId}", ex);
                throw new FlockNetworkException($"Failed to fetch player data {playerDataId}", ex);
            }
        }

        public async Task<PaginatedResponse<PlayerData>> GetAllAsync(int page = 1, int limit = 10, string playerId = null, CancellationToken cancellationToken = default)
        {
            if (page < 1)
            {
                throw new FlockValidationException("Page must be greater than 0");
            }

            if (limit < 1 || limit > 100)
            {
                throw new FlockValidationException("Limit must be between 1 and 100");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching all player data, page: {page}, limit: {limit}");

                    var url = $"{_baseUrl}?game_id={_client.GameId}&page={page}&limit={limit}";
                    if (!string.IsNullOrEmpty(playerId))
                    {
                        url += $"&player_id={playerId}";
                    }

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<PaginatedResponse<PlayerData>>(
                        url,
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Items?.Length ?? 0} player data entries");
                    return response;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError("Failed to get all player data", ex);
                throw new FlockNetworkException("Failed to fetch all player data", ex);
            }
        }

        public async Task<PlayerData> UpdateAsync(string playerDataId, Dictionary<string, object> data, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(playerDataId))
            {
                throw new FlockValidationException("Player data ID cannot be null or empty");
            }

            if (data == null)
            {
                throw new FlockValidationException("Data cannot be null");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Updating player data: {playerDataId}");

                    var request = new UpdatePlayerDataRequest
                    {
                        Data = data
                    };

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.PutAsync<GenericResponse<PlayerData>>(
                        $"{_baseUrl}/{playerDataId}",
                        request,
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogInfo($"Successfully updated player data: {playerDataId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to update player data {playerDataId}", ex);
                throw new FlockNetworkException($"Failed to update player data {playerDataId}", ex);
            }
        }
    }
}
