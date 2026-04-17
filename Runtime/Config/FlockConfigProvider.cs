using System.Collections.Generic;
using System.Linq;
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
                GenericResponse<List<GamePatchSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                    $"{Client.GetApiUrl()}/v1/game_patch", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch game configs", cancellationToken);
        }

        public async Task<List<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
        {
            List<GamePatchSchema> configs = await GetAllAsync(cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }

        public async Task<GamePatchSchema> GetByIdAsync(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");

            return await ExecuteAsync(async () =>
            {
                GenericResponse<GamePatchSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GamePatchSchema>>(
                    $"{Client.GetApiUrl()}/v1/game_patch/{configId}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch config {configId}", cancellationToken);
        }

        public async Task<T> GetByIdAsync<T>(string configId, CancellationToken cancellationToken = default)
        {
            GamePatchSchema config = await GetByIdAsync(configId, cancellationToken);
            return config.GetDataAs<T>();
        }

        public async Task<List<GamePatchSchema>> GetBySchemaAsync(string schemaId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(schemaId, "Schema ID");

            return await ExecuteAsync(async () =>
            {
                GenericResponse<List<GamePatchSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                    $"{Client.GetApiUrl()}/v1/game_patch/config/{schemaId}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch configs for schema {schemaId}", cancellationToken);
        }

        public async Task<List<T>> GetBySchemaAsync<T>(string schemaId, CancellationToken cancellationToken = default)
        {
            List<GamePatchSchema> configs = await GetBySchemaAsync(schemaId, cancellationToken);
            return configs.Select(c => c.GetDataAs<T>()).ToList();
        }
    }
}
