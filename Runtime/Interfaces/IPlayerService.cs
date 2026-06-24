using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IPlayerService
    {
        //TODO add summaries
        Task<PlayerData> GetDataByIdAsync(string playerDataId, CancellationToken cancellationToken = default);
        Task<PaginatedResponse<PlayerData>> GetAllDataAsync(string playerId = null, int page = 1, int limit = 100, CancellationToken cancellationToken = default);

        Task<PlayerBan> GetBanAsync(string playerId, CancellationToken cancellationToken = default);
    }
}
