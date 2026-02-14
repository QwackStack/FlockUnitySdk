using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using System.Collections.Generic;

namespace Flock.Services
{
    public class GameConfigService
    {
        private readonly FlockClient _client;

        public GameConfigService(FlockClient flockClient)
        {
            _client = flockClient;
        }

        /// <summary>
        /// Gets all game configurations for the current game
        /// </summary>
        public async Task<List<GameConfig>> GetAllAsync()
        {
            var response = await HttpClient.GetAsync<GenericResponse<List<GameConfig>>>(
                $"{_client.GetApiUrl()}/game-config",
                _client.GetAuthenticatedHeaders()
            );
            return response.Result;
        }

        /// <summary>
        /// Gets a specific game configuration by ID
        /// </summary>
        public async Task<GameConfig> GetByIdAsync(string configId)
        {
            var response = await HttpClient.GetAsync<GenericResponse<GameConfig>>(
                $"{_client.GetApiUrl()}/game-config/{configId}",
                _client.GetAuthenticatedHeaders()
            );
            return response.Result;
        }

        public async Task<GameConfig> GetGameConfigAsync()
        {
            var response = await HttpClient.GetAsync<GenericResponse<GameConfig>>($"{_client.GetApiUrl()}/game-config", _client.GetAuthenticatedHeaders());
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
        public string Version { get; set; }
        public bool MaintenanceMode { get; set; }
        public string[] EnabledFeatures { get; set; }
    }
} 