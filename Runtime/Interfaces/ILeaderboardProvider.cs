using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface ILeaderboardProvider
    {
        Task<List<LeaderboardInfo>> GetAllLeaderboardsAsync(CancellationToken cancellationToken = default);
        Task<LeaderboardInfo> GetLeaderboardByIdAsync(string leaderboardId, CancellationToken cancellationToken = default);
        Task<PaginatedResponse<LeaderboardEntry>> GetLeaderboardEntriesAsync(string leaderboardId, int page = 1, int limit = 10, CancellationToken cancellationToken = default);
        Task<List<LeaderboardEntry>> GetTopEntriesAsync(string leaderboardId, int count = 10, CancellationToken cancellationToken = default);
        Task<LeaderboardEntry> GetPlayerEntryAsync(string leaderboardId, string playerId, CancellationToken cancellationToken = default);
        Task<LeaderboardEntry> SubmitScoreAsync(string leaderboardId, string playerId, long score, Dictionary<string, object> metadata = null, CancellationToken cancellationToken = default);
        Task<List<LeaderboardEntry>> GetEntriesAroundPlayerAsync(string leaderboardId, string playerId, int range = 5, CancellationToken cancellationToken = default);
    }
}
