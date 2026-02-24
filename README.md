# Flock Unity SDK

The Flock Unity SDK provides access to Flock's game backend services from Unity games.

## Features

- Player authentication (email, device, registration)
- Game configuration with automatic patch merging
- Game patches
- Game and game version metadata
- Achievements
- Leaderboards
- Player data (CRUD with pagination)
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
// Game configs (with automatic patch merging)
var configs = await client.Config.GetAllConfigsAsync();
var configs = await client.Config.GetAllConfigsAsync(tag: "gameplay");
var versionConfigs = await client.Config.GetConfigsByVersionAsync();
var config = await client.Config.GetConfigByIdAsync("config-id");
var patches = await client.Config.GetConfigPatchesAsync("config-id");

// Game patches
var patches = await client.Patches.GetAllPatchesAsync();
var patch = await client.Patches.GetPatchByIdAsync("patch-id");
var configPatches = await client.Patches.GetPatchesByConfigIdAsync("config-id");

// Game info
var game = await client.Game.GetGameAsync();
var version = await client.Game.GetGameVersionAsync();

// Achievements
var achievements = await client.Achievements.GetAllAchievementsAsync();
var achievement = await client.Achievements.GetAchievementByIdAsync("achievement-id");

// Leaderboards
var boards = await client.Leaderboards.GetAllLeaderboardsAsync();
var board = await client.Leaderboards.GetLeaderboardByIdAsync("leaderboard-id");

// Player data
var data = await client.PlayerData.CreateAsync("player-id", new Dictionary<string, object> { { "score", 100 } });
var data = await client.PlayerData.GetByIdAsync("player-data-id");
var all = await client.PlayerData.GetAllAsync(page: 1, limit: 10);
var updated = await client.PlayerData.UpdateAsync("player-data-id", new Dictionary<string, object> { { "score", 200 } });
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
| Game Configs | `GET /v1/game_config` | API Key |
| Configs by Version | `GET /v1/game_config/version` | API Key |
| Config by ID | `GET /v1/game_config/{id}` | API Key |
| Config Patches | `GET /v1/game_config/{id}/patches` | API Key |
| Game Patches | `GET /v1/game_patch` | API Key |
| Patch by ID | `GET /v1/game_patch/{id}` | API Key |
| Patches by Config | `GET /v1/game_patch/config/{id}` | API Key |
| Game Info | `GET /v1/game` | API Key |
| Game Version | `GET /v1/game_version` | API Key |
| Player Data | `GET /v1/player_data` | API Key |
| Achievements | `GET /achievement` | Bearer |
| Leaderboards | `GET /leaderboard/{game_id}` | Bearer |
