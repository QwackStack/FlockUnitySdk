using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;
using Flock.Exceptions;

namespace Flock.Leaderboard
{
    public class FlockLeaderboardProvider : ILeaderboardProvider
    {
        private readonly FlockClient _client;
        private readonly string _baseUrl;

        public FlockLeaderboardProvider(FlockClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _baseUrl = $"{client.GetApiUrl()}/leaderboards";
        }

        public async Task<List<LeaderboardInfo>> GetAllLeaderboardsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching all leaderboards for game: {_client.GameId}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<List<LeaderboardInfo>>>(
                        $"{_baseUrl}?game_id={_client.GameId}",
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Result.Count} leaderboards");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError("Failed to get all leaderboards", ex);
                throw new FlockNetworkException("Failed to fetch leaderboards", ex);
            }
        }

        public async Task<LeaderboardInfo> GetLeaderboardByIdAsync(string leaderboardId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(leaderboardId))
            {
                throw new FlockValidationException("Leaderboard ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching leaderboard: {leaderboardId}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<LeaderboardInfo>>(
                        $"{_baseUrl}/{leaderboardId}",
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched leaderboard: {leaderboardId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get leaderboard {leaderboardId}", ex);
                throw new FlockNetworkException($"Failed to fetch leaderboard {leaderboardId}", ex);
            }
        }

        public async Task<PaginatedResponse<LeaderboardEntry>> GetLeaderboardEntriesAsync(
            string leaderboardId,
            int page = 1,
            int limit = 10,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(leaderboardId))
            {
                throw new FlockValidationException("Leaderboard ID cannot be null or empty");
            }

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
                    _client.Logger.LogDebug($"Fetching leaderboard entries for {leaderboardId}, page: {page}, limit: {limit}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<PaginatedResponse<LeaderboardEntry>>(
                        $"{_baseUrl}/{leaderboardId}/entries?page={page}&limit={limit}",
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Items?.Length ?? 0} leaderboard entries");
                    return response;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get leaderboard entries for {leaderboardId}", ex);
                throw new FlockNetworkException($"Failed to fetch leaderboard entries for {leaderboardId}", ex);
            }
        }

        public async Task<List<LeaderboardEntry>> GetTopEntriesAsync(string leaderboardId, int count = 10, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(leaderboardId))
            {
                throw new FlockValidationException("Leaderboard ID cannot be null or empty");
            }

            if (count < 1 || count > 100)
            {
                throw new FlockValidationException("Count must be between 1 and 100");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching top {count} entries for leaderboard: {leaderboardId}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<List<LeaderboardEntry>>>(
                        $"{_baseUrl}/{leaderboardId}/top?count={count}",
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Result.Count} top entries");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get top entries for leaderboard {leaderboardId}", ex);
                throw new FlockNetworkException($"Failed to fetch top entries for {leaderboardId}", ex);
            }
        }

        public async Task<LeaderboardEntry> GetPlayerEntryAsync(string leaderboardId, string playerId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(leaderboardId))
            {
                throw new FlockValidationException("Leaderboard ID cannot be null or empty");
            }

            if (string.IsNullOrEmpty(playerId))
            {
                throw new FlockValidationException("Player ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching player {playerId} entry for leaderboard: {leaderboardId}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<LeaderboardEntry>>(
                        $"{_baseUrl}/{leaderboardId}/player/{playerId}",
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched player entry for {playerId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get player entry for {playerId} on leaderboard {leaderboardId}", ex);
                throw new FlockNetworkException($"Failed to fetch player entry for {playerId}", ex);
            }
        }

        public async Task<LeaderboardEntry> SubmitScoreAsync(
            string leaderboardId,
            string playerId,
            long score,
            Dictionary<string, object> metadata = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(leaderboardId))
            {
                throw new FlockValidationException("Leaderboard ID cannot be null or empty");
            }

            if (string.IsNullOrEmpty(playerId))
            {
                throw new FlockValidationException("Player ID cannot be null or empty");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Submitting score {score} for player {playerId} to leaderboard: {leaderboardId}");

                    var request = new SubmitScoreRequest
                    {
                        LeaderboardId = leaderboardId,
                        PlayerId = playerId,
                        Score = score,
                        Metadata = metadata
                    };

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.PostAsync<GenericResponse<LeaderboardEntry>>(
                        $"{_baseUrl}/{leaderboardId}/submit",
                        request,
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogInfo($"Score {score} submitted successfully for player {playerId} to leaderboard {leaderboardId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to submit score for player {playerId} to leaderboard {leaderboardId}", ex);
                throw new FlockNetworkException($"Failed to submit score to leaderboard {leaderboardId}", ex);
            }
        }

        public async Task<List<LeaderboardEntry>> GetEntriesAroundPlayerAsync(
            string leaderboardId,
            string playerId,
            int range = 5,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(leaderboardId))
            {
                throw new FlockValidationException("Leaderboard ID cannot be null or empty");
            }

            if (string.IsNullOrEmpty(playerId))
            {
                throw new FlockValidationException("Player ID cannot be null or empty");
            }

            if (range < 1 || range > 50)
            {
                throw new FlockValidationException("Range must be between 1 and 50");
            }

            try
            {
                return await _client.RetryHandler.ExecuteAsync(async () =>
                {
                    _client.Logger.LogDebug($"Fetching entries around player {playerId} (range: {range}) for leaderboard: {leaderboardId}");

                    var token = await _client.GetValidAccessTokenAsync(cancellationToken);
                    var response = await HttpClient.GetAsync<GenericResponse<List<LeaderboardEntry>>>(
                        $"{_baseUrl}/{leaderboardId}/around/{playerId}?range={range}",
                        _client.GetAuthenticatedHeaders(),
                        cancellationToken
                    );

                    if (response == null || response.Result == null)
                    {
                        throw new FlockNetworkException("Invalid response from server");
                    }

                    _client.Logger.LogDebug($"Successfully fetched {response.Result.Count} entries around player {playerId}");
                    return response.Result;
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"Failed to get entries around player {playerId} for leaderboard {leaderboardId}", ex);
                throw new FlockNetworkException($"Failed to fetch entries around player {playerId}", ex);
            }
        }
    }
}
