using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using UnityEngine;

namespace Flock.Providers
{
    public class FlockAnalyticsProvider : FlockProviderBase
    {
        private readonly FlockAnalyticsConfig _config;
        private FlockSession _session;
        private bool _initialized;
        private string _currentPlayerId;
        private bool _heartbeatInFlight;

        public FlockAnalyticsProvider(FlockClient client) : base(client)
        {
            _config = client.InitConfig.Analytics;
        }

        public string CurrentSessionId => _session?.ServerSessionId;
        public bool HasActiveSession => _session?.IsActive ?? false;
        public FlockSessionSnapshot CurrentSnapshot => _session?.IsActive == true ? _session.TakeSnapshot() : null;

        internal async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (!_config.Enabled)
                return;

            var newPlayerId = Client.CurrentPlayerId;

            if (_initialized && _currentPlayerId != newPlayerId)
            {
                Client.Logger.LogInfo(new StringBuilder()
                    .Append("Player changed (").Append(_currentPlayerId)
                    .Append(" -> ").Append(newPlayerId)
                    .Append("), resetting analytics session")
                    .ToString());

                if (_session != null)
                {
                    if (_session.IsActive)
                    {
                        var oldSnapshot = _session.End();
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

            var crashed = _session.RecoverCrashedSession();
            if (crashed != null)
            {
                await SendEndSessionAsync(crashed, cancellationToken);
            }

            if (_config.AutoStartSession)
            {
                await StartSessionAsync(cancellationToken);
            }
        }

        public async Task<string> StartSessionAsync(CancellationToken cancellationToken = default)
        {
            RequireAuth();

            if (_session == null)
            {
                Client.Logger.LogWarning("Analytics is disabled, cannot start session");
                return null;
            }

            var localId = _session.Start();

            var request = new SessionStartRequest
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
                var response = await ExecuteAsync(
                    () => FlockHttpClient.PostAsync<SessionStartResponse>(
                        new StringBuilder().Append(Client.GetApiUrl())
                            .Append("/v1/analytics/sessions")
                            .ToString(),
                        request, Client.GetBaseHeaders(), cancellationToken),
                    "Start session", cancellationToken);

                _session.ServerSessionId = response.SessionId;

                Client.Logger.LogInfo(new StringBuilder()
                    .Append("Session registered with server: ").Append(response.SessionId)
                    .ToString());

                return response.SessionId;
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning(new StringBuilder()
                    .Append("Failed to register session with server, continuing locally: ")
                    .Append(ex.Message)
                    .ToString());
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

            var snapshot = _session.End();
            if (snapshot != null)
            {
                await SendEndSessionAsync(snapshot, cancellationToken);
            }
        }

        public void RecordScreenView(string screenName)
        {
            if (_session == null || !_session.IsActive)
                return;

            _session.RecordScreenView(screenName);

            Client.Logger.LogDebug(new StringBuilder()
                .Append("Screen view recorded: ").Append(screenName)
                .ToString());
        }

        public async Task TrackEventAsync(
            string eventName,
            string eventCategory = null,
            Dictionary<string, object> parameters = null,
            CancellationToken cancellationToken = default)
        {
            RequireAuth();
            RequireNotEmpty(eventName, "Event name");

            var request = new AnalyticsEventRequest
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
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/analytics/events/single")
                        .ToString(),
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Track event", cancellationToken);

            Client.Logger.LogDebug(new StringBuilder()
                .Append("Event tracked: ").Append(eventName).ToString());
        }

        public async Task TrackEventsAsync(
            List<AnalyticsEventRequest> events,
            CancellationToken cancellationToken = default)
        {
            RequireAuth();

            if (events == null || events.Count == 0)
                return;

            foreach (var evt in events)
            {
                if (string.IsNullOrEmpty(evt.PlayerId))
                    evt.PlayerId = Client.CurrentPlayerId;
                if (string.IsNullOrEmpty(evt.SessionId))
                    evt.SessionId = CurrentSessionId;
                if (string.IsNullOrEmpty(evt.Timestamp))
                    evt.Timestamp = DateTime.UtcNow.ToString("o");
            }

            var request = new AnalyticsEventsRequest { Events = events };

            await ExecuteAsync(
                () => FlockHttpClient.PostAsync<Dictionary<string, object>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/analytics/events")
                        .ToString(),
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Track events batch", cancellationToken);

            Client.Logger.LogDebug(new StringBuilder()
                .Append("Batch events tracked: ").Append(events.Count).Append(" events")
                .ToString());
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
            var request = new AnalyticsTransactionRequest
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
                throw new FlockValidationException(new StringBuilder()
                    .Append("Transaction amount must be greater than zero, got: ")
                    .Append(request.Amount)
                    .ToString());

            if (string.IsNullOrEmpty(request.PlayerId))
                request.PlayerId = Client.CurrentPlayerId;
            if (string.IsNullOrEmpty(request.SessionId))
                request.SessionId = CurrentSessionId;
            if (string.IsNullOrEmpty(request.CreatedAt))
                request.CreatedAt = DateTime.UtcNow.ToString("o");

            await ExecuteAsync(
                () => FlockHttpClient.PostAsync<Dictionary<string, object>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/analytics/transactions")
                        .ToString(),
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Record transaction", cancellationToken);

            Client.Logger.LogDebug(new StringBuilder()
                .Append("Transaction recorded: ").Append(request.Amount)
                .Append(" ").Append(request.CurrencyCode)
                .ToString());
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
            var token = _session.SessionToken;

            try
            {
                var snapshot = _session.TakeSnapshot();

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
                Client.Logger.LogWarning(new StringBuilder()
                    .Append("Heartbeat event failed: ")
                    .Append(ex.Message)
                    .ToString());
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
                Client.Logger.LogWarning(new StringBuilder()
                    .Append("End session on quit failed: ")
                    .Append(ex.Message)
                    .ToString());
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
                Client.Logger.LogWarning(new StringBuilder()
                    .Append("Session timeout rotation failed: ")
                    .Append(ex.Message)
                    .ToString());
            }
        }

        private async Task SendEndSessionAsync(
            FlockSessionSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            var sessionId = snapshot.ServerSessionId ?? snapshot.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                Client.Logger.LogWarning("Cannot end session: no session ID available");
                return;
            }

            var request = new SessionEndRequest
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
                        new StringBuilder().Append(Client.GetApiUrl())
                            .Append("/v1/analytics/sessions/")
                            .Append(sessionId)
                            .ToString(),
                        request, Client.GetBaseHeaders(), cancellationToken),
                    "End session", cancellationToken);

                Client.Logger.LogInfo(new StringBuilder()
                    .Append("Session ended on server: ").Append(sessionId)
                    .Append(snapshot.WasCrash ? " (recovered from crash)" : "")
                    .ToString());
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning(new StringBuilder()
                    .Append("Failed to end session on server: ").Append(ex.Message)
                    .ToString());
            }
        }

        private void RequireAuth()
        {
            if (!Client.IsAuthenticated)
                throw new FlockAuthException(
                    "Analytics requires authentication. Call a login method first.");
        }
    }
}