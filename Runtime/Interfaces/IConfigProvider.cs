using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IConfigProvider
    {
        Task<List<GameConfigSchema>> GetAllConfigsAsync(string tag = null, CancellationToken cancellationToken = default);
        Task<List<GameConfigSchema>> GetConfigsByVersionAsync(string tag = null, CancellationToken cancellationToken = default);
        Task<GameConfigSchema> GetConfigByIdAsync(string configId, CancellationToken cancellationToken = default);
        Task<List<GamePatchSchema>> GetConfigPatchesAsync(string configId, CancellationToken cancellationToken = default);
    }
}
