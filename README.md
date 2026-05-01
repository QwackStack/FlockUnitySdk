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
// Game configuration — raw (returns GamePatchSchema with Data dictionary)
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

// Game commands — server-side operations (gameCommandId from backend dashboard)
var results = await FlockClient.Instance.Commands.UpdatePlayerDataAsync(
    "game-command-id", "player-data-id",
    new Dictionary<string, object> { { "level", 5 }, { "xp", 1200 } });

var results = await FlockClient.Instance.Commands.UpdatePlayerDataFieldAsync(
    "game-command-id", "player-data-id", "score", 9999);

var results = await FlockClient.Instance.Commands.AddGameFundsAsync(
    "game-command-id", "player-data-id", "gold", 500);

// Shop
var shops = await FlockClient.Instance.Shop.GetAllAsync(page: 1, limit: 10);
var shop = await FlockClient.Instance.Shop.GetByIdAsync("shop-id");
var item = await FlockClient.Instance.Shop.GetItemAsync("shop-item-id");
var items = await FlockClient.Instance.Shop.GetItemsByShopAsync("shop-id");
var inventory = await FlockClient.Instance.Shop.PurchaseAsync("shop-item-id", FlockClient.Instance.CurrentPlayerId);
var playerItems = await FlockClient.Instance.Shop.GetPlayerInventoryAsync(FlockClient.Instance.CurrentPlayerId);

// Player ban — returns active ban data keyed by feature, or null if not banned
var ban = await FlockClient.Instance.Ban.GetPlayerBanAsync(FlockClient.Instance.CurrentPlayerId);

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
```

## Codegen

Run **Flock > Sync Schemas** (or the Codegen tab in **Qwacks > Editor**) to fetch your game's player templates and game configs from the backend and generate typed C# accessors. Output goes to `Assets/Flock/Generated/` by default; change the path on the FlockConfig asset if you want it elsewhere. Treat the folder as Flock-owned — sync wipes its subdirectories on each run, and **Delete Generated Code** clears the whole tree.

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

// Configs — Flock.Generated.Configs.GameplayConfig
GameplayConfig gameplay = await FlockClient.Instance.Config.GetGameplayAsync();
float baseMoveSpeed = gameplay.BaseMoveSpeed;

// Commands — typed wrappers over UpdatePlayerData / UpdatePlayerDataKey
progress.Level = 5;
await FlockClient.Instance.Commands.UpdatePlayerProgressAsync(progress);
await FlockClient.Instance.Commands.UpdatePlayerProgressFieldAsync(progress, "xp", 1200);
```

Method names come from the template / config name on the backend (PascalCase). Field names follow the schema keys, also PascalCased. Source identifiers are exposed as `const`s on each generated class (`PlayerProgressTemplate.SourceId`, `GameplayConfig.SourceId`, `GameplayConfig.SourceTag`).

Re-run sync whenever:
- You change the schema of a template or config in the dashboard
- You change **Game Version** on the FlockConfig asset (the SDK warns at init if the generated `SchemasManifest.GameVersionId` doesn't match the configured version)
- You add or remove templates / configs

Type mapping is in `Editor/Codegen/TypeMap.cs`: primitives map directly, `datetime`/`date`/`timestamp` to `System.DateTime`, nested objects emit nested classes, `list<T>` resolves recursively. See [Backend backlog](#backend-backlog) for current limits.

## Backend backlog

A few SDK behaviors are constrained by the current backend surface and will improve as the backend grows. None of these block normal usage; they all surface as warnings in the console with workarounds in place.

- **Typed arrays in codegen** — schema `array` and `list` types map to `List<object>` because the schema response sends bare `"array"` with no element type info. The codegen `TypeMap` already handles `list<T>` recursively, so once the backend emits typed element info, generated code picks it up with no SDK change.
- **Nested object schemas in codegen** — schema `object` maps to `Newtonsoft.Json.Linq.JObject` (call `.ToObject<T>()` for typed views). Once the backend serializes nested object schemas inline rather than as opaque `object`, codegen will emit nested classes.
- **Game command IDs in codegen** — generated `Update*Async` and `Update*FieldAsync` extension methods read command IDs from `Editor/Codegen/CommandLookup.cs`, which currently has placeholder constants you must replace with your dashboard's `UpdatePlayerData` and `UpdatePlayerDataKey` IDs. Will resolve automatically once the backend exposes a "command by name" lookup endpoint.
- **Asset by name** — `client.Asset.GetByNameAsync` lists all assets and filters client-side (O(N)). Will switch to `GET /v1/asset/by-name/{name}` once the backend adds it.

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
| Execute Command | `POST /v1/game_command/execute` | API Key |
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
