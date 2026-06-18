# Flock Unity SDK

The Flock Unity SDK provides access to Flock's game backend services from Unity games.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Requirements](#requirements)
- [Setup](#setup)
  - [Editor Configuration](#editor-configuration)
  - [Drop-in Initialization](#drop-in-initialization)
  - [Code-Based Configuration](#code-based-configuration)
- [Quick Start](#quick-start)
  - [Minimal example](#minimal-example)
  - [Authentication](#authentication)
  - [Token Refresh](#token-refresh)
  - [Services](#services)
  - [Analytics](#analytics)
  - [Events](#events)
- [Offline caching](#offline-caching)
- [Codegen](#codegen)
- [Backend backlog](#backend-backlog)
- [Platform notes](#platform-notes)

## Features

- Player authentication (email, device, Google, Apple, Steam)
- Token refresh with automatic silent retry, plus a session-expired event
- Game configuration (fetched from the backend, filterable by tag)
- Config schema validation (backend validation of config types)
- Per-player feature config lookup
- Game and game version metadata (lookup by ID or name)
- Player data and player templates (read with pagination, template lookup by ID or name)
- Shop (browse shops, items, purchase, inventory)
- Game commands (server-side operations: update player data, add funds)
- Player ban lookup
- Asset listing and lookup by ID
- Analytics (session tracking, events, transactions — no-op safe when disabled)
- Automatic retry with exponential backoff
- Offline-safe init (no network at startup) — plus disk-cached static content that keeps serving without network after one online session
- JWT token management
- Strongly typed codegen for player templates, game configs, and game commands (`Flock > Sync Schemas`)
- Hands-off startup — the SDK auto-initializes from your config at launch (or use the drop-in `FlockBootstrap` component, or call `Create` yourself)

## Installation

The SDK is distributed through the Flock website — download the latest release, then add it to your Unity project one of two ways:

**Download: [FILL_IN_FLOCK LINK]**

- **Import the `.unitypackage`** — double-click the downloaded file (or use **Assets > Import Package > Custom Package**) and import all items.

## Requirements

- **Unity 2020.3 or newer.**
- **Newtonsoft Json for Unity** (`com.unity.nuget.newtonsoft-json`, 3.0.2+) — the SDK depends on it for serialization. Installing through the Package Manager git URL resolves this automatically; if you import the `.unitypackage` instead, add it first via **Window > Package Manager > + → Add package by name** using `com.unity.nuget.newtonsoft-json`.

## Setup

### Editor Configuration

Open **Qwacks > Editor** in the Unity menu bar. The window is a view of the `FlockConfig` ScriptableObject — edits save straight into the asset (no separate Save step). Required values:

- **API URL** — Flock API endpoint (default: `https://api-flock.qwacks.com`)
- **API Key** — Your Flock API key
- **Game ID** — Your game ID from the Flock dashboard
- **Game Version** — Your game version name (the matching ID is resolved from the backend at edit time and baked into the asset, so init makes no network call)

The asset is saved to `Assets/Resources/FlockConfig.asset` so it loads in builds via `Resources.Load<FlockConfigAsset>("FlockConfig")`. The same window has a Codegen tab that drives `Flock > Sync Schemas` (see [Codegen](#codegen)).

### Automatic Initialization (default)

The SDK starts itself — no component, no init code. With `FlockConfig` set up (above), **Auto-Initialize On Load** is on by default, so the SDK initializes from `Assets/Resources/FlockConfig.asset` before the first scene loads and restores a saved session in the background. Just use `FlockClient.Instance` when you need it.

```csharp
using Flock;

// Nothing to call at startup. Optionally react to the events (safe to subscribe anytime):
FlockEvents.OnInitialized     += ()       => Debug.Log("Flock SDK ready");
FlockEvents.OnSessionRestored += signedIn => Debug.Log(signedIn ? "Session resumed" : "Show login");
```

To drive init yourself — e.g. to defer past a splash screen or EULA — turn **Auto-Initialize On Load** off (Qwacks > Editor → Advanced Settings → Tools), then use one of the options below.

> **Init is fail-fast.** A bad config makes `Create` throw (the auto-init path catches and logs it instead of crashing startup). Either way the SDK stays uninitialized and `FlockClient.Instance` throws until a successful init — so guard with `FlockClient.IsInitialized`, inspect `FlockClient.InitializationError`, or handle `FlockEvents.OnInitializationFailed`.

### Drop-in Initialization

Prefer a scene component (or turned auto-init off)? Click **Add Flock Bootstrap to Scene** in the editor window (or add a `FlockBootstrap` component to a GameObject yourself). The component holds a reference to your `FlockConfig` asset and calls `FlockClient.Create(asset.ToInitConfig())` in `Awake`.

```csharp
// Optional: gate init or react to result
var bootstrap = FindObjectOfType<FlockBootstrap>();
bootstrap.OnInitialized += () => Debug.Log("Flock SDK ready");
bootstrap.OnInitializationFailed += ex => Debug.LogError(ex);

// Or trigger init manually if you turned off "Initialize On Awake"
bootstrap.Initialize();
```

The component never copies values from the asset — it always reads through the reference, so editing the asset (here or in the inspector) is enough to change runtime behavior. Recommended pattern: place the bootstrap in a Boot scene loaded once at startup, leave **Don't Destroy On Load** on.

### Code-Based Configuration

For full manual control, turn **Auto-Initialize On Load** off and create the client yourself:

> **Important:** the SDK must be initialized **once** at startup before any
> feature accesses it. After `Create` returns, call everything via
> `FlockClient.Instance` from anywhere in your project. Accessing
> `FlockClient.Instance` before init throws a `FlockException`.

```csharp
var configAsset = Resources.Load<FlockConfigAsset>("FlockConfig");

// Create is synchronous and makes no network call. The Game Version ID was
// resolved at edit time (Qwacks > Editor) and baked into FlockConfig, applied
// to every request; init stores the singleton in FlockClient.Instance.
FlockClient.Create(configAsset.ToInitConfig());

// Use anywhere — no need to pass the client around.
var response = await FlockClient.Instance.Authentication.LoginWithEmailAsync(email, password);
```

## Quick Start

### Minimal example

The shortest path from zero to reading data — initialize once at startup, log a player in, then read something back. Everything else in this section is the same pattern with more surface area.

```csharp
using Flock;

// 1. With Auto-Initialize On Load on (the default), the SDK is already running by the time
//    your scene starts — just use FlockClient.Instance. (Opted out? Call
//    FlockClient.Create(Resources.Load<FlockConfigAsset>("FlockConfig").ToInitConfig()) once first.)

// 2. Log the player in. Auth methods throw on failure.
await FlockClient.Instance.Authentication.LoginWithDeviceAsync("device-uuid");

// 3. Read something back — call any service via FlockClient.Instance.
var game = await FlockClient.Instance.Game.GetGameAsync();
Debug.Log($"Signed in as {FlockClient.Instance.CurrentPlayerId}, playing {game.Name}");
```

### Authentication

All auth methods live on `FlockClient.Instance.Authentication` and return a `PlayerLoginResponse`
with access and refresh tokens. The SDK stores the tokens internally.

```csharp
// Email
var response = await FlockClient.Instance.Authentication.LoginWithEmailAsync("player@example.com", "password");
var response = await FlockClient.Instance.Authentication.RegisterWithEmailAsync("player@example.com", "password", "PlayerName");

// Device
var response = await FlockClient.Instance.Authentication.LoginWithDeviceAsync("device-uuid");
var response = await FlockClient.Instance.Authentication.RegisterWithDeviceAsync("device-uuid", "PlayerName");

// Google
var response = await FlockClient.Instance.Authentication.LoginWithGoogleAsync(idToken);
var response = await FlockClient.Instance.Authentication.RegisterWithGoogleAsync(idToken, "PlayerName");

// Apple
var response = await FlockClient.Instance.Authentication.LoginWithAppleAsync(identityToken);
var response = await FlockClient.Instance.Authentication.RegisterWithAppleAsync(identityToken, "PlayerName");

// Steam
var response = await FlockClient.Instance.Authentication.LoginWithSteamAsync(sessionTicket);
var response = await FlockClient.Instance.Authentication.RegisterWithSteamAsync(sessionTicket, "PlayerName");

// Logout — clears local token state
FlockClient.Instance.Authentication.Logout();
```

> **Note on `name` during registration.** The backend enforces a **unique** display name across registered players. The SDK has a temporary string-match (`IsAlreadyRegisteredError`) that swallows "already registered" errors and returns `null` from `RegisterWith*` instead of throwing — whether it catches name collisions specifically depends on the exact backend error wording, which isn't structured today (see [Backend backlog](#backend-backlog)).
>
> Until the backend ships a structured "name taken" error code (or a name-availability check), the recommended path is to **pass `null` (or omit `name`)** and let the backend assign a default. If you need a display name, collect it on a separate post-registration screen where retrying on collision is natural UX.

### Token Refresh

The SDK silently refreshes the access token on `401` responses. You can also
trigger it manually, and listen for the case where the refresh fails (the
player must re-authenticate).

```csharp
FlockClient.Instance.OnSessionExpired += () => ShowLoginScreen();

bool refreshed = await FlockClient.Instance.RefreshTokenAsync();
```

### Services

```csharp
// Game configuration — raw (returns GamePatchSchema with a flattened DataField list)
var configs = await FlockClient.Instance.Config.GetAllAsync();
var config = await FlockClient.Instance.Config.GetByIdAsync("config-id");
var bySchema = await FlockClient.Instance.Config.GetBySchemaAsync("schema-id");

// Game configuration — typed (deserializes Data into your class)
var configs = await FlockClient.Instance.Config.GetAllAsync<GameplayConfig>();
var config = await FlockClient.Instance.Config.GetByIdAsync<GameplayConfig>("config-id");
var bySchema = await FlockClient.Instance.Config.GetBySchemaAsync<GameplayConfig>("schema-id");

// Game configs by tag (currency, gameplay)
var currencyConfigs = await FlockClient.Instance.Config.GetGameConfigsAsync(SchemaTag.currency);
var gameplayConfigs = await FlockClient.Instance.Config.GetGameConfigsByVersionAsync(SchemaTag.gameplay);
var typed = await FlockClient.Instance.Config.GetGameConfigsAsync<CurrencyConfig>(SchemaTag.currency);

// Per-player feature config (game version the player last logged into)
var features = await FlockClient.Instance.Config.GetPlayerFeaturesAsync(FlockClient.Instance.CurrentPlayerId);
var typedFeatures = await FlockClient.Instance.Config.GetPlayerFeaturesAsync<FeatureFlags>(FlockClient.Instance.CurrentPlayerId);

// Config schema validation (backend concern, most games skip this)
var schemas = await FlockClient.Instance.Schema.GetAllSchemasAsync(SchemaTag.gameplay);
var versionSchemas = await FlockClient.Instance.Schema.GetSchemasByVersionAsync(SchemaTag.currency);
var schema = await FlockClient.Instance.Schema.GetSchemaByIdAsync("schema-id");
var configs = await FlockClient.Instance.Schema.GetSchemaConfigsAsync("schema-id");

// Game info
var game = await FlockClient.Instance.Game.GetGameAsync();
var version = await FlockClient.Instance.Game.GetGameVersionAsync();
var versionByName = await FlockClient.Instance.Game.GetGameVersionByNameAsync("v1.0.0");

// Player data (read-only, create/update via game commands)
var data = await FlockClient.Instance.Player.GetDataByIdAsync("player-data-id");
var all = await FlockClient.Instance.Player.GetAllDataAsync(page: 1, limit: 10);
var byPlayer = await FlockClient.Instance.Player.GetAllDataAsync(playerId: "player-id");

// Player templates
var templates = await FlockClient.Instance.Player.GetTemplatesAsync();
var template = await FlockClient.Instance.Player.GetTemplateByIdAsync("template-id");
var template = await FlockClient.Instance.Player.GetTemplateByNameAsync("currency");
var playerData = await FlockClient.Instance.Player.GetTemplatePlayerDataAsync("template-id");

// Reading player data — preferred path: the codegen'd accessors. `Flock > Sync Schemas`
// emits one Get<TemplateName>Async() extension per template returning a generated typed
// class; numeric conversion is handled by the deserializer.
var currency = await FlockClient.Instance.Player.GetCurrencyAsync(); // generated

// Raw fields (no codegen, ad-hoc fields, or schema drift): JSON integers arrive boxed
// as long and decimals as double, so read through GetValue<T>() instead of casting
// Value directly — (int)field.Value throws on a boxed long.
int score = playerData.Data.Find(f => f.FieldName == "score").GetValue<int>();

// Game commands — server-side operations. Each runs its own typed command
// and returns the updated PlayerData.
PlayerData updated = await FlockClient.Instance.Commands.UpdatePlayerDataAsync(
    "player-data-id",
    new List<DataField> { /* flattened typed fields */ });

PlayerData updated = await FlockClient.Instance.Commands.UpdatePlayerDataFieldAsync(
    "player-data-id", "score", 9999);

PlayerData updated = await FlockClient.Instance.Commands.AddGameFundsAsync(
    "player-data-id", "gold", 500);

PlayerData updated = await FlockClient.Instance.Commands.UnlockAchievementAsync(
    "player-data-id", "first_win");

// Shop
var shops = await FlockClient.Instance.Shop.GetAllAsync(page: 1, limit: 10);
var shop = await FlockClient.Instance.Shop.GetByIdAsync("shop-id");
var item = await FlockClient.Instance.Shop.GetItemAsync("shop-item-id");
var items = await FlockClient.Instance.Shop.GetItemsByShopAsync("shop-id");
var inventory = await FlockClient.Instance.Shop.PurchaseAsync("shop-item-id", FlockClient.Instance.CurrentPlayerId);
var playerItems = await FlockClient.Instance.Shop.GetPlayerInventoryAsync(FlockClient.Instance.CurrentPlayerId);

// Player ban — returns active ban data keyed by feature, or null if not banned
var ban = await FlockClient.Instance.Player.GetBanAsync(FlockClient.Instance.CurrentPlayerId);

// Assets are stand-alone files you upload via the Flock dashboard (images, audio,
// JSON, raw bytes) and download at runtime — think "files on a CDN with metadata".
// Good for content you want to swap without rebuilding the game (icons, sound effects,
// art swaps), and for content shared with the Unreal SDK. NOT a replacement for Unity
// Addressables: prefabs, scenes, ScriptableObjects, materials and shaders still need
// Unity's own pipeline.

// Assets — list / lookup
var assets = await FlockClient.Instance.Asset.GetAllAsync();
var asset = await FlockClient.Instance.Asset.GetByIdAsync("asset-id");
var asset = await FlockClient.Instance.Asset.GetByNameAsync("iconTest"); // O(N) client-side filter
// asset.S3DownloadUrl is the direct download URL

// Assets — generic typed download. Supported T: Texture2D, Sprite, AudioClip, string, byte[]
// By id (extra lookup round-trip):
Sprite sprite = await FlockClient.Instance.Asset.DownloadAsync<Sprite>("asset-id");
// By already-fetched schema (skips the lookup):
Sprite sprite = await FlockClient.Instance.Asset.DownloadAsync<Sprite>(asset);

// Batch downloads — caller picks which assets, all downloaded in parallel as the chosen type
List<Sprite> sprites = await FlockClient.Instance.Asset.DownloadAsync<Sprite>(new[] { "id1", "id2", "id3" });
List<Sprite> sprites = await FlockClient.Instance.Asset.DownloadAsync<Sprite>(assets);

// Asset cache — downloads are cached on disk, keyed by asset ID + UpdatedAt.
// Default location: Application.persistentDataPath/flock_assets
// Override via FlockInitConfig.AssetCacheDirectory; disable via EnableAssetCache=false.
// Cap total size with FlockInitConfig.AssetCacheMaxSizeMB (default 100 MB; 0 = unlimited).
// When the cap is exceeded the oldest entries are evicted (LRU by last access).
// Writes are atomic (.tmp + move) and previous versions of the same asset
// are deleted automatically when a newer UpdatedAt is cached.
string cacheDir = FlockClient.Instance.Asset.CacheDirectory; // resolved absolute path
FlockClient.Instance.Asset.ClearCache();

// Warm the disk cache at boot without decoding into a Unity type.
// Cache-hit short-circuits, so re-calling for an unchanged asset is cheap.
await FlockClient.Instance.Asset.PreloadAsync(asset);

// Ask "is this asset already on disk for this UpdatedAt?" without downloading.
bool ready = FlockClient.Instance.Asset.IsCached(asset);
```

### Analytics

> **Auth dependency.** All analytics calls are **bearer-authenticated** (they require a logged-in player). Behavior depends on the call:
>
> - `TrackEventAsync`, `TrackEventsAsync`, `LogExceptionAsync`, `LogErrorAsync`, `LogEventAsync` — **safe to call before login**. They enqueue to the on-disk cache and drain automatically after authentication (entries tagged with the unauthenticated placeholder are retagged with the real `PlayerId` at login). They also drain on interval (`EventBufferFlushIntervalSeconds`, default 10s), on session pause, and on session end.
> - `RecordTransactionAsync`, `StartSessionAsync` — **best-effort.** They attempt an immediate send and will 401 if called before login; session start swallows the error and continues locally, transaction does not.
>
> A console warning ("Player must be authenticated for analytics") is logged whenever a pre-auth call is made, but the SDK never throws for analytics — observability should not break the game.

```csharp
// Sessions auto-start at login when AutoStartSession is true (default). Otherwise:
await FlockClient.Instance.Analytics.StartSessionAsync();
await FlockClient.Instance.Analytics.EndSessionAsync();

// Events — queued to disk, drained on interval/pause/end/login
await FlockClient.Instance.Analytics.TrackEventAsync(
    "level_complete",
    eventCategory: "gameplay",
    parameters: new Dictionary<string, object> { { "level", 5 }, { "stars", 3 } });

// Batch
await FlockClient.Instance.Analytics.TrackEventsAsync(eventList);

// Logs — same queue-and-drain semantics
await FlockClient.Instance.Analytics.LogExceptionAsync(exception);
await FlockClient.Instance.Analytics.LogErrorAsync("inventory desync", errorCode: "INV_001");

// Transactions — immediate send, requires auth
await FlockClient.Instance.Analytics.RecordTransactionAsync(new AnalyticsTransactionRequest {
    Amount = 4.99f, CurrencyCode = "USD", TransactionType = "Purchase", Status = "Purchased"
});

// Screen views — local-only, contributes to session ScreensViewed counter
FlockClient.Instance.Analytics.RecordScreenView("MainMenu");
```

#### Events

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
| `OnAuthenticated` | `Action<FlockAuthInfo>` | Every successful login/register (email, device, Google, Apple, Steam) and successful `TryRestoreSessionAsync`. Payload: `PlayerId` + `FlockAuthMethod`. |
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

## Offline caching

The SDK keeps a disk snapshot of everything it reads from the server
(`persistentDataPath/Flock/snapshots/`). When the device is offline or the
server can't be reached, the last-known data is served instead of throwing —
this works after at least one online session. Online behavior is unchanged:
the server is always fetched first, and there are no TTLs to tune.

When data refreshes:

| Data | Refreshes |
|---|---|
| Configs, schemas, shop catalog, game info, asset metadata, player templates, player features | Once per game launch (first access). Newly published content shows up on the next launch. |
| Player data | At launch, and automatically after every game command — the command's response updates the cache for you. |
| Ban status, inventory, purchases | Never cached — always live. |

Settings (`FlockInitConfig` / the FlockConfig asset, under "Offline Cache"):

- `EnableOfflineCache` (default `true`) — master switch. Set `false` on WebGL (see Platform notes).
- `OfflineCacheDirectory` — optional custom location for the snapshots.

Each service has a `ClearCache()` that drops both its in-memory cache and its
disk snapshots. Purchases always require a connection — they are never cached
or queued.

## Codegen

Run **Flock > Sync Schemas** (or the Codegen tab in **Qwacks > Editor**) to fetch your game's player templates and game configs from the backend and generate typed C# accessors. Output goes to `Assets/Flock/Generated/` by default; change the path on the FlockConfig asset if you want it elsewhere. Treat the folder as Flock-owned — sync wipes the `Templates/`, `Commands/`, and `Configs/` subdirectories on each run, and **Delete Generated Code** clears the whole tree.

What gets generated, given a player template named `PlayerProgress` and a game config named `Gameplay`:

```csharp
// Templates — Flock.Generated.Templates.PlayerProgressTemplate
// No ID needed: resolves the current player's PlayerData via Client.CurrentPlayerId.
// First call paginates and caches all of the player's PlayerData; subsequent
// Get*Async calls (any template) hit the in-memory cache. Use Player.ClearCache()
// after a known external mutation, or when switching player.
PlayerProgressTemplate progress = await FlockClient.Instance.Player.GetPlayerProgressAsync();
int level = progress.Level;
int xp = progress.Xp;

// Each generated class carries its source identity and its typed schema:
string id = PlayerProgressTemplate.SourceId;
string name = PlayerProgressTemplate.SourceName;
IReadOnlyList<TypedSchema> schema = PlayerProgressTemplate.Schema;

// Commands — extension method on the template itself. Mutate the populated POCO
// and call .UpdateAsync() on it directly. Same one-liner per template.
progress.Level = 5;
progress.Xp = 1200;
PlayerData updated = await progress.UpdateAsync();

// Configs — Flock.Generated.Configs.GameplayConfig
GameplayConfig gameplay = await FlockClient.Instance.Config.GetGameplayAsync();
float baseMoveSpeed = gameplay.BaseMoveSpeed;
// SourceId / SourceName / Schema / SourceTag are exposed on the generated class:
SchemaTag tag = GameplayConfig.SourceTag;
```

`UpdateAsync` is emitted as an extension method on each generated template type, so it lights up in IntelliSense the moment you have `using Flock.Generated.Templates;` (which you need anyway for the typed class). It validates `template.PlayerDataId` (set automatically by the matching `Get*Async`), turns the populated POCO back into a flattened DataField list via `{Template}.Schema.ToDataFieldList(template)`, and routes through `FlockCommandProvider.UpdatePlayerDataAsync`. After a write you typically want fresh reads — call `client.Player.ClearCache()` before the next `Get*Async` if you need to bypass the per-player snapshot cache.

Generated config classes have read-only properties (`{ get; private set; }`) — configs are game-wide and shouldn't be mutated client-side; mutations are admin-only on the backend.

Method names come from the template name on the backend (PascalCase). Field names follow each `TypedSchema.field_name`, also PascalCased.

Re-run sync whenever:
- You change the schema of a template in the dashboard
- You change **Game Version** on the FlockConfig asset (the editor warns, and the build guard fails, if the generated `SchemasManifest.GameVersionId` doesn't match the baked version ID)
- You add or remove templates

Type mapping for primitives lives in `Editor/Codegen/TypeMap.cs` (`integer` → `int`, `string` → `string`, `datetime`/`date`/`timestamp` → `System.DateTime`, etc.). Composite types are walked structurally by `SchemaPropertyEmitter`: `object` fields emit a nested partial class, `list`/`array` emit `List<T>`, `dict` emits `Dictionary<string, T>`, all resolved recursively through the same walker.

## Backend backlog

A few SDK behaviors are constrained by the current backend surface and will improve as the backend grows. None of these block normal usage; they all surface as warnings in the console with workarounds in place.

- **Asset by name** — `client.Asset.GetByNameAsync` lists all assets and filters client-side (O(N)). Will switch to a dedicated server-side lookup once the backend adds it.
- **Structured registration error codes** — registration failures are returned as plain text without an error-code field. The SDK uses string-matching (`IsAlreadyRegisteredError`) to swallow "already registered" cases and return `null` from `RegisterWith*`, which is brittle and conflates name collisions with credential collisions. Once the backend returns structured codes (e.g. `NAME_TAKEN`, `EMAIL_REGISTERED`, `DEVICE_REGISTERED`), the SDK can surface them as typed exceptions and the `RegisterWith*` methods can take `name` again with reliable error UX. A dedicated name-availability check would also let callers validate as the user types.
- **Retry-safe session registration** — session registration creates a brand-new session row on every call, and only the server knows the issued id. If the app quits (or the session rotates) while a registration request is still on the wire, the client never learns that id; on the next launch it has to register the session again just to close it, leaving the server with two rows for one play session — and the first row stays open forever. The SDK logs a warning when it detects this. Fix: accept the client's own session id (`client_session_id`) and, when the same id is sent twice, return the existing row instead of creating another. A retried registration then maps back to the original session.

## Platform notes

- **WebGL**: SDK HTTP automatically switches to `UnityWebRequest` on WebGL builds
  (`System.Net.Http.HttpClient` has no WebGL transport), so auth, read APIs, and
  analytics work online with no caller changes. The asset cache and the offline
  snapshot cache, however, are backed by `Application.persistentDataPath`, which on
  WebGL is IndexedDB-backed and does not support synchronous file writes
  (`File.WriteAllBytes` will fail). Set `FlockInitConfig.EnableAssetCache = false`
  and `FlockInitConfig.EnableOfflineCache = false` on WebGL builds — everything else
  works online, just without the disk caches. See the "Offline caching" section.
