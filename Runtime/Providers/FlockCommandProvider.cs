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

        internal async Task<List<GameCommandExecutionResult>> ExecuteCommandAsync(
            string gameCommandId, List<ICommandPayload> inputs, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(gameCommandId, "Game Command ID");

            return await ExecuteAsync(async () =>
            {
                GameCommandExecutionRequest request = new GameCommandExecutionRequest
                {
                    GameCommandId = gameCommandId,
                    Inputs = inputs
                };

                GenericResponse<List<GameCommandExecutionResult>> response = await FlockHttpClient.PostAsync<GenericResponse<List<GameCommandExecutionResult>>>(
                    $"{Client.GetApiUrl()}/v1/game_command/execute", request, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Execute game command", cancellationToken);
        }

        public async Task<List<GameCommandExecutionResult>> UpdatePlayerDataAsync(
            string gameCommandId, string playerDataId, Dictionary<string, object> data,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");

            List<ICommandPayload> inputs = new List<ICommandPayload>
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

            List<ICommandPayload> inputs = new List<ICommandPayload>
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

            List<ICommandPayload> inputs = new List<ICommandPayload>
            {
                new AddGameFundsInput { PlayerDataId = playerDataId, Currency = currency, Amount = amount }
            };

            return await ExecuteCommandAsync(gameCommandId, inputs, cancellationToken);
        }
    }
}
