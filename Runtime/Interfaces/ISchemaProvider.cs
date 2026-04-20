using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    //TODO add defined types for all of these 
    public enum SchemaTag
    {
        gameplay,
        currency,
        asset,
        feature,
        empty
    }
    public interface ISchemaProvider
    {
        //TODO add summaries
        Task<List<GameConfigSchema>> GetAllSchemasAsync(SchemaTag tag , CancellationToken cancellationToken = default);
        Task<List<GameConfigSchema>> GetSchemasByVersionAsync(SchemaTag tag , CancellationToken cancellationToken = default);
        Task<GameConfigSchema> GetSchemaByIdAsync(string schemaId, CancellationToken cancellationToken = default);
        Task<List<GamePatchSchema>> GetSchemaConfigsAsync(string schemaId, CancellationToken cancellationToken = default);
    }
}
