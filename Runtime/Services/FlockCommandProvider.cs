using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Services
{
    public class FlockCommandProvider : FlockProviderBase
    {
        public FlockCommandProvider(FlockClient client) : base(client) { }

        internal async Task<List<GameCommandExecutionResult>> ExecuteCommandAsync(
            string gameCommandId, List<ICommandPayload> inputs, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(gameCommandId, "Game Command ID");

            return await ExecuteAsync(async () =>
            {
                var request = new GameCommandExecutionRequest
                {
                    GameCommandId = gameCommandId,
                    Inputs = inputs
                };

                var response = await FlockHttpClient.PostAsync<GenericResponse<List<GameCommandExecutionResult>>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_command/execute")
                        .ToString(), request, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Execute game command", cancellationToken);
        }

        public async Task<List<GameCommandExecutionResult>> UpdatePlayerDataAsync(
            string gameCommandId, string playerDataId, Dictionary<string, object> data,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");

            var inputs = new List<ICommandPayload>
            {
                new UpdatePlayerDataInput { PlayerDataId = playerDataId, Data = data }
            };

            return await ExecuteCommandAsync(gameCommandId, inputs, cancellationToken);
        }

        public async Task<List<GameCommandExecutionResult>> UpdatePlayerDataFieldAsync(
            string gameCommandId, string playerDataId, string key, object value,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");
            RequireNotEmpty(key, "Key");

            var inputs = new List<ICommandPayload>
            {
                new UpdatePlayerDataKeyInput { PlayerDataId = playerDataId, Key = key, Value = value }
            };

            return await ExecuteCommandAsync(gameCommandId, inputs, cancellationToken);
        }

        public async Task<List<GameCommandExecutionResult>> AddGameFundsAsync(
            string gameCommandId, string playerDataId, string currency, int amount,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");
            RequireNotEmpty(currency, "Currency");

            var inputs = new List<ICommandPayload>
            {
                new AddGameFundsInput { PlayerDataId = playerDataId, Currency = currency, Amount = amount }
            };

            return await ExecuteCommandAsync(gameCommandId, inputs, cancellationToken);
        }
    }
}
