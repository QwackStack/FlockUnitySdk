# Flock Unity SDK

The Flock Unity SDK provides easy access to Flock's game backend services from Unity games.

## Features

- Multiple Authentication Methods:
  - Email/Password
  - Steam
  - Game Center
  - Play Store
  - Device ID
- Game configuration management
- Leaderboards
- Achievements
- Documents
- Events
- Currencies
- Shop system
- Segmentation
- Asset management
- Game versioning
- Patch management
- Player data management

## Installation

1. Download the latest release from the releases page
2. Import the .unitypackage file into your Unity project
3. Add the following dependencies to your project's manifest.json:

```json
{
  "dependencies": {
    "com.unity.nuget.newtonsoft-json": "3.0.2"
  }
}
```

## Configuration UI

The SDK includes a user-friendly configuration window that you can access from Unity's menu:

1. Open the configuration window: Window > Flock > Configuration
2. Fill in the required settings:
   - Game ID: Your unique game identifier
   - Client ID: Your API client identifier
   - Client Secret: Your API client secret (securely stored)
3. Configure Authentication Methods:
   - Enable/disable specific auth methods (Email, Steam, Game Center, Play Store, Device ID)
   - Each method can be toggled independently
4. Optional settings:
   - API URL: Custom API endpoint (defaults to https://api-flock.qwacks.com)
   - Enable Debug Logs: Toggle detailed logging
   - Timeout: Request timeout in seconds (1-120)

The configuration window provides:

- Secure storage of sensitive credentials
- Input validation
- Dark/Light theme support
- Easy reset functionality
- Configuration backup
- Authentication method management

The settings are stored in:

- EditorPrefs for editor-time configuration
- A ScriptableObject (FlockConfig.asset) for runtime use

Best practices for configuration:

1. Never commit the FlockConfig.asset file to version control
2. Use different credentials for development and production
3. Store the configuration file in a .gitignored location
4. Regularly backup your configuration
5. Only enable authentication methods you plan to use

## Quick Start

1. Initialize the SDK:

```csharp
using Flock;
using Flock.Config;
using Flock.Auth;

public class GameManager : MonoBehaviour
{
    private FlockClient _client;

    void Start()
    {
        var config = new FlockConfig.Builder()
            .SetGameId("your-game-id")
            .EnableAuthMethod(AuthProviderType.Steam)
            .EnableAuthMethod(AuthProviderType.GameCenter)
            .SetEnableDebugLogs(true)
            .Build();

        _client = new FlockClient(config);
    }
}
```

2. Authenticate a player:

```csharp
// Steam Authentication
try
{
    var steamProvider = new SteamAuthProvider(_client, steamTicket);
    _client.SetAuthProvider(steamProvider);
    var authResult = await _client.AuthenticateAsync();
    
    if (authResult.Success)
    {
        Debug.Log("Steam authentication successful!");
    }
    else
    {
        Debug.LogError($"Steam authentication failed: {authResult.ErrorMessage}");
    }
}
catch (FlockException ex)
{
    Debug.LogError($"Authentication failed: {ex.Message}");
}

// Game Center Authentication
try
{
    var gameCenterProvider = new GameCenterAuthProvider(_client);
    _client.SetAuthProvider(gameCenterProvider);
    var authResult = await _client.AuthenticateAsync();
    
    if (authResult.Success)
    {
        Debug.Log("Game Center authentication successful!");
    }
    else
    {
        Debug.LogError($"Game Center authentication failed: {authResult.ErrorMessage}");
    }
}
catch (FlockException ex)
{
    Debug.LogError($"Authentication failed: {ex.Message}");
}
```

3. Work with game configurations:

```csharp
try
{
    // Get all game configs
    var configs = await _client.GameConfigs.GetAllAsync();

    // Get specific config
    var config = await _client.GameConfigs.GetByIdAsync("config-id");
}
catch (FlockException ex)
{
    Debug.LogError($"Failed to get configs: {ex.Message}");
}
```

4. Work with player data:

```csharp
try
{
    // Create player data
    var data = new Dictionary<string, object>
    {
        { "level", 1 },
        { "coins", 100 },
        { "inventory", new[] { "sword", "shield" } }
    };

    var playerData = await _client.PlayerData.CreateAsync(
        playerId: "player-id",
        data: data
    );

    // Update player data
    data["level"] = 2;
    data["coins"] = 200;

    await _client.PlayerData.UpdateAsync(
        playerDataId: playerData.Id,
        data: data
    );

    // Get player data
    var allPlayerData = await _client.PlayerData.GetAllAsync(
        page: 1,
        limit: 10,
        playerId: "player-id"
    );
}
catch (FlockException ex)
{
    Debug.LogError($"Failed to handle player data: {ex.Message}");
}
```

## Error Handling

The SDK uses the `FlockException` class for error handling. All errors include:

- Message: A human-readable error message
- StatusCode: The HTTP status code (if applicable)
- AuthResult: For authentication errors, includes provider type and detailed error message

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

## Thread Safety

The SDK is thread-safe and uses async/await for all network operations. Make sure to:

- Call SDK methods from the main thread
- Use proper async/await patterns
- Don't block the main thread with network operations
- Handle authentication state changes on the main thread

## Security

The SDK implements several security best practices:

- All network communication uses HTTPS
- Authentication tokens are stored securely
- Sensitive data is never logged
- Input validation before sending to the server
- Support for multiple secure authentication methods
- Proper token refresh handling
- Secure storage of credentials

## Support

For issues and feature requests, please visit our [issue tracker](https://github.com/flock/unity-sdk/issues).
