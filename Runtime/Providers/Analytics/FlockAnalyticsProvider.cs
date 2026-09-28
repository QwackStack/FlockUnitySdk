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
using Flock.Models;
using Newtonsoft.Json;
using UnityEngine;

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
        private bool _exceptionHookInstalled;
        private string _currentPlayerId;
        private bool _heartbeatInFlight;
        private bool _registrationInFlight;
        private readonly FlockConsentStore _consentStore = new FlockConsentStore();
        private bool _hasConsent;

        public FlockAnalyticsProvider(FlockClient client) : base(client)
        {
            _config = client.InitConfig.AnalyticsConfig;
            _launches = client.AnalyticsLaunches;
            _eventCache = TryCreateCache<AnalyticsEventRequest>(client, FlockAnalyticsLaunches.AnalyticsEventsQueueName, _config.CacheFailedEvents);
            _logEventCache = TryCreateCache<LogEventRequest>(client, FlockAnalyticsLaunches.LogEventsQueueName, _config.CacheFailedEvents);
            _sessionEndCache = TryCreateCache<FlockSessionSnapshot>(client, FlockAnalyticsLaunches.SessionEndsQueueName, _config.PersistSessionOnDisk);

            // A previously-recorded decision always wins; otherwise fall back to the
            // config's default policy (opt-out unless RequireExplicitConsent is on).
            bool? storedConsent = _consentStore.Load();
            _hasConsent = storedConsent ?? !_config.RequireExplicitConsent;

            // Enabled is resolved once here so the tracker itself stays platform-agnostic and testable:
            // needs disk persistence, and Editor/WebGL are excluded (Stop isn't a death; WebGL has no reliable lifecycle).
            bool terminationTrackingEnabled = _config.PersistSessionOnDisk
                && !Application.isEditor
                && Application.platform != RuntimePlatform.WebGLPlayer;
            _terminationTracker = new FlockTerminationTracker(client.Logger, terminationTrackingEnabled, _launches.TerminationMarkerPath);
            if (_config.PersistSessionOnDisk && Application.platform == RuntimePlatform.WebGLPlayer)
                client.Logger.LogWarning("Termination tracking is disabled on WebGL (no reliable quit/pause lifecycle)");
        }

        private IEventCache<T> TryCreateCache<T>(FlockClient client, string subfolder, bool enabled) where T : class
        {
            if (!enabled)
                return null;

            try
            {
                return new FlockEventCache<T>(
                    _launches.Folder,
                    subfolder,
                    _config.MaxCachedEvents, _config.CacheFlushBatchSize, client.Logger);
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

                if (Client.IsAuthenticated && _config.AutoStartSession && (_session == null || !_session.IsActive))
                    ResumeSessionOnConsentGranted();

                return;
            }

            Client.Logger.LogInfo("Analytics consent revoked");

            if (_session != null && _session.IsActive)
                _session.Discard();

            // Discard deliberately skips OnSessionEnded, so the tombstone needs its own stop.
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
            _session.OnHeartbeat -= _terminationTracker.HandleHeartbeat;
            _session.OnHeartbeat += _terminationTracker.HandleHeartbeat;

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

            InstallGlobalExceptionHook();

            FlushCacheInBackground();
        }

        // Logout funnel (ClearTokens): session handlers were detached — force a full re-wire on next InitializeAsync.
        internal void HandleAuthCleared()
        {
            _initialized = false;
        }

        // Subscribed once per provider lifetime; FlockBehaviour.OnException fires for
        // every Unity LogType.Exception, so unhandled errors flow into log_event without
        // any caller-side wiring. Safe to call repeatedly — re-init won't double-hook.
        private void InstallGlobalExceptionHook()
        {
            if (_exceptionHookInstalled)
                return;

            FlockBehaviour behaviour = FlockBehaviour.Instance;
            if (behaviour == null)
                return;

            behaviour.OnException += HandleGlobalException;
            _exceptionHookInstalled = true;
        }

        // Mirror of InstallGlobalExceptionHook — must run on Shutdown so a re-init
        // doesn't leave this provider bound to the DontDestroyOnLoad FlockBehaviour
        // (stale handler → duplicate sends + a leaked FlockClient).
        internal void UninstallGlobalExceptionHook()
        {
            if (!_exceptionHookInstalled)
                return;

            if (FlockBehaviour.IsAvailable)
                FlockBehaviour.Instance.OnException -= HandleGlobalException;

            _exceptionHookInstalled = false;
        }

        private void HandleGlobalException(string message, string stackTrace)
        {
            try
            {
                LogDiagnosticException(message, stackTrace);
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Global exception capture failed: {ex.Message}");
            }
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
            _terminationTracker.BeginTracking(localId);

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
                throw new FlockNetworkException("Session registration returned no session id");

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

            LogDiagnosticException(exception.Message, exception.StackTrace, errorData, extraData);
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
                    GameVersion = Client.InitConfig.GameVersion,
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

        // Enqueue only — delivery happens on the flush triggers (interval/pause/end/login).
        private void EnqueueLog(LogEventRequest request)
        {
            if (!ConsentGiven())
                return;

            EnsureSerializable(request, "log_event");
            _logEventCache?.Enqueue(request);
            Client.Logger.LogDebug("Log event queued");
        }

        // Kept as an escape hatch for ad-hoc, critical sends that must bypass the buffer.
        // Public APIs go through the cache; the buffered flush uses the batch endpoint.
        private Task SendLogEventAsync(LogEventRequest request, CancellationToken cancellationToken)
        {
            return ExecuteWithoutResultAsync(
                () => FlockHttpClient.PostAsync(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.LogEventSingle}",
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Log event (single)", cancellationToken);
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
                throw new FlockValidationException($"'{label}' has non-serializable parameters: {ex.Message}", ex);
            }
        }
        // Kept as an escape hatch for ad-hoc, critical sends that must bypass the buffer.
        // Public APIs go through the cache; the buffered flush uses the batch endpoint.
        private Task SendEventAsync(
            AnalyticsEventRequest eve,
            CancellationToken cancellationToken)
        {
            return ExecuteWithoutResultAsync(
                () => FlockHttpClient.PostAsync(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.AnalyticsEventsSingle}",
                    eve, Client.GetBaseHeaders(), cancellationToken),
                "Track single event", cancellationToken);
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
                await FlushAllAsync(token).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
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
        /// tracking surface — resolves when the send attempts finish. Transient failures
        /// keep records queued for a later flush; never throws.
        /// </summary>
        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return FlushAllAsync(cancellationToken);
        }

        private async Task FlushAllAsync(CancellationToken token)
        {
            // Session ends first: rare, small, and the most important record — the quit
            // time budget must not be spent draining a large event backlog before them.
            await TryFlush(_sessionEndCache, SendSessionEndsAsync, token).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            // Gameplay events wait for a signed-in player: a pre-sign-in placeholder is refused (404) with its whole batch.
            if (Client.IsAuthenticated)
                await TryFlush(_eventCache, SendEventsAsync, token).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            await TryFlush(_logEventCache, SendLogEventsAsync, token).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
        }

        private async Task TryFlush<T>(
            IEventCache<T> cache,
            Func<IReadOnlyList<T>, CancellationToken, Task> sender,
            CancellationToken token) where T : class
        {
            // Egress is consent-gated too - withdrawal stops transmission, not just collection. Gated here and
            // not in FlushAllAsync because session ends also flush directly. Nothing is deleted (decisions.md 5).
            if (!_hasConsent)
                return;

            if (cache == null || cache.PendingCount == 0)
                return;

            try
            {
                await cache.FlushAsync(sender, token).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Client.Logger.LogDebug($"Cache flush deferred: {ex.Message}");
            }
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
                throw new FlockValidationException($"Transaction amount must be non-negative, got: {request.Amount}");

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
                "Record transaction", cancellationToken);

            Client.Logger.LogDebug($"Transaction recorded: {request.Amount} {request.CurrencyCode}");
        }

        private void HandleFlushInterval()
        {
            FlushCacheInBackground();
        }

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
            // Every clean end path lands here — the tombstone must go before any early return.
            _terminationTracker.StopTracking();

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

            // No queue means the report could never be delivered; no consent means the data
            // must be discarded. Either way, drop the marker instead of retrying.
            if (_logEventCache == null || !_hasConsent)
            {
                FlockTerminationTracker.DeleteMarker(markerPath, Client.Logger);
                return true;
            }

            string classification = FlockTerminationTracker.Classify(marker);
            LogEventRequest report = BuildLogEvent(
                LogEventType.Debug,
                FlockTerminationTracker.EventName,
                extraData: new Dictionary<string, object>
                {
                    { "previous_session_id", marker.SessionId },
                    { "classification", classification },
                    { "last_alive_at", marker.LastAliveUtc.ToString("o") },
                    { "unhandled_exception_count", marker.ExceptionCount },
                    { "app_version", Application.version },
                    { "sdk_version", FlockSdkVersion.Current }
                });

            string handle = _logEventCache.Enqueue(report);
            if (handle != null)
            {
                // Deleted only after the durable enqueue — a failed write retries next launch.
                FlockTerminationTracker.DeleteMarker(markerPath, Client.Logger);
                Client.Logger.LogInfo($"Previous run terminated dirty ({classification}); app_termination queued under Diagnostics for session {marker.SessionId}");
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
                    await FlushAllAsync(cts.Token).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);

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
