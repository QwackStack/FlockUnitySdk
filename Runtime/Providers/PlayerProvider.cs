using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;

namespace Flock.Providers
{
    public class PlayerProvider : FlockProviderBase, IPlayerService
    {
        public PlayerProvider(FlockClient client) : base(client) { }

        public async Task<PlayerData> GetDataByIdAsync(string playerDataId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");

            return await ExecuteAsync(async () =>
            {
                GenericResponse<PlayerData> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerData>>(
                    $"{Client.GetApiUrl()}/v1/player_data/{playerDataId}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch player data {playerDataId}", cancellationToken);
        }

        public async Task<PaginatedResponse<PlayerData>> GetAllDataAsync( string playerId = null,int page = 1, int limit = 100, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/player_data?page={page}&limit={limit}";
                if (!string.IsNullOrEmpty(playerId))
                    url += $"&player_id={playerId}";

                return await FlockHttpClient.GetAsync<PaginatedResponse<PlayerData>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
            }, "Fetch player data list", cancellationToken);
        }

        public async Task<List<PlayerTemplateSchema>> GetTemplatesAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/player_template";
                GenericResponse<List<PlayerTemplateSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<PlayerTemplateSchema>>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch player templates", cancellationToken);
        }

        public async Task<PlayerTemplateSchema> GetTemplateByIdAsync(string playerTemplateId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerTemplateId, "Player Template ID");
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/player_template/{playerTemplateId}";
                GenericResponse<PlayerTemplateSchema> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerTemplateSchema>>(url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch player template {playerTemplateId}", cancellationToken);
        }

        public async Task<PlayerTemplateSchema> GetTemplateByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(name, "Player Template Name");
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/player_template/by-name/{name}";
                GenericResponse<PlayerTemplateSchema> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerTemplateSchema>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch player template by name {name}", cancellationToken);
        }

        public async Task<List<PlayerData>> GetTemplatePlayerDataAsync(string playerTemplateId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerTemplateId, "Player Template ID");
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/player_template/{playerTemplateId}/player-data";
                GenericResponse<List<PlayerData>> response = await FlockHttpClient.GetAsync<GenericResponse<List<PlayerData>>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch player data for template {playerTemplateId}", cancellationToken);
        }
    }
}
