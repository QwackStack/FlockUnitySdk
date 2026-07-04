# Analytics

[← Back to README](../README.md)

> **Auth dependency.** All analytics calls are **bearer-authenticated** (they require a logged-in player). Behavior depends on the call:
>
> - `LogException`, `LogError`, `LogEvent` — **synchronous, enqueue-only, safe to call before login**. They only write to the on-disk cache (no network, nothing to await) and drain automatically after authentication (entries tagged with the unauthenticated placeholder are retagged with the real `PlayerId` at login). They also drain on interval (`EventBufferFlushIntervalSeconds`, default 10s), on session pause, and on session end.
> - `RecordTransactionAsync`, `StartSessionAsync` — **best-effort async.** They attempt an immediate send and will 401 if called before login; session start swallows the error and continues locally, transaction does not.
>
> A console warning ("Player must be authenticated for analytics") is logged whenever a pre-auth call is made, but the SDK never throws for analytics — observability should not break the game.

```csharp
// Sessions auto-start at login when AutoStartSession is true (default). Otherwise:
await FlockClient.Instance.Analytics.StartSessionAsync();
await FlockClient.Instance.Analytics.EndSessionAsync();

// Logs — synchronous enqueue; delivery happens on the flush triggers (interval/pause/end/login)
FlockClient.Instance.Analytics.LogException(exception);
FlockClient.Instance.Analytics.LogError("inventory desync", errorCode: "INV_001");
FlockClient.Instance.Analytics.LogEvent("checkpoint reached");

// Optional: awaitable drain of everything queued — the one real await in tracking
await FlockClient.Instance.Analytics.FlushAsync();

// Transactions — immediate send, requires auth
await FlockClient.Instance.Analytics.RecordTransactionAsync(new AnalyticsTransactionRequest {
    Amount = 4.99f, CurrencyCode = "USD", TransactionType = "Purchase", Status = "Purchased"
});

// Screen views — local-only, contributes to session ScreensViewed counter
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
- `LogException`, `LogError`, and `LogEvent` are gated the same as sessions/event tracking — they carry player-identifiable data too.
- `RecordTransactionAsync` is the one exception — **not** gated by consent, since purchase records typically need to be retained for financial/tax reasons independent of tracking consent.
- `EraseLocalAnalyticsData()` is local-only: it clears events, session-end records, and log/crash events queued on-device but not yet sent. It does not delete analytics already delivered to Flock's backend — there's no backend endpoint for that today.

## Unexpected-termination detection

If the previous run died without a clean quit (crash, hang force-kill, foreground OOM, power loss), the SDK detects it on the next launch and queues one `app_termination` analytics event automatically — nothing to call.

| Property | Meaning |
|---|---|
| `previous_session_id` | The session that died |
| `classification` | `background_kill` (died while backgrounded — OS eviction / swipe-close) or `abnormal` (died foregrounded without the quit path) |
| `last_alive_at` | Approximate death time (last persisted heartbeat) |
| `unhandled_exception_count` | Unhandled exceptions seen during that run — context only, not proof of a crash |
| `app_version` / `sdk_version` | Versions of the run that died |

- Quitting via Alt-F4 / the window close button is a **clean** exit (Unity runs its quit path) — no event.
- Swipe-closing on mobile reports `background_kill`, because the app switcher backgrounds the app first.
- Requires `PersistSessionOnDisk`; disabled in the Editor and on WebGL (no reliable lifecycle there).
- Consent-gated like all analytics; a dirty exit found while consent is off is discarded.

See also: [SDK Events](events.md) for the session lifecycle events (`OnSessionStarted`, `OnSessionEnded`, `OnSessionPaused`, `OnSessionResumed`).
