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

### Quick Install (Recommended)
Add via Unity Package Manager:
```
https://github.com/QwackStack/FlockUnitySDK.git
```

### Alternative: .unitypackage
1. Download from [Releases](https://github.com/QwackStack/FlockUnitySDK/releases)
2. Import into Unity
3. Dependencies auto-install (Newtonsoft.Json)

**⚠️ Important:** If you see Burst compiler errors after importing, restart Unity and delete the `Library` folder.

📖 **Detailed installation instructions:** See [INSTALLATION.md](INSTALLATION.md)

## Package Builder

The SDK includes a Package Builder tool to create distribution packages:

**Window > Flock SDK > Package Builder**

### Package Builder Features

- **Version Control**: Set custom version numbers for your packages
- **Output Path**: Choose where to save the generated .unitypackage
- **Build Options**:
  - Include/exclude .meta files
  - Include package dependencies
  - Interactive mode for manual file selection
- **Real-time Validation**: See file counts and validation errors before building
- **Quick Access**: Open output folder directly from the window

### Building a Package

1. Open the Package Builder window
2. Set the version number (e.g., "1.0.0")
3. Configure output path (default: "Build")
4. Choose build options
5. Click **Build Package** or **Build (Interactive)** for manual selection
6. Package will be saved as `FlockSDK-{version}.unitypackage`

## Configuration

The SDK includes an intuitive configuration window accessible from Unity's menu bar:

**Window > Flock SDK > Configuration**

### Configuration Window Features

#### Required Settings
- **Game ID**: Your unique Flock game identifier from the dashboard
- **Client ID**: Your API Client ID from the Flock dashboard
- **Client Secret**: Your API Client Secret (securely stored, never logged)
- **Environment**: Select Production (live games) or Development (testing)

#### Advanced Settings (Optional)
- **API URL**: Custom API endpoint (default: https://api-flock.qwacks.com)
- **Enable Debug Logs**: Toggle detailed SDK logging for troubleshooting

#### User-Friendly Interface
- ✅ **Status Bar**: Shows config file status and unsaved changes
- ⚠️ **Real-time Validation**: Inline warnings for missing or invalid values
- 💡 **Helpful Tooltips**: Hover over fields for detailed descriptions
- 🚀 **Quick Actions**:
  - Test Configuration: Validate your settings
  - Locate Config File: Jump to the config asset in the project
- 📖 **Documentation Links**: Quick access to docs and support

#### Configuration Storage

Settings are stored in two locations:
- **EditorPrefs**: Backup storage for editor-time configuration
- **FlockConfig.asset**: Runtime ScriptableObject in `Assets/Resources/`

#### Best Practices

1. ✅ Always configure before running the game
2. 🔒 Never commit sensitive credentials to version control
3. 🏷️ Use Development environment for testing
4. 📝 Use the Test Configuration feature to validate settings
5. 🔄 Keep separate configs for development and production builds

## Quick Start

### 1. Initialize the SDK

```csharp
using Flock;
using Flock.Config;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    private FlockClient _flockClient;

    void Start()
    {
        // Initialize with configuration
        var config = new FlockInitConfig(
            apiUrl: "https://api-flock.qwacks.com",
            gameId: "your-game-id",
            clientId: "your-client-id",
            clientSecret: "your-client-secret",
            enableDebugLogs: true,
            flockEnvironment: FlockEnvironment.Production
        );

        _flockClient = new FlockClient(config);

        Debug.Log("Flock SDK initialized successfully!");
    }
}
```

### 2. Authentication

#### Email/Password Login
```csharp
using Flock.Models;

public async void LoginWithEmail()
{
    try
    {
        LoginResponse response = await _flockClient.LoginAsync(
            email: "player@example.com",
            password: "securePassword123"
        );

        Debug.Log($"Login successful! Player ID: {response.PlayerId}");
        Debug.Log($"Access Token: {response.AccessToken}");
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"Login failed: {ex.Message}");
    }
}
```

#### Email/Password Registration
```csharp
public async void RegisterNewPlayer()
{
    try
    {
        RegisterResponse response = await _flockClient.RegisterAsync(
            email: "newplayer@example.com",
            password: "securePassword123",
            confirmPassword: "securePassword123"
        );

        Debug.Log($"Registration successful! Player ID: {response.PlayerId}");
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"Registration failed: {ex.Message}");
    }
}
```

#### Steam Authentication
```csharp
public async void AuthenticateWithSteam(string steamTicket)
{
    try
    {
        AuthResponse response = await _flockClient.AuthenticateWithSteamAsync(steamTicket);

        Debug.Log($"Steam authentication successful!");
        Debug.Log($"Player ID: {response.PlayerId}");
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"Steam authentication failed: {ex.Message}");
    }
}
```

#### Game Center Authentication
```csharp
public async void AuthenticateWithGameCenter()
{
    try
    {
        AuthResponse response = await _flockClient.AuthenticateWithGameCenterAsync();

        Debug.Log($"Game Center authentication successful!");
        Debug.Log($"Player ID: {response.PlayerId}");
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"Game Center authentication failed: {ex.Message}");
    }
}
```

### 3. Achievements

```csharp
using Flock.Models;
using System.Collections.Generic;

public async void WorkWithAchievements()
{
    try
    {
        // Get all achievements for the game
        List<Achievement> allAchievements = await _flockClient.Achievements.GetAllAchievementsAsync();
        Debug.Log($"Total achievements: {allAchievements.Count}");

        // Get a specific achievement
        Achievement achievement = await _flockClient.Achievements.GetAchievementByIdAsync("achievement-id");
        Debug.Log($"Achievement: {achievement.Name}");

        // Get player's achievements
        List<Achievement> playerAchievements = await _flockClient.Achievements.GetPlayerAchievementsAsync("player-id");
        Debug.Log($"Player has {playerAchievements.Count} achievements");

        // Unlock an achievement
        Achievement unlocked = await _flockClient.Achievements.UnlockAchievementAsync(
            playerId: "player-id",
            achievementId: "achievement-id"
        );
        Debug.Log($"Achievement unlocked: {unlocked.Name}");

        // Update achievement progress
        Achievement updated = await _flockClient.Achievements.UpdateProgressAsync(
            playerId: "player-id",
            achievementId: "achievement-id",
            progress: 75.5f
        );
        Debug.Log($"Progress updated: {updated.Progress}%");
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"Achievement operation failed: {ex.Message}");
    }
}
```

### 4. Leaderboards

```csharp
using Flock.Models;
using System.Collections.Generic;

public async void WorkWithLeaderboards()
{
    try
    {
        // Get all leaderboards
        List<LeaderboardInfo> leaderboards = await _flockClient.Leaderboards.GetAllLeaderboardsAsync();
        Debug.Log($"Total leaderboards: {leaderboards.Count}");

        // Get specific leaderboard
        LeaderboardInfo leaderboard = await _flockClient.Leaderboards.GetLeaderboardByIdAsync("leaderboard-id");
        Debug.Log($"Leaderboard: {leaderboard.Name}");

        // Get leaderboard entries with pagination
        PaginatedResponse<LeaderboardEntry> entries = await _flockClient.Leaderboards.GetLeaderboardEntriesAsync(
            leaderboardId: "leaderboard-id",
            page: 1,
            limit: 10
        );
        Debug.Log($"Entries: {entries.Total} total, showing {entries.Data.Count}");

        // Get top entries
        List<LeaderboardEntry> topEntries = await _flockClient.Leaderboards.GetTopEntriesAsync(
            leaderboardId: "leaderboard-id",
            count: 10
        );
        Debug.Log($"Top 10 entries retrieved");

        // Get player's entry
        LeaderboardEntry playerEntry = await _flockClient.Leaderboards.GetPlayerEntryAsync(
            leaderboardId: "leaderboard-id",
            playerId: "player-id"
        );
        Debug.Log($"Player rank: {playerEntry.Rank}, Score: {playerEntry.Score}");

        // Submit score to leaderboard
        LeaderboardEntry newEntry = await _flockClient.Leaderboards.SubmitScoreAsync(
            leaderboardId: "leaderboard-id",
            playerId: "player-id",
            score: 1000
        );
        Debug.Log($"Score submitted! New rank: {newEntry.Rank}");
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"Leaderboard operation failed: {ex.Message}");
    }
}
```

### 5. Player Data

```csharp
using Flock.Models;
using System.Collections.Generic;

public async void WorkWithPlayerData()
{
    try
    {
        // Create player data
        var data = new Dictionary<string, object>
        {
            { "level", 1 },
            { "xp", 0 },
            { "coins", 100 },
            { "inventory", new List<string> { "sword", "shield", "potion" } },
            { "settings", new Dictionary<string, object>
                {
                    { "musicVolume", 0.8f },
                    { "sfxVolume", 1.0f }
                }
            }
        };

        PlayerData playerData = await _flockClient.PlayerData.CreateAsync(
            playerId: "player-id",
            data: data
        );
        Debug.Log($"Player data created with ID: {playerData.Id}");

        // Get player data
        PlayerData retrievedData = await _flockClient.PlayerData.GetPlayerDataAsync();
        Debug.Log($"Retrieved player data: Level {retrievedData.Data["level"]}");

        // Update player data
        data["level"] = 2;
        data["xp"] = 150;
        data["coins"] = 250;

        PlayerData updatedData = await _flockClient.PlayerData.UpdatePlayerDataAsync(playerData);
        Debug.Log($"Player data updated successfully");

        // Get player data by ID
        PlayerData specificData = await _flockClient.PlayerData.GetByIdAsync("player-data-id");
        Debug.Log($"Retrieved specific player data");
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"Player data operation failed: {ex.Message}");
    }
}
```

### 6. Game Configuration

```csharp
using Flock.Models;
using System.Collections.Generic;

public async void WorkWithGameConfig()
{
    try
    {
        // Get all game configurations
        List<GameConfig> configs = await _flockClient.Config.GetAllConfigAsync();
        Debug.Log($"Total configs: {configs.Count}");

        foreach (var config in configs)
        {
            Debug.Log($"Config: {config.Key} = {config.Value}");
        }

        // Get specific configuration
        GameConfig specificConfig = await _flockClient.Config.GetConfigByIdAsync("config-id");
        Debug.Log($"Config value: {specificConfig.Value}");
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"Config operation failed: {ex.Message}");
    }
}
```

## Complete Example

Here's a complete example integrating multiple SDK features:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock;
using Flock.Config;
using Flock.Models;
using UnityEngine;

public class FlockSDKManager : MonoBehaviour
{
    private FlockClient _flockClient;
    private string _currentPlayerId;

    async void Start()
    {
        InitializeSDK();
        await AuthenticatePlayer();
        await LoadGameData();
    }

    private void InitializeSDK()
    {
        // Load config from Resources or use manual config
        var config = new FlockInitConfig(
            apiUrl: "https://api-flock.qwacks.com",
            gameId: "your-game-id",
            clientId: "your-client-id",
            clientSecret: "your-client-secret",
            enableDebugLogs: Application.isEditor,
            flockEnvironment: Application.isEditor ?
                FlockEnvironment.Development :
                FlockEnvironment.Production
        );

        _flockClient = new FlockClient(config);
        Debug.Log("✅ Flock SDK initialized");
    }

    private async Task AuthenticatePlayer()
    {
        try
        {
            // Try to authenticate with saved credentials or use new login
            var response = await _flockClient.LoginAsync(
                email: "player@example.com",
                password: "password123"
            );

            _currentPlayerId = response.PlayerId;
            Debug.Log($"✅ Authenticated as: {_currentPlayerId}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Authentication failed: {ex.Message}");
        }
    }

    private async Task LoadGameData()
    {
        if (string.IsNullOrEmpty(_currentPlayerId)) return;

        try
        {
            // Load player data
            var playerData = await _flockClient.PlayerData.GetPlayerDataAsync();
            Debug.Log($"✅ Player data loaded");

            // Load achievements
            var achievements = await _flockClient.Achievements.GetPlayerAchievementsAsync(_currentPlayerId);
            Debug.Log($"✅ Loaded {achievements.Count} achievements");

            // Load leaderboard position
            var leaderboardEntry = await _flockClient.Leaderboards.GetPlayerEntryAsync(
                leaderboardId: "main-leaderboard",
                playerId: _currentPlayerId
            );
            Debug.Log($"✅ Player rank: {leaderboardEntry.Rank}");

            // Load game configs
            var configs = await _flockClient.Config.GetAllConfigAsync();
            Debug.Log($"✅ Loaded {configs.Count} game configs");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Failed to load game data: {ex.Message}");
        }
    }

    public async Task UnlockAchievement(string achievementId)
    {
        try
        {
            var achievement = await _flockClient.Achievements.UnlockAchievementAsync(
                _currentPlayerId,
                achievementId
            );
            Debug.Log($"🏆 Achievement unlocked: {achievement.Name}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Failed to unlock achievement: {ex.Message}");
        }
    }

    public async Task SubmitScore(int score)
    {
        try
        {
            var entry = await _flockClient.Leaderboards.SubmitScoreAsync(
                leaderboardId: "main-leaderboard",
                playerId: _currentPlayerId,
                score: score
            );
            Debug.Log($"📊 Score submitted! Rank: {entry.Rank}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Failed to submit score: {ex.Message}");
        }
    }

    public async Task SavePlayerProgress(int level, int xp, int coins)
    {
        try
        {
            var data = new Dictionary<string, object>
            {
                { "level", level },
                { "xp", xp },
                { "coins", coins },
                { "lastSaved", DateTime.UtcNow.ToString("o") }
            };

            await _flockClient.PlayerData.CreateAsync(_currentPlayerId, data);
            Debug.Log("💾 Player progress saved");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Failed to save progress: {ex.Message}");
        }
    }
}
```

## Error Handling

The SDK uses standard .NET exceptions for error handling:

```csharp
try
{
    var result = await _flockClient.LoginAsync(email, password);
}
catch (System.Net.Http.HttpRequestException ex)
{
    // Network errors
    Debug.LogError($"Network error: {ex.Message}");
}
catch (System.Exception ex)
{
    // General errors
    Debug.LogError($"Error: {ex.Message}");
}
```

**Common Error Scenarios:**
- Network connectivity issues
- Invalid credentials
- Missing or invalid game ID
- Rate limiting
- Server errors

## Best Practices

### Configuration
1. ✅ Always configure SDK via the Configuration Window before first use
2. ✅ Use Environment.Development for testing, Environment.Production for release
3. ✅ Enable debug logs during development
4. ✅ Never commit FlockConfig.asset with production credentials

### Authentication
1. ✅ Store access tokens securely
2. ✅ Handle authentication failures gracefully
3. ✅ Implement token refresh logic
4. ✅ Choose appropriate auth method for your platform

### Data Management
1. ✅ Cache frequently accessed data locally
2. ✅ Implement retry logic for failed requests
3. ✅ Validate data before sending to server
4. ✅ Use pagination for large datasets

### Performance
1. ✅ Use async/await properly to avoid blocking main thread
2. ✅ Batch API calls when possible
3. ✅ Implement proper loading states in UI
4. ✅ Handle background/foreground transitions

### Error Handling
1. ✅ Always use try-catch blocks for async operations
2. ✅ Provide user-friendly error messages
3. ✅ Log errors for debugging
4. ✅ Implement fallback mechanisms

## Thread Safety

The SDK is thread-safe and uses async/await for all network operations:

```csharp
// ✅ Correct - async/await pattern
public async void OnButtonClick()
{
    var result = await _flockClient.LoginAsync(email, password);
    UpdateUI(result);
}

// ❌ Incorrect - blocking main thread
public void OnButtonClick()
{
    var result = _flockClient.LoginAsync(email, password).Result; // Don't do this!
}
```

**Guidelines:**
- ✅ Use async/await for all SDK calls
- ✅ Call SDK methods from the main Unity thread
- ✅ Handle callbacks on the main thread
- ❌ Never use `.Result` or `.Wait()` on async operations

## Security

The SDK implements security best practices:

- 🔒 **HTTPS Only** - All network communication uses HTTPS
- 🔑 **Token Management** - Secure storage of access/refresh tokens
- 🛡️ **Input Validation** - Data validated before sending to server
- 🚫 **No Sensitive Logging** - Credentials never logged in production
- 🔐 **Multiple Auth Methods** - Steam, Game Center, Email, Play Store
- 🔄 **Token Refresh** - Automatic token refresh handling

**Security Checklist:**
1. ✅ Never hardcode credentials in code
2. ✅ Use HTTPS for all API endpoints
3. ✅ Store tokens securely (not in PlayerPrefs)
4. ✅ Implement proper logout functionality
5. ✅ Use environment-specific configurations
6. ✅ Enable debug logs only in development builds

## Platform Support

| Platform | Status | Auth Methods |
|----------|--------|--------------|
| **Windows** | ✅ Supported | Email, Steam, Device ID |
| **macOS** | ✅ Supported | Email, Steam, Device ID |
| **Linux** | ✅ Supported | Email, Steam, Device ID |
| **iOS** | ✅ Supported | Email, Game Center, Device ID |
| **Android** | ✅ Supported | Email, Play Store, Device ID |
| **WebGL** | ⚠️ Limited | Email, Device ID |

## Troubleshooting

### Common Issues

**SDK fails to initialize**
```csharp
// Make sure credentials are set
var config = new FlockInitConfig(
    apiUrl: "https://api-flock.qwacks.com",
    gameId: "your-game-id", // ⚠️ Required!
    clientId: "your-client-id", // ⚠️ Required!
    clientSecret: "your-client-secret", // ⚠️ Required!
    enableDebugLogs: true,
    flockEnvironment: FlockEnvironment.Production
);
```

**Authentication fails**
- Verify credentials are correct
- Check network connectivity
- Ensure API URL is correct
- Enable debug logs to see detailed errors

**Network timeout errors**
- Check internet connection
- Verify firewall settings
- Ensure API endpoint is accessible
- Try increasing timeout values

**Data not syncing**
- Verify player is authenticated
- Check access token validity
- Ensure proper error handling
- Review API response messages

### Debug Logging

Enable detailed logging for troubleshooting:

```csharp
var config = new FlockInitConfig(
    apiUrl: "https://api-flock.qwacks.com",
    gameId: "your-game-id",
    clientId: "your-client-id",
    clientSecret: "your-client-secret",
    enableDebugLogs: true, // ⚠️ Enable for debugging
    flockEnvironment: FlockEnvironment.Development
);
```

## Version History

### v1.0.0 (Current)
- ✅ Core SDK implementation
- ✅ Authentication (Email, Steam, Game Center, Play Store)
- ✅ Achievements system
- ✅ Leaderboards system
- ✅ Player data management
- ✅ Game configuration
- ✅ Editor tools (Configuration Window, Package Builder)

## Support & Resources

- 📖 **Documentation**: [https://flock.qwacks.com/](https://flock.qwacks.com/)
- 💬 **Support**: [https://flock.qwacks.com/](https://flock.qwacks.com/)
- 🐛 **Issues**: [GitHub Issues](https://github.com/QwackStack/FlockUnitySdk/issues)

## License

Copyright © 2026 Qwacks. All rights reserved.

For licensing information, please contact support@qwacks.com
