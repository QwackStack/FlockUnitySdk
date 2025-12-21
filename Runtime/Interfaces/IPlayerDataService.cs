using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IPlayerDataService
    {
        Task<PlayerData> GetPlayerDataAsync(CancellationToken cancellationToken = default);
        Task<PlayerData> UpdatePlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default);
        Task<PlayerData> CreateAsync(string playerId, Dictionary<string, object> data, CancellationToken cancellationToken = default);
        Task<PlayerData> GetByIdAsync(string playerDataId, CancellationToken cancellationToken = default);
        Task<PaginatedResponse<PlayerData>> GetAllAsync(int page = 1, int limit = 10, string playerId = null, CancellationToken cancellationToken = default);
        Task<PlayerData> UpdateAsync(string playerDataId, Dictionary<string, object> data, CancellationToken cancellationToken = default);
    }
}
