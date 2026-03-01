using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Auth;
using Flock.Config;
using Flock.Services;

namespace Flock.Interfaces
{
    public interface IFlockClient
    {
        string CurrentPlayerId { get; }
        string GameId { get; }
        string GameVersionId { get; }
        bool IsAuthenticated { get; }
        JwtTokenClaims TokenClaims { get; }

        FlockConfigProvider Config { get; }
        FlockSchemaProvider Schema { get; }
        FlockGameService Game { get; }
        PlayerDataService PlayerData { get; }

        Task<PlayerLoginResponse> LoginWithEmailAsync(string email, string password, CancellationToken cancellationToken = default);
        Task<PlayerLoginResponse> LoginWithDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
        Task<PlayerLoginResponse> RegisterWithEmailAsync(string email, string password, string name = null, CancellationToken cancellationToken = default);
        Task<PlayerLoginResponse> RegisterWithDeviceAsync( string deviceId, string name = null, CancellationToken cancellationToken = default);

        Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default);
        string GetAccessToken();
        void ClearTokens();
        bool IsTokenExpired();
        TimeSpan? GetTimeUntilTokenExpiration();
        string GetApiUrl();
    }
}
