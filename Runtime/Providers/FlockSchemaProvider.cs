using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;

namespace Flock.Providers
{
    public class FlockSchemaProvider : FlockProviderBase, ISchemaProvider
    {
        public FlockSchemaProvider(FlockClient client) : base(client) { }

        public async Task<List<GameConfigSchema>> GetAllSchemasAsync(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                GenericResponse<List<GameConfigSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                    $"{Client.GetApiUrl()}/v1/game_config?tag={tag}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch config schemas", cancellationToken);
        }

        public async Task<List<GameConfigSchema>> GetSchemasByVersionAsync(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                GenericResponse<List<GameConfigSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                    $"{Client.GetApiUrl()}/v1/game_config/version?tag={tag}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch config schemas by version", cancellationToken);
        }

        public async Task<GameConfigSchema> GetSchemaByIdAsync(string schemaId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(schemaId, "Schema ID");

            return await ExecuteAsync(async () =>
            {
                GenericResponse<GameConfigSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameConfigSchema>>(
                    $"{Client.GetApiUrl()}/v1/game_config/{schemaId}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch schema {schemaId}", cancellationToken);
        }

        public async Task<List<GamePatchSchema>> GetSchemaConfigsAsync(string schemaId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(schemaId, "Schema ID");

            return await ExecuteAsync(async () =>
            {
                GenericResponse<List<GamePatchSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                    $"{Client.GetApiUrl()}/v1/game_config/{schemaId}/patches", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch configs for schema {schemaId}", cancellationToken);
        }
    }
}
