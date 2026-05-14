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
#if !FLOCK_NO_CONFIG
        FlockConfigProvider Config { get; }
#endif
#if !FLOCK_NO_SCHEMA
        FlockSchemaProvider Schema { get; }
#endif
#if !FLOCK_NO_GAME
        FlockGameProvider Game { get; }
#endif
#if !FLOCK_NO_PLAYER
        PlayerProvider Player { get; }
#endif
#if !FLOCK_NO_COMMANDS
        FlockCommandProvider Commands { get; }
#endif
#if !FLOCK_NO_SHOP
        FlockShopProvider Shop { get; }
#endif
#if !FLOCK_NO_BAN
        FlockBanProvider Ban { get; }
#endif
#if !FLOCK_NO_ASSET
        FlockAssetProvider Asset { get; }
#endif
#if !FLOCK_NO_ANALYTICS
        IAnalyticProvider Analytics { get; }
#endif
        bool HasActiveSession { get; }
        string CurrentSessionId { get; }
        string GetApiUrl();
    }
}
