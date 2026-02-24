using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Services
{
    public class FlockGameService : FlockProviderBase
    {
        public FlockGameService(FlockClient client) : base(client) { }

        public async Task<GameSchema> GetGameAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<GameSchema>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game")
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch game info", cancellationToken);
        }

        public async Task<GameVersionSchema> GetGameVersionAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_version")
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch game version", cancellationToken);
        }
    }
}
