using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;

namespace Flock.Providers
{
    public class PlayerDataProvider : FlockProviderBase, IPlayerDataService
    {
        private readonly string _baseUrl;

        public PlayerDataProvider(FlockClient client) : base(client)
        {
            _baseUrl = $"{client.GetApiUrl()}/v1/player_data";
        }

        public async Task<PlayerData> GetByIdAsync(string playerDataId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");

            return await ExecuteAsync(async () =>
            {
                GenericResponse<PlayerData> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerData>>(
                    $"{_baseUrl}/{playerDataId}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch player data {playerDataId}", cancellationToken);
        }

        public async Task<PaginatedResponse<PlayerData>> GetAllAsync(int page = 1, int limit = 100, string playerId = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                string url = $"{_baseUrl}?page={page}&limit={limit}{(!string.IsNullOrEmpty(playerId) ? $"&player_id={playerId}" : "")}";

                return await FlockHttpClient.GetAsync<PaginatedResponse<PlayerData>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
            }, "Fetch player data list", cancellationToken);
        }
    }
}
