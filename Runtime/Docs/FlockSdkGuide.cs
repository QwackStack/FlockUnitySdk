using UnityEngine;

namespace Flock.Docs
{
    /// <summary>
    /// In-Editor cheat sheet for the Flock Unity SDK. Opened from the
    /// 'Documentation' button in the Qwacks/Flock editor window. Rendered by
    /// FlockSdkGuideEditor — content lives in the constants below, so updates
    /// to the .cs file are always reflected without re-creating the asset.
    /// </summary>
    [CreateAssetMenu(fileName = "FlockSdkGuide", menuName = "Flock/SDK Guide", order = 100)]
    public class FlockSdkGuide : ScriptableObject
    {
        public const string Initialization =
@"The SDK is a singleton. Call FlockClient.CreateAsync(config) once at startup, then access everything through FlockClient.Instance.

Two ways to initialize:

A) Drop-in (recommended)
   In 'Qwacks > Flock', click 'Add Flock Bootstrap to Scene'. The bootstrap GameObject initializes the SDK on Awake.

      var bootstrap = FindObjectOfType<FlockBootstrap>();
      bootstrap.OnInitialized          += () => Debug.Log(""Ready"");
      bootstrap.OnInitializationFailed += ex => Debug.LogError(ex);

B) Code-based
      var config = new FlockInitConfig(
          apiUrl:      ""https://api-flock.qwacks.com"",
          apiKey:      ""your-api-key"",
          gameId:      ""your-game-id"",
          gameVersion: ""your-game-version-name"");
      await FlockClient.CreateAsync(config);

Accessing FlockClient.Instance before init throws — always initialize first.";

        public const string InitParameters =
@"Required:
  apiUrl       Flock API endpoint (default: https://api-flock.qwacks.com).
  apiKey       Your game's API key.
  gameId       Your game's ID.
  gameVersion  Your game version name (e.g. 'v1.0.0').

Optional:
  enableDebugLogs  Verbose console logging (default: false).
  analyticsConfig  See 'Analytics Parameters' below.
  retryPolicy      HTTP retry behaviour.

Asset cache:
  EnableAssetCache       Disk cache for asset downloads (default: true). Disable on WebGL.
  AssetCacheDirectory    Override path; empty uses the default location.
  AssetCacheMaxSizeMB    Cache size cap (default 100; 0 = unlimited).";

        public const string AnalyticsOverview =
@"Sessions start automatically once the SDK is initialized and the player is logged in. Heartbeats keep the session active, long backgrounding rotates it, and quitting ends it cleanly. If the app crashes, the session is recovered on the next launch.

Events tracked before login or while offline are kept on disk and sent later when both auth and network are available.

Turning analytics off (Enabled = false) makes every analytics call a no-op, so you don't need to wrap callsites in conditionals.";

        public const string AnalyticsParameters =
@"Enabled                    Master switch.
AutoStartSession           Start a session as soon as the SDK is ready.
AutoEndSessionOnQuit       Close the session on app quit.
SessionTimeoutSeconds      Background time before the session rotates.
HeartbeatIntervalSeconds   Heartbeat cadence (0 disables).
BounceThresholdSeconds     Below this, sessions are flagged as bounces.
PersistSessionOnDisk       Survive crashes — recovered on next launch.
TrackFps                   Sample FPS into the session.
FpsSampleIntervalSeconds   FPS sampling cadence.

Caching:
CacheFailedEvents          Keep failed events on disk for retry.
MaxCachedEvents            Cap on persisted events.
CacheFlushBatchSize        How many to send per flush.

These mirror the Analytics fields on the FlockConfig asset.";

        public const string CodeUsage =
@"All entry points are on FlockClient.Instance.Analytics.

// Screen view (aggregated, no immediate request)
FlockClient.Instance.Analytics.RecordScreenView(""MainMenu"");

// Single event
await FlockClient.Instance.Analytics.TrackEventAsync(
    eventName:     ""level_complete"",
    eventCategory: ""progression"",
    parameters: new Dictionary<string, object> {
        { ""level"", 7 },
        { ""duration_s"", 92 }
    });

// Free-form debug log
await FlockClient.Instance.Analytics.LogEventAsync(
    message: ""Boss spawned"",
    extraData: new Dictionary<string, object> { { ""arena"", ""crypt"" } });

// State / logical error
await FlockClient.Instance.Analytics.LogErrorAsync(
    message: ""Inventory check failed"",
    errorCode: ""SHOP_INSUFFICIENT_FUNDS"",
    errorData: new Dictionary<string, object> {
        { ""coins"", 30 }, { ""price"", 50 }
    });

// Caught exception
try { /* ... */ }
catch (Exception ex) {
    await FlockClient.Instance.Analytics.LogExceptionAsync(
        ex,
        errorData: new Dictionary<string, object> { { ""level"", 7 } });
}

// Raw exception (no Exception object)
await FlockClient.Instance.Analytics.LogExceptionAsync(
    message: ""Save failed"",
    stackTrace: Environment.StackTrace);

Notes:
  - Login is required. Calls before login are queued and flushed afterwards.
  - Parameters must be JSON-serializable (no UnityEngine objects).";

        public const string ExceptionCapturing =
@"Unhandled exceptions are captured automatically — anything that would show as a Unity exception in the console is also reported.

Use these for caught exceptions or extra context:

  // Caught exception
  try { /* ... */ }
  catch (Exception ex) {
      await FlockClient.Instance.Analytics.LogExceptionAsync(
          ex,
          errorData: new Dictionary<string, object> { { ""level"", 7 } });
  }

  // State error (not an exception)
  await FlockClient.Instance.Analytics.LogErrorAsync(
      message: ""Inventory check failed"",
      errorCode: ""SHOP_INSUFFICIENT_FUNDS"",
      errorData: new Dictionary<string, object> {
          { ""coins"", 30 }, { ""price"", 50 }
      });

  // Free-form debug log
  await FlockClient.Instance.Analytics.LogEventAsync(
      message: ""Boss spawned"",
      extraData: new Dictionary<string, object> { { ""arena"", ""crypt"" } });

Logs are queued, retried on failure, and only dropped when the payload itself is invalid.";
    }
}
