using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Providers
{
    public class FlockBanProvider : FlockProviderBase
    {
        public FlockBanProvider(FlockClient client) : base(client) { }

        public async Task<PlayerBan> GetPlayerBanAsync(
            string playerId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerId, "Player ID");

            return await ExecuteAsync(async () =>
            {
                GenericResponse<PlayerBan> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerBan>>(
                    $"{Client.GetApiUrl()}/v1/player-ban?player_id={playerId}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Get player ban", cancellationToken);
        }
    }
}
