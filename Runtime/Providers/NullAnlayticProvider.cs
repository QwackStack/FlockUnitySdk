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

        public void RecordScreenView(string screenName)
        {
            this.Client.Logger.LogDebug("Analytics is disabled , trying to RecordScreenView");
        }

        public Task RecordTransactionAsync(AnalyticsTransactionRequest request, CancellationToken cancellationToken = default)
        {
            this.Client.Logger.LogDebug("Analytics is disabled ,trying to record transaction");
            return Task.CompletedTask;
        }

        public Task TrackEventsAsync(List<AnalyticsEventRequest> events, CancellationToken cancellationToken = default)
        {
            this.Client.Logger.LogDebug("Analytics is disabled ,trying to track events");
            return Task.CompletedTask;
        }

        public Task TrackEventAsync(string eventName, string eventCategory = null, Dictionary<string, object> parameters = null,
            CancellationToken cancellationToken = default)
        {
            this.Client.Logger.LogDebug("Analytics is disabled ,trying to track event");
            return Task.CompletedTask;
        }

        public Task RecordTransactionAsync(double amount, string currencyCode = "USD", string shopItemId = null, int quantity = 1,
            string transactionType = "purchase", string status = "completed", string paymentProvider = null,
            string externalTransactionId = null, string currencyId = null, CancellationToken cancellationToken = default)
        {
            this.Client.Logger.LogDebug("Analytics is disabled , trying to record transaction");
            return Task.CompletedTask;
        }
    }
}