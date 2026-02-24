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
    }
}
