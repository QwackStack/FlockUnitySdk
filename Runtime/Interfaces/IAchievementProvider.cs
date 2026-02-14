using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IAchievementProvider
    {
        Task<List<Achievement>> GetAllAchievementsAsync(CancellationToken cancellationToken = default);
        Task<Achievement> GetAchievementByIdAsync(string achievementId, CancellationToken cancellationToken = default);
        Task<List<Achievement>> GetPlayerAchievementsAsync(string playerId, CancellationToken cancellationToken = default);
        Task<Achievement> UnlockAchievementAsync(string playerId, string achievementId, CancellationToken cancellationToken = default);
        Task<Achievement> UpdateProgressAsync(string playerId, string achievementId, float progress, CancellationToken cancellationToken = default);
    }
}
