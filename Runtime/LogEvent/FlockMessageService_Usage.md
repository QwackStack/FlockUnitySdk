# FlockLogEventService Usage Guide

## Overview
`FlockLogEventService` is a static service that provides a simple way to log events, exceptions, and errors to the Flock API. It automatically includes device and platform information.

## Initialization

The service is **static** - no initialization required! You can use it directly or through `FlockClient` helper methods.

### Direct Static Usage

```csharp
using Flock.LogEvent;

// Use directly with gameId
await FlockLogEventService.LogExceptionAsync(
    gameId: "your-game-id",
    message: "Error occurred",
    exception: ex
);
```

### Using FlockClient Helper Methods

For convenience, `FlockClient` provides helper methods that automatically pass the game ID:

```csharp
using Flock;
using Flock.Config;

// Initialize and authenticate FlockClient
var config = new FlockInitConfig(
    apiUrl: "https://api.flock.qwacks.com",
    apiKey: "your-api-key",
    environment: FlockEnvironment.Production
);

var client = new FlockClient(config);
await client.AuthenticateWithDeviceIdAsync("device-id-123");

// Use helper methods (automatically uses client's gameId)
await client.LogExceptionAsync("Error occurred", ex);
await client.LogLogicErrorAsync("Logic error", "ERROR_CODE");
```

## Usage Examples

### Example 1: Logging Exceptions (Static)

The simplest way to log exceptions:

```csharp
try
{
    // Your game logic
    PerformGameAction();
}
catch (Exception ex)
{
    var logEvent = await FlockLogEventService.LogExceptionAsync(
        gameId: "game-123",
        message: "Game action failed",
        exception: ex
    );
    
    Debug.Log($"Log event created: {logEvent.Id}");
}
```

### Example 2: Logging Exceptions with FlockClient

Using FlockClient helper methods (recommended):

```csharp
try
{
    int score = CalculateScore();
}
catch (Exception ex)
{
    var extraData = new Dictionary<string, object>
    {
        ["player_id"] = client.CurrentPlayerId,
        ["level"] = 5,
        ["score"] = 1500,
        ["action"] = "calculate_score"
    };

    var logEvent = await client.LogExceptionAsync(
        message: "Score calculation error",
        exception: ex,
        extraData: extraData
    );
}
```

### Example 3: Logging Logic Errors

For non-exception errors or business logic issues:

```csharp
// Static usage
if (playerInventory.Count > maxInventorySize)
{
    var logEvent = await FlockLogEventService.LogLogicErrorAsync(
        gameId: client.GameId,
        message: "Player inventory exceeded maximum size",
        errorCode: "INVENTORY_OVERFLOW",
        errorData: new Dictionary<string, object>
        {
            ["current_size"] = playerInventory.Count,
            ["max_size"] = maxInventorySize,
            ["player_id"] = client.CurrentPlayerId
        }
    );
}

// Or using FlockClient helper
await client.LogLogicErrorAsync(
    message: "Player inventory exceeded maximum size",
    errorCode: "INVENTORY_OVERFLOW",
    errorData: new Dictionary<string, object>
    {
        ["current_size"] = playerInventory.Count,
        ["max_size"] = maxInventorySize
    }
);
```

### Example 4: Global Exception Handler

Set up a global exception handler for your Unity game:

```csharp
using UnityEngine;
using Flock;
using Flock.Config;
using Flock.LogEvent;
using System;

public class GlobalExceptionHandler : MonoBehaviour
{
    private FlockClient _client;

    private void Awake()
    {
        var config = new FlockInitConfig(
            apiUrl: "https://api.flock.qwacks.com",
            apiKey: "your-api-key"
        );
        
        _client = new FlockClient(config);
        
        // Authenticate
        _ = InitializeClient();
        
        Application.logMessageReceived += HandleLog;
    }

    private async Task InitializeClient()
    {
        try
        {
            await _client.AuthenticateWithDeviceIdAsync(SystemInfo.deviceUniqueIdentifier);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to authenticate: {ex.Message}");
        }
    }

    private void HandleLog(string logString, string stackTrace, LogType type)
    {
        if (type == LogType.Exception || type == LogType.Error)
        {
            _ = SendErrorToFlock(logString, stackTrace);
        }
    }

    private async Task SendErrorToFlock(string message, string stackTrace)
    {
        try
        {
            if (!_client.IsAuthenticated || string.IsNullOrEmpty(_client.GameId))
            {
                Debug.LogWarning("Client not authenticated, skipping log event");
                return;
            }

            var extraData = new Dictionary<string, object>
            {
                ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                ["time_since_startup"] = Time.realtimeSinceStartup,
                ["log_type"] = "unity_log"
            };

            // Using FlockClient helper
            await _client.LogExceptionAsync(
                message: message,
                exception: new Exception(message),
                extraData: extraData
            );
        }
        catch (Exception ex)
        {
            // Log locally, don't crash the game
            Debug.LogWarning($"Failed to send log event to Flock: {ex.Message}");
        }
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= HandleLog;
    }
}
```

### Example 5: With Cancellation Token

Handle cancellation properly:

```csharp
using System.Threading;

CancellationTokenSource cts = new CancellationTokenSource();
cts.CancelAfter(5000); // Cancel after 5 seconds

try
{
    // Static usage
    var logEvent = await FlockLogEventService.LogExceptionAsync(
        gameId: "game-123",
        message: "Important error",
        exception: ex,
        cancellationToken: cts.Token
    );
    
    // Or using FlockClient helper
    var logEvent2 = await client.LogExceptionAsync(
        message: "Important error",
        exception: ex,
        cancellationToken: cts.Token
    );
}
catch (OperationCanceledException)
{
    Debug.Log("Log event request was cancelled");
}
```

## Log Event Types

The service supports two types of log events:

### LogEventType.Exception
For exceptions and errors:
- Automatically extracts exception message, stack trace, and error code
- Splits stack trace into lines for better analysis
- Includes exception type name as error code

### LogEventType.LogicError
For business logic errors and validation failures:
- Use when you want to log errors that aren't exceptions
- Useful for tracking game state inconsistencies

## Default Context Data

The service automatically includes these fields in every log event's `extra_data`:
- `platform` - Unity platform (e.g., "WindowsEditor", "Android")
- `unity_version` - Unity version
- `app_version` - Application version (from `Application.version`)
- `device_model` - Device model
- `device_name` - Device name
- `device_type` - Device type (Desktop, Handheld, etc.)
- `operating_system` - Operating system information

Custom data provided in `extraData` parameter will be merged with these defaults.

## Headers

The service automatically sets the following headers:
- `game-id` - The game ID (required parameter)
- `client-id` - Same as game-id (treated as the same value)
- `version` - Application version (automatically uses `Application.version`)
- `device-id` - Device identifier (automatically uses `SystemInfo.deviceUniqueIdentifier`)

The API URL is hardcoded to `https://api.flock.qwacks.com`.

## Response Handling

The service returns a `LogEventSchema` object containing:
- `Id` - Unique identifier for the log event
- `GameId` - The game ID associated with the event
- `Message` - The log message
- `Data` - The log event data (type, error info, etc.)
- `AdditionalData` - Any additional data
- `CreatedAt` - Timestamp when the event was created
- `UpdatedAt` - Timestamp when the event was last updated

```csharp
var logEvent = await client.LogExceptionAsync(
    message: "Test error",
    exception: ex
);

Debug.Log($"Log event ID: {logEvent.Id}");
Debug.Log($"Created at: {logEvent.CreatedAt}");
```

## Error Handling

The service throws `FlockException` and its subclasses:

```csharp
try
{
    var logEvent = await client.LogExceptionAsync(
        message: "Error",
        exception: ex
    );
}
catch (FlockException ex)
{
    Debug.LogError($"Flock error: {ex.Message}");
}
catch (FlockNetworkException ex)
{
    Debug.LogError($"Network error: {ex.Message}, Status: {ex.StatusCode}");
}
catch (FlockValidationException ex)
{
    Debug.LogError($"Validation error: {ex.Message}");
}
```

## Best Practices

1. **Use FlockClient Helper Methods**: For convenience, use the helper methods in `FlockClient` which automatically pass the game ID
   ```csharp
   // Recommended
   await client.LogExceptionAsync("Error", ex);
   
   // Direct static usage (when you don't have FlockClient)
   await FlockLogEventService.LogExceptionAsync(gameId, "Error", ex);
   ```

2. **Authenticate First**: Ensure the client is authenticated before using helper methods
   ```csharp
   if (!client.IsAuthenticated)
   {
       await client.AuthenticateWithDeviceIdAsync(deviceId);
   }
   await client.LogExceptionAsync("Error", ex);
   ```

3. **Don't Block Main Thread**: Use async/await properly
   ```csharp
   // Good
   private async void OnError(Exception ex) 
   { 
       await client.LogExceptionAsync("Error", ex); 
   }
   
   // Bad - blocks thread
   client.LogExceptionAsync("Error", ex).Wait();
   ```

4. **Handle API Failures Gracefully**: Don't throw if API call fails
   ```csharp
   try
   {
       await client.LogExceptionAsync("Error", ex);
   }
   catch (FlockException apiEx)
   {
       // Log locally, don't crash the game
       Debug.LogWarning($"Failed to send to Flock: {apiEx.Message}");
   }
   ```

5. **Use Meaningful Messages**: Make log messages descriptive
   ```csharp
   // Good
   message: "Player inventory desync on item purchase"
   
   // Bad
   message: "Error"
   ```

6. **Include Relevant Context**: Add extra data that helps debug issues
   ```csharp
   var extraData = new Dictionary<string, object>
   {
       ["player_id"] = client.CurrentPlayerId,
       ["level"] = currentLevel,
       ["action"] = "purchase_item",
       ["item_id"] = itemId
   };
   ```

## API Reference

### Static Methods

#### `LogExceptionAsync`
```csharp
public static Task<LogEventSchema> LogExceptionAsync(
    string gameId,
    string message,
    Exception exception,
    Dictionary<string, object> extraData = null,
    CancellationToken cancellationToken = default)
```

#### `LogLogicErrorAsync`
```csharp
public static Task<LogEventSchema> LogLogicErrorAsync(
    string gameId,
    string message,
    string errorCode = null,
    Dictionary<string, object> errorData = null,
    Dictionary<string, object> extraData = null,
    CancellationToken cancellationToken = default)
```

### FlockClient Helper Methods

#### `LogExceptionAsync`
```csharp
public Task<LogEvent.LogEventSchema> LogExceptionAsync(
    string message,
    Exception exception,
    Dictionary<string, object> extraData = null,
    CancellationToken cancellationToken = default)
```

#### `LogLogicErrorAsync`
```csharp
public Task<LogEvent.LogEventSchema> LogLogicErrorAsync(
    string message,
    string errorCode = null,
    Dictionary<string, object> errorData = null,
    Dictionary<string, object> extraData = null,
    CancellationToken cancellationToken = default)
```
