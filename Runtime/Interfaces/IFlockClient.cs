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
        FlockAuthProvider Authentication { get; }
        FlockConfigProvider Config { get; }
        FlockSchemaProvider Schema { get; }
        FlockGameProvider Game { get; }
        PlayerProvider Player { get; }
        FlockCommandProvider Commands { get; }
        FlockShopProvider Shop { get; }
        FlockBanProvider Ban { get; }
        FlockAssetProvider Asset { get; }
        IAnalyticProvider Analytics { get; }
        bool HasActiveSession { get; }
        string CurrentSessionId { get; }
        string GetApiUrl();
    }
}
