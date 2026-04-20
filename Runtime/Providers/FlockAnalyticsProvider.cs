using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Exceptions;
using Flock.Http;
using Flock.Interfaces;
using Flock.Models;
using UnityEngine;

namespace Flock.Providers
{
    public class FlockAnalyticsProvider : FlockProviderBase ,IAnalyticProvider
    {
        private readonly FlockAnalyticsConfig _config;
        private FlockSession _session;
        private bool _initialized;
        private string _currentPlayerId;
        private bool _heartbeatInFlight;

        public FlockAnalyticsProvider(FlockClient client) : base(client)
        {
            _config = client.InitConfig.AnalyticsConfig;
        }

        public string CurrentSessionId => _session?.ServerSessionId;
        private bool HasActiveSession => _session?.IsActive ?? false;
        public FlockSessionSnapshot CurrentSnapshot => _session?.IsActive == true ? _session.TakeSnapshot() : null;

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (!_config.Enabled)
                return;

            string newPlayerId = Client.CurrentPlayerId;

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
            _session.OnSessionTimedOut += HandleSessionTimedOut;
            _session.OnSessionEnding += HandleSessionEnding;

            FlockSessionSnapshot crashed = _session.RecoverCrashedSession();
            if (crashed != null)
                await SendEndSessionAsync(crashed, cancellationToken);

            if (_config.AutoStartSession)
                await StartSessionAsync(cancellationToken);
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
                PlayerId = Client.CurrentPlayerId,
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
                        $"{Client.GetApiUrl()}/v1/analytics/sessions",
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

        public async Task TrackEventAsync(
            string eventName,
            string eventCategory = null,
            Dictionary<string, object> parameters = null,
            CancellationToken cancellationToken = default)
        {
            RequireAuth();
            RequireNotEmpty(eventName, "Event name");

            AnalyticsEventRequest request = new AnalyticsEventRequest
            {
                PlayerId = Client.CurrentPlayerId,
                EventName = eventName,
                EventCategory = eventCategory,
                SessionId = CurrentSessionId,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Properties = parameters ?? new Dictionary<string, object>()
            };

            await ExecuteAsync(
                () => FlockHttpClient.PostAsync<Dictionary<string, object>>(
                    $"{Client.GetApiUrl()}/v1/analytics/events/single",
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Track event", cancellationToken);

            Client.Logger.LogDebug($"Event tracked: {eventName}");
        }

        public async Task TrackEventsAsync(
            List<AnalyticsEventRequest> events,
            CancellationToken cancellationToken = default)
        {
            RequireAuth();

            if (events == null || events.Count == 0)
                return;

            foreach (AnalyticsEventRequest evt in events)
            {
                if (string.IsNullOrEmpty(evt.PlayerId))
                    evt.PlayerId = Client.CurrentPlayerId;
                if (string.IsNullOrEmpty(evt.SessionId))
                    evt.SessionId = CurrentSessionId;
                if (string.IsNullOrEmpty(evt.Timestamp))
                    evt.Timestamp = DateTime.UtcNow.ToString("o");
            }

            AnalyticsEventsRequest request = new AnalyticsEventsRequest { Events = events };

            await ExecuteAsync(
                () => FlockHttpClient.PostAsync<Dictionary<string, object>>(
                    $"{Client.GetApiUrl()}/v1/analytics/events",
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Track events batch", cancellationToken);

            Client.Logger.LogDebug($"Batch events tracked: {events.Count} events");
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

        public async Task RecordTransactionAsync(
            AnalyticsTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            RequireAuth();

            if (request.Amount <= 0)
                throw new FlockValidationException($"Transaction amount must be greater than zero, got: {request.Amount}");

            if (string.IsNullOrEmpty(request.PlayerId))
                request.PlayerId = Client.CurrentPlayerId;
            if (string.IsNullOrEmpty(request.SessionId))
                request.SessionId = CurrentSessionId;
            if (string.IsNullOrEmpty(request.CreatedAt))
                request.CreatedAt = DateTime.UtcNow.ToString("o");

            await ExecuteAsync(
                () => FlockHttpClient.PostAsync<Dictionary<string, object>>(
                    $"{Client.GetApiUrl()}/v1/analytics/transactions",
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Record transaction", cancellationToken);

            Client.Logger.LogDebug($"Transaction recorded: {request.Amount} {request.CurrencyCode}");
        }

        private async void HandleHeartbeat()
        {
            if (!HasActiveSession || string.IsNullOrEmpty(Client.CurrentPlayerId))
                return;

            if (_heartbeatInFlight)
                return;

            if (Application.internetReachability == NetworkReachability.NotReachable)
                return;

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
                        $"{Client.GetApiUrl()}/v1/analytics/sessions/{sessionId}",
                        request, Client.GetBaseHeaders(), cancellationToken),
                    "End session", cancellationToken);

                Client.Logger.LogInfo($"Session ended on server: {sessionId}{(snapshot.WasCrash ? " (recovered from crash)" : "")}");
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Failed to end session on server: {ex.Message}");
            }
        }

        private void RequireAuth()
        {
            if (!Client.IsAuthenticated)
                throw new FlockAuthException("Analytics requires authentication. Call a login method first.");
        }
    }
}
