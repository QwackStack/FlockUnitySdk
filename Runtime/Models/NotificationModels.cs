using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Flock.Models
{
    /// <summary>Channels to deliver on — combine with <c>|</c>. <see cref="None"/> sends nothing and lets the template's own channels apply.</summary>
    [Flags]
    public enum FlockNotificationChannels
    {
        None = 0,
        InApp = 1 << 0,
        Email = 1 << 1,
        Push = 1 << 2
    }

    /// <summary>Wire values for <see cref="FlockNotificationChannels"/>. Mapped explicitly rather than via ToString() so casing can't drift with the caller's locale.</summary>
    public static class FlockNotificationChannelsExtensions
    {
        /// <summary>Returns null for <see cref="FlockNotificationChannels.None"/> so the field is omitted rather than sent empty.</summary>
        public static List<string> ToWireValues(this FlockNotificationChannels channels)
        {
            if (channels == FlockNotificationChannels.None) return null;

            List<string> wire = new List<string>();
            if ((channels & FlockNotificationChannels.InApp) != 0) wire.Add("in_app");
            if ((channels & FlockNotificationChannels.Email) != 0) wire.Add("email");
            if ((channels & FlockNotificationChannels.Push) != 0) wire.Add("push");
            return wire;
        }
    }

    /// <summary>A dashboard-authored template this game can schedule, addressed by name.</summary>
    /// <remarks>Authoring content (title, body, data) stays server-side — this carries only enough to pick one. The rendered result reaches the player through the inbox.</remarks>
    public class NotificationTemplate
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Free-form grouping from the dashboard — the API doesn't constrain it to a fixed set.</summary>
        [JsonProperty("category")]
        public string Category { get; set; }
    }

    /// <summary>A notification queued for future delivery to one player.</summary>
    public class ScheduledNotification
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("studio_id")]
        public string StudioId { get; set; }

        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("template_id")]
        public string TemplateId { get; set; }

        [JsonProperty("variables")]
        public Dictionary<string, object> Variables { get; set; }

        [JsonProperty("channels")]
        public List<string> Channels { get; set; }

        [JsonProperty("deliver_at")]
        public string DeliverAt { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }

        /// <summary>Set once delivery has produced an inbox notification; null while still pending.</summary>
        [JsonProperty("notification_id")]
        public string NotificationId { get; set; }

        [JsonProperty("delivered_at")]
        public string DeliveredAt { get; set; }

        [JsonProperty("canceled_at")]
        public string CanceledAt { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }
    }

    /// <summary>One delivered notification in the player's inbox.</summary>
    public class Notification
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("studio_id")]
        public string StudioId { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("recipient_type")]
        public string RecipientType { get; set; }

        [JsonProperty("recipient_id")]
        public string RecipientId { get; set; }

        /// <summary>Free-form category from the template — the API doesn't constrain it to a fixed set.</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>Usually info/warning/critical, but the API types this as a plain string, so it isn't an enum here.</summary>
        [JsonProperty("severity")]
        public string Severity { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("body")]
        public string Body { get; set; }

        /// <summary>Template payload — the hook for deep links or in-game actions.</summary>
        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("read_at")]
        public string ReadAt { get; set; }

        /// <summary>Set when the notification came from a dashboard campaign rather than a direct schedule.</summary>
        [JsonProperty("campaign_id")]
        public string CampaignId { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }

        /// <summary>Convenience over <see cref="ReadAt"/> — the API has no separate read flag.</summary>
        [JsonIgnore]
        public bool IsRead => !string.IsNullOrEmpty(ReadAt);

        /// <summary>Reads one <see cref="Data"/> entry as <typeparamref name="T"/>. False when the key is absent or the value can't become a T — never throws.</summary>
        public bool TryGetData<T>(string key, out T value)
        {
            value = default;
            if (Data == null || string.IsNullOrEmpty(key)) return false;

            object raw;
            if (!Data.TryGetValue(key, out raw)) return false;
            return TryConvert(raw, out value);
        }

        /// <summary>Reads one <see cref="Data"/> entry, returning <paramref name="fallback"/> when it's absent or the wrong shape.</summary>
        public T GetData<T>(string key, T fallback = default)
        {
            T value;
            return TryGetData(key, out value) ? value : fallback;
        }

        /// <summary>Deserializes the whole <see cref="Data"/> payload into your own type — for templates whose data is a known structure.</summary>
        public T GetDataAs<T>()
        {
            if (Data == null || Data.Count == 0) return default;

            try
            {
                return JObject.FromObject(Data).ToObject<T>();
            }
            catch (JsonException)
            {
                return default;
            }
        }

        // JSON round-tripping into Dictionary<string, object> leaves whole numbers as long, decimals as
        // double, and anything nested as JObject/JArray — so a plain (int) cast on a JSON 7 throws. Going
        // back through JToken normalises all of those, and keeps Newtonsoft's types out of the public API.
        private static bool TryConvert<T>(object raw, out T value)
        {
            value = default;
            if (raw == null) return false;

            if (raw is T direct)
            {
                value = direct;
                return true;
            }

            try
            {
                JToken token = raw as JToken ?? JToken.FromObject(raw);
                value = token.ToObject<T>();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (InvalidCastException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }

    /// <summary>Platforms the push backend accepts a device token for. There is no desktop value — PC/Mac/Linux cannot receive push at all.</summary>
    public enum FlockDevicePlatform
    {
        Android,
        Ios,
        Web
    }

    /// <summary>Wire values for <see cref="FlockDevicePlatform"/>. Mapped explicitly rather than via ToString() so casing can't drift with the caller's locale.</summary>
    public static class FlockDevicePlatformExtensions
    {
        public static string ToWireValue(this FlockDevicePlatform platform)
        {
            switch (platform)
            {
                case FlockDevicePlatform.Android: return "android";
                case FlockDevicePlatform.Web: return "web";
                default: return "ios";
            }
        }
    }

    /// <summary>A push token the backend holds for one player on one device.</summary>
    public class DeviceToken
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("platform")]
        public string Platform { get; set; }

        [JsonProperty("is_active")]
        public bool IsActive { get; set; }

        [JsonProperty("last_seen_at")]
        public string LastSeenAt { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }
    }

    internal class RegisterDeviceTokenInput
    {
        [JsonProperty("platform")]
        public string Platform { get; set; }

        [JsonProperty("token")]
        public string Token { get; set; }
    }

    internal class UnregisterDeviceTokenInput
    {
        [JsonProperty("token")]
        public string Token { get; set; }
    }

    internal class UnregisterDeviceTokenResult
    {
        [JsonProperty("deactivated")]
        public bool Deactivated { get; set; }
    }

    /// <summary>A scheduled notification this install created, remembered locally because the API has no route for a game to list its own pending schedules.</summary>
    public class PendingSchedule
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>The template name passed to ScheduleAsync, kept so callers can tell entries apart without another call.</summary>
        [JsonProperty("template_name")]
        public string TemplateName { get; set; }

        /// <summary>The id that name resolved to when the schedule was created.</summary>
        [JsonProperty("template_id")]
        public string TemplateId { get; set; }

        [JsonProperty("deliver_at")]
        public string DeliverAt { get; set; }
    }

    // What the SDK has already surfaced through OnNotificationReceived. State, not cache: losing it either
    // re-announces old notifications or silently swallows new ones, so it survives ClearCache.
    internal class NotificationWatermark
    {
        /// <summary>False until the first fetch for this player, which seeds silently so an existing inbox isn't replayed as a burst.</summary>
        [JsonProperty("seeded")]
        public bool Seeded { get; set; }

        /// <summary>Newest created_at surfaced so far; null when the inbox was empty at seed time, which makes everything later new.</summary>
        [JsonProperty("newest_created_at")]
        public string NewestCreatedAt { get; set; }
    }

    /// <summary>Unread count plus the first page, from the one call that serves both.</summary>
    public class NotificationSummary
    {
        [JsonProperty("unread_count")]
        public int UnreadCount { get; set; }

        [JsonProperty("items")]
        public List<Notification> Items { get; set; }
    }

    // Single-scalar envelopes; the provider unwraps these so callers get an int.
    internal class UnreadCount
    {
        [JsonProperty("count")]
        public int Count { get; set; }
    }

    internal class MarkAllReadResult
    {
        [JsonProperty("updated")]
        public int Updated { get; set; }
    }

    // Optional members are omitted rather than sent empty, matching the rest of the wire surface.
    internal class ScheduleNotificationInput
    {
        [JsonProperty("template_id")]
        public string TemplateId { get; set; }

        [JsonProperty("deliver_at")]
        public string DeliverAt { get; set; }

        [JsonProperty("variables", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, object> Variables { get; set; }

        [JsonProperty("channels", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Channels { get; set; }
    }
}
