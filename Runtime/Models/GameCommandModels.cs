using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
    internal class UpdatePlayerDataInput
    {
        [JsonProperty("player_data_id")]
        public string PlayerDataId { get; set; }

        [JsonProperty("data")]
        public List<DataField> Data { get; set; }
    }

    internal class UpdatePlayerDataKeyInput
    {
        [JsonProperty("player_data_id")]
        public string PlayerDataId { get; set; }

        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("value")]
        public object Value { get; set; }
    }

    internal class AddGameFundsInput
    {
        [JsonProperty("player_data_id")]
        public string PlayerDataId { get; set; }

        [JsonProperty("currency")]
        public string Currency { get; set; }

        [JsonProperty("amount")]
        public int Amount { get; set; }
    }

    internal class UnlockAchievementInput
    {
        [JsonProperty("player_data_id")]
        public string PlayerDataId { get; set; }

        [JsonProperty("achievement_name")]
        public string AchievementName { get; set; }
    }

    internal class ShopTransactionRequest
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
}
