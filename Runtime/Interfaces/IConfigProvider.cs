using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IConfigProvider
    {
        Task<List<GameConfig>> GetAllConfigAsync(CancellationToken cancellationToken = default);
        Task<GameConfig> GetConfigByIdAsync(string configId, CancellationToken cancellationToken = default);
    }
}
