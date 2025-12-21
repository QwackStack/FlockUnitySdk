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
        bool IsAuthenticated { get; }
        JwtTokenClaims TokenClaims { get; }

        // Service Providers
        FlockAchievementProvider Achievements { get; }
        FlockLeaderboardProvider Leaderboards { get; }
        FlockConfigProvider Config { get; }
        PlayerDataService PlayerData { get; }

        // Authentication
        Task<LoginResponse> LoginAsync(string email, string password, string otp = null, CancellationToken cancellationToken = default);
        Task<RegisterResponse> RegisterAsync(string email, string password, string confirmPassword, string otp = null, CancellationToken cancellationToken = default);
        Task<AuthResponse> AuthenticateWithSteamAsync(string steamTicket, CancellationToken cancellationToken = default);
        Task<AuthResponse> AuthenticateWithGameCenterAsync(CancellationToken cancellationToken = default);
        Task<AuthResponse> AuthenticateWithPlayStoreAsync(string playStoreToken, CancellationToken cancellationToken = default);
        Task<AuthResponse> AuthenticateWithDeviceIdAsync(string deviceId, CancellationToken cancellationToken = default);

        // Token Management
        Task<RefreshTokenResponse> RefreshTokenAsync(CancellationToken cancellationToken = default);
        Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default);
        string GetAccessToken();
        void ClearTokens();
        bool IsTokenExpired();
        TimeSpan? GetTimeUntilTokenExpiration();

        // Configuration
        string GetApiUrl();
    }
}
