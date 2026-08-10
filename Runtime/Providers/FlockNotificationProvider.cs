using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Models;
using Flock.Http;
using UnityEngine;

namespace Flock.Providers
{
    /// <summary>The player's notification inbox, plus scheduling a dashboard-authored template for them at a future time.</summary>
    /// <remarks>There is no background poll by design — the API meters these calls, so the game decides when to refresh. <see cref="FlockEvents.OnUnreadCountChanged"/> fires whenever a fetch brings back a server-reported count.</remarks>
    public class FlockNotificationProvider : FlockProviderBase
    {
        private const string SnapshotCategory = "notification";
        private const string PendingSchedulesKey = "pending_schedules";
        private const string WatermarkKey = "seen_watermark";

        // Last count the server reported; null until a count-bearing call succeeds.
        private int? _lastUnreadCount;

        // Name+locale → template memo: scheduling resolves a name to an id first, and that shouldn't cost a round trip each time.
        private readonly Dictionary<string, NotificationTemplate> _templatesByName = new Dictionary<string, NotificationTemplate>();

        public FlockNotificationProvider(FlockClient client) : base(client) { }

        /// <summary>Drops cached inbox reads and the template memo. Pending schedules and the seen-watermark survive — neither is cache: one is the only handle on a cancellable reminder, the other decides what counts as newly received.</summary>
        public void ClearCache()
        {
            List<PendingSchedule> pending = LoadPendingSchedules();
            NotificationWatermark mark = LoadWatermark();
            _lastUnreadCount = null;
            _templatesByName.Clear();
            DeleteSnapshotCategory(SnapshotCategory);
            if (pending.Count > 0)
                SavePendingSchedules(pending);
            if (mark.Seeded)
                SaveWatermark(mark);
        }

        /// <summary>A page of the signed-in player's inbox, newest first. Set <paramref name="unreadOnly"/> to skip already-read entries.</summary>
        public async Task<PaginatedResponse<Notification>> GetInboxAsync(
            bool unreadOnly = false,
            int page = 1,
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();

            string query = $"?page={page}&limit={limit}" + (unreadOnly ? "&unread_only=true" : string.Empty);

            PaginatedResponse<Notification> inbox = await FetchWithSnapshotAsync(
                SnapshotCategory,
                PlayerScopedKey($"inbox_{unreadOnly}_p{page}_l{limit}"),
                async () => await FlockHttpClient.GetAsync<PaginatedResponse<Notification>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.Notification}{query}", Client.GetBaseHeaders(), cancellationToken),
                "Fetch notifications", cancellationToken);

            RaiseNewNotifications(inbox != null ? inbox.Items : null);
            return inbox;
        }

        /// <summary>How many notifications the player hasn't read. Raises <see cref="FlockEvents.OnUnreadCountChanged"/> when the value moves.</summary>
        public async Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();

            UnreadCount result = await FetchWithSnapshotAsync(
                SnapshotCategory,
                PlayerScopedKey("unread_count"),
                async () =>
                {
                    GenericResponse<UnreadCount> response = await FlockHttpClient.GetAsync<GenericResponse<UnreadCount>>(
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.NotificationUnreadCount}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                },
                "Fetch unread notification count", cancellationToken);

            SetUnreadCount(result.Count);
            return result.Count;
        }

        /// <summary>Unread count and the first <paramref name="limit"/> notifications in one call — the cheapest way to refresh a mailbox badge and preview together.</summary>
        public async Task<NotificationSummary> GetSummaryAsync(
            int limit = 10,
            CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();

            NotificationSummary summary = await FetchWithSnapshotAsync(
                SnapshotCategory,
                PlayerScopedKey($"summary_l{limit}"),
                async () =>
                {
                    GenericResponse<NotificationSummary> response = await FlockHttpClient.GetAsync<GenericResponse<NotificationSummary>>(
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.NotificationSummary}?limit={limit}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                },
                "Fetch notification summary", cancellationToken);

            RaiseNewNotifications(summary != null ? summary.Items : null);
            SetUnreadCount(summary.UnreadCount);
            return summary;
        }

        /// <summary>Marks a notification read straight from a list entry, so callers don't have to reach for <c>.Id</c>.</summary>
        public Task<Notification> MarkReadAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            if (notification == null) throw new FlockValidationException("Notification cannot be null");
            return MarkReadAsync(notification.Id, cancellationToken);
        }

        /// <summary>Marks one notification read. Does not adjust the unread count — the server doesn't report a new total here, so refresh with <see cref="GetUnreadCountAsync"/> or <see cref="GetSummaryAsync"/>.</summary>
        public async Task<Notification> MarkReadAsync(
            string notificationId,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(notificationId, "Notification ID");
            RequireAuthenticated();

            return await ExecuteAsync(async () =>
            {
                GenericResponse<Notification> response = await FlockHttpClient.PostAsync<GenericResponse<Notification>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.NotificationReadById(notificationId)}", EmptyBody(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Mark notification read", cancellationToken);
        }

        /// <summary>Marks every unread notification read and returns how many changed. Unread is zero afterwards, so this raises <see cref="FlockEvents.OnUnreadCountChanged"/>.</summary>
        public async Task<int> MarkAllReadAsync(CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();

            MarkAllReadResult result = await ExecuteAsync(async () =>
            {
                GenericResponse<MarkAllReadResult> response = await FlockHttpClient.PostAsync<GenericResponse<MarkAllReadResult>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.NotificationReadAll}", EmptyBody(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Mark all notifications read", cancellationToken);

            SetUnreadCount(0);
            return result.Updated;
        }

        /// <summary>Acquires this device's push token from the OS and registers it in one call — no token argument needed.</summary>
        /// <remarks>Works on iOS when <c>com.unity.mobile.notifications</c> is installed (APNs is an OS service, so nothing else is required). Android cannot work this way: an FCM token comes from Google's messaging client, which ships as the Firebase SDK and cannot be a package dependency — fetch it yourself and use the token overload. Throws with that explanation everywhere else.</remarks>
        public async Task<DeviceToken> RegisterThisDeviceAsync(CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();

#if FLOCK_MOBILE_NOTIFICATIONS && UNITY_IOS && !UNITY_EDITOR
            string token = await AcquireApnsTokenAsync();
            if (string.IsNullOrEmpty(token))
                throw new FlockValidationException(
                    "iOS returned no APNs token. The player declined notification permission, or the build has no push entitlement.");

            return await RegisterDeviceTokenAsync(FlockDevicePlatform.Ios, token, cancellationToken);
#else
            await Task.CompletedTask;
            throw new FlockValidationException(
                "RegisterThisDeviceAsync can only fetch a token on an iOS build with com.unity.mobile.notifications installed. " +
                "On Android get the token from Firebase Cloud Messaging, then call RegisterDeviceTokenAsync(platform, token) — " +
                "the FCM client cannot ship as a package dependency, so the SDK can't do it for you.");
#endif
        }

#if FLOCK_MOBILE_NOTIFICATIONS && UNITY_IOS && !UNITY_EDITOR
        // Asks iOS for notification permission AND remote registration in one request; the token lands on the
        // request object once it finishes. Wrapped here so the Unity.Notifications types never reach callers.
        private static async Task<string> AcquireApnsTokenAsync()
        {
            using (Unity.Notifications.iOS.AuthorizationRequest request = new Unity.Notifications.iOS.AuthorizationRequest(
                Unity.Notifications.iOS.AuthorizationOption.Alert |
                Unity.Notifications.iOS.AuthorizationOption.Badge |
                Unity.Notifications.iOS.AuthorizationOption.Sound,
                true))
            {
                while (!request.IsFinished)
                    await Task.Yield();

                return request.DeviceToken;
            }
        }
#endif

        /// <summary>Registers this device for push, detecting the platform from the running build.</summary>
        /// <remarks>The SDK cannot obtain <paramref name="token"/> itself — Unity has no first-party FCM/APNs. Get it from Firebase or com.unity.mobile.notifications and pass it here. Re-register whenever the OS issues a new token.</remarks>
        /// <exception cref="FlockValidationException">On a platform the push backend has no value for — PC, Mac, Linux, console, and the Editor.</exception>
        public Task<DeviceToken> RegisterDeviceTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            return RegisterDeviceTokenAsync(CurrentDevicePlatform(), token, cancellationToken);
        }

        /// <summary>Registers this device for push against an explicit platform — for builds where the running platform isn't the one the token came from.</summary>
        public async Task<DeviceToken> RegisterDeviceTokenAsync(
            FlockDevicePlatform platform,
            string token,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(token, "Device Token");
            RequireAuthenticated();

            RegisterDeviceTokenInput request = new RegisterDeviceTokenInput
            {
                Platform = platform.ToWireValue(),
                Token = token
            };

            return await ExecuteAsync(async () =>
            {
                GenericResponse<DeviceToken> response = await FlockHttpClient.PostAsync<GenericResponse<DeviceToken>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.DeviceTokenRegister}", request, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Register device token", cancellationToken);
        }

        /// <summary>Stops push going to this token. Returns whether the server deactivated one.</summary>
        /// <remarks>Call this before <c>Logout()</c> if the device is shared — logout is local-only by design and will not do it for you.</remarks>
        public async Task<bool> UnregisterDeviceTokenAsync(
            string token,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(token, "Device Token");
            RequireAuthenticated();

            UnregisterDeviceTokenInput request = new UnregisterDeviceTokenInput { Token = token };

            UnregisterDeviceTokenResult result = await ExecuteAsync(async () =>
            {
                GenericResponse<UnregisterDeviceTokenResult> response = await FlockHttpClient.PostAsync<GenericResponse<UnregisterDeviceTokenResult>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.DeviceTokenUnregister}", request, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Unregister device token", cancellationToken);

            return result.Deactivated;
        }

        // The API accepts android/ios/web only. Anything else is refused outright rather than mapped to a
        // plausible-looking value — a token registered under the wrong platform fails silently at delivery time.
        private static FlockDevicePlatform CurrentDevicePlatform()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android: return FlockDevicePlatform.Android;
                case RuntimePlatform.IPhonePlayer: return FlockDevicePlatform.Ios;
                case RuntimePlatform.WebGLPlayer: return FlockDevicePlatform.Web;
                default:
                    throw new FlockValidationException(
                        $"Push is not available on {Application.platform} — the backend accepts android, ios and web only. " +
                        "Use the overload taking an explicit FlockDevicePlatform if you are supplying a token from elsewhere.");
            }
        }

        /// <summary>Every active template this game can schedule, name-ascending. Open to signed-out players — the catalog is game-scoped, not per-player.</summary>
        /// <remarks>A game holding one template in several locales gets one entry per locale, sharing a name and differing only by id. Use <see cref="GetTemplateByNameAsync"/> when the locale matters.</remarks>
        public async Task<List<NotificationTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default)
        {
            return await FetchWithSnapshotAsync(
                SnapshotCategory,
                "templates",
                async () =>
                {
                    GenericResponse<List<NotificationTemplate>> response = await FlockHttpClient.GetAsync<GenericResponse<List<NotificationTemplate>>>(
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.NotificationTemplate}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                },
                "Fetch notification templates", cancellationToken);
        }

        /// <summary>One active template by name. Open to signed-out players; memoized for the session after the first call.</summary>
        /// <param name="locale">Picks a specific localisation. Omitted, the server prefers English and falls back to the first locale on file.</param>
        public async Task<NotificationTemplate> GetTemplateByNameAsync(
            string templateName,
            string locale = null,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(templateName, "Template Name");

            string memoKey = $"{templateName}|{locale}";
            if (_templatesByName.TryGetValue(memoKey, out NotificationTemplate memoized))
                return memoized;

            NotificationTemplate template = await FetchWithSnapshotAsync(
                SnapshotCategory,
                $"template_name_{templateName}_{locale}",
                async () =>
                {
                    GenericResponse<NotificationTemplate> response = await FlockHttpClient.GetAsync<GenericResponse<NotificationTemplate>>(
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.NotificationTemplateByName(templateName, locale)}", Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                },
                "Fetch notification template", cancellationToken);

            if (template != null && !string.IsNullOrEmpty(template.Id))
                _templatesByName[memoKey] = template;
            return template;
        }

        /// <summary>The template's id, for logging or a deep link. Scheduling takes the name — nothing in this provider consumes an id.</summary>
        public async Task<string> ResolveTemplateIdAsync(
            string templateName,
            string locale = null,
            CancellationToken cancellationToken = default)
        {
            NotificationTemplate template = await GetTemplateByNameAsync(templateName, locale, cancellationToken);
            return template?.Id;
        }

        /// <summary>Schedules the template to arrive after <paramref name="delay"/> — the shape most games want ("energy full in 4 hours").</summary>
        public Task<ScheduledNotification> ScheduleAsync(
            string templateName,
            TimeSpan delay,
            Dictionary<string, object> variables = null,
            FlockNotificationChannels channels = FlockNotificationChannels.None,
            string locale = null,
            CancellationToken cancellationToken = default)
        {
            return ScheduleAsync(templateName, DateTime.UtcNow + delay, variables, channels, locale, cancellationToken);
        }

        /// <summary>Schedules a notification template to reach the signed-in player at <paramref name="deliverAtUtc"/>.</summary>
        /// <param name="templateName">The template's name as authored on the dashboard; it resolves to an id on the way through.</param>
        /// <param name="locale">Picks a specific localisation. Omitted, the server prefers English and falls back to the first locale on file.</param>
        /// <remarks>Keep the returned Id to cancel; the SDK also tracks it locally, see <see cref="GetPendingSchedules"/>.</remarks>
        public async Task<ScheduledNotification> ScheduleAsync(
            string templateName,
            DateTime deliverAtUtc,
            Dictionary<string, object> variables = null,
            FlockNotificationChannels channels = FlockNotificationChannels.None,
            string locale = null,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(templateName, "Template Name");
            RequireAuthenticated();

            // Resolved before the write, so a name this game doesn't have never leaves a schedule behind.
            string templateId = await RequireTemplateIdAsync(templateName, locale, cancellationToken);

            ScheduleNotificationInput request = new ScheduleNotificationInput
            {
                TemplateId = templateId,
                DeliverAt = deliverAtUtc.ToUniversalTime().ToString("o"),
                Variables = variables != null && variables.Count > 0 ? variables : null,
                Channels = channels.ToWireValues()
            };

            ScheduledNotification scheduled = await ExecuteAsync(async () =>
            {
                GenericResponse<ScheduledNotification> response = await FlockHttpClient.PostAsync<GenericResponse<ScheduledNotification>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.NotificationSchedule}", request, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            },
            // Not idempotent: a re-sent schedule after an ambiguous failure creates a second reminder the game
            // can't cancel, because it only ever learns one scheduled id. Surface the failure instead.
            "Schedule notification", cancellationToken, idempotent: false);

            TrackPending(scheduled, templateName, templateId);
            return scheduled;
        }

        // A name this game doesn't have is a caller mistake, not an empty result.
        private async Task<string> RequireTemplateIdAsync(string templateName, string locale, CancellationToken cancellationToken)
        {
            string templateId = await ResolveTemplateIdAsync(templateName, locale, cancellationToken);
            if (string.IsNullOrEmpty(templateId))
                throw new FlockValidationException($"No notification template named '{templateName}'");
            return templateId;
        }

        /// <summary>Scheduled notifications this install created that haven't reached their delivery time yet.</summary>
        /// <remarks>Local bookkeeping, not a server query: it only knows what THIS install scheduled, and it infers delivery from the clock because <c>/v1</c> has no route to read a schedule back. Entries whose time has passed are dropped on read.</remarks>
        public List<PendingSchedule> GetPendingSchedules()
        {
            RequireAuthenticated();
            return LoadPendingSchedules();
        }

        /// <summary>Cancels every schedule this install is still tracking and returns how many the server actually cancelled. Entries the server no longer recognises are dropped rather than failing the batch.</summary>
        public async Task<int> CancelAllScheduledAsync(CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();

            List<PendingSchedule> pending = LoadPendingSchedules();
            int cancelled = 0;

            foreach (PendingSchedule entry in pending)
            {
                try
                {
                    await CancelScheduledAsync(entry.Id, cancellationToken);
                    cancelled++;
                }
                catch (FlockNetworkException ex) when (FlockNetworkException.IsPermanentStatus(ex.StatusCode))
                {
                    // Already delivered, already cancelled, or unknown to the server — it isn't pending either way.
                    UntrackPending(entry.Id);
                }
            }

            return cancelled;
        }

        /// <summary>Cancels the notification returned by <see cref="ScheduleAsync"/>, so callers can hold the object rather than its id.</summary>
        public Task<ScheduledNotification> CancelScheduledAsync(ScheduledNotification scheduled, CancellationToken cancellationToken = default)
        {
            if (scheduled == null) throw new FlockValidationException("Scheduled notification cannot be null");
            return CancelScheduledAsync(scheduled.Id, cancellationToken);
        }

        /// <summary>Cancels a pending scheduled notification by the Id returned from <see cref="ScheduleAsync"/>.</summary>
        public async Task<ScheduledNotification> CancelScheduledAsync(
            string scheduledId,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(scheduledId, "Scheduled Notification ID");
            RequireAuthenticated();

            ScheduledNotification cancelled = await ExecuteAsync(async () =>
            {
                GenericResponse<ScheduledNotification> response = await FlockHttpClient.DeleteAsync<GenericResponse<ScheduledNotification>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.NotificationScheduleById(scheduledId)}", Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Cancel scheduled notification", cancellationToken);

            UntrackPending(scheduledId);
            return cancelled;
        }

        // local pending-schedule bookkeeping 
        // The id from ScheduleAsync is the only handle on a pending reminder and /v1 has no route to list or read
        // one back, so the SDK persists what it scheduled. Player-scoped: a second player on the same device must
        // not see, or be able to cancel, the first player's reminders.

        private List<PendingSchedule> LoadPendingSchedules()
        {
            List<PendingSchedule> stored;
            if (!TryReadSnapshot(SnapshotCategory, PlayerScopedKey(PendingSchedulesKey), out stored) || stored == null)
                return new List<PendingSchedule>();

            // Delivery can only be inferred from the clock — nothing to query — so a passed entry stops being pending.
            List<PendingSchedule> live = new List<PendingSchedule>();
            foreach (PendingSchedule entry in stored)
            {
                if (entry != null && !HasElapsed(entry.DeliverAt))
                    live.Add(entry);
            }

            if (live.Count != stored.Count)
                SavePendingSchedules(live);
            return live;
        }

        private void SavePendingSchedules(List<PendingSchedule> pending)
        {
            WriteSnapshot(SnapshotCategory, PlayerScopedKey(PendingSchedulesKey), pending);
        }

        private void TrackPending(ScheduledNotification scheduled, string templateName, string templateId)
        {
            if (scheduled == null || string.IsNullOrEmpty(scheduled.Id)) return;

            List<PendingSchedule> pending = LoadPendingSchedules();
            pending.Add(new PendingSchedule
            {
                Id = scheduled.Id,
                TemplateName = templateName,
                TemplateId = templateId,
                DeliverAt = scheduled.DeliverAt
            });
            SavePendingSchedules(pending);
        }

        private void UntrackPending(string scheduledId)
        {
            List<PendingSchedule> pending = LoadPendingSchedules();
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].Id == scheduledId)
                    pending.RemoveAt(i);
            }
            SavePendingSchedules(pending);
        }

        // seen-watermark bookkeeping
        // "Received" is fetch-derived: there is no realtime channel and the SDK never polls, so the watermark is
        // what separates a notification the game has already been told about from a genuinely new one.

        private NotificationWatermark LoadWatermark()
        {
            NotificationWatermark stored;
            if (!TryReadSnapshot(SnapshotCategory, PlayerScopedKey(WatermarkKey), out stored) || stored == null)
                return new NotificationWatermark();
            return stored;
        }

        private void SaveWatermark(NotificationWatermark mark)
        {
            WriteSnapshot(SnapshotCategory, PlayerScopedKey(WatermarkKey), mark);
        }

        // Raises once per notification newer than the watermark, oldest first. The first fetch for a player seeds
        // silently — replaying a whole existing inbox as a burst of events is worse than not reporting its history.
        private void RaiseNewNotifications(IList<Notification> items)
        {
            NotificationWatermark mark = LoadWatermark();
            bool firstEver = !mark.Seeded;

            DateTime cutoff = DateTime.MinValue;
            if (!firstEver && !string.IsNullOrEmpty(mark.NewestCreatedAt))
                TryParseUtc(mark.NewestCreatedAt, out cutoff);

            DateTime newest = cutoff;
            List<Notification> fresh = new List<Notification>();

            foreach (Notification entry in items ?? new List<Notification>())
            {
                DateTime created;
                // An unparseable created_at is skipped rather than announced — a duplicate is worse than a miss.
                if (entry == null || !TryParseUtc(entry.CreatedAt, out created)) continue;
                if (created > newest) newest = created;
                if (!firstEver && created > cutoff) fresh.Add(entry);
            }

            // Persisted before anything is raised, so a handler that throws can't make the same one fire twice.
            if (firstEver || newest > cutoff)
            {
                mark.Seeded = true;
                if (newest > DateTime.MinValue)
                    mark.NewestCreatedAt = newest.ToString("o", CultureInfo.InvariantCulture);
                SaveWatermark(mark);
            }

            // The page arrives newest-first; walk back so handlers see them in the order they were created.
            for (int i = fresh.Count - 1; i >= 0; i--)
                FlockEvents.InvokeNotificationReceived(fresh[i]);
        }

        private static bool TryParseUtc(string timestamp, out DateTime parsed)
        {
            return DateTime.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed);
        }

        // Unparseable timestamps are kept rather than silently dropped — losing a cancellable id is the worse failure.
        private static bool HasElapsed(string deliverAt)
        {
            DateTime parsed;
            if (!TryParseUtc(deliverAt, out parsed))
                return false;
            return parsed <= DateTime.UtcNow;
        }

        // The read routes take no body, but PostAsync always serializes one — send {} rather than a bare
        // `null`, which strict server-side validation is more likely to reject. First bodyless POSTs in the SDK.
        private static Dictionary<string, object> EmptyBody()
        {
            return new Dictionary<string, object>();
        }

        // Only fires when the number actually moves, so a repeated refresh doesn't spam a badge.
        private void SetUnreadCount(int count)
        {
            if (_lastUnreadCount == count) return;
            _lastUnreadCount = count;
            FlockEvents.InvokeUnreadCountChanged(count);
        }

        // Bearer-only endpoints — fail fast instead of a guaranteed server 401.
        private void RequireAuthenticated()
        {
            if (!Client.IsAuthenticated)
                throw new FlockAuthException("No player is signed in");
        }

        // Player-scoped so one player's inbox can't be served to the next player on a shared device.
        private string PlayerScopedKey(string key)
        {
            return $"{key}_{Client.CurrentPlayerId}";
        }
    }
}
