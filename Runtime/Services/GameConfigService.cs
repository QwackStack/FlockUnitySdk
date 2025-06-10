using System.Threading.Tasks;
using Flock.Models;
using System.Collections.Generic;

namespace Flock.Services
{
    public class GameConfigService
    {
        private readonly FlockClient _client;
        private readonly string _baseUrl;

        internal GameConfigService(FlockClient client)
        {
            _client = client;
            _baseUrl = $"{client.GetApiUrl()}/game-config";
        }

        /// <summary>
        /// Gets all game configurations for the current game
        /// </summary>
        public async Task<List<GameConfig>> GetAllAsync()
        {
            var response = await HttpClient.GetAsync<GenericResponse<List<GameConfig>>>(
                $"{_baseUrl}?game_id={_client.GameId}",
                _client.GetAccessToken()
            );
            return response.Result;
        }

        /// <summary>
        /// Gets a specific game configuration by ID
        /// </summary>
        public async Task<GameConfig> GetByIdAsync(string configId)
        {
            var response = await HttpClient.GetAsync<GenericResponse<GameConfig>>(
                $"{_baseUrl}/{configId}",
                _client.GetAccessToken()
            );
            return response.Result;
        }
    }

    public class GameConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string GameId { get; set; }
        public Dictionary<string, object> Data { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
    }
} 