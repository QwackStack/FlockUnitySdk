using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Constants;
using Flock.Exceptions;
using Flock.Http;
using Flock.Interfaces;
using Flock.Logging;
using Flock.Models;
using Newtonsoft.Json;
using UnityEngine;
using static System.FormattableString;

namespace Flock.Providers
{
    public class FlockAnalyticsProvider : FlockProviderBase ,IAnalyticProvider
    {
        private readonly FlockAnalyticsConfig _config;
        private readonly IEventCache<AnalyticsEventRequest> _eventCache;
        private readonly IEventCache<LogEventRequest> _logEventCache;
        // Write-ahead spool: session ends hit disk before any network attempt and are
        // retried until the server confirms them.
        private readonly IEventCache<FlockSessionSnapshot> _sessionEndCache;
        private FlockSession _session;
        private readonly FlockTerminationTracker _terminationTracker;
        // This launch's own folder, and the launches that ended before it.
        private readonly FlockAnalyticsLaunches _launches;
        private bool _initialized;
        // Exception capture: the hooks, the repeat rule, and the frame and quit handlers that take what the hooks kept.
        private readonly FlockExceptionCapture _exceptionCapture;
        private readonly FlockRepeatedExceptionCounter _repeatedExceptions;
        private readonly List<FlockExceptionRepeatSummary> _repeatSummaries = new List<FlockExceptionRepeatSummary>();
        private FlockBehaviour _lifecycle;
        private double _heldBackReportedAt = double.MinValue;
        private string _currentPlayerId;
        private bool _heartbeatInFlight;
        private bool _registrationInFlight;
        private readonly FlockConsentStore _consentStore = new FlockConsentStore();
        private bool _hasConsent;

        /// <summary>Tracks crashes in the editor too, where a stopped Play session is no crash; for tests of the launch's crash marker.</summary>
        internal static bool TrackTerminationInTheEditorForTesting;

        /// <summary>Whether this provider's exception capture is listening to Unity.</summary>
        internal bool IsListeningForExceptions => _exceptionCapture != null && _exceptionCapture.IsListening;

        /// <summary>What the capture hears now: nothing unless it is listening, and no logged exception while Unity's logger is off.</summary>
        public FlockExceptionCaptureCoverage GetExceptionCaptureCoverage()
            => new FlockExceptionCaptureCoverage(IsListeningForExceptions, Debug.unityLogger.logEnabled);

        public FlockAnalyticsProvider(FlockClient client) : base(client)
        {
            _config = client.InitConfig.AnalyticsConfig;
            _launches = client.AnalyticsLaunches;
            // One player's events a batch: the server refuses a whole batch for one entry whose player it does not know.
            _eventCache = TryCreateCache<AnalyticsEventRequest>(client, FlockAnalyticsLaunches.AnalyticsEventsQueueName, _config.CacheFailedEvents,
                request => request.PlayerId);
            _logEventCache = TryCreateCache<LogEventRequest>(client, FlockAnalyticsLaunches.LogEventsQueueName, _config.CacheFailedEvents);
            _sessionEndCache = TryCreateCache<FlockSessionSnapshot>(client, FlockAnalyticsLaunches.SessionEndsQueueName, _config.PersistSessionOnDisk);

            // A previously-recorded decision always wins; otherwise fall back to the
            // config's default policy (opt-out unless RequireExplicitConsent is on).
            bool? storedConsent = _consentStore.Load();
            _hasConsent = storedConsent ?? !_config.RequireExplicitConsent;

            // Enabled is resolved once here so the tracker itself stays platform-agnostic and testable:
            // needs disk persistence, and Editor/WebGL are excluded (Stop isn't a death; WebGL has no reliable lifecycle).
            bool terminationTrackingEnabled = _config.PersistSessionOnDisk && _launches.IsHoldingItsFolder
                && (!Application.isEditor || TrackTerminationInTheEditorForTesting)
                && Application.platform != RuntimePlatform.WebGLPlayer;
            _terminationTracker = new FlockTerminationTracker(client.Logger, terminationTrackingEnabled, _launches.TerminationMarkerPath,
                _config.HeartbeatIntervalSeconds);
            if (_config.PersistSessionOnDisk && Application.platform == RuntimePlatform.WebGLPlayer)
                client.Logger.LogWarning("Termination tracking is disabled on WebGL (no reliable quit/pause lifecycle)");

            // From start-up, not sign-in: log events carry no player, and a crash before sign-in is a crash too.
            _repeatedExceptions = new FlockRepeatedExceptionCounter(_config.ExceptionRepeatWindowSeconds);
            if (_config.CaptureExceptions)
            {
                _exceptionCapture = new FlockExceptionCapture();
                _exceptionCapture.Start();
            }
            if (_hasConsent)
                _terminationTracker.BeginTracking();
            _lifecycle = FlockBehaviour.Instance;
            if (_lifecycle != null)
            {
                _lifecycle.OnTick += HandleLaunchTick;
                _lifecycle.OnQuit += HandleLaunchQuit;
                _lifecycle.OnDisabled += HandleLifecycleDisabled;
            }
        }

        private IEventCache<T> TryCreateCache<T>(FlockClient client, string subfolder, bool enabled, Func<T, string> sendTogetherKey = null) where T : class
        {
            // A launch that holds no folder keeps nothing on disk: no later launch could take a lockless folder over.
            if (!enabled || !_launches.IsHoldingItsFolder)
                return null;

            try
            {
                return new FlockEventCache<T>(
                    _launches.Folder,
                    subfolder,
                    _config.MaxCachedEvents, _config.CacheFlushBatchSize, client.Logger, sendTogetherKey);
            }
            catch (Exception ex)
            {
                client.Logger.LogWarning($"Event cache '{subfolder}' unavailable, falling back to direct send: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Server-issued id of the active session, or null until registration succeeds.
        /// Narrower than <see cref="FlockClient.CurrentSessionId"/> (which falls back to
        /// the local id): events must never carry an id the server hasn't issued.
        /// </summary>
        public string CurrentSessionId => _session?.ServerSessionId;
        private bool HasActiveSession => _session?.IsActive ?? false;
        public FlockSessionSnapshot CurrentSnapshot => _session?.IsActive == true ? _session.TakeSnapshot() : null;

        public bool HasConsent => _hasConsent;

        public void SetConsent(bool granted)
        {
            if (granted == _hasConsent)
                return;

            _hasConsent = granted;
            _consentStore.Save(granted);
            FlockEvents.InvokeConsentChanged(granted);

            if (granted)
            {
                Client.Logger.LogInfo("Analytics consent granted");
                _terminationTracker.BeginTracking();

                if (Client.IsAuthenticated && _config.AutoStartSession && (_session == null || !_session.IsActive))
                    ResumeSessionOnConsentGranted();

                return;
            }

            Client.Logger.LogInfo("Analytics consent revoked");

            if (_session != null && _session.IsActive)
                _session.Discard();

            // Nothing is collected without consent, a crash marker included.
            _terminationTracker.StopTracking();

            _initialized = false;
        }

        // Fire-and-forget resume: SetConsent is a synchronous API surface (matching every
        // competitor's consent toggle), and StartSessionAsync already swallows registration
        // failures internally, so nothing here can throw uncaught.
        private async void ResumeSessionOnConsentGranted()
        {
            try
            {
                await StartSessionAsync();
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Resuming session after consent grant failed: {ex.Message}");
            }
        }

        // Local-only: purges the on-disk queue of events not yet delivered to Flock's
        // backend, including crash/log events (gated by consent same as behavioral events —
        // both carry player-identifiable data). Does not touch anything already ingested by
        // the server — there's no backend endpoint that could do that from the client SDK today.
        public void EraseLocalAnalyticsData()
        {
            _eventCache?.Clear();
            _sessionEndCache?.Clear();
            _logEventCache?.Clear();
            Client.Logger.LogInfo("Local analytics data erased (queued events + session-end spool + log events)");
        }

        private bool ConsentGiven()
        {
            if (_hasConsent)
                return true;

            Client.Logger.LogDebug("Analytics consent not granted; call SetConsent(true) to enable tracking");
            return false;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (!_config.Enabled)
                return;

            string newPlayerId = Client.CurrentPlayerId ?? FlockConstant.DummyUserID;

            if (_initialized && _currentPlayerId != newPlayerId)
            {
                Client.Logger.LogInfo($"Player changed ({_currentPlayerId} -> {newPlayerId}), resetting analytics session");

                if (_session != null)
                {
                    if (_session.IsActive)
                    {
                        FlockSessionSnapshot oldSnapshot = _session.End(FlockSessionEndReason.Restarted);
                        if (oldSnapshot != null)
                            await DeliverSessionEndAsync(oldSnapshot, cancellationToken);
                    }

                    _session.Reset(FlockSessionEndReason.Restarted);
                }

                _initialized = false;
            }

            if (_initialized)
                return;

            _session = Client.Session;
            if (_session == null)
                return;

            _initialized = true;
            _currentPlayerId = newPlayerId;

            // -= first — a logout with no active session leaves these attached (Reset never ran).
            _session.OnHeartbeat -= HandleHeartbeat;
            _session.OnHeartbeat += HandleHeartbeat;
            _session.OnFlushInterval -= HandleFlushInterval;
            _session.OnFlushInterval += HandleFlushInterval;
            _session.OnSessionPaused -= HandleSessionPaused;
            _session.OnSessionPaused += HandleSessionPaused;
            _session.OnSessionTimedOut -= HandleSessionTimedOut;
            _session.OnSessionTimedOut += HandleSessionTimedOut;
            _session.OnSessionEnded -= HandleSessionEnded;
            _session.OnSessionEnded += HandleSessionEnded;
            _session.OnQuitFlush -= HandleQuitFlush;
            _session.OnQuitFlush += HandleQuitFlush;

            await ReportWhatEndedLaunchesLeftAsync(cancellationToken);

            // Deliver unconfirmed ends from previous runs before a new session registers.
            // When offline, the spool drains on later flush triggers instead.
            if (_sessionEndCache != null && _sessionEndCache.PendingCount > 0 && IsServerReachable())
            {
                Client.Logger.LogInfo($"Delivering {_sessionEndCache.PendingCount} pending session end(s)");
                await FlushSessionEndsAsync(cancellationToken);
            }

            if (_config.AutoStartSession)
                await StartSessionAsync(cancellationToken);

            // Reattribute events that were queued under the "Default" placeholder (tracked
            // before login completed) to the real player ID, then flush.
            if (_eventCache != null && Client.IsAuthenticated)
            {
                _eventCache.Rewrite(
                    evt => evt.PlayerId == FlockConstant.DummyUserID, evt => evt.PlayerId = newPlayerId);
            }

            // What was captured before sign-in waits in the log queue until now.
            FlushCacheInBackground();
        }

        // Logout funnel (ClearTokens): session handlers were detached — force a full re-wire on next InitializeAsync.
        internal void HandleAuthCleared()
        {
            _initialized = false;
        }

        /// <summary>A diagnostic saying how many different exceptions were counted rather than reported, past the launch's limit.</summary>
        internal const string ExceptionReportsHeldBackEvent = "exception_reports_held_back";

        // The held-back count goes out at most this often, and whatever is left at quit.
        private const double HeldBackReportIntervalSeconds = 60.0;

        /// <summary>The Flock SDK shuts down: capture stops, what it kept is queued, and the launch's crash marker is cleared.</summary>
        internal void StopForShutdown()
        {
            if (_lifecycle != null)
            {
                _lifecycle.OnTick -= HandleLaunchTick;
                _lifecycle.OnQuit -= HandleLaunchQuit;
                _lifecycle.OnDisabled -= HandleLifecycleDisabled;
                _lifecycle = null;
            }
            StopListeningForExceptions();
            ReportCapturedExceptions(Time.realtimeSinceStartupAsDouble, true);
            // A clean end: a marker left behind would read as a crash to the next Create.
            _terminationTracker.StopTracking();
        }

        /// <summary>Takes the hooks off Unity's static events, which outlive a Play session when domain reload is off.</summary>
        internal void StopListeningForExceptions() => _exceptionCapture?.Stop();

        /// <summary>Every frame: what the capture hooks kept, then the crash marker's heartbeat, which saves their count.</summary>
        internal void HandleLaunchTick()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            ReportCapturedExceptions(now, false);
            _terminationTracker.HandleTick(now);
        }

        // A clean quit: open repeat counts are queued, since no later frame will close them, and the crash marker goes.
        private void HandleLaunchQuit()
        {
            ReportCapturedExceptions(Time.realtimeSinceStartupAsDouble, true);
            _terminationTracker.StopTracking();
        }

        /// <summary>Unity disables the SDK's object at quit after every script's OnApplicationQuit, whose exceptions no frame will take.</summary>
        internal void HandleLifecycleDisabled() => ReportCapturedExceptions(Time.realtimeSinceStartupAsDouble, true);

        /// <summary>Main thread: what the hooks kept goes through the repeat rule into the log queue. Nothing is made when there is nothing to do.</summary>
        internal void ReportCapturedExceptions(double nowSeconds, bool quitting)
        {
            if (_exceptionCapture == null)
                return;

            try
            {
                while (_exceptionCapture.TryTake(out FlockCapturedException captured))
                {
                    // Every occurrence counts toward the next launch's crash report, repeats included.
                    _terminationTracker.NoteException();
                    if (!_hasConsent)
                        continue;
                    string key = FlockRepeatedExceptionCounter.MakeSameFaultKey(captured.Source, captured.Message, captured.StackTrace);
                    if (_repeatedExceptions.ShouldReportNow(key, captured, nowSeconds))
                        EnqueueLog(BuildCapturedException(captured.Message, captured.StackTrace, captured.Source, null));
                }
                int lost = _exceptionCapture.TakeLost();
                _terminationTracker.NoteExceptions(lost);
                _repeatedExceptions.CountHeldBack(lost);

                if (quitting)
                    _repeatedExceptions.CollectAll(_repeatSummaries);
                else
                    _repeatedExceptions.CollectFinished(nowSeconds, _repeatSummaries);
                if (_repeatSummaries.Count > 0)
                {
                    foreach (FlockExceptionRepeatSummary summary in _repeatSummaries)
                        EnqueueLog(BuildCapturedException(summary.Message, summary.StackTrace, summary.Source, summary.Repeats));
                    _repeatSummaries.Clear();
                }

                if (_repeatedExceptions.HeldBack > 0 && (quitting || nowSeconds - _heldBackReportedAt >= HeldBackReportIntervalSeconds))
                {
                    _heldBackReportedAt = nowSeconds;
                    EnqueueLog(BuildLogEvent(LogEventType.Debug, ExceptionReportsHeldBackEvent, extraData: new Dictionary<string, object>
                    {
                        { "held_back_count", _repeatedExceptions.TakeHeldBack() },
                        { "most_different_faults_per_launch", FlockRepeatedExceptionCounter.MostDifferentFaultsPerLaunch }
                    }));
                }
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"A captured exception could not be queued: {ex.Message}");
            }
        }

        // A captured exception; a repeat summary also says how many more times it happened in its window.
        private LogEventRequest BuildCapturedException(string message, string stackTrace, string source, int? repeats)
        {
            Dictionary<string, object> extraData = new Dictionary<string, object> { { "exception_source", source } };
            if (repeats.HasValue)
            {
                extraData["repeat_count"] = repeats.Value;
                extraData["repeat_window_seconds"] = _repeatedExceptions.WindowSeconds;
            }
            return BuildLogEvent(
                LogEventType.Exception,
                message: message,
                errorMessage: message,
                errorTraceback: stackTrace,
                errorTracebackLines: SplitStackTrace(stackTrace),
                extraData: extraData);
        }

        public async Task<string> StartSessionAsync(CancellationToken cancellationToken = default)
        {
            RequireAuth();

            if (!ConsentGiven())
                return null;

            if (_session == null)
            {
                Client.Logger.LogWarning("Analytics is disabled, cannot start session");
                return null;
            }

            // Deliver the previous session's end before the new one registers.
            if (_session.IsActive)
            {
                FlockSessionSnapshot stale = _session.End(FlockSessionEndReason.Restarted);
                if (stale != null)
                    await DeliverSessionEndAsync(stale, cancellationToken);
            }

            string localId = _session.Start(Client.CurrentPlayerId ?? FlockConstant.DummyUserID);
            _terminationTracker.NoteSessionStarted(localId);

            string serverId = await TryRegisterSessionAsync(cancellationToken);
            return serverId ?? localId;
        }

        // Non-fatal registration: on failure the session continues locally and the
        // heartbeat retries until a server id is obtained. Never throws.
        private async Task<string> TryRegisterSessionAsync(CancellationToken cancellationToken = default)
        {
            if (_session == null || !_session.IsActive)
                return null;

            if (!string.IsNullOrEmpty(_session.ServerSessionId))
                return _session.ServerSessionId;

            if (_registrationInFlight)
                return null;

            _registrationInFlight = true;
            string localSessionId = _session.SessionId;

            try
            {
                string serverSessionId = await PostSessionStartAsync(
                    BuildSessionStartRequest(_session.TakeSnapshot()), cancellationToken);

                // Adopt the id only if the session didn't rotate or end while the POST was in flight.
                if (_session.IsActive && _session.SessionId == localSessionId)
                {
                    _session.SetServerSessionId(serverSessionId);
                    _terminationTracker.NoteSessionRegistered(serverSessionId);
                    Client.Logger.LogInfo($"Session registered with server: {serverSessionId}");
                    return serverSessionId;
                }

                Client.Logger.LogWarning(
                    $"Session '{localSessionId}' ended before registration completed; server session '{serverSessionId}' may be left open (see README backend backlog)");
                return null;
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Failed to register session with server, continuing locally: {ex.Message}");
                return null;
            }
            finally
            {
                _registrationInFlight = false;
            }
        }

        private SessionStartRequest BuildSessionStartRequest(FlockSessionSnapshot snapshot)
        {
            return new SessionStartRequest
            {
                // Snapshots persisted by older SDK versions carry no player id.
                PlayerId = snapshot.PlayerId ?? Client.CurrentPlayerId ?? FlockConstant.DummyUserID,
                Platform = snapshot.DeviceInfo?.Platform,
                DeviceType = snapshot.DeviceInfo?.DeviceType,
                GameVersionId = Client.GameVersionId,
                StartedAt = snapshot.StartTimeUtc.ToString("o")
            };
        }

        // Throws on failure; shared by live registration and spool recovery.
        private async Task<string> PostSessionStartAsync(SessionStartRequest request, CancellationToken cancellationToken)
        {
            SessionStartResponse response = await ExecuteAsync(
                () => FlockHttpClient.PostAsync<SessionStartResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.AnalyticsSessions}",
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Start session", cancellationToken);

            if (response == null || string.IsNullOrEmpty(response.SessionId))
                throw new FlockNetworkException("The server's answer to the session registration has no session id") { Hint = FlockErrorHints.NotFlocksAnswer };

            return response.SessionId;
        }

        public async Task EndSessionAsync(CancellationToken cancellationToken = default)
        {
            if (_session == null || !_session.IsActive)
            {
                Client.Logger.LogWarning("No active session to end");
                return;
            }

            FlockSessionSnapshot snapshot = _session.End(FlockSessionEndReason.Manual);
            if (snapshot != null)
                await DeliverSessionEndAsync(snapshot, cancellationToken);
        }

        public void RecordScreenView(string screenName)
        {
            if (!ConsentGiven())
                return;

            if (_session == null || !_session.IsActive)
                return;

            _session.RecordScreenView(screenName);

            Client.Logger.LogDebug($"Screen view recorded: {screenName}");
        }

        /// <summary>The longest event name the server can store; a longer one fails every event sent with it.</summary>
        public const int MaxEventNameLength = 200;

        /// <summary>The longest event category the server can store; a longer one fails every event sent with it.</summary>
        public const int MaxEventCategoryLength = 100;

        /// <summary>Reserved: the server records this event itself when a session starts.</summary>
        public const string ReservedSessionStartedEvent = "session_started";

        // The old diagnostics names wrote to Diagnostics while reading like gameplay analytics.
        internal const string ObsoleteDiagnosticsMessage =
            "It writes to Diagnostics, never to the Game Metrics dashboards (a gameplay event is TrackEvent). Renamed to ";

        public bool TrackEvent(
            string eventName,
            Dictionary<string, object> properties = null,
            string eventCategory = null)
        {
            if (!ConsentGiven())
                return false;

            string refusal = WhyEventCannotBeRecorded(eventName, eventCategory);
            if (refusal != null)
            {
                Client.Logger.LogWarning($"Track event refused: {refusal}");
                return false;
            }

            // Written to JSON before this returns, so a dictionary the game changes afterwards is not what is sent.
            AnalyticsEventRequest request = new AnalyticsEventRequest
            {
                PlayerId = Client.CurrentPlayerId ?? FlockConstant.DummyUserID,
                EventName = eventName,
                EventCategory = eventCategory,
                SessionId = CurrentSessionId,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Properties = properties ?? new Dictionary<string, object>()
            };

            try
            {
                EnsureSerializable(request, eventName);
            }
            catch (FlockValidationException ex)
            {
                Client.Logger.LogWarning($"Track event refused: {ex.Message}");
                return false;
            }

            if (_eventCache?.Enqueue(request) != null)
            {
                Client.Logger.LogDebug($"Event queued: {eventName}");
                return true;
            }

            // Not queued; with nobody signed in the server would refuse it, so it is dropped.
            if (string.IsNullOrEmpty(Client.CurrentPlayerId))
            {
                if (_eventCache != null)
                    Client.Logger.LogWarning($"Event '{eventName}' dropped: the event queue could not save it, and with nobody signed in it cannot be sent.");
                else
                    Client.Logger.LogDebug($"Event '{eventName}' dropped: nobody is signed in and the event queue is off (CacheFailedEvents).");
                return false;
            }
            SendEventOnceInBackground(request);
            return true;
        }

        // Why the server would store an event wrongly or fail its whole batch; null when it can be recorded.
        private static string WhyEventCannotBeRecorded(string eventName, string eventCategory)
        {
            if (string.IsNullOrWhiteSpace(eventName))
                return "an event needs a name.";
            if (eventName.Length > MaxEventNameLength || (eventCategory?.Length ?? 0) > MaxEventCategoryLength)
                return $"its name has {eventName.Length} characters and its category {eventCategory?.Length ?? 0}, and the server stores at most {MaxEventNameLength} and {MaxEventCategoryLength}. Name: '{eventName.Substring(0, Math.Min(eventName.Length, MaxEventNameLength))}'.";
            if (string.Equals(eventName, ReservedSessionStartedEvent, StringComparison.Ordinal))
                return $"'{ReservedSessionStartedEvent}' is reserved; the server records it when a session starts.";
            return null;
        }

        // Sent once, never retried: an ambiguous failure may already be stored, and a second copy would count twice.
        private async void SendEventOnceInBackground(AnalyticsEventRequest request)
        {
            try
            {
                await PostEventsAsync(new List<AnalyticsEventRequest> { request }, false, CancellationToken.None).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Event '{request.EventName}' could not be queued or sent: {ex.Message}");
            }
        }
        public void LogDiagnosticException(
            Exception exception,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null)
        {
            if (exception == null)
                return;

            // A game's exception type may throw from its own getters; a report must not throw back at the game.
            LogDiagnosticException(FlockExceptionText.MessageOf(exception), FlockExceptionText.StackTraceOf(exception), errorData, extraData);
        }

        public void LogDiagnosticException(
            string message,
            string stackTrace,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null)
        {
            LogEventRequest request = BuildLogEvent(
                LogEventType.Exception,
                message: message,
                errorMessage: message,
                errorTraceback: stackTrace,
                errorTracebackLines: SplitStackTrace(stackTrace),
                errorData: errorData,
                extraData: extraData);

            EnqueueLog(request);
        }

        public void LogDiagnosticError(
            string message,
            string logicalExpression = null,
            string errorCode = null,
            string errorMessage = null,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null)
        {
            LogEventRequest request = BuildLogEvent(
                LogEventType.LogicError,
                message: message,
                logicalExpression: logicalExpression,
                errorCode: errorCode,
                errorMessage: errorMessage,
                errorData: errorData,
                extraData: extraData);

            EnqueueLog(request);
        }

        public void LogDiagnosticEvent(string message,
            Dictionary<string, object> extraData = null)
        {
            LogEventRequest request = BuildLogEvent(
                LogEventType.Debug,
                message: message,
                logicalExpression: null,
                errorCode: null,
                errorMessage: null,
                errorData: null,
                extraData: extraData);

            EnqueueLog(request);
        }

        [Obsolete(ObsoleteDiagnosticsMessage + "LogDiagnosticException.")]
        public void LogException(Exception exception, Dictionary<string, object> errorData = null, Dictionary<string, object> extraData = null)
            => LogDiagnosticException(exception, errorData, extraData);

        [Obsolete(ObsoleteDiagnosticsMessage + "LogDiagnosticException.")]
        public void LogException(string message, string stackTrace, Dictionary<string, object> errorData = null, Dictionary<string, object> extraData = null)
            => LogDiagnosticException(message, stackTrace, errorData, extraData);

        [Obsolete(ObsoleteDiagnosticsMessage + "LogDiagnosticError.")]
        public void LogError(string message, string logicalExpression = null, string errorCode = null, string errorMessage = null,
            Dictionary<string, object> errorData = null, Dictionary<string, object> extraData = null)
            => LogDiagnosticError(message, logicalExpression, errorCode, errorMessage, errorData, extraData);

        [Obsolete(ObsoleteDiagnosticsMessage + "LogDiagnosticEvent.")]
        public void LogEvent(string message, Dictionary<string, object> extraData = null)
            => LogDiagnosticEvent(message, extraData);

        private LogEventRequest BuildLogEvent(
            LogEventType type,
            string message,
            string logicalExpression = null,
            string errorCode = null,
            string errorMessage = null,
            string errorTraceback = null,
            List<string> errorTracebackLines = null,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null)
        {
            return new LogEventRequest
            {
                Message = message ?? string.Empty,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Data = new LogEventDataSchema
                {
                    Type = type,
                    // The ID, as the request's X-Game-Version-ID header: the server stores the header's and falls back to this.
                    GameVersion = Client.InitConfig.GameVersionId,
                    LogicalExpression = logicalExpression,
                    ErrorMessage = errorMessage,
                    ErrorCode = errorCode,
                    ErrorData = errorData,
                    ErrorTraceback = errorTraceback,
                    ErrorTracebackLines = errorTracebackLines,
                    ExtraData = extraData
                }
            };
        }

        private static List<string> SplitStackTrace(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
                return null;

            string[] lines = stackTrace.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            return new List<string>(lines);
        }

        /// <summary>The most entries a launch sends at once past a queue that could not keep them; log events are metered, and a disk that refuses every write must not turn each entry into a request.</summary>
        internal const int MostSentPastAFailedQueue = 100;
        private int _sentPastAFailedQueue;

        // Queued, and delivered on the flush triggers (interval/pause/end/login). With no queue (CacheFailedEvents off, or a
        // launch holding no folder) it is sent at once, and a failure loses it. With a queue that could not keep it (a disk
        // refusing the write) it is sent at once too, up to MostSentPastAFailedQueue a launch, and dropped past that. Never throws.
        private void EnqueueLog(LogEventRequest request)
        {
            if (!ConsentGiven())
                return;

            if (!TryKeepWritable(request))
                return;
            if (_logEventCache == null)
            {
                SendLogEventAtOnce(request);
                return;
            }
            if (_logEventCache.Enqueue(request) != null)
            {
                Client.Logger.LogDebug("Log event queued");
                return;
            }
            if (_sentPastAFailedQueue >= MostSentPastAFailedQueue)
            {
                if (_sentPastAFailedQueue == MostSentPastAFailedQueue)
                    Client.Logger.LogWarning($"Log event '{request.Message}' dropped: the queue cannot keep entries and {MostSentPastAFailedQueue} were already sent past it this launch, the most a launch sends that way. Later ones this launch are dropped.");
                _sentPastAFailedQueue++;
                return;
            }
            _sentPastAFailedQueue++;
            SendLogEventAtOnce(request);
        }

        // A diagnostics call never throws into the game: data JSON cannot hold is left out, and the entry still goes.
        private bool TryKeepWritable(LogEventRequest request)
        {
            try
            {
                EnsureSerializable(request, "log_event");
                return true;
            }
            catch (FlockValidationException ex)
            {
                Client.Logger.LogWarning($"Log event '{request.Message}': its extra and error data were left out, since {ex.Message}");
                request.Data.ExtraData = null;
                request.Data.ErrorData = null;
            }
            try
            {
                EnsureSerializable(request, "log_event");
                return true;
            }
            catch (FlockValidationException ex)
            {
                Client.Logger.LogWarning($"Log event dropped: {ex.Message}");
                return false;
            }
        }

        private async void SendLogEventAtOnce(LogEventRequest request)
        {
            try
            {
                await SendLogEventsAsync(new List<LogEventRequest> { request }, CancellationToken.None).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
                Client.Logger.LogDebug("Log event sent (nothing queues it)");
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Log event '{request.Message}' could not be sent, and nothing queues it: {ex.Message}");
            }
        }

        private Task SendLogEventsAsync(IReadOnlyList<LogEventRequest> requests, CancellationToken cancellationToken)
        {
            // Server expects { "events": [...] }, not a bare array.
            LogEventsRequest payload = new LogEventsRequest
            {
                Events = requests as List<LogEventRequest> ?? new List<LogEventRequest>(requests)
            };

            return ExecuteWithoutResultAsync(
                () => FlockHttpClient.PostAsync(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.LogEvent}",
                    payload, Client.GetBaseHeaders(), cancellationToken),
                "Log events (batch)", cancellationToken);
        }
        // Catches non-serializable values (Unity objects, circular refs)
        private static void EnsureSerializable(object payload, string label)
        {
            try
            {
                JsonConvert.SerializeObject(payload);
            }
            catch (Exception ex)
            {
                throw new FlockValidationException($"'{label}' has parameters that cannot be written as JSON: {ex.Message}", ex) { Hint = "Pass plain values: numbers, strings, booleans, and lists and dictionaries of them." };
            }
        }
        private Task SendEventsAsync(
            IReadOnlyList<AnalyticsEventRequest> events,
            CancellationToken cancellationToken)
        {
            return PostEventsAsync(events, true, cancellationToken);
        }

        private Task PostEventsAsync(IReadOnlyList<AnalyticsEventRequest> events, bool idempotent, CancellationToken cancellationToken)
        {
            // Server expects { "events": [...] }, not a bare array.
            AnalyticsEventsRequest payload = new AnalyticsEventsRequest
            {
                Events = events as List<AnalyticsEventRequest> ?? new List<AnalyticsEventRequest>(events)
            };

            return ExecuteWithoutResultAsync(
                () => FlockHttpClient.PostAsync(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.AnalyticsEvents}",
                    payload, Client.GetBaseHeaders(), cancellationToken),
                "Track events", cancellationToken, idempotent);
        }

        // Drains every cache the provider owns. Each cache flushes to its own endpoint —
        // analytics events to /v1/analytics/events, log events to /v1/log_event — but the
        // trigger is unified so a single online-event empties both.
        // async void is intentional fire-and-forget; try/catch is non-negotiable because
        // any escaping exception would land at the SynchronizationContext root unhandled.
        // Off WebGL a flush resumes on any thread, as it touches no Unity API; WebGL has only the main thread (FlockWaiting).
        private async void FlushCacheInBackground()
        {
            try
            {
                CancellationToken token = _session?.SessionToken ?? CancellationToken.None;
                await FlushAllAsync(token, false).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Background flush failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Awaitable drain of everything queued (session ends, events, logs) to the server
        /// now, instead of waiting for the next flush trigger. The one real await in the
        /// tracking surface — resolves when the send attempts finish, a flush already
        /// running included. Transient failures keep records queued for a later flush; never throws.
        /// The result counts what this call sent and what the server refused for good.
        /// </summary>
        public async Task<FlockFlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushPass pass = await FlushAllAsync(cancellationToken, true).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            return pass.Result;
        }

        /// <summary>One pass over the queues: what it sent and refused, and which queues it drained at all.</summary>
        private struct FlushPass
        {
            public FlockFlushResult Result;
            public bool SessionEndsDrained;
            public bool EventsDrained;
            public bool LogEventsDrained;

            public bool AnythingDrained => SessionEndsDrained || EventsDrained || LogEventsDrained;
        }

        /// <summary>One queue's part of a pass: whether it was drained at all, and what that sent and refused.</summary>
        private struct QueueDrain
        {
            public bool Drained;
            public FlockFlushResult Result;
        }

        private async Task<FlushPass> FlushAllAsync(CancellationToken token, bool waitForRunningFlushes)
        {
            FlushPass pass = new FlushPass { Result = FlockFlushResult.Nothing };
            // Session ends first: rare, small, and the most important record — the quit
            // time budget must not be spent draining a large event backlog before them.
            QueueDrain ends = await TryFlush(_sessionEndCache, SendSessionEndsAsync, token, waitForRunningFlushes).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            pass.SessionEndsDrained = ends.Drained;
            pass.Result = pass.Result.Plus(ends.Result);
            // Gameplay events wait for a signed-in player: a pre-sign-in placeholder is refused (404) with its whole batch.
            if (Client.IsAuthenticated)
            {
                QueueDrain events = await TryFlush(_eventCache, SendEventsAsync, token, waitForRunningFlushes).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
                pass.EventsDrained = events.Drained;
                pass.Result = pass.Result.Plus(events.Result);
            }
            QueueDrain logs = await TryFlush(_logEventCache, SendLogEventsAsync, token, waitForRunningFlushes).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            pass.LogEventsDrained = logs.Drained;
            pass.Result = pass.Result.Plus(logs.Result);
            return pass;
        }

        private async Task<QueueDrain> TryFlush<T>(
            IEventCache<T> cache,
            Func<IReadOnlyList<T>, CancellationToken, Task> sender,
            CancellationToken token,
            bool waitForARunningFlush = false) where T : class
        {
            QueueDrain drain = new QueueDrain { Drained = false, Result = FlockFlushResult.Nothing };
            // Egress is consent-gated too - withdrawal stops transmission, not just collection. Gated here and
            // not in FlushAllAsync because session ends also flush directly. Nothing is deleted (decisions.md 5).
            if (!_hasConsent)
                return drain;

            if (cache == null || cache.PendingCount == 0)
                return drain;

            drain.Drained = true;
            try
            {
                drain.Result = await cache.FlushAsync(sender, token, waitForARunningFlush).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Client.Logger.LogDebug($"Cache flush deferred: {ex.Message}");
            }
            return drain;
        }

        public async Task RecordTransactionAsync(
            double amount,
            string currencyCode = "USD",
            string shopItemId = null,
            int quantity = 1,
            string transactionType = "purchase",
            string status = "completed",
            string paymentProvider = null,
            string externalTransactionId = null,
            string currencyId = null,
            CancellationToken cancellationToken = default)
        {
            AnalyticsTransactionRequest request = new AnalyticsTransactionRequest
            {
                Amount = amount,
                CurrencyCode = currencyCode,
                CurrencyId = currencyId,
                ShopItemId = shopItemId,
                Quantity = quantity,
                TransactionType = transactionType,
                Status = status,
                PaymentProvider = paymentProvider,
                ExternalTransactionId = externalTransactionId
            };
            
            await RecordTransactionAsync(request, cancellationToken);
        }

        /// <summary>
        /// Records a monetary transaction. <b>Requires an authenticated session</b> — unlike
        /// <see cref="TrackEvent"/> this is sent immediately and is not queued, so pre-auth
        /// calls will fail with a 401 from the server. Call after a successful
        /// <see cref="FlockAuthProvider"/> login.
        /// </summary>
        public async Task RecordTransactionAsync(
            AnalyticsTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            RequireAuth();

            if (request.Amount < 0)
                throw new FlockValidationException(Invariant($"The transaction amount is below zero: {request.Amount}")) { Hint = "Pass an amount of zero or more." };

            if (string.IsNullOrEmpty(request.PlayerId))
                request.PlayerId = Client.CurrentPlayerId ??FlockConstant.DummyUserID;
            if (string.IsNullOrEmpty(request.SessionId))
                request.SessionId = CurrentSessionId;
            if (string.IsNullOrEmpty(request.CreatedAt))
                request.CreatedAt = DateTime.UtcNow.ToString("o");

            await ExecuteWithoutResultAsync(
                () => FlockHttpClient.PostAsync(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.AnalyticsTransactions}",
                    request, Client.GetBaseHeaders(), cancellationToken),
                // Sent like a purchase: a transaction the server may already hold is never sent again (only 408 and 429 are),
                // or its revenue would count twice.
                "Record transaction", cancellationToken, idempotent: false);

            Client.Logger.LogDebug(Invariant($"Transaction recorded: {request.Amount} {request.CurrencyCode}"));
        }

        /// <summary>The longest the periodic flush waits, however long the server has been failing.</summary>
        internal const double MaxFlushWaitSeconds = 900.0;

        // The periodic flush waits longer each time it meets a failure the server answered, doubling up to MaxFlushWaitSeconds,
        // so an outage is not met with a request every interval from every player; a delivery, or an empty queue, brings it back.
        private readonly object _flushWaitLock = new object();
        private double _flushWaitSeconds;
        private double _nextIntervalFlushAt;

        internal double FlushWaitSecondsForTesting
        {
            get { lock (_flushWaitLock) return _flushWaitSeconds; }
        }

        private void HandleFlushInterval()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            lock (_flushWaitLock)
            {
                if (now < _nextIntervalFlushAt)
                    return;
            }
            FlushOnTheIntervalInBackground(now);
        }

        private async void FlushOnTheIntervalInBackground(double startedAt)
        {
            try
            {
                await FlushOnTheIntervalAsync(startedAt).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Background flush failed: {ex.Message}");
            }
        }

        internal int PendingAnalyticsEventCountForTesting => _eventCache?.PendingCount ?? 0;

        /// <summary>The periodic flush, then the wait before the next one, from what it met.</summary>
        internal async Task FlushOnTheIntervalAsync(double startedAt)
        {
            FlushPass pass = default;
            try
            {
                CancellationToken token = _session?.SessionToken ?? CancellationToken.None;
                pass = await FlushAllAsync(token, false).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            }
            catch (OperationCanceledException)
            {
            }
            UpdateFlushWait(startedAt, pass);
        }

        private void UpdateFlushWait(double startedAt, FlushPass pass)
        {
            // Only a queue this pass drained says anything about the server now: one it skipped (the events queue with nobody
            // signed in) keeps the ending of an older drain, which must not stretch the wait pass after pass.
            bool answered = (pass.SessionEndsDrained && Ended(_sessionEndCache, FlockDrainEnding.StoppedAnswered))
                || (pass.EventsDrained && Ended(_eventCache, FlockDrainEnding.StoppedAnswered))
                || (pass.LogEventsDrained && Ended(_logEventCache, FlockDrainEnding.StoppedAnswered));
            bool unanswered = (pass.SessionEndsDrained && Ended(_sessionEndCache, FlockDrainEnding.StoppedUnanswered))
                || (pass.EventsDrained && Ended(_eventCache, FlockDrainEnding.StoppedUnanswered))
                || (pass.LogEventsDrained && Ended(_logEventCache, FlockDrainEnding.StoppedUnanswered));
            bool stillQueued = (_sessionEndCache?.PendingCount ?? 0) + (_eventCache?.PendingCount ?? 0) + (_logEventCache?.PendingCount ?? 0) > 0;
            double interval = _config.EventBufferFlushIntervalSeconds;
            lock (_flushWaitLock)
            {
                if (!stillQueued || (pass.AnythingDrained && !answered && !unanswered))
                    _flushWaitSeconds = 0.0;
                // An unanswered failure spent nothing on the server, and a pass that drained nothing learnt nothing: the wait stays.
                else if (answered)
                    _flushWaitSeconds = Math.Min(Math.Max(_flushWaitSeconds, interval) * 2.0, Math.Max(MaxFlushWaitSeconds, interval));
                _nextIntervalFlushAt = startedAt + _flushWaitSeconds;
            }
        }

        private static bool Ended<T>(IEventCache<T> cache, FlockDrainEnding ending) where T : class
            => cache != null && cache.PendingCount > 0 && cache.LastDrainEnding == ending;

        private void HandleSessionPaused()
        {
            FlushCacheInBackground();
        }

        private async void HandleHeartbeat()
        {
            if (!HasActiveSession || string.IsNullOrEmpty(Client.CurrentPlayerId))
                return;

            if (_heartbeatInFlight)
                return;

            if (!IsServerReachable())
                return;

            FlushCacheInBackground();

            _heartbeatInFlight = true;
            CancellationToken token = _session.SessionToken;

            try
            {
                // Heal a failed startup registration; the heartbeat records no event, the Game Metrics dashboards are the game's.
                if (string.IsNullOrEmpty(_session.ServerSessionId))
                    await TryRegisterSessionAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Heartbeat session registration failed: {ex.Message}");
            }
            finally
            {
                _heartbeatInFlight = false;
            }
        }

        // Sole spool entry point — fired synchronously from inside FlockSession.End() for
        // every end path, including logout (ClearTokens -> Reset), before the live marker
        // is cleared. A logout end delivers on the next login's drain.
        private void HandleSessionEnded(FlockSessionSnapshot snapshot)
        {
            // The launch goes on after a session ends, and its crash marker with it; only a clean quit or shutdown clears it.
            _terminationTracker.NoteSessionEnded();

            if (_sessionEndCache == null)
                return;

            // A null handle means it never reached disk - say so, or End() clears the marker and the session is gone.
            string handle = _sessionEndCache.Enqueue(snapshot);
            if (string.IsNullOrEmpty(handle))
            {
                Client.Logger.LogError($"Session end {snapshot.SessionId} was NOT spooled — recovering it on the next launch instead.");
                _session.ReportEndSpoolFailed();
                return;
            }

            Client.Logger.LogDebug($"Session end spooled: {snapshot.SessionId}");
        }

        // Once per launch, before this launch's session starts: the crash marker and the open session of every launch
        // that ended, each reported once. A launch whose records could not be queued keeps its folder for a later launch.
        private async Task ReportWhatEndedLaunchesLeftAsync(CancellationToken cancellationToken)
        {
            // Handed over before the first await, so a second initialization reports none of them again.
            List<FlockEndedLaunch> endedLaunches = _launches.TakeEndedLaunches();
            foreach (FlockEndedLaunch ended in endedLaunches)
            {
                bool reported = EmitSurvivingTermination(ended.TerminationMarkerPath);
                reported &= await RecoverOrphanedSessionAsync(ended.SessionStatePath, cancellationToken);
                if (reported)
                    ended.DeleteEverything();
                else
                    ended.Dispose();
            }
        }

        // Returns false when the end could not reach the spool, so the ended launch is kept for a later one.
        private async Task<bool> RecoverOrphanedSessionAsync(string sessionStatePath, CancellationToken cancellationToken)
        {
            FlockSessionSnapshot orphaned = FlockSession.ReadOrphanedSession(sessionStatePath, Client.Logger);
            if (orphaned == null)
                return true;

            if (_sessionEndCache == null)
            {
                FlockSession.DeleteRecord(sessionStatePath, Client.Logger);
                await TrySendSessionEndAsync(orphaned, cancellationToken);
                return true;
            }

            // Spooled before the record is deleted — deleting first loses the session if the write fails.
            if (_sessionEndCache.Enqueue(orphaned) == null)
            {
                Client.Logger.LogWarning($"Could not spool the end of orphaned session '{orphaned.SessionId}'; keeping it for the next launch");
                return false;
            }
            FlockSession.DeleteRecord(sessionStatePath, Client.Logger);
            Client.Logger.LogDebug($"Orphaned session end spooled: {orphaned.SessionId}");
            return true;
        }

        // An ended launch's marker is a crash: reported under Diagnostics (type debug), false when kept for a later launch.
        private bool EmitSurvivingTermination(string markerPath)
        {
            FlockTerminationMarker marker = FlockTerminationTracker.ReadMarker(markerPath, Client.Logger);
            if (marker == null)
                return true;

            // No consent means the data must be discarded: drop the marker instead of retrying.
            if (!_hasConsent)
            {
                FlockTerminationTracker.DeleteMarker(markerPath, Client.Logger);
                return true;
            }

            string classification = FlockTerminationTracker.Classify(marker);
            Dictionary<string, object> details = new Dictionary<string, object>
            {
                { "classification", classification },
                { "last_alive_at", marker.LastAliveUtc.ToString("o") },
                { "unhandled_exception_count", marker.ExceptionCount }
            };
            // The ended launch's own versions, which a marker from before 1.86.0 does not hold.
            if (!string.IsNullOrEmpty(marker.AppVersion))
                details["app_version"] = marker.AppVersion;
            if (!string.IsNullOrEmpty(marker.SdkVersion))
                details["sdk_version"] = marker.SdkVersion;
            // The server's id when the session registered, else the local one; left out when no session was running.
            string previousSessionId = string.IsNullOrEmpty(marker.ServerSessionId) ? marker.SessionId : marker.ServerSessionId;
            if (!string.IsNullOrEmpty(previousSessionId))
                details["previous_session_id"] = previousSessionId;
            LogEventRequest report = BuildLogEvent(LogEventType.Debug, FlockTerminationTracker.EventName, extraData: details);

            // With no queue it is sent at once, as every log event is then, and the marker goes after it was handed over.
            if (_logEventCache == null)
            {
                SendLogEventAtOnce(report);
                FlockTerminationTracker.DeleteMarker(markerPath, Client.Logger);
                return true;
            }
            string handle = _logEventCache.Enqueue(report);
            if (handle != null)
            {
                // Deleted only after the durable enqueue — a failed write retries next launch.
                FlockTerminationTracker.DeleteMarker(markerPath, Client.Logger);
                string during = string.IsNullOrEmpty(marker.SessionId) ? "with no session running" : $"during session {marker.SessionId}";
                Client.Logger.LogInfo($"Previous run terminated dirty ({classification}) {during}; app_termination queued under Diagnostics");
                return true;
            }

            Client.Logger.LogWarning("app_termination enqueue failed; marker kept for retry next launch");
            return false;
        }

        // Quit path: the end is already spooled by HandleSessionEnded. Best-effort delivery
        // within the timeout; the next launch drains whatever didn't finish.
        private async void HandleQuitFlush(FlockSessionSnapshot snapshot)
        {
            try
            {
                using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    await FlushAllAsync(cts.Token, false).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);

                    if (_sessionEndCache == null)
                        await TrySendSessionEndAsync(snapshot, cts.Token).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"End session on quit failed: {ex.Message}");
            }
        }

        private async void HandleSessionTimedOut(FlockSessionSnapshot snapshot)
        {
            try
            {
                await DeliverSessionEndAsync(snapshot, CancellationToken.None);
                await StartSessionAsync();
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Session timeout rotation failed: {ex.Message}");
            }
        }

        // Awaited delivery attempt for an end that HandleSessionEnded already spooled.
        // Falls back to a one-shot send when the spool is unavailable.
        private async Task DeliverSessionEndAsync(FlockSessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (snapshot == null)
                return;

            if (_sessionEndCache == null)
            {
                await TrySendSessionEndAsync(snapshot, cancellationToken);
                return;
            }

            await FlushSessionEndsAsync(cancellationToken);
        }

        private Task FlushSessionEndsAsync(CancellationToken cancellationToken)
        {
            return TryFlush(_sessionEndCache, SendSessionEndsAsync, cancellationToken);
        }

        // Spool sender. A record without a server id was never registered — register it
        // first (the POST accepts a historical started_at) and rewrite the spooled file so
        // a retry can't register twice. Permanent rejections are logged and skipped so the
        // batch can clear; transient failures propagate so the cache defers the batch.
        private async Task SendSessionEndsAsync(
            IReadOnlyList<FlockSessionSnapshot> batch,
            CancellationToken cancellationToken)
        {
            HashSet<string> deliveredSessionIds = new HashSet<string>();

            foreach (FlockSessionSnapshot snapshot in batch)
            {
                // A quit and a crash recovery can both spool the same session. The batch is
                // ordered oldest-first and the older record is the accurate one (the quit
                // record precedes its heartbeat-stale recovery copy), so later ones are skipped.
                if (!deliveredSessionIds.Add(snapshot.SessionId))
                {
                    Client.Logger.LogDebug($"Skipping duplicate spooled end: {snapshot.SessionId}");
                    continue;
                }

                try
                {
                    string serverSessionId = snapshot.ServerSessionId;
                    if (string.IsNullOrEmpty(serverSessionId))
                    {
                        serverSessionId = await PostSessionStartAsync(
                            BuildSessionStartRequest(snapshot), cancellationToken).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);

                        string localSessionId = snapshot.SessionId;
                        _sessionEndCache.Rewrite(
                            record => record.SessionId == localSessionId && string.IsNullOrEmpty(record.ServerSessionId),
                            record => record.ServerSessionId = serverSessionId);

                        Client.Logger.LogInfo($"Spooled session registered with server: {localSessionId} -> {serverSessionId}");
                    }

                    await PatchSessionEndAsync(serverSessionId, snapshot, cancellationToken).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (FlockValidationException ex)
                {
                    Client.Logger.LogWarning($"Session end for '{snapshot.SessionId}' dropped (validation): {ex.Message}");
                }
                catch (FlockNetworkException ex) when (FlockNetworkException.IsPermanentStatus(ex.StatusCode))
                {
                    Client.Logger.LogWarning($"Session end for '{snapshot.SessionId}' rejected by server (HTTP {ex.StatusCode}), dropping");
                }
            }
        }

        // One-shot best-effort send, used only when the spool is unavailable. Never throws.
        private async Task TrySendSessionEndAsync(
            FlockSessionSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            // Sends one end directly, bypassing the caches, so it needs its own consent gate.
            if (!_hasConsent)
                return;

            string sessionId = snapshot.ServerSessionId ?? snapshot.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                Client.Logger.LogWarning("Cannot end session: no session ID available");
                return;
            }

            try
            {
                await PatchSessionEndAsync(sessionId, snapshot, cancellationToken);
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Failed to end session on server: {ex.Message}");
            }
        }

        // Throws on failure; shared by the spool sender and the one-shot path.
        private async Task PatchSessionEndAsync(
            string sessionId,
            FlockSessionSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            SessionEndRequest request = new SessionEndRequest
            {
                DurationSeconds = (int)snapshot.DurationSeconds,
                ScreensViewed = snapshot.ScreensViewed,
                IsBounce = snapshot.IsBounce,
                EndedAt = (snapshot.EndTimeUtc ?? DateTime.UtcNow).ToString("o")
            };

            await ExecuteWithoutResultAsync(
                () => FlockHttpClient.PatchAsync(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.AnalyticsSessionById(sessionId)}",
                    request, Client.GetBaseHeaders(), cancellationToken),
                "End session", cancellationToken).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);

            Client.Logger.LogInfo($"Session ended on server: {sessionId}");
        }

        private void RequireAuth()
        {
            if (!Client.IsAuthenticated)
                Client.Logger.LogError("Player must be authenticated for analytics");
        }
    }
}
