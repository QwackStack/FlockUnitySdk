# FlockMessageService Usage Guide

## Overview
`FlockMessageService` sends expression events, errors, and custom data to the Flock API using API secret authentication.

## Initialization

### Basic Setup
```csharp
using Flock.Expression;

// Minimal initialization with API secret only
FlockMessageService flockService = new FlockMessageService(
    apiSecret: "your-api-secret-here"
);
```

### With Optional Parameters
```csharp
// Full initialization with game ID and custom timeout
FlockMessageService flockService = new FlockMessageService(
    apiSecret: "your-api-secret-here",
    gameId: "your-game-id",
    timeoutSeconds: 30
);
```

## Usage Examples

### Example 1: Basic Error Logging
```csharp
try
{
    // Your game logic
    PerformGameAction();
}
catch (Exception ex)
{
    ExpressionSendResult result = await flockService.SendMessageAsync(
        trackedMessage: "Game action failed",
        exceptionMessage: ex.Message,
        exceptionStackTrace: ex.StackTrace
    );

    if (result.Ok)
    {
        Debug.Log("Error logged successfully");
    }
}
```

### Example 2: Error with Custom Data
```csharp
try
{
    int score = CalculateScore();
}
catch (Exception ex)
{
    Dictionary<string, object> customData = new Dictionary<string, object>
    {
        ["player_id"] = "player_12345",
        ["level"] = 5,
        ["score"] = 1500,
        ["timestamp"] = DateTime.UtcNow.ToString("o")
    };

    ExpressionSendResult result = await flockService.SendMessageAsync(
        trackedMessage: "Score calculation error",
        exceptionMessage: ex.Message,
        exceptionStackTrace: ex.StackTrace,
        customData: customData
    );
}
```

### Example 3: Tracking Events Without Exceptions
```csharp
// Track a game event without an exception
Dictionary<string, object> eventData = new Dictionary<string, object>
{
    ["event_type"] = "level_completed",
    ["level_number"] = 10,
    ["completion_time"] = 125.5f
};

ExpressionSendResult result = await flockService.SendMessageAsync(
    trackedMessage: "Player completed level 10",
    exceptionMessage: null,
    exceptionStackTrace: null,
    customData: eventData
);
```

### Example 4: With Cancellation Token
```csharp
using System.Threading;

CancellationTokenSource cts = new CancellationTokenSource();
cts.CancelAfter(5000); // Cancel after 5 seconds

try
{
    ExpressionSendResult result = await flockService.SendMessageAsync(
        trackedMessage: "Important event",
        exceptionMessage: "Something went wrong",
        exceptionStackTrace: null,
        customData: null,
        ct: cts.Token
    );
}
catch (OperationCanceledException)
{
    Debug.Log("Request was cancelled");
}
```

### Example 5: Global Exception Handler
```csharp
public class GlobalExceptionHandler : MonoBehaviour
{
    private FlockMessageService _flockService;

    private void Awake()
    {
        _flockService = new FlockMessageService(
            apiSecret: "your-api-secret-here",
            gameId: "your-game-id"
        );

        Application.logMessageReceived += HandleLog;
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
        Dictionary<string, object> context = new Dictionary<string, object>
        {
            ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            ["time_since_startup"] = Time.realtimeSinceStartup
        };

        await _flockService.SendMessageAsync(
            trackedMessage: message,
            exceptionMessage: message,
            exceptionStackTrace: stackTrace,
            customData: context
        );
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= HandleLog;
    }
}
```

## Response Handling

### ExpressionSendResult Properties
```csharp
ExpressionSendResult result = await flockService.SendMessageAsync(...);

// Check if request was successful
bool success = result.Ok; // true if status code 200-299

// Get HTTP status code
int statusCode = result.StatusCode;

// Get response body
string responseBody = result.ResponseBody;

// Get error message (if any)
string error = result.Error;
```

### Complete Response Handling
```csharp
ExpressionSendResult result = await flockService.SendMessageAsync(
    trackedMessage: "Test message",
    exceptionMessage: "Test error"
);

if (result.Ok)
{
    Debug.Log($"✓ Success! Status: {result.StatusCode}");
    Debug.Log($"Response: {result.ResponseBody}");
}
else
{
    Debug.LogError($"✗ Failed! Status: {result.StatusCode}");
    Debug.LogError($"Error: {result.Error}");
    Debug.LogError($"Response: {result.ResponseBody}");
}
```

## Default Context Data

The service automatically includes these fields in every request:
- `platform` - Unity platform (e.g., "WindowsEditor", "Android")
- `unity_version` - Unity version
- `app_version` - Application version
- `device_model` - Device model
- `device_name` - Device name

Custom data provided in `customData` parameter will be merged with these defaults.

## Best Practices

1. **Initialize Once**: Create a single instance and reuse it
   ```csharp
   // Good - Singleton pattern
   public static FlockMessageService Instance { get; private set; }
   ```

2. **Don't Block Main Thread**: Use async/await properly
   ```csharp
   // Good
   private async void OnError(Exception ex) { await SendError(ex); }

   // Bad - blocks thread
   SendError(ex).Wait();
   ```

3. **Handle API Failures Gracefully**: Don't throw if API call fails
   ```csharp
   try
   {
       await flockService.SendMessageAsync(...);
   }
   catch (Exception apiEx)
   {
       // Log locally, don't crash the game
       Debug.LogWarning($"Failed to send to Flock: {apiEx.Message}");
   }
   ```

4. **Use Meaningful Messages**: Make tracked messages descriptive
   ```csharp
   // Good
   trackedMessage: "Player inventory desync on item purchase"

   // Bad
   trackedMessage: "Error"
   ```

## API Authentication

The service uses `X-API-Key` header for authentication. Ensure your API secret is:
- Kept secure (not committed to version control)
- Loaded from a secure source (e.g., environment variables, secure config)
- Different for development and production environments


