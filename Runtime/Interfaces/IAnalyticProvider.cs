using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IAnalyticProvider
    {
        //TODO add summaries
        public Task InitializeAsync(CancellationToken ct);
        public void RecordScreenView(string screenName);
        public Task RecordTransactionAsync(AnalyticsTransactionRequest request, CancellationToken cancellationToken = default);
        public Task TrackEventsAsync(
            List<AnalyticsEventRequest> events,
            CancellationToken cancellationToken = default);
        public Task TrackEventAsync(
            string eventName,
            string eventCategory = null,
            Dictionary<string, object> parameters = null,
            CancellationToken cancellationToken = default);
        public Task RecordTransactionAsync(
            double amount,
            string currencyCode = "USD",
            string shopItemId = null,
            int quantity = 1,
            string transactionType = "purchase",
            string status = "completed",
            string paymentProvider = null,
            string externalTransactionId = null,
            string currencyId = null,
            CancellationToken cancellationToken = default);

    }  
}