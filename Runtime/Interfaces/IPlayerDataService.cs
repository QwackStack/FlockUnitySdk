using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IPlayerDataService
    {
        Task<PlayerData> GetByIdAsync(string playerDataId, CancellationToken cancellationToken = default);
        Task<PaginatedResponse<PlayerData>> GetAllAsync(int page = 1, int limit = 100, string playerId = null, CancellationToken cancellationToken = default);
    }
}
