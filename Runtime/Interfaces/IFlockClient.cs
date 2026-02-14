using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Auth;
using Flock.Achievements;
using Flock.Leaderboard;
using Flock.Config;
using Flock.Services;

namespace Flock.Interfaces
{
    public interface IFlockClient
    {
        // Properties
        string CurrentPlayerId { get; }
        string GameId { get; }
        string GameVersionId { get; }
        bool IsAuthenticated { get; }
        JwtTokenClaims TokenClaims { get; }

        // Service Providers
        FlockAchievementProvider Achievements { get; }
        FlockLeaderboardProvider Leaderboards { get; }
        FlockConfigProvider Config { get; }
        FlockGamePatchProvider Patches { get; }
        FlockGameService Game { get; }
        PlayerDataService PlayerData { get; }

        // Authentication
        Task<PlayerLoginResponse> LoginWithEmailAsync(string email, string password, CancellationToken cancellationToken = default);
        Task<PlayerLoginResponse> RegisterWithEmailAsync(string email, string password, string name = null, CancellationToken cancellationToken = default);
        Task<PlayerLoginResponse> LoginWithDeviceAsync(string deviceType, string deviceId, CancellationToken cancellationToken = default);
        Task<PlayerLoginResponse> RegisterWithDeviceAsync(string deviceType, string deviceId, string name = null, CancellationToken cancellationToken = default);

        // Token Management
        Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default);
        string GetAccessToken();
        void ClearTokens();
        bool IsTokenExpired();
        TimeSpan? GetTimeUntilTokenExpiration();

        // Configuration
        string GetApiUrl();
    }
}
