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
        // Shares the "config" snapshot category with FlockConfigProvider — same endpoints, stored once.
        private const string SnapshotCategory = "config";

        private readonly Dictionary<SchemaTag, List<GameConfigSchema>> _schemasByTag = new Dictionary<SchemaTag, List<GameConfigSchema>>();
        private readonly Dictionary<SchemaTag, List<GameConfigSchema>> _schemasByVersionTag = new Dictionary<SchemaTag, List<GameConfigSchema>>();
        private readonly Dictionary<string, GameConfigSchema> _schemasById = new Dictionary<string, GameConfigSchema>();
        private readonly Dictionary<string, List<GamePatchSchema>> _patchesBySchema = new Dictionary<string, List<GamePatchSchema>>();

        public FlockSchemaProvider(FlockClient client) : base(client) { }

        public void ClearCache()
        {
            _schemasByTag.Clear();
            _schemasByVersionTag.Clear();
            _schemasById.Clear();
            _patchesBySchema.Clear();
            Client.SnapshotStore?.DeleteScope(GetSnapshotScope(SnapshotCategory));
        }

        public async Task<List<GameConfigSchema>> GetAllSchemasAsync(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            if (_schemasByTag.TryGetValue(tag, out List<GameConfigSchema> cached))
                return new List<GameConfigSchema>(cached);

            List<GameConfigSchema> schemas = await FetchWithSnapshotAsync(
                GetSnapshotScope(SnapshotCategory), $"game_config_tag_{tag}", async () =>
                {
                    GenericResponse<List<GameConfigSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                        $"{Client.GetVersionedApiUrl()}/game_config?tag={tag}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch config schemas", cancellationToken);

            _schemasByTag[tag] = schemas;
            return new List<GameConfigSchema>(schemas);
        }

        public async Task<List<GameConfigSchema>> GetSchemasByVersionAsync(SchemaTag tag, CancellationToken cancellationToken = default)
        {
            if (_schemasByVersionTag.TryGetValue(tag, out List<GameConfigSchema> cached))
                return new List<GameConfigSchema>(cached);

            List<GameConfigSchema> schemas = await FetchWithSnapshotAsync(
                GetSnapshotScope(SnapshotCategory), $"game_config_version_tag_{tag}", async () =>
                {
                    GenericResponse<List<GameConfigSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(
                        $"{Client.GetVersionedApiUrl()}/game_config/version?tag={tag}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch config schemas by version", cancellationToken);

            _schemasByVersionTag[tag] = schemas;
            return new List<GameConfigSchema>(schemas);
        }

        public async Task<GameConfigSchema> GetSchemaByIdAsync(string schemaId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(schemaId, "Schema ID");
            if (_schemasById.TryGetValue(schemaId, out GameConfigSchema cached))
                return cached;

            GameConfigSchema schema = await FetchWithSnapshotAsync(
                GetSnapshotScope(SnapshotCategory), $"game_config_{schemaId}", async () =>
                {
                    GenericResponse<GameConfigSchema> response = await FlockHttpClient.GetAsync<GenericResponse<GameConfigSchema>>(
                        $"{Client.GetVersionedApiUrl()}/game_config/{schemaId}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, $"Fetch schema {schemaId}", cancellationToken);

            _schemasById[schemaId] = schema;
            return schema;
        }

        public async Task<List<GamePatchSchema>> GetSchemaConfigsAsync(string schemaId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(schemaId, "Schema ID");
            if (_patchesBySchema.TryGetValue(schemaId, out List<GamePatchSchema> cached))
                return new List<GamePatchSchema>(cached);

            List<GamePatchSchema> patches = await FetchWithSnapshotAsync(
                GetSnapshotScope(SnapshotCategory), $"game_config_patches_{schemaId}", async () =>
                {
                    GenericResponse<List<GamePatchSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                        $"{Client.GetVersionedApiUrl()}/game_config/{schemaId}/patches", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, $"Fetch configs for schema {schemaId}", cancellationToken);

            _patchesBySchema[schemaId] = patches;
            return new List<GamePatchSchema>(patches);
        }
    }
}
