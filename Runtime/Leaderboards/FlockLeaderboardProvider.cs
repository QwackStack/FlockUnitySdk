using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;

namespace Flock.Leaderboards
{
    public class FlockLeaderboardProvider : FlockProviderBase, ILeaderboardProvider
    {
        public FlockLeaderboardProvider(FlockClient client) : base(client) { }

        public async Task<List<Leaderboard>> GetAllLeaderboardsAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                var url = new StringBuilder().Append(Client.GetApiUrl())
                    .Append("/leaderboard/")
                    .Append(Client.GameId)
                    .ToString();
                var response = await FlockHttpClient.GetAsync<GenericResponse<List<Leaderboard>>>(
                    url, Client.GetAuthenticatedHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch leaderboards", cancellationToken);
        }

        public async Task<Leaderboard> GetLeaderboardByIdAsync(string leaderboardId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(leaderboardId, "Leaderboard ID");

            return await ExecuteAsync(async () =>
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                var url = new StringBuilder().Append(Client.GetApiUrl())
                    .Append("/leaderboard/")
                    .Append(Client.GameId)
                    .Append("/")
                    .Append(leaderboardId)
                    .ToString();
                var response = await FlockHttpClient.GetAsync<GenericResponse<Leaderboard>>(
                    url, Client.GetAuthenticatedHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch leaderboard ").Append(leaderboardId).ToString(), cancellationToken);
        }
    }
}
