using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IConfigProvider
    {
        Task<List<GamePatchSchema>> GetAllAsync(CancellationToken cancellationToken = default);
        Task<List<T>> GetAllAsync<T>(CancellationToken cancellationToken = default);

        Task<GamePatchSchema> GetByIdAsync(string configId, CancellationToken cancellationToken = default);
        Task<T> GetByIdAsync<T>(string configId, CancellationToken cancellationToken = default);

        Task<List<GamePatchSchema>> GetBySchemaAsync(string schemaId, CancellationToken cancellationToken = default);
        Task<List<T>> GetBySchemaAsync<T>(string schemaId, CancellationToken cancellationToken = default);
    }
}
