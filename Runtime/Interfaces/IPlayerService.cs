using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IPlayerService
    {
        //TODO add summaries
        Task<PlayerData> GetDataByIdAsync(string playerDataId, CancellationToken cancellationToken = default);
        Task<PaginatedResponse<PlayerData>> GetAllDataAsync(string playerId = null,int page = 1, int limit = 100,  CancellationToken cancellationToken = default);

        Task<List<PlayerTemplateSchema>> GetTemplatesAsync(CancellationToken cancellationToken = default);
        Task<PlayerTemplateSchema> GetTemplateByIdAsync(string playerTemplateId, CancellationToken cancellationToken = default);
        Task<PlayerTemplateSchema> GetTemplateByNameAsync(string name, CancellationToken cancellationToken = default);
        Task<List<PlayerData>> GetTemplatePlayerDataAsync(string playerTemplateId, CancellationToken cancellationToken = default);
    }
}
