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
        private FlockSession _session;
        private bool _initialized;
        private bool _exceptionHookInstalled;
        private string _currentPlayerId;
        private bool _heartbeatInFlight;

        public FlockAnalyticsProvider(FlockClient client) : base(client)
        {
            _config = client.InitConfig.AnalyticsConfig;
            _eventCache = TryCreateCache<AnalyticsEventRequest>(client, "analytics_events");
            _logEventCache = TryCreateCache<LogEventRequest>(client, "log_events");
        }

        private IEventCache<T> TryCreateCache<T>(FlockClient client, string subfolder) where T : class
        {
            if (!_config.CacheFailedEvents)
                return null;

            try
            {
                return new FlockEventCache<T>(
                    Path.Combine(Application.persistentDataPath, "Flock"),
                    subfolder,
                    _config.MaxCachedEvents, _config.CacheFlushBatchSize, client.Logger);
            }
            catch (Exception ex)
            {
                client.Logger.LogWarning($"Event cache '{subfolder}' unavailable, falling back to direct send: {ex.Message}");
                return null;
            }
        }

        public string CurrentSessionId => _session?.ServerSessionId;
        private bool HasActiveSession => _session?.IsActive ?? false;
        public FlockSessionSnapshot CurrentSnapshot => _session?.IsActive == true ? _session.TakeSnapshot() : null;

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
                        FlockSessionSnapshot oldSnapshot = _session.End();
                        if (oldSnapshot != null)
                            await SendEndSessionAsync(oldSnapshot, cancellationToken);
                    }

                    _session.Reset();
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

            _session.OnHeartbeat += HandleHeartbeat;
            _session.OnFlushInterval += HandleFlushInterval;
            _session.OnSessionPaused += HandleSessionPaused;
            _session.OnSessionTimedOut += HandleSessionTimedOut;
            _session.OnSessionEnding += HandleSessionEnding;

            FlockSessionSnapshot orphaned = _session.RecoverOrphanedSession();
            if (orphaned != null)
                await SendEndSessionAsync(orphaned, cancellationToken);

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

        // Subscribed once per provider lifetime; FlockBehaviour.OnException fires for
        // every Unity LogType.Exception, so unhandled errors flow into log_event without
        // any caller-side wiring. Idempotent — re-init of the provider won't double-hook.
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

        private async void HandleGlobalException(string message, string stackTrace)
        {
            try
            {
                await LogExceptionAsync(message, stackTrace).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Global exception capture failed: {ex.Message}");
            }
        }

        private async Task<string> StartSessionAsync(CancellationToken cancellationToken = default)
        {
            RequireAuth();

            if (_session == null)
            {
                Client.Logger.LogWarning("Analytics is disabled, cannot start session");
                return null;
            }

            string localId = _session.Start();

            SessionStartRequest request = new SessionStartRequest
            {
                PlayerId = Client.CurrentPlayerId ?? FlockConstant.DummyUserID,
                Platform = _session.DeviceInfo?.Platform,
                DeviceType = _session.DeviceInfo?.DeviceType,
                GameVersionId = Client.GameVersionId,
                SdkVersion = FlockSdkVersion.Current,
                StartedAt = _session.StartTimeUtc.ToString("o")
            };

            try
            {
                SessionStartResponse response = await ExecuteAsync(
                    () => FlockHttpClient.PostAsync<SessionStartResponse>(
                        $"{Client.GetVersionedApiUrl()}/analytics/sessions",
                        request, Client.GetBaseHeaders(), cancellationToken),
                    "Start session", cancellationToken);

                _session.ServerSessionId = response.SessionId;

                Client.Logger.LogInfo($"Session registered with server: {response.SessionId}");

                return response.SessionId;
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Failed to register session with server, continuing locally: {ex.Message}");
                return localId;
            }
        }

        public async Task EndSessionAsync(CancellationToken cancellationToken = default)
        {
            if (_session == null || !_session.IsActive)
            {
                Client.Logger.LogWarning("No active session to end");
                return;
            }

            FlockSessionSnapshot snapshot = _session.End();
            if (snapshot != null)
                await SendEndSessionAsync(snapshot, cancellationToken);
        }

        public void RecordScreenView(string screenName)
        {
            if (_session == null || !_session.IsActive)
                return;

            _session.RecordScreenView(screenName);

            Client.Logger.LogDebug($"Screen view recorded: {screenName}");
        }

        // Not exposed to user until log_event/analytic clean up
        /// <summary>
        /// Tracks a single analytics event. Safe to call before login — the event is
        /// enqueued to the on-disk cache and drains automatically after authentication
        /// (retagged from the unauthenticated placeholder to the real <c>PlayerId</c>) and
        /// on interval / pause / session end thereafter. A console warning is logged on
        /// pre-auth calls but this method never throws for auth reasons.
        /// </summary>
        private Task TrackEventAsync(
            string eventName,
            string eventCategory = null,
            Dictionary<string, object> parameters = null,
            CancellationToken cancellationToken = default)
        {
            RequireAuth();
            RequireNotEmpty(eventName, "Event name");

            AnalyticsEventRequest request = new AnalyticsEventRequest
            {
                PlayerId = Client.CurrentPlayerId ?? FlockConstant.DummyUserID,
                EventName = eventName,
                EventCategory = eventCategory,
                SessionId = CurrentSessionId,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Properties = parameters ?? new Dictionary<string, object>()
            };

            EnsureSerializable(request, eventName);
            _eventCache?.Enqueue(request);
            Client.Logger.LogDebug($"Event queued: {eventName}");

            return Task.CompletedTask;
        }
        public Task LogExceptionAsync(
            Exception exception,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null,
            CancellationToken cancellationToken = default)
        {
            if (exception == null)
                return Task.CompletedTask;

            return LogExceptionAsync(exception.Message, exception.StackTrace, errorData, extraData, cancellationToken);
        }

        public Task LogExceptionAsync(
            string message,
            string stackTrace,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null,
            CancellationToken cancellationToken = default)
        {
            LogEventRequest request = BuildLogEvent(
                LogEventType.Exception,
                message: message,
                errorMessage: message,
                errorTraceback: stackTrace,
                errorTracebackLines: SplitStackTrace(stackTrace),
                errorData: errorData,
                extraData: extraData);

            return EnqueueAndSendLogAsync(request, cancellationToken);
        }

        public Task LogErrorAsync(
            string message,
            string logicalExpression = null,
            string errorCode = null,
            string errorMessage = null,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null,
            CancellationToken cancellationToken = default)
        {
            LogEventRequest request = BuildLogEvent(
                LogEventType.LogicError,
                message: message,
                logicalExpression: logicalExpression,
                errorCode: errorCode,
                errorMessage: errorMessage,
                errorData: errorData,
                extraData: extraData);

            return EnqueueAndSendLogAsync(request, cancellationToken);
        }

        public Task LogEventAsync(string message, 
            Dictionary<string, object> extraData = null, CancellationToken cancellationToken = default)
        {
            LogEventRequest request = BuildLogEvent(
                LogEventType.Debug,
                message: message,
                logicalExpression: null,
                errorCode: null,
                errorMessage: null,
                errorData: null,
                extraData: extraData);

            return EnqueueAndSendLogAsync(request, cancellationToken);
        }

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
                TsUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
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

        private Task EnqueueAndSendLogAsync(LogEventRequest request, CancellationToken cancellationToken)
        {
            EnsureSerializable(request, "log_event");
            _logEventCache?.Enqueue(request);
            Client.Logger.LogDebug("Log event queued");
            return Task.CompletedTask;
        }

        // Kept as an escape hatch for ad-hoc, critical sends that must bypass the buffer.
        // Public APIs go through the cache; the buffered flush uses the batch endpoint.
        private Task SendLogEventAsync(LogEventRequest request, CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                () => FlockHttpClient.PostAsync<Dictionary<string, object>>(
                    $"{Client.GetVersionedApiUrl()}/log_event/single",
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

            return ExecuteAsync(
                () => FlockHttpClient.PostAsync<Dictionary<string, object>>(
                    $"{Client.GetVersionedApiUrl()}/log_event",
                    payload, Client.GetBaseHeaders(), cancellationToken),
                "Log events (batch)", cancellationToken);
        }
        // Not exposed to user until log_event/analytic clean up
        // public async Task TrackEventsAsync(
        //     List<AnalyticsEventRequest> events,
        //     CancellationToken cancellationToken = default)
        // {
        //     RequireAuth();
        //
        //     if (events == null || events.Count == 0)
        //         return;
        //
        //     foreach (AnalyticsEventRequest evt in events)
        //     {
        //         if (string.IsNullOrEmpty(evt.PlayerId))
        //             evt.PlayerId = Client.CurrentPlayerId ?? FlockConstant.DummyUserID;
        //         if (string.IsNullOrEmpty(evt.SessionId))
        //             evt.SessionId = CurrentSessionId;
        //         if (string.IsNullOrEmpty(evt.Timestamp))
        //             evt.Timestamp = DateTime.UtcNow.ToString("o");
        //         if (evt.Properties == null)
        //             evt.Properties = new Dictionary<string, object>();
        //     }
        //
        //     EnsureSerializable(events, "events batch");
        //
        //     // Write-ahead: persist every event first, send live, delete the whole batch on success.
        //     List<string> handles = null;
        //     if (_eventCache != null)
        //     {
        //         handles = new List<string>(events.Count);
        //         foreach (AnalyticsEventRequest evt in events)
        //             handles.Add(_eventCache.Enqueue(evt));
        //     }
        //
        //     // Not authenticated yet — hold the batch in the cache for retag-and-flush after auth.
        //     if (!Client.IsAuthenticated)
        //     {
        //         Client.Logger.LogDebug($"Batch events queued (awaiting auth): {events.Count} events");
        //         return;
        //     }
        //
        //     try
        //     {
        //         await SendEventsAsync(events, cancellationToken).ConfigureAwait(false);
        //         Client.Logger.LogDebug($"Batch events tracked: {events.Count} events");
        //         RemoveHandles(handles);
        //         FlushCacheInBackground();
        //     }
        //     catch (OperationCanceledException)
        //     {
        //         throw;
        //     }
        //     catch (FlockValidationException)
        //     {
        //         RemoveHandles(handles);
        //         throw;
        //     }
        //     catch (FlockException ex) when (_eventCache != null)
        //     {
        //         Client.Logger.LogDebug($"Batch events queued for retry: {events.Count} events ({ex.Message})");
        //         FlushCacheInBackground();
        //     }
        // }

        private void RemoveHandles(List<string> handles)
        {
            if (handles == null || _eventCache == null)
                return;

            foreach (string handle in handles)
                _eventCache.Remove(handle);
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
            return ExecuteAsync(
                () => FlockHttpClient.PostAsync<Dictionary<string, object>>(
                    $"{Client.GetVersionedApiUrl()}/analytics/events/single",
                    eve, Client.GetBaseHeaders(), cancellationToken),
                "Track single event", cancellationToken);
        }
        private Task SendEventsAsync(
            IReadOnlyList<AnalyticsEventRequest> events,
            CancellationToken cancellationToken)
        {
            // Server expects { "events": [...] }, not a bare array.
            AnalyticsEventsRequest payload = new AnalyticsEventsRequest
            {
                Events = events as List<AnalyticsEventRequest> ?? new List<AnalyticsEventRequest>(events)
            };

            return ExecuteAsync(
                () => FlockHttpClient.PostAsync<Dictionary<string, object>>(
                    $"{Client.GetVersionedApiUrl()}/analytics/events",
                    payload, Client.GetBaseHeaders(), cancellationToken),
                "Track events", cancellationToken);
        }

        // Drains every cache the provider owns. Each cache flushes to its own endpoint —
        // analytics events to /v1/analytics/events, log events to /v1/log_event — but the
        // trigger is unified so a single online-event opportunistically empties both.
        // async void is intentional fire-and-forget; try/catch is non-negotiable because
        // any escaping exception would land at the SynchronizationContext root unhandled.
        // ConfigureAwait(false) because flush is pure I/O — no Unity APIs touched.
        private async void FlushCacheInBackground()
        {
            try
            {
                CancellationToken token = _session?.SessionToken ?? CancellationToken.None;
                await FlushAllAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Background flush failed: {ex.Message}");
            }
        }

        private async Task FlushAllAsync(CancellationToken token)
        {
            await TryFlush(_eventCache, SendEventsAsync, token).ConfigureAwait(false);
            await TryFlush(_logEventCache, SendLogEventsAsync, token).ConfigureAwait(false);
        }

        private async Task TryFlush<T>(
            IEventCache<T> cache,
            Func<IReadOnlyList<T>, CancellationToken, Task> sender,
            CancellationToken token) where T : class
        {
            if (cache == null || cache.PendingCount == 0)
                return;

            try
            {
                await cache.FlushAsync(sender, token).ConfigureAwait(false);
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
            Client.Logger.LogDebug("Tracking transactions is Not Supported");
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
        /// <see cref="TrackEventAsync"/> this is sent immediately and is not queued, so pre-auth
        /// calls will fail with a 401 from the server. Call after a successful
        /// <see cref="FlockAuthProvider"/> login.
        /// </summary>
        public async Task RecordTransactionAsync(
            AnalyticsTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            RequireAuth();

            if (request.Amount <= 0)
                throw new FlockValidationException($"Transaction amount must be greater than zero, got: {request.Amount}");

            if (string.IsNullOrEmpty(request.PlayerId))
                request.PlayerId = Client.CurrentPlayerId ??FlockConstant.DummyUserID;
            if (string.IsNullOrEmpty(request.SessionId))
                request.SessionId = CurrentSessionId;
            if (string.IsNullOrEmpty(request.CreatedAt))
                request.CreatedAt = DateTime.UtcNow.ToString("o");

            await ExecuteAsync(
                () => FlockHttpClient.PostAsync<Dictionary<string, object>>(
                    $"{Client.GetVersionedApiUrl()}/analytics/transactions",
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

            if (Application.internetReachability == NetworkReachability.NotReachable)
                return;

            FlushCacheInBackground();

            _heartbeatInFlight = true;
            CancellationToken token = _session.SessionToken;

            try
            {
                FlockSessionSnapshot snapshot = _session.TakeSnapshot();

                await TrackEventAsync(
                    "sdk_heartbeat",
                    "system",
                    new Dictionary<string, object>
                    {
                        { "duration_seconds", (int)snapshot.DurationSeconds },
                        { "screens_viewed", snapshot.ScreensViewed },
                        { "average_fps", snapshot.AverageFps },
                        { "pause_count", snapshot.PauseCount }
                    },
                    token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Heartbeat event failed: {ex.Message}");
            }
            finally
            {
                _heartbeatInFlight = false;
            }
        }

        private async void HandleSessionEnding(FlockSessionSnapshot snapshot)
        {
            try
            {
                // Best-effort: unreliable on quit since the app may exit before completion.
                // Anything not flushed stays on disk and drains on next launch.
                using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    await FlushAllAsync(cts.Token).ConfigureAwait(false);

                await SendEndSessionAsync(snapshot);
                _session?.ClearPersistedState();
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
                await SendEndSessionAsync(snapshot);
                await StartSessionAsync();
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Session timeout rotation failed: {ex.Message}");
            }
        }

        private async Task SendEndSessionAsync(
            FlockSessionSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            string sessionId = snapshot.ServerSessionId ?? snapshot.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                Client.Logger.LogWarning("Cannot end session: no session ID available");
                return;
            }

            SessionEndRequest request = new SessionEndRequest
            {
                DurationSeconds = (int)snapshot.DurationSeconds,
                ScreensViewed = snapshot.ScreensViewed,
                IsBounce = snapshot.IsBounce,
                EndedAt = (snapshot.EndTimeUtc ?? DateTime.UtcNow).ToString("o")
            };

            try
            {
                await ExecuteAsync(
                    () => FlockHttpClient.PatchAsync<Dictionary<string, object>>(
                        $"{Client.GetVersionedApiUrl()}/analytics/sessions/{sessionId}",
                        request, Client.GetBaseHeaders(), cancellationToken),
                    "End session", cancellationToken);

                Client.Logger.LogInfo($"Session ended on server: {sessionId}");
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Failed to end session on server: {ex.Message}");
            }
        }

        private void RequireAuth()
        {
            if (!Client.IsAuthenticated)
                Client.Logger.LogError("Player must be authenticated for analytics");
        }
    }
}
