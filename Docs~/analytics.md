# Analytics

[← Back to README](../README.md)

A game reports two different things, and they land on two different dashboards:

| | Gameplay analytics | Diagnostics |
|---|---|---|
| Answers | What players did | What went wrong |
| Read on | Dashboards → Game Metrics | Diagnostics → Events and Errors |
| Calls | `TrackEvent`, `RecordScreenView`, sessions, `RecordTransactionAsync` | `LogDiagnosticEvent`, `LogDiagnosticError`, `LogDiagnosticException` |
| Custom data | Properties keep their JSON type, so a number can be charted | Extra data and error data, stored with the entry |
| Nobody signed in | Held, and credited to whoever signs in next | Sent as usual |

`TrackEvent` and the `LogDiagnostic...` calls never throw and never wait on the network: each writes one small file to a queue
on disk, on the calling thread, and returns; entries are sent on the flush triggers (every `EventBufferFlushIntervalSeconds`, default 10 s, and on pause, session end and sign-in). `StartSessionAsync`
and `RecordTransactionAsync` send at once and need a signed-in player: before sign-in the session runs locally (an error is
logged) and a transaction fails.

## Gameplay events

```csharp
bool recorded = FlockClient.Instance.Analytics.TrackEvent("level_complete",
    new Dictionary<string, object> { { "level", 3 }, { "time", 42.5 }, { "stars", 2 } },
    eventCategory: "progress");
```

- Safe offline and from any thread. Each call writes one file, so record what happened (a level completed), not every
  frame. Properties keep their keys and JSON types, 64-bit numbers included.
- Recorded while nobody is signed in, an event is held and credited to whoever signs in next; the server refuses an event
  without a player it knows.
- Refused with `false` and a warning: an empty name, a name over `FlockAnalyticsProvider.MaxEventNameLength` (200)
  characters or a category over `MaxEventCategoryLength` (100) (the server cannot store either, and would fail every event
  sent with it), `session_started` (the server records it itself when a session starts), or properties that cannot be
  written as JSON. Without consent it is refused quietly.
- With `CacheFailedEvents` off there is no queue: the event is sent once, straight away, while a player is signed in, and
  dropped otherwise.
- The SDK records no gameplay events of its own, so the Game Metrics dashboards show only what your game tracks.

## Diagnostics

```csharp
FlockClient.Instance.Analytics.LogDiagnosticException(exception);
FlockClient.Instance.Analytics.LogDiagnosticError("inventory desync", errorCode: "INV_001");
FlockClient.Instance.Analytics.LogDiagnosticEvent("checkpoint reached");
```

Unhandled exceptions are captured for you. `LogEvent`, `LogError` and `LogException`, the former names, still work and forward
to these; they are marked obsolete, so the compiler names the replacement.

## Sessions, transactions and screen views

```csharp
// Sessions auto-start at login when AutoStartSession is true (default). Otherwise:
await FlockClient.Instance.Analytics.StartSessionAsync();
await FlockClient.Instance.Analytics.EndSessionAsync();

// Optional: awaitable drain of everything queued
await FlockClient.Instance.Analytics.FlushAsync();

// Transactions: immediate send, requires a signed-in player
await FlockClient.Instance.Analytics.RecordTransactionAsync(new AnalyticsTransactionRequest {
    Amount = 4.99f, CurrencyCode = "USD", TransactionType = "Purchase", Status = "Purchased"
});

// Screen views: local-only, counted into the session's ScreensViewed
FlockClient.Instance.Analytics.RecordScreenView("MainMenu");
```

## Consent

By default, analytics behaves as it always has — collection runs once a player is authenticated. Turn on **Analytics Require Explicit Consent** (Flock > Settings, or `FlockAnalyticsConfig.RequireExplicitConsent`) for a real opt-in gate: no session, no event tracking, no device/FPS/screen-view capture until the game calls `SetConsent(true)`.

```csharp
FlockClient.Instance.Analytics.SetConsent(true);            // grant — starts/resumes the session
FlockClient.Instance.Analytics.SetConsent(false);           // revoke — pauses; does not delete queued data
FlockClient.Instance.Analytics.EraseLocalAnalyticsData();   // explicit purge of unsent local data

FlockEvents.OnConsentChanged += granted => Debug.Log($"Consent: {granted}");
```

- The decision persists across launches — no need to call `SetConsent` again unless it changes.
- `TrackEvent` and the `LogDiagnostic...` calls are gated the same as sessions — they carry player-identifiable data too.
- `RecordTransactionAsync` is the one exception — **not** gated by consent, since purchase records typically need to be retained for financial/tax reasons independent of tracking consent.
- `EraseLocalAnalyticsData()` is local-only: it clears events, session-end records, and log/crash events queued on-device but not yet sent. It does not delete analytics already delivered to Flock's backend — there's no backend endpoint for that today.

## Unexpected-termination detection

If a run died without a clean quit (crash, hang force-kill, foreground OOM, power loss), the SDK detects it on a later launch and queues one `app_termination` diagnostic (type debug, under Diagnostics → Events) automatically — nothing to call. It is a record about a crash, so it stays off the Game Metrics dashboards.

Each launch keeps its crash marker, its live-session record and its event queues in a folder of its own under `Application.persistentDataPath/Flock/analytics/launches/`, locked while it runs. A launch takes over only the folders of launches that have ended, so two copies of a game running at once (or the Editor beside a player) never report each other as crashed, end each other's session or send each other's queued events. What an ended launch left is reported once, by whichever launch takes it over.

| Extra data | Meaning |
|---|---|
| `previous_session_id` | The session that died |
| `classification` | `background_kill` (died while backgrounded — OS eviction / swipe-close) or `abnormal` (died foregrounded without the quit path) |
| `last_alive_at` | Approximate death time (last persisted heartbeat) |
| `unhandled_exception_count` | Unhandled exceptions seen during that run — context only, not proof of a crash |
| `app_version` / `sdk_version` | Versions of the run that died |

- Quitting via Alt-F4 / the window close button is a **clean** exit (Unity runs its quit path) — no event.
- Swipe-closing on mobile reports `background_kill`, because the app switcher backgrounds the app first.
- Requires `PersistSessionOnDisk`; the Editor and WebGL never record a marker of their own (no reliable lifecycle there). The Editor does report a standalone player's crash, since they share the folder.
- On macOS and Linux the lock may hold only within one process (unverified there), so two copies running at the same moment may not be told apart. A copy that has ended is taken over as usual.
- Consent-gated like all analytics; a dirty exit found while consent is off is discarded.

See also: [SDK Events](events.md) for the session lifecycle events (`OnSessionStarted`, `OnSessionEnded`, `OnSessionPaused`, `OnSessionResumed`).
