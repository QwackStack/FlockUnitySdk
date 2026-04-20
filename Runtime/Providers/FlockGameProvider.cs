using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Providers
{
    public class FlockGameProvider : FlockProviderBase
    {
        public FlockGameProvider(FlockClient client) : base(client) { }

        public async Task<GameSchema> GetGameAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/game";
                GenericResponse<GameSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameSchema>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch game info", cancellationToken);
        }

        public async Task<GameVersionSchema> GetGameVersionAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/game_version";
                GenericResponse<GameVersionSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch game version", cancellationToken);
        }
        public async Task<GameVersionSchema> GetGameVersionByNameAsync(string name,CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/game_version/by-name/{name}";
                GenericResponse<GameVersionSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch game version By Name", cancellationToken);
        }
    }
}
