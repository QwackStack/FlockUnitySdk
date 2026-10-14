using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
    // Reward kinds the server sends. Strings, not an enum: rewards are stored as typed entries so new kinds can ship without a schema migration, and an unknown value must not break deserialization.
    public static class ShopItemRewardTypes
    {
        public const string Currency = "currency";
    }

    /// <summary>Where a purchase has got to. A wallet purchase is born paid; the others belong to real-money checkouts, whose result comes later. Strings rather than an enum: the server can add a status, and an unknown one must not break reading the purchase.</summary>
    public static class PurchaseResultStatuses
    {
        public const string Pending = "pending";
        public const string Paid = "paid";
        public const string Failed = "failed";
        public const string Expired = "expired";
        public const string Refunded = "refunded";
    }

    // One thing a reward-bearing shop item grants. Code is a currency code from the game version's currency config — the same namespace ShopItem.Currency draws from.
    public class ShopItemReward
    {
        [JsonProperty("type")]
        public string Type { get; set; } = ShopItemRewardTypes.Currency;

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("amount")]
        public int Amount { get; set; }
    }

    // Result of a shop purchase — the inventory row plus whatever the purchase granted immediately.
    public class PurchaseResult
    {
        [JsonProperty("purchase_id")]
        public string PurchaseId { get; set; }

        [JsonProperty("item_type")]
        public string ItemType { get; set; }

        /// <summary>Where the purchase has got to: one of <see cref="PurchaseResultStatuses"/>. Paid when the server leaves it out, as its schema does.</summary>
        [JsonProperty("status", NullValueHandling = NullValueHandling.Ignore)]
        public string Status { get; set; } = PurchaseResultStatuses.Paid;

        /// <summary>True when the purchase is paid, so what it grants is the player's. A pending one has handed nothing over yet.</summary>
        [JsonIgnore]
        public bool IsPaid => string.Equals(Status, PurchaseResultStatuses.Paid, StringComparison.Ordinal);

        [JsonProperty("inventory")]
        public PlayerInventory Inventory { get; set; }

        // Server omits this for non-reward items; initialized so callers never null-check before iterating.
        [JsonProperty("granted")]
        public List<ShopItemReward> Granted { get; set; } = new List<ShopItemReward>();

        // Wallet after the grant, when the purchase moved currency.
        [JsonProperty("wallet")]
        public PlayerData Wallet { get; set; }
    }

    // Result of consuming an inventory item — the updated row plus whatever consuming it granted.
    public class ConsumeResult
    {
        [JsonProperty("inventory")]
        public PlayerInventory Inventory { get; set; }

        [JsonProperty("granted")]
        public List<ShopItemReward> Granted { get; set; } = new List<ShopItemReward>();

        [JsonProperty("wallet")]
        public PlayerData Wallet { get; set; }
    }

    public class ShopData
    {
        [JsonProperty("stats")]
        public Dictionary<string, object> Stats { get; set; }

        [JsonProperty("web_shop_url")]
        public string WebShopUrl { get; set; }

        [JsonProperty("pwa_shop_url")]
        public string PwaShopUrl { get; set; }
    }

    public class Shop
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("game_version_id")]
        public string GameVersionId { get; set; }

        [JsonProperty("data")]
        public ShopData Data { get; set; }

        [JsonProperty("shop_items")]
        public List<ShopItem> ShopItems { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }
    }

    public class ShopItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("shop_id")]
        public string ShopId { get; set; }

        [JsonProperty("patch_id")]
        public string PatchId { get; set; }

        [JsonProperty("price")]
        public int Price { get; set; }

        [JsonProperty("currency")]
        public string Currency { get; set; }

        // What kind of item this is — drives whether buying it grants rewards or lands in inventory.
        [JsonProperty("type")]
        public string Type { get; set; }

        // What this item grants when bought or consumed. Empty for plain inventory items.
        [JsonProperty("rewards")]
        public List<ShopItemReward> Rewards { get; set; } = new List<ShopItemReward>();

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }
    }
}
