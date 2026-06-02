using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
    public class SessionStartRequest
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("platform")]
        public string Platform { get; set; }

        [JsonProperty("device_type")]
        public string DeviceType { get; set; }

        [JsonProperty("game_version_id")]
        public string GameVersionId { get; set; }

        [JsonProperty("sdk_version")]
        public string SdkVersion { get; set; }

        [JsonProperty("started_at")]
        public string StartedAt { get; set; }
    }

    public class SessionStartResponse
    {
        [JsonProperty("session_id")]
        public string SessionId { get; set; }
    }

    public class SessionEndRequest
    {
        [JsonProperty("duration_seconds")]
        public int? DurationSeconds { get; set; }

        [JsonProperty("screens_viewed")]
        public int ScreensViewed { get; set; }

        [JsonProperty("is_bounce")]
        public bool IsBounce { get; set; }

        [JsonProperty("ended_at")]
        public string EndedAt { get; set; }
    }

    public class AnalyticsEventRequest
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("event_name")]
        public string EventName { get; set; }

        [JsonProperty("event_category")]
        public string EventCategory { get; set; }

        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        [JsonProperty("properties")]
        public Dictionary<string, object> Properties { get; set; }
    }

    public class AnalyticsEventsRequest
    {
        // No field initializer: the only construction site assigns Events directly.
        [JsonProperty("events")]
        public List<AnalyticsEventRequest> Events { get; set; }
    }

    public class AnalyticsTransactionRequest
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("amount")]
        public double Amount { get; set; }

        [JsonProperty("currency_id")]
        public string CurrencyId { get; set; }

        [JsonProperty("currency_code")]
        public string CurrencyCode { get; set; } = "USD";

        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("shop_item_id")]
        public string ShopItemId { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; } = 1;

        [JsonProperty("transaction_type")]
        public string TransactionType { get; set; } = "purchase";

        [JsonProperty("status")]
        public string Status { get; set; } = "completed";

        [JsonProperty("payment_provider")]
        public string PaymentProvider { get; set; }

        [JsonProperty("external_transaction_id")]
        public string ExternalTransactionId { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }
    }
}
