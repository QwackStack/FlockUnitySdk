using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;

namespace Flock.Achievements
{
    public class FlockAchievementProvider : FlockProviderBase, IAchievementProvider
    {
        private readonly string _baseUrl;

        public FlockAchievementProvider(FlockClient client) : base(client)
        {
            _baseUrl = new StringBuilder().Append(client.GetApiUrl()).Append("/achievement").ToString();
        }

        public async Task<List<Achievement>> GetAllAchievementsAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                var response = await FlockHttpClient.GetAsync<GenericResponse<List<Achievement>>>(
                    new StringBuilder().Append(_baseUrl).Append("?game_id=").Append(Client.GameId).ToString(), Client.GetAuthenticatedHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch achievements", cancellationToken);
        }

        public async Task<Achievement> GetAchievementByIdAsync(string achievementId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(achievementId, "Achievement ID");

            return await ExecuteAsync(async () =>
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                var response = await FlockHttpClient.GetAsync<GenericResponse<Achievement>>(
                    new StringBuilder().Append(_baseUrl).Append("/").Append(achievementId).ToString(), Client.GetAuthenticatedHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch achievement ").Append(achievementId).ToString(), cancellationToken);
        }
    }
}
