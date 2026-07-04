# SDK Events

[← Back to README](../README.md)

All SDK lifecycle events live on the static `FlockEvents` class (`using Flock;`). Key behaviors:

- **Subscribe anytime** — even before `FlockClient.Create`, and `OnInitialized` / `OnInitializationFailed` are *replayed* to late subscribers, so they fire even if the SDK already auto-initialized before your script ran. Unlike `FlockClient.Instance`, the hub never throws.
- **Raised on the Unity main thread** — you can touch Unity objects directly inside handlers.
- **Logged when `EnableDebugLogs` is on** — every raise prints `[Flock SDK] OnSessionStarted fired -> 1 subscriber(s)`, so you can verify wiring straight from the console.
- **Cleared automatically** on `FlockClient.Shutdown()` and on play-session start (with domain reload disabled), so a leaked handler never outlives one play session. Still, subscribe in `OnEnable` and unsubscribe in `OnDisable`, and prefer method groups over lambdas (lambdas can't be unsubscribed).
- **Isolated from your bugs** — a subscriber that throws is logged via `Debug.LogError` and never breaks the SDK or other subscribers.

```csharp
private void OnEnable()
{
    FlockEvents.OnAuthenticated += HandleAuthenticated;
    FlockEvents.OnSessionEnded += HandleSessionEnded;
}

private void OnDisable()
{
    FlockEvents.OnAuthenticated -= HandleAuthenticated;
    FlockEvents.OnSessionEnded -= HandleSessionEnded;
}

private void HandleAuthenticated(FlockAuthInfo info)
{
    Debug.Log($"Signed in: {info.PlayerId} via {info.Method}");
}

private void HandleSessionEnded(FlockSessionEndedArgs args)
{
    Debug.Log($"Session over ({args.Reason}): {args.Snapshot.DurationSeconds:F0}s");
}
```

**Lifecycle**

| Event | Signature | Hooks up to |
|-------|-----------|-------------|
| `OnInitialized` | `Action` | `FlockClient.Create` success — raised right after the singleton is set, so `FlockClient.Instance` is usable inside handlers. Replayed immediately if you subscribe after init (e.g. under auto-init). |
| `OnInitializationFailed` | `Action<Exception>` | A failed `FlockClient.Create` attempt (still thrown to direct callers; the auto-init path logs instead). Replayed to late subscribers from `FlockClient.InitializationError`. The "already initialized" misuse guard does not raise it. |
| `OnShutdown` | `Action` | `FlockClient.Shutdown()` — raised after tokens are cleared and the singleton is gone. Always the last event; every `FlockEvents` subscription is wiped right after. |

**Auth**

| Event | Signature | Hooks up to |
|-------|-----------|-------------|
| `OnAuthenticated` | `Action<FlockAuthInfo>` | Every successful login/register (email, device, Google, Apple, Steam) or login (Facebook, Discord), and successful `TryRestoreSessionAsync`. Payload: `PlayerId` + `FlockAuthMethod`. |
| `OnTokenRefreshed` | `Action` | A successful token refresh — manual `RefreshTokenAsync` or the SDK's automatic refresh. |
| `OnAuthExpired` | `Action` | A failed/rejected token refresh: tokens are cleared and the player must log in again. Same moment as `FlockClient.OnSessionExpired` (kept for back-compat). |
| `OnLoggedOut` | `Action` | `Logout()` completing while a player was signed in. Local-only by design — tokens dropped on this device, nothing revoked server-side. |
| `OnSessionRestored` | `Action<bool>` | A persisted-session restore finished — `true` = signed in (go to game), `false` = none (show login). Also exposed as the `FlockClient.IsRestoringSession` flag for a startup spinner; fires whether or not you use `FlockBootstrap`. |

**Session** (gameplay/analytics session — distinct from auth)

| Event | Signature | Hooks up to |
|-------|-----------|-------------|
| `OnSessionStarted` | `Action<string>` | `FlockSession.Start` (runs after login when analytics initializes). Payload: the local session id. On a restart, fires after the old session's `OnSessionEnded`. |
| `OnSessionEnded` | `Action<FlockSessionEndedArgs>` | Every session end path. `Reason`: `Logout`, `Timeout` (backgrounded past the session timeout), `Quit` (app quit), `Restarted` (a new session replaced an active one), `Manual` (explicit `EndSessionAsync`). `Snapshot`: final metrics (duration, screens, pauses, FPS). Sessions recovered from a previous crashed launch do not raise this. |
| `OnSessionPaused` | `Action` | The active session pausing (app backgrounded). |
| `OnSessionResumed` | `Action` | The paused session resuming (app foregrounded). Returning after the session timeout raises `OnSessionEnded(Timeout)` instead — a timed-out session never resumes. |
