# Notifications

[← Back to README](../README.md)

Two things live here: the player's **inbox** (read notifications delivered to them) and **scheduling** (ask the server to deliver one later).

## Which kind of notification is this?

| | Timer held by | Appears in | App closed? | Platforms |
|---|---|---|---|---|
| **Inbox** | Server | Your own game UI | No | All, including desktop |
| **Push** | Server | OS banner | Yes | Android / iOS / Web only |
| **Device-local** | The OS | OS banner | Yes | Not a Flock feature — use `com.unity.mobile.notifications` |

This guide covers the **inbox**, the server-side scheduler that feeds it, and **push device-token registration**.

## Inbox

```csharp
FlockNotificationProvider notifications = FlockClient.Instance.Notification;

// Cheapest refresh: unread count + first page in one call.
NotificationSummary summary = await notifications.GetSummaryAsync(limit: 10);

// Full pages when the player opens their mailbox.
PaginatedResponse<Notification> page = await notifications.GetInboxAsync(unreadOnly: false, page: 1, limit: 50);

int unread = await notifications.GetUnreadCountAsync();

await notifications.MarkReadAsync(page.Items[0]);   // or MarkReadAsync(id)
int changed = await notifications.MarkAllReadAsync();
```

A `Notification` carries `Title`, `Body`, `Type`, `Severity`, `CreatedAt`, `IsRead`, and a `Data` payload — that last one is your hook for deep links and in-game actions.

### Reading `Data`

Use the typed accessors rather than indexing the dictionary:

```csharp
string screen = n.GetData<string>("deep_link");
int    level  = n.GetData<int>("level");
bool   isVip  = n.GetData<bool>("vip", fallback: false);

if (n.TryGetData("reward", out RewardPayload reward))
    Grant(reward);

// Or take the whole payload as one type, when the template's shape is known.
RewardPayload payload = n.GetDataAs<RewardPayload>();
```

**Don't cast out of `Data` directly.** JSON round-trips into `Dictionary<string, object>` as `long` for whole numbers, `double` for decimals, and `JObject`/`JArray` for anything nested — so `(int)n.Data["level"]` throws on a perfectly ordinary payload, and nested values force `Newtonsoft.Json.Linq` types into your own code. The accessors normalise all of that.

They never throw: a missing key, a null `Data`, or a value that can't become the requested type returns the fallback, and `TryGetData` returns false. Reading a notification payload shouldn't need a try/catch.

### Unread badge

```csharp
void OnEnable()  => FlockEvents.OnUnreadCountChanged += UpdateBadge;
void OnDisable() => FlockEvents.OnUnreadCountChanged -= UpdateBadge;

void UpdateBadge(int count) => badgeLabel.text = count > 0 ? count.ToString() : "";
```

**The SDK does not poll.** These calls are metered, so the game decides when to refresh — typically on menu open or when the app regains focus. The event fires whenever a call returns a server-reported count (`GetUnreadCountAsync`, `GetSummaryAsync`, `MarkAllReadAsync`) and only when the number actually changes, so refreshing repeatedly won't flicker a badge.

`MarkReadAsync` deliberately does **not** move the badge — the server reports no new total on that route, so the SDK would have to guess. Refresh afterwards if you need an exact count.

### Reacting to arrivals

`FlockEvents.OnNotificationReceived` fires for each notification the SDK hasn't surfaced before, seen during an inbox or summary fetch:

```csharp
private void OnEnable()  => FlockEvents.OnNotificationReceived += HandleNotification;
private void OnDisable() => FlockEvents.OnNotificationReceived -= HandleNotification;

private void HandleNotification(Notification n) => ShowToast(n.Title, n.Body);
```

"Received" means *first seen by a read*, not the moment the server created it — same constraint as the rest of this page. It is not a poller: it rides the fetches you already make, adds no traffic, and raises each notification once, **oldest first** so a batch reads in the order it happened.

The **first fetch for a player is silent**. It seeds a watermark instead, so a player with a full inbox doesn't get one event per historical row on launch. The watermark is player-scoped and survives `ClearCache()` — it's state, not cache, so dropping cache can't replay what the game already handled.

One event covers all three sources, because the payload already separates them: campaign deliveries carry `CampaignId`, trigger deliveries carry `trigger_id` in `Data`, and a reminder you scheduled has neither.

## Scheduling

Ask the server to deliver a dashboard-authored template to the signed-in player later.

```csharp
ScheduledNotification scheduled = await notifications.ScheduleAsync(
    templateName: "energy_full",         // the name as authored on the dashboard
    delay: TimeSpan.FromHours(4),
    variables: new Dictionary<string, object> { { "player", playerName } },
    channels: FlockNotificationChannels.InApp | FlockNotificationChannels.Push);

await notifications.CancelScheduledAsync(scheduled);
```

`variables` fill the `{placeholders}` in the template's title and body. `channels` is a `[Flags]` enum; leave it `None` to use whatever channels the template itself declares. There's also an absolute overload taking a `DateTime` when you know the wall-clock time.

The schedule route itself takes an ID, so the SDK resolves the name first and memoizes it for the session — scheduling the same template repeatedly costs one lookup, not one per call. A name this game doesn't have fails before anything is scheduled.

### Templates

Templates are authored on the dashboard; the SDK reads the catalog to turn a name into an ID.

```csharp
List<NotificationTemplate> all = await notifications.GetTemplatesAsync();
NotificationTemplate one = await notifications.GetTemplateByNameAsync("energy_full");
string id = await notifications.ResolveTemplateIdAsync("energy_full");   // for logging or a deep link
```

Both reads are game-scoped rather than player-scoped, so they work signed out, and they carry only `Id`, `Name` and `Category` — authoring content stays server-side. Only active templates are listed, so deactivating one on the dashboard is the kill switch.

A template can exist in several locales under one name. Pass `locale:` to pick a specific one; omitted, the server prefers English and falls back to the first locale on file.

### How a scheduled notification reaches the inbox

The scheduler is the timer; the inbox is the delivery surface. On `ScheduleAsync` the server stores a pending record with `NotificationId` null. At the delivery time it creates an inbox notification and back-fills `NotificationId` and `DeliveredAt`. Your next `GetInboxAsync` or `GetSummaryAsync` picks it up.

So: nothing appears before the delivery time, a push-only schedule never touches the inbox, and there is no realtime channel into a running client — it surfaces on your next fetch.

### Pending schedules

Ask the server what the player has pending — this is the one that survives a reinstall and sees schedules made on another device:

```csharp
PaginatedResponse<ScheduledNotification> pending = await notifications.GetScheduledAsync();
foreach (ScheduledNotification s in pending.Items)
    Debug.Log($"{s.Id} delivers at {s.DeliverAt}");

// Other states are available too.
PaginatedResponse<ScheduledNotification> delivered =
    await notifications.GetScheduledAsync(ScheduledNotificationStatuses.Delivered);

// Cancels everything pending for the player, including schedules this install never made.
int cancelled = await notifications.CancelAllScheduledAsync();
```

There is also a local, no-network list of what **this install** scheduled:

```csharp
List<PendingSchedule> pending = notifications.GetPendingSchedules();   // local, no network
```

That one is bookkeeping rather than a server query: it only knows this install's own schedules and infers delivery from the clock, dropping entries once their time passes. Prefer `GetScheduledAsync` and keep this for offline reads. `CancelAllScheduledAsync` uses the server list and falls back to this one if that read fails. `ClearCache()` preserves the local list; it's state, not cache.

## Push device tokens

Push needs a **device token** issued by the OS — FCM on Android, APNs on iOS. Flock stores it and pushes to it; it never creates one. How much work that is differs sharply by platform, because APNs is an operating-system service while FCM is a Google library.

### iOS — one call, one package

Install **`com.unity.mobile.notifications`** (Package Manager → Add package by name). That's the entire setup: the SDK's assembly definition carries a `versionDefines` entry, so `FLOCK_MOBILE_NOTIFICATIONS` is defined automatically when the package is present and the code below compiles in. Nothing to configure.

```csharp
// Requests permission, registers with APNs, gets the token, sends it to Flock.
DeviceToken registered = await notifications.RegisterThisDeviceAsync();
```

Then in Xcode: enable the **Push Notifications** capability, and make sure your APNs key is configured on the backend.

If the package isn't installed — or you're on Android, desktop, or in the Editor — `RegisterThisDeviceAsync` throws and tells you which of those applies. It never guesses.

### Android — bring the token yourself

An FCM token comes from Google's messaging client, which ships as the **Firebase Unity SDK** and can't be a package dependency, so this step is yours:

1. Install `FirebaseMessaging.unitypackage` from the [Firebase Unity SDK](https://firebase.google.com/download/unity)
2. Add your `google-services.json` from the Firebase console
3. On Android 13+, request the `POST_NOTIFICATIONS` permission
4. Fetch the token and hand it over:

```csharp
string token = await Firebase.Messaging.FirebaseMessaging.GetTokenAsync();
await notifications.RegisterDeviceTokenAsync(FlockDevicePlatform.Android, token);
```

You only need `firebase-messaging` — not Firebase Analytics, Auth, or anything else, and you never have to use Firebase's own notification console.

### Either way

```csharp
// Explicit platform, for a token sourced from somewhere the running build doesn't match.
await notifications.RegisterDeviceTokenAsync(FlockDevicePlatform.Android, token);

bool deactivated = await notifications.UnregisterDeviceTokenAsync(token);
```

**Re-register whenever the OS issues a new token** — they rotate on reinstall, restore, and at the platform's discretion. Subscribe to your provider's token-refresh callback and call register again, or push quietly stops reaching that player.

**`Logout()` does not unregister.** It's local-only by design (no network call), so on a shared device call `UnregisterDeviceTokenAsync` *before* logging out, or the next player keeps receiving the last one's push.

### Where push works, and where it can't

| Platform | Push | What's needed |
|---|---|---|
| Android | ✅ FCM | Firebase Unity SDK, `google-services.json`, `POST_NOTIFICATIONS` prompt on 13+ |
| iOS | ✅ APNs | `com.unity.mobile.notifications`, Xcode push capability, APNs key server-side |
| WebGL | ⚠️ Web Push | Service worker + VAPID — not provided by Unity; custom JS required |
| **PC / Mac / Linux / console** | ❌ **never** | No OS push service reachable from a generic backend |

`RegisterDeviceTokenAsync(token)` **throws** on an unsupported platform rather than guessing one. A token filed under the wrong platform fails silently when delivery is attempted, which is far harder to diagnose than an exception at the call site.

On desktop, the inbox and email are the only delivery paths. That is a platform constraint, not a Flock one — push requires something outside your app staying connected while it's closed, and only mobile OSes and browsers provide that.

## Retry behaviour

`ScheduleAsync` is **not** retried after an ambiguous failure (a 5xx). A re-sent schedule would create a second reminder the game can never cancel, since it only ever learns one id. A 408/429 *is* retried — the server provably never processed those. Everything else here is idempotent and retries normally.

Reads are snapshotted to disk and served when the server is unreachable, keyed per player so one player's inbox is never shown to the next on a shared device. Mark-read is not queued offline; it fails cleanly and the next fetch re-syncs from the server.

## Limitations

- **The catalog can't tell locales apart.** `GetTemplatesAsync` returns one entry per locale, all sharing a name and differing only by ID, because the client projection carries no `locale` field. Use `GetTemplateByNameAsync(name, locale)` when the locale matters.
- **No route lists a player's pending schedules**, hence the local tracking above and its limits.
- **Push has not yet been seen end to end from this SDK.** The backend does deliver to FCM/APNs, and registration is wired, but nobody has watched a banner arrive on a real device via Flock. Obtaining the token remains the game's job — see the platform table above.
- **Desktop can never receive push** — see the platform table above. This is a platform constraint, not a Flock one.
- **No player-facing notification preferences.** The preferences routes are dashboard-scoped to studio staff, so players have no opt-out through the SDK.

## Related

- [SDK Events](events.md) — the `FlockEvents` hub, including `OnUnreadCountChanged`
- [Error handling](errors.md) — exception types and codes
