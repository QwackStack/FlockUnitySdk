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
                GenericResponse<GameSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameSchema>>(
                    $"{Client.GetApiUrl()}/v1/game", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch game info", cancellationToken);
        }

        public async Task<GameVersionSchema> GetGameVersionAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                GenericResponse<GameVersionSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(
                    $"{Client.GetApiUrl()}/v1/game_version", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch game version", cancellationToken);
        }
    }
}
