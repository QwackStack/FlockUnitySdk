using System.Collections.Generic;
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

        public async Task<PlayerData> CreateAsync(string playerId, Dictionary<string, object> data, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerId, "Player ID");

            return await ExecuteAsync(async () =>
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                var request = new { player_id = playerId, data };
                var response = await FlockHttpClient.PostAsync<GenericResponse<PlayerData>>(
                    _baseUrl, request, Client.GetAuthenticatedHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Create player data", cancellationToken);
        }

        public async Task<PlayerData> GetByIdAsync(string playerDataId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");

            return await ExecuteAsync(async () =>
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                var response = await FlockHttpClient.GetAsync<GenericResponse<PlayerData>>(
                    new StringBuilder().Append(_baseUrl).Append("/").Append(playerDataId).ToString(), Client.GetAuthenticatedHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch player data ").Append(playerDataId).ToString(), cancellationToken);
        }

        public async Task<PaginatedResponse<PlayerData>> GetAllAsync(int page = 1, int limit = 10, string playerId = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                var url = new StringBuilder().Append(_baseUrl)
                    .Append("?page=").Append(page)
                    .Append("&limit=").Append(limit);

                if (!string.IsNullOrEmpty(playerId))
                    url.Append("&player_id=").Append(playerId);

                var response = await FlockHttpClient.GetAsync<PaginatedResponse<PlayerData>>(
                    url.ToString(), Client.GetAuthenticatedHeaders(), cancellationToken);
                return response;
            }, "Fetch player data list", cancellationToken);
        }

        public async Task<PlayerData> UpdateAsync(string playerDataId, Dictionary<string, object> data, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");

            return await ExecuteAsync(async () =>
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                var request = new { data };
                var response = await FlockHttpClient.PutAsync<GenericResponse<PlayerData>>(
                    new StringBuilder().Append(_baseUrl).Append("/").Append(playerDataId).ToString(), request, Client.GetAuthenticatedHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Update player data ").Append(playerDataId).ToString(), cancellationToken);
        }
    }
}
