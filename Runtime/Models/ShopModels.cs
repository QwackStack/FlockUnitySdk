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
        /// <summary>Lands in the player's inventory. In a store, set it up as a one-time (non-consumable) product: the store sells it once. The wallet sells it again on every purchase, so check <c>GetOwnedItemCountsAsync</c> before offering a wallet one.</summary>
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

        /// <summary>The wallet's balances after the purchase, by currency code; null when no currency moved.</summary>
        [JsonIgnore]
        public FlockWallet Balances => Wallet == null ? null : FlockWallet.From(Wallet);
    }

    /// <summary>A web checkout the server opened for a real-money item: send the player to <see cref="PaymentUrl"/>. Nothing is paid or delivered until the gateway confirms; read how it ended with <c>Shop.GetPurchaseAsync(PurchaseId)</c>.</summary>
    public class FlockCheckout
    {
        /// <summary>The purchase this checkout pays for, pending until the gateway confirms.</summary>
        [JsonProperty("purchase_id")]
        public string PurchaseId { get; set; }

        /// <summary>The gateway it opened at, one of <see cref="PaymentProviders"/>.</summary>
        [JsonProperty("provider")]
        public string Provider { get; set; }

        /// <summary>The gateway's payment page, to open in the player's browser.</summary>
        [JsonProperty("payment_url")]
        public string PaymentUrl { get; set; }

        /// <summary>When the server closes it unpaid as expired (UTC); null when the server gave no time.</summary>
        [JsonProperty("expires_at")]
        public DateTime? ExpiresAt { get; set; }
    }

    /// <summary>One of the signed-in player's purchases as the server holds it now: where it has got to, what it was for and what it granted.</summary>
    public class PlayerPurchase
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("shop_item_id")]
        public string ShopItemId { get; set; }

        /// <summary>One of <see cref="ShopItemTypes"/>.</summary>
        [JsonProperty("item_type")]
        public string ItemType { get; set; }

        /// <summary>Where it has got to, one of <see cref="PurchaseResultStatuses"/>: a web checkout is pending until it is paid, failed or expired.</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        /// <summary>The price in the currency's smallest unit (cents, halalas) for a real currency.</summary>
        [JsonProperty("price_amount")]
        public long PriceAmount { get; set; }

        [JsonProperty("price_currency")]
        public string PriceCurrency { get; set; }

        /// <summary>"wallet" for a game currency, "external" for a store or a web gateway.</summary>
        [JsonProperty("settlement_method")]
        public string SettlementMethod { get; set; }

        /// <summary>The store or web gateway it was paid through, one of <see cref="PaymentProviders"/>; null for a wallet purchase.</summary>
        [JsonProperty("payment_provider")]
        public string PaymentProvider { get; set; }

        /// <summary>What it granted at once, filled once paid.</summary>
        [JsonProperty("granted")]
        public List<ShopItemReward> Granted { get; set; } = new List<ShopItemReward>();

        /// <summary>The inventory entry it left, filled once paid for an item that is kept; null for a currency pack.</summary>
        [JsonProperty("player_inventory_id")]
        public string PlayerInventoryId { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("settled_at")]
        public DateTime? SettledAt { get; set; }

        [JsonProperty("refunded_at")]
        public DateTime? RefundedAt { get; set; }

        /// <summary>When a pending web checkout is closed as expired (UTC).</summary>
        [JsonProperty("expires_at")]
        public DateTime? ExpiresAt { get; set; }

        /// <summary>True once paid, so what it grants is the player's.</summary>
        [JsonIgnore]
        public bool IsPaid => string.Equals(Status, PurchaseResultStatuses.Paid, StringComparison.Ordinal);

        /// <summary>True while the server still waits for the gateway to say how it ended.</summary>
        [JsonIgnore]
        public bool IsPending => string.Equals(Status, PurchaseResultStatuses.Pending, StringComparison.Ordinal);
    }

    // POST shop/checkout's body; the player is the one the token names, and what is left out is the server's default.
    internal class CheckoutRequest
    {
        [JsonProperty("shop_item_id")]
        public string ShopItemId { get; set; }

        [JsonProperty("provider", NullValueHandling = NullValueHandling.Ignore)]
        public string Provider { get; set; }

        [JsonProperty("return_url", NullValueHandling = NullValueHandling.Ignore)]
        public string ReturnUrl { get; set; }

        [JsonProperty("cancel_url", NullValueHandling = NullValueHandling.Ignore)]
        public string CancelUrl { get; set; }
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

        /// <summary>The wallet's balances after consuming, by currency code; null when nothing was granted.</summary>
        [JsonIgnore]
        public FlockWallet Balances => Wallet == null ? null : FlockWallet.From(Wallet);
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
