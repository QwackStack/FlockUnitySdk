using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Flock.Models
{
    internal class UpdatePlayerDataInput
    {
        [JsonProperty("player_data_id")]
        public string PlayerDataId { get; set; }

        // Flat {field: value} object (built via the DataField list's ToFlatObject) because the backend 422s on the DataField array.
        [JsonProperty("data")]
        public JObject Data { get; set; }
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

        // One per purchase call: the server answers a resend carrying it with the first purchase instead of charging again.
        [JsonProperty("idempotency_key")]
        public string IdempotencyKey { get; set; }
    }

    // No player_id: the server redeems for the player the sign-in names.
    internal class StoreReceiptRedemption
    {
        [JsonProperty("shop_item_id")]
        public string ShopItemId { get; set; }

        [JsonProperty("provider")]
        public string Provider { get; set; }

        // Ids only: Apple's transaction_id, or Google's product_id and purchase_token.
        [JsonProperty("receipt")]
        public Dictionary<string, string> Receipt { get; set; }
    }

    // shop/account-token's answer, at the root.
    internal class StoreAccountTokenAnswer
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("account_token")]
        public string AccountToken { get; set; }
    }

    /// <summary>What <see cref="PlayerInventory.Status"/> reads. A string rather than an enum, so a status the server adds later still reads.</summary>
    public static class PlayerInventoryStatuses
    {
        /// <summary>Owned and not used up yet.</summary>
        public const string Unused = "unused";
        /// <summary>Used up (consumed).</summary>
        public const string Used = "used";
        /// <summary>Taken back because its purchase was refunded.</summary>
        public const string Revoked = "revoked";
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

        // What consuming this row grants. Empty for items that carry no rewards.
        [JsonProperty("rewards")]
        public List<ShopItemReward> Rewards { get; set; } = new List<ShopItemReward>();
    }
}
