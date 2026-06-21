using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Exceptions;

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

            PlayerData result = await ExecuteAsync(async () =>
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

            return ApplyToPlayerCache(result);
        }

        public async Task<PlayerData> UpdatePlayerDataFieldAsync(
            string playerDataId, string key, object value,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");
            RequireNotEmpty(key, "Key");

            PlayerData result = await ExecuteAsync(async () =>
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

            return ApplyToPlayerCache(result);
        }

#if !FLOCK_NO_PLAYER
        /// <summary>Adds funds to the current player's currency wallet, resolving the "currency"-tagged player template at runtime. Use the id overload (or the generated FlockFundId method) to pass a known currency template id and skip the lookup.</summary>
        public async Task<PlayerData> AddGameFundsAsync(
            string currency, int amount,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(currency, "Currency");
            PlayerTemplateSchema currencyTemplate = await Client.Player.GetTemplateByTagAsync("currency", cancellationToken);
            return await AddGameFundsAsync(currency, amount, currencyTemplate.Id, cancellationToken);
        }

        /// <summary>Adds funds to the currency wallet for <paramref name="currencyTemplateId"/> (the "currency"-tagged player template); resolves the player's wallet row by that template, so no player-data id. Codegen passes the baked id here.</summary>
        public async Task<PlayerData> AddGameFundsAsync(
            string currency, int amount, string currencyTemplateId,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(currency, "Currency");
            RequireNotEmpty(currencyTemplateId, "Currency Template ID");
            PlayerData wallet = await Client.Player.GetMyDataByTemplateAsync(currencyTemplateId, cancellationToken);
            if (wallet == null || string.IsNullOrEmpty(wallet.Id))
                throw new FlockValidationException("No currency wallet found for the current player.");

            // Money grant is non-idempotent: an ambiguous failure may mean it already committed, so surface it rather than re-send (double-credit). Only 408/429 (not processed) retry.
            PlayerData result = await ExecuteAsync(async () =>
            {
                AddGameFundsInput request = new AddGameFundsInput
                {
                    PlayerDataId = wallet.Id,
                    Currency = currency,
                    Amount = amount
                };

                return await FlockHttpClient.PostAsync<PlayerData>(
                    $"{Client.GetVersionedApiUrl()}/game_command/add_game_funds",
                    request, Client.GetBaseHeaders(), cancellationToken);
            }, "Add game funds", cancellationToken, idempotent: false);

            return ApplyToPlayerCache(result);
        }

        /// <summary>Unlocks an achievement on the current player's achievements row (the player template tagged "achievement"); the row is resolved for you, so no player-data id.</summary>
        public async Task<PlayerData> UnlockAchievementAsync(
            string achievementName,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(achievementName, "Achievement Name");
            PlayerData row = await Client.Player.GetMyDataByTagAsync("achievement", cancellationToken);
            if (row == null || string.IsNullOrEmpty(row.Id))
                throw new FlockValidationException("No achievements data found for the current player.");

            PlayerData result = await ExecuteAsync(async () =>
            {
                UnlockAchievementInput request = new UnlockAchievementInput
                {
                    PlayerDataId = row.Id,
                    AchievementName = achievementName
                };

                return await FlockHttpClient.PostAsync<PlayerData>(
                    $"{Client.GetVersionedApiUrl()}/game_command/unlock_achievement",
                    request, Client.GetBaseHeaders(), cancellationToken);
            }, "Unlock achievement", cancellationToken);

            return ApplyToPlayerCache(result);
        }
#endif

        private PlayerData ApplyToPlayerCache(PlayerData data)
        {
#if !FLOCK_NO_PLAYER
            Client.Player?.ApplyServerPlayerData(data);
#endif
            return data;
        }
    }
}
