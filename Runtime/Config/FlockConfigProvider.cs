using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;

namespace Flock.Config
{
    public class FlockConfigProvider : FlockProviderBase, IConfigProvider
    {
        public FlockConfigProvider(FlockClient client) : base(client) { }

        public async Task<List<GamePatchSchema>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_patch")
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch game configs", cancellationToken);
        }

        public async Task<List<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
        {
            var configs = await GetAllAsync(cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        public async Task<GamePatchSchema> GetByIdAsync(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");

            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<GamePatchSchema>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_patch/")
                        .Append(configId)
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch config ").Append(configId).ToString(), cancellationToken);
        }

        public async Task<T> GetByIdAsync<T>(string configId, CancellationToken cancellationToken = default)
        {
            var config = await GetByIdAsync(configId, cancellationToken);
            return config.GetDataAs<T>();
        }

        public async Task<List<GamePatchSchema>> GetBySchemaAsync(string schemaId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(schemaId, "Schema ID");

            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_patch/config/")
                        .Append(schemaId)
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch configs for schema ").Append(schemaId).ToString(), cancellationToken);
        }

        public async Task<List<T>> GetBySchemaAsync<T>(string schemaId, CancellationToken cancellationToken = default)
        {
            var configs = await GetBySchemaAsync(schemaId, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }
    }
}
