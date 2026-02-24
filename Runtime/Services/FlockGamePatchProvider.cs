using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Services
{
    public class FlockGamePatchProvider : FlockProviderBase
    {
        public FlockGamePatchProvider(FlockClient client) : base(client) { }

        public async Task<List<GamePatchSchema>> GetAllPatchesAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_patch")
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch game patches", cancellationToken);
        }

        public async Task<GamePatchSchema> GetPatchByIdAsync(string patchId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(patchId, "Patch ID");

            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<GamePatchSchema>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_patch/")
                        .Append(patchId)
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch patch ").Append(patchId).ToString(), cancellationToken);
        }

        public async Task<List<GamePatchSchema>> GetPatchesByConfigIdAsync(string gameConfigId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(gameConfigId, "Game Config ID");

            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_patch/config/")
                        .Append(gameConfigId)
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch patches for config ").Append(gameConfigId).ToString(), cancellationToken);
        }
    }
}
