using System.Threading.Tasks;
using System.Collections.Generic;
using Flock.Models;

namespace Flock.Services
{
    public class PlayerDataService
    {
        private readonly string _apiUrl;
        private readonly string _accessToken;

        public PlayerDataService(string apiUrl, string accessToken)
        {
            _apiUrl = apiUrl;
            _accessToken = accessToken;
        }

        public async Task<PlayerData> GetPlayerDataAsync()
        {
            return await HttpClient.GetAsync<PlayerData>($"{_apiUrl}/player-data");
        }

        public async Task<PlayerData> UpdatePlayerDataAsync(PlayerData data)
        {
            return await HttpClient.PutAsync<PlayerData>($"{_apiUrl}/player-data", data);
        }

        /// <summary>
        /// Creates new player data
        /// </summary>
        public async Task<PlayerData> CreateAsync(string playerId, Dictionary<string, object> data)
        {
            var request = new PlayerDataRequest
            {
                GameId = _client.GameId,
                PlayerId = playerId,
                Data = data
            };

            var response = await HttpClient.PostAsync<GenericResponse<PlayerData>>(
                _baseUrl,
                request,
                _client.GetAccessToken()
            );
            return response.Result;
        }

        /// <summary>
        /// Gets player data by ID
        /// </summary>
        public async Task<PlayerData> GetByIdAsync(string playerDataId)
        {
            var response = await HttpClient.GetAsync<GenericResponse<PlayerData>>(
                $"{_baseUrl}/{playerDataId}",
                _client.GetAccessToken()
            );
            return response.Result;
        }

        /// <summary>
        /// Gets all player data for the current game
        /// </summary>
        public async Task<PaginatedResponse<PlayerData>> GetAllAsync(int page = 1, int limit = 10, string playerId = null)
        {
            var url = $"{_baseUrl}?game_id={_client.GameId}&page={page}&limit={limit}";
            if (!string.IsNullOrEmpty(playerId))
            {
                url += $"&player_id={playerId}";
            }

            var response = await HttpClient.GetAsync<PaginatedResponse<PlayerData>>(
                url,
                _client.GetAccessToken()
            );
            return response;
        }

        /// <summary>
        /// Updates player data
        /// </summary>
        public async Task<PlayerData> UpdateAsync(string playerDataId, Dictionary<string, object> data)
        {
            var request = new UpdatePlayerDataRequest
            {
                Data = data
            };

            var response = await HttpClient.PutAsync<GenericResponse<PlayerData>>(
                $"{_baseUrl}/{playerDataId}",
                request,
                _client.GetAccessToken()
            );
            return response.Result;
        }
    }

    public class PlayerData
    {
        public string PlayerId { get; set; }
        public string Username { get; set; }
        public int Level { get; set; }
        public int Experience { get; set; }
        public Dictionary<string, object> CustomData { get; set; }
    }

    internal class PlayerDataRequest
    {
        public string GameId { get; set; }
        public string PlayerId { get; set; }
        public Dictionary<string, object> Data { get; set; }
    }

    internal class UpdatePlayerDataRequest
    {
        public Dictionary<string, object> Data { get; set; }
    }
} 