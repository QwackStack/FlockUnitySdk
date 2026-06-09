# Flock Unity SDK

The Flock Unity SDK provides access to Flock's game backend services from Unity games.

## Features

- Player authentication (email, device, Google, Apple, Steam)
- Token refresh with automatic silent retry, plus a session-expired event
- Game configuration (fetched from game patch endpoints, filterable by tag)
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
- JWT token management
- Strongly typed codegen for player templates, game configs, and game commands (`Flock > Sync Schemas`)
- Drop-in `FlockBootstrap` scene component for hands-off SDK initialization

## Installation

Add via Unity Package Manager using the git URL or import the `.unitypackage`.

## Setup

### Editor Configuration

Open **Qwacks > Editor** in the Unity menu bar. The window is a view of the `FlockConfig` ScriptableObject — edits save straight into the asset (no separate Save step). Required values:

- **API URL** — Flock API endpoint (default: `https://api-flock.qwacks.com`)
- **API Key** — Your Flock API key
- **Game ID** — Your game ID from the Flock dashboard
- **Game Version** — Your game version name (the matching ID is fetched from the backend on init)

The asset is saved to `Assets/Resources/FlockConfig.asset` so it loads in builds via `Resources.Load<FlockConfigAsset>("FlockConfig")`. The same window has a Codegen tab that drives `Flock > Sync Schemas` (see [Codegen](#codegen)).

### Drop-in Initialization

If you don't want to write SDK init code, click **Add Flock Bootstrap to Scene** in the editor window (or add a `FlockBootstrap` component to a GameObject yourself). The component holds a reference to your `FlockConfig` asset and calls `FlockClient.CreateAsync(asset.ToInitConfig())` in `Awake`.

```csharp
// Optional: gate init or react to result
var bootstrap = FindObjectOfType<FlockBootstrap>();
bootstrap.OnInitialized += () => Debug.Log("Flock SDK ready");
bootstrap.OnInitializationFailed += ex => Debug.LogError(ex);

// Or trigger init manually if you turned off "Initialize On Awake"
await bootstrap.InitializeAsync();
```

The component never copies values from the asset — it always reads through the reference, so editing the asset (here or in the inspector) is enough to change runtime behavior. Recommended pattern: place the bootstrap in a Boot scene loaded once at startup, leave **Don't Destroy On Load** on.

### Code-Based Configuration

> **Important:** the SDK must be initialized **once** at startup before any
> feature accesses it. After `CreateAsync` completes, call everything via
> `FlockClient.Instance` from anywhere in your project. Accessing
> `FlockClient.Instance` before init throws a `FlockException`.

```csharp
var config = new FlockInitConfig(
    apiUrl: "https://api-flock.qwacks.com",
    apiKey: "your-api-key",
    gameId: "your-game-id",
    gameVersion: "your-game-version-name",
    enableDebugLogs: true
);

// CreateAsync resolves the game version name to its ID via the backend,
// uses that ID for the X-Game-Version-ID header on every request, and
// stores the singleton in FlockClient.Instance.
await FlockClient.CreateAsync(config);

// Use anywhere — no need to pass the client around.
var response = await FlockClient.Instance.Authentication.LoginWithEmailAsync(email, password);
```

Or load from a ScriptableObject:

```csharp
var configAsset = Resources.Load<FlockConfigAsset>("FlockConfig");
await FlockClient.CreateAsync(configAsset.ToInitConfig());
```

## Quick Start

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
> Until the backend ships a structured "name taken" error code (or a `name-available` endpoint), the recommended path is to **pass `null` (or omit `name`)** and let the backend assign a default. If you need a display name, collect it on a separate post-registration screen where retrying on collision is natural UX.

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

// Game configs by tag (currency, gameplay — maps to /v1/game_config)
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

// Game commands — server-side operations. Each posts to its own typed endpoint
// under /v1/game_command/* and returns the updated PlayerData.
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
// By id (extra GET /v1/asset/{id} round-trip):
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

> **Auth dependency.** All analytics endpoints (`/v1/analytics/*` and `/v1/log_event*`) are **bearer-authenticated** — see the [endpoint table](#api-endpoints). Behavior depends on the call:
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
- You change **Game Version** on the FlockConfig asset (the SDK warns at init if the generated `SchemasManifest.GameVersionId` doesn't match the configured version)
- You add or remove templates

Type mapping for primitives lives in `Editor/Codegen/TypeMap.cs` (`integer` → `int`, `string` → `string`, `datetime`/`date`/`timestamp` → `System.DateTime`, etc.). Composite types are walked structurally by `SchemaPropertyEmitter`: `object` fields emit a nested partial class, `list`/`array` emit `List<T>`, `dict` emits `Dictionary<string, T>`, all resolved recursively through the same walker.

## Backend backlog

A few SDK behaviors are constrained by the current backend surface and will improve as the backend grows. None of these block normal usage; they all surface as warnings in the console with workarounds in place.

- **Asset by name** — `client.Asset.GetByNameAsync` lists all assets and filters client-side (O(N)). Will switch to `GET /v1/asset/by-name/{name}` once the backend adds it.
- **Structured registration error codes** — `POST /v1/player/register*` failures are returned as plain text without an error-code field. The SDK uses string-matching (`IsAlreadyRegisteredError`) to swallow "already registered" cases and return `null` from `RegisterWith*`, which is brittle and conflates name collisions with credential collisions. Once the backend returns structured codes (e.g. `NAME_TAKEN`, `EMAIL_REGISTERED`, `DEVICE_REGISTERED`), the SDK can surface them as typed exceptions and the `RegisterWith*` methods can take `name` again with reliable error UX. A `GET /v1/player/name-available?name=` endpoint would also let callers validate as the user types.

## Headers

Every API request includes these headers:

| Header | Source | Description |
|--------|--------|-------------|
| `X-Flock-API-Key` | `FlockInitConfig.ApiKey` | Required. Identifies the game. |
| `X-Game-Version-ID` | `FlockInitConfig.GameVersionId` | Resolved from `GameVersion` (name) during `FlockClient.CreateAsync`. |
| `Authorization` | Bearer token from login | Added after authentication. |

## API Endpoints

| Service | Endpoint | Auth |
|---------|----------|------|
| Email Login | `POST /v1/player/login` | API Key |
| Device Login | `POST /v1/player/login/device` | API Key |
| Google Login | `POST /v1/player/login/google` | API Key |
| Apple Login | `POST /v1/player/login/apple` | API Key |
| Steam Login | `POST /v1/player/login/steam` | API Key |
| Email Register | `POST /v1/player/register` | API Key |
| Device Register | `POST /v1/player/register/device` | API Key |
| Google Register | `POST /v1/player/register/google` | API Key |
| Apple Register | `POST /v1/player/register/apple` | API Key |
| Steam Register | `POST /v1/player/register/steam` | API Key |
| Refresh Token | `POST /v1/player/token/refresh` | API Key |
| Game Configs | `GET /v1/game_patch` | API Key |
| Config by ID | `GET /v1/game_patch/{id}` | API Key |
| Configs by Schema | `GET /v1/game_patch/config/{id}` | API Key |
| Config Schemas | `GET /v1/game_config` | API Key |
| Schemas by Version | `GET /v1/game_config/version` | API Key |
| Schema by ID | `GET /v1/game_config/{id}` | API Key |
| Schema Configs | `GET /v1/game_config/{id}/patches` | API Key |
| Player Feature Config | `GET /v1/game_config/player/{player_id}/features` | API Key |
| Game Info | `GET /v1/game` | API Key |
| Game Version | `GET /v1/game_version` | API Key |
| Game Version by Name | `GET /v1/game_version/by-name/{name}` | API Key |
| Player Data | `GET /v1/player_data` | API Key |
| Player Data by ID | `GET /v1/player_data/{id}` | API Key |
| Player Templates | `GET /v1/player_template` | API Key |
| Player Template by ID | `GET /v1/player_template/{id}` | API Key |
| Player Template by Name | `GET /v1/player_template/by-name/{name}` | API Key |
| Template Player Data | `GET /v1/player_template/{id}/player-data` | API Key |
| Game Configs by Tag | `GET /v1/game_config?tag=` | API Key |
| Game Configs by Version/Tag | `GET /v1/game_config/version?tag=` | API Key |
| Update Player Data | `POST /v1/game_command/update_player_data` | API Key |
| Update Player Data Field | `POST /v1/game_command/update_player_data_key` | API Key |
| Add Game Funds | `POST /v1/game_command/add_game_funds` | API Key |
| Unlock Achievement | `POST /v1/game_command/unlock_achievement` | API Key |
| List Shops | `GET /v1/shop` | API Key |
| Get Shop | `GET /v1/shop/{shop_id}` | API Key |
| Shop Transaction | `POST /v1/shop/transaction` | API Key |
| Get Shop Item | `GET /v1/shop_item/{shop_item_id}` | API Key |
| Shop Items by Shop | `GET /v1/shop_item/shop/{shop_id}` | API Key |
| Player Inventory | `GET /v1/player_inventory/player/{player_id}` | API Key |
| Player Ban | `GET /v1/player-ban` | API Key |
| List Assets | `GET /v1/asset` | API Key |
| Get Asset | `GET /v1/asset/{asset_id}` | API Key |
| Start Session | `POST /v1/analytics/sessions` | Bearer |
| End Session | `PATCH /v1/analytics/sessions/{session_id}` | Bearer |
| Track Event | `POST /v1/analytics/events/single` | Bearer |
| Track Events Batch | `POST /v1/analytics/events` | Bearer |
| Record Transaction | `POST /v1/analytics/transactions` | Bearer |

## Platform notes

- **WebGL**: the asset cache is backed by `Application.persistentDataPath`, which
  on WebGL is IndexedDB-backed and does not support synchronous file writes
  (`File.WriteAllBytes` will fail). Set `FlockInitConfig.EnableAssetCache = false`
  on WebGL builds — assets will still download, just without the disk cache.
