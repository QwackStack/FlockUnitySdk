# Flock Unity SDK

The Flock Unity SDK provides access to Flock's game backend services from Unity games.

## Features

- Player authentication (email, device, registration)
- Game configuration (fetched from game patch endpoints, filterable by tag)
- Config schema validation (backend validation of config types)
- Game and game version metadata
- Player data and player templates (read with pagination, template lookup by ID or name)
- Shop (browse shops, items, purchase, inventory)
- Game commands (server-side operations: update player data, add funds)
- Analytics (session tracking, events, transactions — no-op safe when disabled)
- Automatic retry with exponential backoff
- JWT token management

## Installation

Add via Unity Package Manager using the git URL or import the `.unitypackage`.

## Setup

### Editor Configuration

Go to **Qwacks > Configuration** in the Unity menu bar to set:

- **API URL** — Flock API endpoint (default: `https://api-flock.qwacks.com`)
- **API Key** — Your Flock API key (required)
- **Game ID** — Your game ID from the Flock dashboard
- **Game Version ID** — Your game version ID

### Code-Based Configuration

```csharp
var config = new FlockInitConfig(
    apiUrl: "https://api-flock.qwacks.com",
    apiKey: "your-api-key",
    gameId: "your-game-id",
    gameVersionId: "your-game-version-id",
    enableDebugLogs: true
);

var client = new FlockClient(config);
```

Or load from a ScriptableObject:

```csharp
var configAsset = Resources.Load<FlockConfigAsset>("FlockConfig");
var client = new FlockClient(configAsset.ToInitConfig());
```

## Quick Start

### Authentication

```csharp
// Email login
var response = await client.LoginWithEmailAsync("player@example.com", "password");

// Device login
var response = await client.LoginWithDeviceAsync("ios", "device-uuid");

// Email registration
var response = await client.RegisterWithEmailAsync("player@example.com", "password", "PlayerName");

// Device registration
var response = await client.RegisterWithDeviceAsync("android", "device-uuid", "PlayerName");
```

All auth methods return a `PlayerLoginResponse` with access and refresh tokens. The SDK stores the access token internally.

### Services

```csharp
// Game configuration — raw (returns GamePatchSchema with Data dictionary)
var configs = await client.Config.GetAllAsync();
var config = await client.Config.GetByIdAsync("config-id");
var bySchema = await client.Config.GetBySchemaAsync("schema-id");

// Game configuration — typed (deserializes Data into your class)
var configs = await client.Config.GetAllAsync<GameplayConfig>();
var config = await client.Config.GetByIdAsync<GameplayConfig>("config-id");
var bySchema = await client.Config.GetBySchemaAsync<GameplayConfig>("schema-id");

// Game configs by tag (currency, gameplay — maps to /v1/game_config)
var currencyConfigs = await client.Config.GetGameConfigsAsync(SchemaTag.currency);
var gameplayConfigs = await client.Config.GetGameConfigsByVersionAsync(SchemaTag.gameplay);
var typed = await client.Config.GetGameConfigsAsync<CurrencyConfig>(SchemaTag.currency);

// Config schema validation (backend concern, most games skip this)
var schemas = await client.Schema.GetAllSchemasAsync(SchemaTag.gameplay);
var versionSchemas = await client.Schema.GetSchemasByVersionAsync(SchemaTag.currency);
var schema = await client.Schema.GetSchemaByIdAsync("schema-id");
var configs = await client.Schema.GetSchemaConfigsAsync("schema-id");

// Game info
var game = await client.Game.GetGameAsync();
var version = await client.Game.GetGameVersionAsync();

// Player data (read-only, create/update via game commands)
var data = await client.Player.GetDataByIdAsync("player-data-id");
var all = await client.Player.GetAllDataAsync(page: 1, limit: 10);
var byPlayer = await client.Player.GetAllDataAsync(playerId: "player-id");

// Player templates
var templates = await client.Player.GetTemplatesAsync();
var template = await client.Player.GetTemplateByIdAsync("template-id");
var template = await client.Player.GetTemplateByNameAsync("currency");
var playerData = await client.Player.GetTemplatePlayerDataAsync("template-id");

// Game commands — server-side operations (gameCommandId from backend dashboard)
var results = await client.Commands.UpdatePlayerDataAsync(
    "game-command-id", "player-data-id",
    new Dictionary<string, object> { { "level", 5 }, { "xp", 1200 } });

var results = await client.Commands.UpdatePlayerDataFieldAsync(
    "game-command-id", "player-data-id", "score", 9999);

var results = await client.Commands.AddGameFundsAsync(
    "game-command-id", "player-data-id", "gold", 500);

// Shop
var shops = await client.Shop.GetAllAsync(page: 1, limit: 10);
var shop = await client.Shop.GetByIdAsync("shop-id");
var item = await client.Shop.GetItemAsync("shop-item-id");
var items = await client.Shop.GetItemsByShopAsync("shop-id");
var inventory = await client.Shop.PurchaseAsync("shop-item-id", client.CurrentPlayerId);
var playerItems = await client.Shop.GetPlayerInventoryAsync(client.CurrentPlayerId);
```

## Headers

Every API request includes these headers:

| Header | Source | Description |
|--------|--------|-------------|
| `X-Flock-API-Key` | `FlockInitConfig.ApiKey` | Required. Identifies the game. |
| `X-Game-Version-ID` | `FlockInitConfig.GameVersionId` | Optional. Identifies the game version. |
| `Authorization` | Bearer token from login | Added after authentication. |

## API Endpoints

| Service | Endpoint | Auth |
|---------|----------|------|
| Email Login | `POST /v1/player/login` | API Key |
| Device Login | `POST /v1/player/login/device` | API Key |
| Email Register | `POST /v1/player/register` | API Key |
| Device Register | `POST /v1/player/register/device` | API Key |
| Game Configs | `GET /v1/game_patch` | API Key |
| Config by ID | `GET /v1/game_patch/{id}` | API Key |
| Configs by Schema | `GET /v1/game_patch/config/{id}` | API Key |
| Config Schemas | `GET /v1/game_config` | API Key |
| Schemas by Version | `GET /v1/game_config/version` | API Key |
| Schema by ID | `GET /v1/game_config/{id}` | API Key |
| Schema Configs | `GET /v1/game_config/{id}/patches` | API Key |
| Game Info | `GET /v1/game` | API Key |
| Game Version | `GET /v1/game_version` | API Key |
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
