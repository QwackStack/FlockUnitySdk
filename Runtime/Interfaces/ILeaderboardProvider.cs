using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface ILeaderboardProvider
    {
        Task<List<Leaderboard>> GetAllLeaderboardsAsync(CancellationToken cancellationToken = default);
        Task<Leaderboard> GetLeaderboardByIdAsync(string leaderboardId, CancellationToken cancellationToken = default);
    }
}
