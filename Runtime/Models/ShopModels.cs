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

    /// <summary>What kind of item a shop item is (<see cref="ShopItem.Type"/>). Strings rather than an enum: the server can add a kind, and an unknown one must not break reading the item.</summary>
    public static class ShopItemTypes
    {
        /// <summary>Lands in the player's inventory. In a store, set it up as a one-time (non-consumable) product.</summary>
        public const string Standard = "standard";
        /// <summary>Pays its rewards straight into the wallet. In a store, set it up as a consumable product.</summary>
        public const string CurrencyPack = "currency_pack";
        /// <summary>Lands in the inventory and grants its rewards when consumed. In a store, set it up as a consumable product.</summary>
        public const string Consumable = "consumable";
    }

    /// <summary>The stores and web payment gateways Flock takes real money through, as the server names them (the keys of <see cref="ShopItem.StoreProductIds"/>). Strings rather than an enum: the server can add one, and an unknown one must not break reading an item.</summary>
    public static class PaymentProviders
    {
        public const string AppleAppStore = "apple_app_store";
        public const string GooglePlay = "google_play";
        public const string Stripe = "stripe";
        public const string Tap = "tap";
        public const string Moyasar = "moyasar";
    }

    /// <summary>A purchase a store made, as Flock redeems it: the App Store's transaction id, or Google Play's product id and purchase token. Make one with <see cref="FromAppleAppStore"/> or <see cref="FromGooglePlay"/>.</summary>
    public sealed class StoreReceipt
    {
        /// <summary>The store that made the purchase: <see cref="PaymentProviders.AppleAppStore"/> or <see cref="PaymentProviders.GooglePlay"/>.</summary>
        public string Store { get; }

        /// <summary>The App Store's transaction id (StoreKit's transaction id); null for Google Play.</summary>
        public string TransactionId { get; }

        /// <summary>The Google Play product id the player bought; null for the App Store.</summary>
        public string ProductId { get; }

        /// <summary>The Google Play purchase token; null for the App Store.</summary>
        public string PurchaseToken { get; }

        private StoreReceipt(string store, string transactionId, string productId, string purchaseToken)
        {
            Store = store;
            TransactionId = transactionId;
            ProductId = productId;
            PurchaseToken = purchaseToken;
        }

        /// <summary>An App Store purchase, by its transaction id, exactly as StoreKit gave it.</summary>
        public static StoreReceipt FromAppleAppStore(string transactionId)
            => new StoreReceipt(PaymentProviders.AppleAppStore, transactionId, null, null);

        /// <summary>A Google Play purchase, by its product id and purchase token, exactly as Google Play gave them.</summary>
        public static StoreReceipt FromGooglePlay(string productId, string purchaseToken)
            => new StoreReceipt(PaymentProviders.GooglePlay, null, productId, purchaseToken);
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

        /// <summary>The price in <see cref="Currency"/>; for a real-money item, in the currency's smallest unit (199 is USD 1.99).</summary>
        [JsonProperty("price")]
        public int Price { get; set; }

        [JsonProperty("currency")]
        public string Currency { get; set; }

        /// <summary>True when <see cref="Currency"/> is one the game's currency config lists as real money: the item is paid for through a store or a web checkout, never from the wallet, and <c>PurchaseAsync</c> refuses it with <c>ShopRealCurrencyNeedsCheckout</c>. False when the server does not say.</summary>
        [JsonProperty("is_real_money", NullValueHandling = NullValueHandling.Ignore)]
        public bool IsRealMoney { get; set; }

        /// <summary>What kind of item this is, one of <see cref="ShopItemTypes"/>: drives whether buying it grants rewards or lands in inventory.</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        // What this item grants when bought or consumed. Empty for plain inventory items.
        [JsonProperty("rewards")]
        public List<ShopItemReward> Rewards { get; set; } = new List<ShopItemReward>();

        /// <summary>This item's product id in each store, keyed by <see cref="PaymentProviders"/> name, exactly as set in the Flock dashboard; empty when none is set. A phone shows the player the store's price for that product, not <see cref="Price"/>.</summary>
        [JsonProperty("store_skus", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> StoreProductIds { get; set; } = new Dictionary<string, string>();

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }
    }
}
