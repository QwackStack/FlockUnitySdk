using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Providers
{
    public class FlockCommandProvider : FlockProviderBase
    {
        public FlockCommandProvider(FlockClient client) : base(client) { }

        public async Task<PlayerData> UpdatePlayerDataAsync(
            string playerDataId, List<DataField> data,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");

            return await ExecuteAsync(async () =>
            {
                UpdatePlayerDataInput request = new UpdatePlayerDataInput
                {
                    PlayerDataId = playerDataId,
                    Data = data
                };

                return await FlockHttpClient.PostAsync<PlayerData>(
                    $"{Client.GetVersionedApiUrl()}/game_command/update_player_data",
                    request, Client.GetBaseHeaders(), cancellationToken);
            }, "Update player data", cancellationToken);
        }

        public async Task<PlayerData> UpdatePlayerDataFieldAsync(
            string playerDataId, string key, object value,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");
            RequireNotEmpty(key, "Key");

            return await ExecuteAsync(async () =>
            {
                UpdatePlayerDataKeyInput request = new UpdatePlayerDataKeyInput
                {
                    PlayerDataId = playerDataId,
                    Key = key,
                    Value = value
                };

                return await FlockHttpClient.PostAsync<PlayerData>(
                    $"{Client.GetVersionedApiUrl()}/game_command/update_player_data_key",
                    request, Client.GetBaseHeaders(), cancellationToken);
            }, "Update player data field", cancellationToken);
        }

        public async Task<PlayerData> AddGameFundsAsync(
            string playerDataId, string currency, int amount,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");
            RequireNotEmpty(currency, "Currency");

            return await ExecuteAsync(async () =>
            {
                AddGameFundsInput request = new AddGameFundsInput
                {
                    PlayerDataId = playerDataId,
                    Currency = currency,
                    Amount = amount
                };

                return await FlockHttpClient.PostAsync<PlayerData>(
                    $"{Client.GetVersionedApiUrl()}/game_command/add_game_funds",
                    request, Client.GetBaseHeaders(), cancellationToken);
            }, "Add game funds", cancellationToken);
        }

        public async Task<PlayerData> UnlockAchievementAsync(
            string playerDataId, string achievementName,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");
            RequireNotEmpty(achievementName, "Achievement Name");

            return await ExecuteAsync(async () =>
            {
                UnlockAchievementInput request = new UnlockAchievementInput
                {
                    PlayerDataId = playerDataId,
                    AchievementName = achievementName
                };

                return await FlockHttpClient.PostAsync<PlayerData>(
                    $"{Client.GetVersionedApiUrl()}/game_command/unlock_achievement",
                    request, Client.GetBaseHeaders(), cancellationToken);
            }, "Unlock achievement", cancellationToken);
        }
    }
}
