# Flock SDK Documentation

## Package Structure

```
com.flock.sdk/
├── Documentation~/
│   └── FlockSDK.md
├── Editor/
│   └── FlockConfigWindow.cs
├── Runtime/
│   ├── Auth/
│   │   └── FlockAuthProvider.cs
│   ├── Config/
│   │   └── FlockConfig.cs
│   ├── Http/
│   │   └── HttpClient.cs
│   ├── Models/
│   │   └── ...
│   ├── Services/
│   │   └── ...
│   └── FlockClient.cs
├── Samples~/
│   └── BasicUsage/
│       ├── Scenes/
│       │   └── FlockExample.unity
│       └── Scripts/
│           └── FlockExample.cs
├── CHANGELOG.md
├── LICENSE
├── package.json
└── README.md
```

## Installation

### Using Unity Package Manager

1. Open your Unity project
2. Open the Package Manager (Window > Package Manager)
3. Click the + button in the top-left corner
4. Select "Add package from git URL..."
5. Enter: `https://github.com/flock/unity-sdk.git`
6. Click "Add"

### Using .unitypackage

1. Download the latest .unitypackage from the releases page
2. Double-click the .unitypackage file
3. Import all assets into your project

## Configuration

1. Open the Flock Configuration window (Window > Flock > Configuration)
2. Fill in your game credentials:
   - Game ID
   - Client ID
   - Client Secret
3. Configure authentication methods
4. Set optional parameters
5. Click Save

## Quick Start

1. Create a new scene or use the example scene
2. Add the FlockExample script to a GameObject
3. Set up the UI references in the inspector
4. Run the scene and test the authentication methods

## Authentication

The SDK supports multiple authentication methods:

### Steam Authentication

```csharp
var steamProvider = new SteamAuthProvider(client, steamTicket);
client.SetAuthProvider(steamProvider);
var authResult = await client.AuthenticateAsync();
```

### Game Center Authentication

```csharp
var gameCenterProvider = new GameCenterAuthProvider(client);
client.SetAuthProvider(gameCenterProvider);
var authResult = await client.AuthenticateAsync();
```

### Device ID Authentication

```csharp
var deviceProvider = new DeviceIdAuthProvider(client);
client.SetAuthProvider(deviceProvider);
var authResult = await client.AuthenticateAsync();
```

## Services

The SDK provides several services:

### Game Configuration

```csharp
var configs = await client.GameConfigs.GetAllAsync();
var config = await client.GameConfigs.GetByIdAsync("config-id");
```

### Leaderboards

```csharp
var leaderboard = await client.Leaderboards.GetByIdAsync("leaderboard-id");
var entries = await client.Leaderboards.GetEntriesAsync("leaderboard-id");
```

### Achievements

```csharp
var achievements = await client.Achievements.GetAllAsync();
var achievement = await client.Achievements.GetByIdAsync("achievement-id");
```

### Player Data

```csharp
var data = new Dictionary<string, object>
{
    { "level", 1 },
    { "coins", 100 }
};

var playerData = await client.PlayerData.CreateAsync("player-id", data);
```

## Best Practices

1. Always initialize the SDK before using it
2. Handle exceptions appropriately
3. Store sensitive data securely
4. Use appropriate timeouts for network operations
5. Cache frequently accessed data when possible
6. Follow Unity's best practices for async operations
7. Choose appropriate authentication methods for your game
8. Handle authentication state changes properly
9. Implement proper error recovery for failed auth attempts

## Support

For issues and feature requests, please visit our [issue tracker](https://github.com/flock/unity-sdk/issues). 