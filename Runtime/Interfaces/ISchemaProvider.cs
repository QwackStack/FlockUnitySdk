using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface ISchemaProvider
    {
        Task<List<GameConfigSchema>> GetAllSchemasAsync(string tag = null, CancellationToken cancellationToken = default);
        Task<List<GameConfigSchema>> GetSchemasByVersionAsync(string tag = null, CancellationToken cancellationToken = default);
        Task<GameConfigSchema> GetSchemaByIdAsync(string schemaId, CancellationToken cancellationToken = default);
        Task<List<GamePatchSchema>> GetSchemaConfigsAsync(string schemaId, CancellationToken cancellationToken = default);
    }
}
