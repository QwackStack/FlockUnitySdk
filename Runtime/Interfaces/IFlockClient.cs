using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Auth;
using Flock.Config;
using Flock.Providers;

namespace Flock.Interfaces
{
    public interface IFlockClient
    {
        //TODO add summaries
        string CurrentPlayerId { get; }
        string GameId { get; }
        string GameVersionId { get; }
        bool IsAuthenticated { get; }
        JwtTokenClaims TokenClaims { get; }

        FlockConfigProvider Config { get; }
        FlockSchemaProvider Schema { get; }
        FlockGameProvider Game { get; }
        PlayerProvider Player { get; }
        FlockCommandProvider Commands { get; }
        FlockShopProvider Shop { get; }
        IAnalyticProvider Analytics { get; }

        bool HasActiveSession { get; }
        string CurrentSessionId { get; }

        Task<PlayerLoginResponse> LoginWithEmailAsync(string email, string password, CancellationToken cancellationToken = default);
        Task<PlayerLoginResponse> LoginWithDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
        Task<PlayerLoginResponse> RegisterWithEmailAsync(string email, string password, string name = null, CancellationToken cancellationToken = default);
        Task<PlayerLoginResponse> RegisterWithDeviceAsync(string deviceId, string name = null, CancellationToken cancellationToken = default);

        void ClearTokens();
        string GetApiUrl();
    }
}
