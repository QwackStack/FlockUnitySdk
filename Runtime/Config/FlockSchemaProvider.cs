using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;

namespace Flock.Config
{
    public class FlockSchemaProvider : FlockProviderBase, ISchemaProvider
    {
        public FlockSchemaProvider(FlockClient client) : base(client) { }

        public async Task<List<GameConfigSchema>> GetAllSchemasAsync(string tag = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                var url = new StringBuilder().Append(Client.GetApiUrl()).Append("/v1/game_config");

                if (!string.IsNullOrEmpty(tag))
                    url.Append("?tag=").Append(tag);

                var response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                    url.ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch config schemas", cancellationToken);
        }

        public async Task<List<GameConfigSchema>> GetSchemasByVersionAsync(string tag = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                var url = new StringBuilder().Append(Client.GetApiUrl()).Append("/v1/game_config/version");

                if (!string.IsNullOrEmpty(tag))
                    url.Append("?tag=").Append(tag);

                var response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                    url.ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch config schemas by version", cancellationToken);
        }

        public async Task<GameConfigSchema> GetSchemaByIdAsync(string schemaId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(schemaId, "Schema ID");

            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<GameConfigSchema>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_config/")
                        .Append(schemaId)
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch schema ").Append(schemaId).ToString(), cancellationToken);
        }

        public async Task<List<GamePatchSchema>> GetSchemaConfigsAsync(string schemaId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(schemaId, "Schema ID");

            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_config/")
                        .Append(schemaId)
                        .Append("/patches")
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch configs for schema ").Append(schemaId).ToString(), cancellationToken);
        }
    }
}
