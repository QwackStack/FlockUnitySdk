using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;

namespace Flock.Services
{
    public class PlayerDataService : FlockProviderBase, IPlayerDataService
    {
        private readonly string _baseUrl;

        public PlayerDataService(FlockClient client) : base(client)
        {
            _baseUrl = new StringBuilder().Append(client.GetApiUrl()).Append("/v1/player_data").ToString();
        }

        public async Task<PlayerData> GetByIdAsync(string playerDataId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");

            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<PlayerData>>(
                    new StringBuilder().Append(_baseUrl).Append("/").Append(playerDataId).ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch player data ").Append(playerDataId).ToString(), cancellationToken);
        }

        public async Task<PaginatedResponse<PlayerData>> GetAllAsync(int page = 1, int limit = 100, string playerId = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                var url = new StringBuilder().Append(_baseUrl)
                    .Append("?page=").Append(page)
                    .Append("&limit=").Append(limit);

                if (!string.IsNullOrEmpty(playerId))
                    url.Append("&player_id=").Append(playerId);

                return await FlockHttpClient.GetAsync<PaginatedResponse<PlayerData>>(
                    url.ToString(), Client.GetBaseHeaders(), cancellationToken);
            }, "Fetch player data list", cancellationToken);
        }
    }
}
