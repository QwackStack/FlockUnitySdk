# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.4.0] - 2026-04-01

### Added
- Analytics system (`FlockAnalyticsProvider`) with full v1 endpoint coverage
- `client.Analytics.StartSessionAsync` — start a player session
- `client.Analytics.EndSessionAsync` — end the current session
- `client.Analytics.TrackEventAsync` — track a single event
- `client.Analytics.TrackEventsAsync` — track events in batch
- `client.Analytics.RecordTransactionAsync` — record a purchase/transaction
- `client.Analytics.RecordScreenView` — manually record a screen view
- `FlockBehaviour` — DontDestroyOnLoad singleton for Unity lifecycle events
- `FlockSession` — session state with pause tracking, FPS sampling, heartbeat, crash recovery
- `FlockAnalyticsConfig` — configurable session timeout, heartbeat interval, bounce threshold, FPS tracking
- `FlockSessionSnapshot` — serializable session state for persistence and server calls
- `FlockDeviceInfo` — captures platform, OS, device model, screen, memory, SDK version
- `FlockSdkVersion` — SDK version constant sent with session start requests
- `PatchAsync` on `FlockHttpClient` for the session end endpoint
- Session crash recovery via PlayerPrefs on next launch
- Session timeout detection on app resume (configurable, default 30s)
- Automatic analytics transaction recording on `Shop.PurchaseAsync`
- Analytics config exposed on `FlockConfigAsset` ScriptableObject

### Changed
- Renamed `Services` folder to `Providers` (`FlockGameService` → `FlockGameProvider`, `PlayerDataService` → `PlayerDataProvider`)
- `FlockInitConfig` now accepts `FlockAnalyticsConfig`
- `ClearTokens` resets the active analytics session
- `IFlockClient` now exposes `Analytics`, `HasActiveSession`, `CurrentSessionId`

## [1.3.0] - 2026-03-14

### Added
- Shop system (`FlockShopProvider`) with full v1 endpoint coverage
- `client.Shop.GetAllAsync` — list shops (paginated)
- `client.Shop.GetByIdAsync` — get shop by ID
- `client.Shop.GetItemAsync` — get shop item by ID
- `client.Shop.GetItemsByShopAsync` — get items by shop (with optional patch_id filter)
- `client.Shop.PurchaseAsync` — execute shop transaction
- `client.Shop.GetPlayerInventoryAsync` — get player inventory (paginated)
- Models: `Shop`, `ShopItem`, `ShopData`

### Changed
- Moved `PurchaseShopItemAsync` and `GetPlayerInventoryAsync` from `FlockCommandProvider` to `FlockShopProvider`
- `PlayerDataService` is now read-only (removed `CreateAsync` and `UpdateAsync` — use game commands instead)
- `PlayerDataService` uses API key headers instead of bearer auth (matching spec)

## [1.2.0] - 2026-03-07

### Added
- Game commands (`FlockCommandProvider`) for server-side operations via `POST /v1/game_command/execute`
- `client.Commands.UpdatePlayerDataAsync` — update player data through a backend command
- `client.Commands.UpdatePlayerDataFieldAsync` — update a single field in player data
- `client.Commands.AddGameFundsAsync` — add currency funds to a player
- `ICommandPayload` internal interface for type-safe command inputs
- Models: `GameCommandExecutionResult`, `PlayerInventory`

## [1.1.0] - 2026-02-25

### Changed
- Restructured config system: `client.Config` now returns game configuration data from `/v1/game_patch` endpoints
- `client.Patches` replaced by `client.Schema` for config schema validation (`/v1/game_config` endpoints)
- `IConfigProvider` now wraps game patch endpoints (returns `GamePatchSchema`)
- Removed automatic patch merging from config provider (patches ARE the config)

### Added
- `FlockSchemaProvider` and `ISchemaProvider` for config schema validation endpoints

### Removed
- `FlockGamePatchProvider` (replaced by `FlockConfigProvider`)
- Auto-merge logic (`ApplyPatchToConfigAsync`, `ApplyPatchesToConfigsAsync`)
- Achievements (`FlockAchievementProvider`, `IAchievementProvider`, achievement models)
- Leaderboards (`FlockLeaderboardProvider`, `ILeaderboardProvider`, leaderboard models)

## [1.0.0] - 2025-02-13

### Added
- **Authentication**: Email login and registration (`LoginWithEmailAsync`, `RegisterWithEmailAsync`)
- **Authentication**: Device login and registration (`LoginWithDeviceAsync`, `RegisterWithDeviceAsync`)
- **Token Management**: JWT access token parsing with expiration checks
- **Game Configuration**: Fetch all configs, by ID, by schema ID (`FlockConfigProvider`)
- **Config Schema Validation**: Fetch schemas, by version, by ID (`FlockSchemaProvider`)
- **Game Info**: Fetch game and game version metadata (`FlockGameService`)
- **Player Data**: Create, read, update, list with pagination (`PlayerDataService`)
- **HTTP Layer**: `FlockHttpClient` with GET, POST, PUT and typed error handling
- **Retry Logic**: Exponential backoff with jitter via `RetryPolicy` and `RetryHandler`
- **Exception Hierarchy**: `FlockException`, `FlockAuthException`, `FlockNetworkException`, `FlockValidationException`
- **Editor Tools**: Configuration window (`Qwacks > Configuration`) and package builder (`Qwacks > Package Builder`)
- **Configuration**: `FlockConfigAsset` ScriptableObject and `FlockInitConfig` for code-based setup
- **Logging**: `IFlockLogger` interface with `UnityFlockLogger` and `NullFlockLogger` implementations
- **Interfaces**: `IFlockClient`, `IConfigProvider`, `IPlayerDataService`
- **Models**: Domain-specific model files for auth, game, config, and player data
- **Generic Response**: `GenericResponse<T>` envelope wrapping API results with error and response metadata
- **Paginated Response**: `PaginatedResponse<T>` for list endpoints
- **Sample**: `FlockExample.cs` demonstrating SDK usage
