using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;
using Flock.Exceptions;

namespace Flock.Achievements
{
    public class FlockAchievementProvider : IAchievementProvider
    {
        private readonly string _apiUrl;
        private readonly FlockClient _client;
        private readonly string _baseUrl;

        public FlockAchievementProvider(FlockClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _apiUrl = client.GetApiUrl();
            _baseUrl = $"{_apiUrl}/achievements";
        }

        /// <summary>
        /// Gets all achievements for the current game
        /// </summary>
        public async Task<List<Achievement>> GetAllAchievementsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching all achievements for game: {_client.GameId}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<List<Achievement>>>(
                        $"{_baseUrl}?game_id={_client.GameId}",
                        token,
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Result.Count} achievements");
                    return response.Result;
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _client.Logger.LogWarning("Get all achievements operation was cancelled");
                throw;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError("Failed to get all achievements", ex);
                throw new FlockNetworkException("Failed to fetch achievements", ex);
            }
        }

        /// <summary>
        /// Gets a specific achievement by ID
        /// </summary>
        public async Task<Achievement> GetAchievementByIdAsync(string achievementId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(achievementId))
            {
                throw new FlockValidationException("Achievement ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching achievement: {achievementId}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<Achievement>>(
                        $"{_baseUrl}/{achievementId}",
                        token,
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched achievement: {achievementId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _client.Logger.LogWarning($"Get achievement {achievementId} operation was cancelled");
                throw;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get achievement {achievementId}", ex);
                throw new FlockNetworkException($"Failed to fetch achievement {achievementId}", ex);
            }
        }

        /// <summary>
        /// Gets all achievements for a specific player
        /// </summary>
        public async Task<List<Achievement>> GetPlayerAchievementsAsync(string playerId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                throw new FlockValidationException("Player ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching achievements for player: {playerId}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<List<Achievement>>>(
                        $"{_baseUrl}/player/{playerId}?game_id={_client.GameId}",
                        token,
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Result.Count} achievements for player: {playerId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _client.Logger.LogWarning($"Get player achievements for {playerId} operation was cancelled");
                throw;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get achievements for player {playerId}", ex);
                throw new FlockNetworkException($"Failed to fetch achievements for player {playerId}", ex);
            }
        }

        /// <summary>
        /// Unlocks an achievement for a player
        /// </summary>
        public async Task<Achievement> UnlockAchievementAsync(string playerId, string achievementId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                throw new FlockValidationException("Player ID cannot be null or empty");
            }

            if (string.IsNullOrEmpty(achievementId))
            {
                throw new FlockValidationException("Achievement ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Unlocking achievement {achievementId} for player: {playerId}");

                    var request = new UnlockAchievementRequest
                    {
                        PlayerId = playerId,
                        AchievementId = achievementId
                    };

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.PostAsync<GenericResponse<Achievement>>(
                        $"{_baseUrl}/unlock",
                        request,
                        token,
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogInfo($"Achievement {achievementId} unlocked for player: {playerId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _client.Logger.LogWarning($"Unlock achievement {achievementId} operation was cancelled");
                throw;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to unlock achievement {achievementId} for player {playerId}", ex);
                throw new FlockNetworkException($"Failed to unlock achievement {achievementId}", ex);
            }
        }

        /// <summary>
        /// Updates achievement progress for a player
        /// </summary>
        public async Task<Achievement> UpdateProgressAsync(string playerId, string achievementId, float progress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                throw new FlockValidationException("Player ID cannot be null or empty");
            }

            if (string.IsNullOrEmpty(achievementId))
            {
                throw new FlockValidationException("Achievement ID cannot be null or empty");
            }

            if (progress < 0 || progress > 100)
            {
                throw new FlockValidationException("Progress must be between 0 and 100");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Updating achievement {achievementId} progress to {progress}% for player: {playerId}");

                    var request = new UpdateAchievementProgressRequest
                    {
                        PlayerId = playerId,
                        AchievementId = achievementId,
                        Progress = progress
                    };

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.PutAsync<GenericResponse<Achievement>>(
                        $"{_baseUrl}/progress",
                        request,
                        token,
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Achievement {achievementId} progress updated to {progress}% for player: {playerId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _client.Logger.LogWarning($"Update achievement {achievementId} progress operation was cancelled");
                throw;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to update achievement {achievementId} progress for player {playerId}", ex);
                throw new FlockNetworkException($"Failed to update achievement {achievementId} progress", ex);
            }
        }
    }
}
