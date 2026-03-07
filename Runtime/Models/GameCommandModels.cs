using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
    internal interface ICommandPayload { }

    internal class GameCommandExecutionRequest
    {
        [JsonProperty("game_command_id")]
        public string GameCommandId { get; set; }

        [JsonProperty("inputs")]
        public List<ICommandPayload> Inputs { get; set; }
    }

    internal class UpdatePlayerDataInput : ICommandPayload
    {
        [JsonProperty("player_data_id")]
        public string PlayerDataId { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }
    }

    internal class UpdatePlayerDataKeyInput : ICommandPayload
    {
        [JsonProperty("player_data_id")]
        public string PlayerDataId { get; set; }

        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("value")]
        public object Value { get; set; }
    }

    internal class AddGameFundsInput : ICommandPayload
    {
        [JsonProperty("player_data_id")]
        public string PlayerDataId { get; set; }

        [JsonProperty("currency")]
        public string Currency { get; set; }

        [JsonProperty("amount")]
        public int Amount { get; set; }
    }

    internal class ShopTransactionRequest : ICommandPayload
    {
        [JsonProperty("shop_item_id")]
        public string ShopItemId { get; set; }

        [JsonProperty("player_id")]
        public string PlayerId { get; set; }
    }

    public class PlayerInventory
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("shop_item_id")]
        public string ShopItemId { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("used_at")]
        public string UsedAt { get; set; }
    }

    public class GameCommandExecutionResult
    {
        [JsonProperty("command")]
        public string Command { get; set; }

        [JsonProperty("output")]
        public object Output { get; set; }
    }
}
