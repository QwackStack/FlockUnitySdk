using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Interfaces;
using Flock.Models;

namespace Flock.Providers
{
    public class NullAnalyticsProvider : FlockProviderBase,  IAnalyticProvider
    {
        public NullAnalyticsProvider(FlockClient client) : base(client)
        {

        }
        public Task InitializeAsync(CancellationToken ct)
        {
            this.Client.Logger.LogDebug("Flock analytics is Disabled");
            return Task.CompletedTask;
        }

        public Task<string> StartSessionAsync(CancellationToken cancellationToken = default)
        {
            this.Client.Logger.LogDebug("Analytics is disabled , trying to start session");
            return Task.FromResult<string>(null);
        }

        public Task EndSessionAsync(CancellationToken cancellationToken = default)
        {
            this.Client.Logger.LogDebug("Analytics is disabled , trying to end session");
            return Task.CompletedTask;
        }

        public void RecordScreenView(string screenName)
        {
            this.Client.Logger.LogDebug("Analytics is disabled , trying to RecordScreenView");
        }

        public Task RecordTransactionAsync(AnalyticsTransactionRequest request, CancellationToken cancellationToken = default)
        {
            this.Client.Logger.LogDebug("Analytics is disabled ,trying to record transaction");
            return Task.CompletedTask;
        }

        public bool TrackEvent(string eventName, Dictionary<string, object> properties = null, string eventCategory = null)
        {
            this.Client.Logger.LogDebug("Analytics is disabled, trying to track an event");
            return false;
        }

        public void LogDiagnosticException(Exception exception, Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null)
        {
            this.Client.Logger.LogDebug("Analytics is disabled ,trying to log exception");
        }

        public void LogDiagnosticException(string message, string stackTrace, Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null)
        {
            this.Client.Logger.LogDebug("Analytics is disabled ,trying to log exception");
        }

        public void LogDiagnosticError(string message, string logicalExpression = null, string errorCode = null,
            string errorMessage = null, Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null)
        {
            this.Client.Logger.LogDebug("Analytics is disabled ,trying to log error");
        }

        public void LogDiagnosticEvent(string message,
            Dictionary<string, object> extraData = null)
        {
            this.Client.Logger.LogDebug("Analytics is disabled ,trying to log event");
        }

        [Obsolete(FlockAnalyticsProvider.ObsoleteDiagnosticsMessage + "LogDiagnosticException.")]
        public void LogException(Exception exception, Dictionary<string, object> errorData = null, Dictionary<string, object> extraData = null)
            => LogDiagnosticException(exception, errorData, extraData);

        [Obsolete(FlockAnalyticsProvider.ObsoleteDiagnosticsMessage + "LogDiagnosticException.")]
        public void LogException(string message, string stackTrace, Dictionary<string, object> errorData = null, Dictionary<string, object> extraData = null)
            => LogDiagnosticException(message, stackTrace, errorData, extraData);

        [Obsolete(FlockAnalyticsProvider.ObsoleteDiagnosticsMessage + "LogDiagnosticError.")]
        public void LogError(string message, string logicalExpression = null, string errorCode = null, string errorMessage = null,
            Dictionary<string, object> errorData = null, Dictionary<string, object> extraData = null)
            => LogDiagnosticError(message, logicalExpression, errorCode, errorMessage, errorData, extraData);

        [Obsolete(FlockAnalyticsProvider.ObsoleteDiagnosticsMessage + "LogDiagnosticEvent.")]
        public void LogEvent(string message, Dictionary<string, object> extraData = null)
            => LogDiagnosticEvent(message, extraData);

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            this.Client.Logger.LogDebug("Analytics is disabled, FlushAsync is a no-op");
            return Task.CompletedTask;
        }

        public Task RecordTransactionAsync(double amount, string currencyCode = "USD", string shopItemId = null, int quantity = 1,
            string transactionType = "purchase", string status = "completed", string paymentProvider = null,
            string externalTransactionId = null, string currencyId = null, CancellationToken cancellationToken = default)
        {
            this.Client.Logger.LogDebug("Analytics is disabled , trying to record transaction");
            return Task.CompletedTask;
        }

        public bool HasConsent => false;

        public void SetConsent(bool granted)
        {
            this.Client.Logger.LogDebug("Analytics is disabled, SetConsent is a no-op");
        }

        public void EraseLocalAnalyticsData()
        {
            this.Client.Logger.LogDebug("Analytics is disabled, EraseLocalAnalyticsData is a no-op");
        }
    }
}
