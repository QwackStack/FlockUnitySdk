using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Models;
using Flock.Http;

namespace Flock.Providers
{
    /// <summary>Read-only leaderboard access. There is no score-submit call by design — a board projects over a player-data field, so a player moves up it by writing that field through the command surface.</summary>
    public class FlockLeaderboardProvider : FlockProviderBase
    {
        private const string SnapshotCategory = "leaderboard";

        public FlockLeaderboardProvider(FlockClient client) : base(client) { }

        public void ClearCache()
        {
            DeleteSnapshotCategory(SnapshotCategory);
        }

        /// <summary>Ranked standings for a board. Open to signed-out players; the default window is whatever the board is currently serving.</summary>
        public async Task<Standings> GetStandingsAsync(
            string leaderboardId,
            FlockLeaderboardWindow window = default,
            string country = null,
            int page = 1,
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(leaderboardId, "Leaderboard ID");

            string windowKey = window.ToWireValue();
            StringBuilder query = new StringBuilder();
            AppendParam(query, "window", windowKey);
            AppendParam(query, "country", country);
            AppendParam(query, "page", page.ToString());
            AppendParam(query, "limit", limit.ToString());

            return await FetchWithSnapshotAsync(
                SnapshotCategory,
                $"standings_{leaderboardId}_{windowKey}_{country}_p{page}_l{limit}",
                async () =>
                {
                    GenericResponse<Standings> response = await FlockHttpClient.GetAsync<GenericResponse<Standings>>(
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.LeaderboardById(leaderboardId)}{query}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                },
                "Fetch leaderboard standings", cancellationToken);
        }

        /// <summary>The signed-in player's own placement. Rank and Score come back null when they have no entry yet — that's a valid result, not an error.</summary>
        public async Task<PlayerRank> GetMyRankAsync(
            string leaderboardId,
            FlockLeaderboardWindow window = default,
            string country = null,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(leaderboardId, "Leaderboard ID");
            RequireAuthenticated();

            string windowKey = window.ToWireValue();
            StringBuilder query = new StringBuilder();
            AppendParam(query, "window", windowKey);
            AppendParam(query, "country", country);

            return await FetchWithSnapshotAsync(
                SnapshotCategory,
                PlayerScopedKey($"me_{leaderboardId}_{windowKey}_{country}"),
                async () =>
                {
                    GenericResponse<PlayerRank> response = await FlockHttpClient.GetAsync<GenericResponse<PlayerRank>>(
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.LeaderboardMe(leaderboardId)}{query}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                },
                "Fetch player rank", cancellationToken);
        }

        /// <summary>The <paramref name="neighbours"/> entries either side of the signed-in player, for a "you are here" view.</summary>
        public async Task<Standings> GetAroundMeAsync(
            string leaderboardId,
            int neighbours = 5,
            FlockLeaderboardWindow window = default,
            string country = null,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(leaderboardId, "Leaderboard ID");
            RequireAuthenticated();

            string windowKey = window.ToWireValue();
            StringBuilder query = new StringBuilder();
            AppendParam(query, "window", windowKey);
            AppendParam(query, "country", country);
            AppendParam(query, "n", neighbours.ToString());

            return await FetchWithSnapshotAsync(
                SnapshotCategory,
                PlayerScopedKey($"around_{leaderboardId}_{windowKey}_{country}_n{neighbours}"),
                async () =>
                {
                    GenericResponse<Standings> response = await FlockHttpClient.GetAsync<GenericResponse<Standings>>(
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.LeaderboardAroundMe(leaderboardId)}{query}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                },
                "Fetch standings around player", cancellationToken);
        }

        // Bearer-only endpoints — fail fast instead of a guaranteed server 401.
        private void RequireAuthenticated()
        {
            if (!Client.IsAuthenticated)
                throw new FlockAuthException("No player is signed in");
        }

        // Player-scoped so one player's cached placement can't be served to the next player on a shared device.
        private string PlayerScopedKey(string key)
        {
            return $"{key}_{Client.CurrentPlayerId}";
        }

        // Optional filters are omitted rather than sent empty; values are escaped here.
        private static void AppendParam(StringBuilder query, string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            query.Append(query.Length == 0 ? '?' : '&');
            query.Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }
    }
}
