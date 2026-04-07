using System.Text;
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
                var url = new StringBuilder().Append(Client.GetApiUrl())
                    .Append("/v1/player-ban")
                    .Append("?player_id=").Append(playerId)
                    .ToString();

                var response = await FlockHttpClient.GetAsync<GenericResponse<PlayerBan>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Get player ban", cancellationToken);
        }
    }
}
