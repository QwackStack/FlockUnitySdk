using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IAnalyticProvider
    {
        /// <summary>
        /// Wires up the analytics session, replays cached events queued before login,
        /// and recovers any session left dangling by a previous crash. Safe to call
        /// repeatedly; re-running with a different player id rotates the session.
        /// </summary>
        Task InitializeAsync(CancellationToken ct);

        /// <summary>
        /// Starts a session manually — pair with <c>AutoStartSession = false</c> for
        /// game-defined session boundaries. Returns the server session id, or the local id
        /// until registration succeeds (pre-login: logs an error, session runs locally).
        /// </summary>
        Task<string> StartSessionAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Ends the active session and delivers its end (raises
        /// <c>FlockEvents.OnSessionEnded</c> with reason <c>Manual</c>). Warns when no
        /// session is active. Not needed on quit/logout — those end the session automatically.
        /// </summary>
        Task EndSessionAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Records that the player navigated to a named screen. Aggregated into the
        /// active session's screen-view counter; no immediate network call.
        /// </summary>
        void RecordScreenView(string screenName);

        /// <summary>
        /// Sends a transaction record (purchase, refund, etc.) to the analytics
        /// transactions endpoint. Caller supplies a fully-populated request.
        /// </summary>
        Task RecordTransactionAsync(AnalyticsTransactionRequest request, CancellationToken cancellationToken = default);

        // /// <summary>
        // /// Tracks a batch of analytics events. Each entry is persisted to the local
        // /// write-ahead cache before sending; on success the batch is dropped, on
        // /// transient failure it stays for the next flush.
        // /// </summary>
        // Task TrackEventsAsync(
        //     List<AnalyticsEventRequest> events,
        //     CancellationToken cancellationToken = default);

        
        // Not exposed to user until log_event/analytic clean up
        // /// <summary>
        // /// Tracks a single analytics event. Enqueued to the on-disk cache; delivery
        // /// happens on the flush triggers. Events tracked before authentication are
        // /// tagged with a placeholder player id and rewritten after login.
        // /// </summary>
        // void TrackEvent(
        //     string eventName,
        //     string eventCategory = null,
        //     Dictionary<string, object> parameters = null);

        /// <summary>
        /// Captures an <see cref="Exception"/> as a <c>LogEventType.Exception</c>
        /// log_event. Enqueue-only (synchronous, never blocks on network): delivery
        /// happens later on the flush triggers (interval/pause/session end/login).
        /// </summary>
        void LogException(
            Exception exception,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null);

        /// <summary>
        /// Captures a raw exception payload (message + stacktrace string) as a
        /// <c>LogEventType.Exception</c> log_event. Used by the global Unity
        /// exception handler where a typed <see cref="Exception"/> isn't available.
        /// Enqueue-only; delivered on the flush triggers.
        /// </summary>
        void LogException(
            string message,
            string stackTrace,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null);

        /// <summary>
        /// Captures a <c>LogEventType.LogicError</c> log_event. Same shape as
        /// <see cref="LogEvent"/> — caller supplies whichever fields are relevant.
        /// Enqueue-only; delivered on the flush triggers.
        /// </summary>
        void LogError(
            string message,
            string logicalExpression = null,
            string errorCode = null,
            string errorMessage = null,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null);

        /// <summary>
        /// Captures a <c>LogEventType.Debug</c> log_event with a message plus any
        /// of the optional diagnostic fields. Enqueue-only; delivered on the flush triggers.
        /// </summary>
        void LogEvent(
            string message,
            Dictionary<string, object> extraData = null);

        /// <summary>
        /// Awaitable drain of everything queued (session ends, events, logs) — for the
        /// rare "make sure it landed before X" moment. Automatic flushing (interval /
        /// pause / session end / login) makes calling this optional. Transient failures
        /// keep records queued for a later flush; never throws.
        /// </summary>
        Task FlushAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Convenience overload that builds an <see cref="AnalyticsTransactionRequest"/>
        /// from primitive fields and forwards it to the transactions endpoint.
        /// </summary>
        Task RecordTransactionAsync(
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

        /// <summary>
        /// Whether the player has granted analytics consent. Always <c>true</c> unless
        /// <see cref="Flock.Analytics.FlockAnalyticsConfig.RequireExplicitConsent"/> is on and
        /// no decision has been recorded yet, or <see cref="SetConsent"/> has revoked it.
        /// </summary>
        bool HasConsent { get; }

        /// <summary>
        /// Grants or revokes analytics consent. Revoking pauses the active session (no final
        /// session-end record is sent) and stops future session/event/log tracking — it does
        /// not delete anything already queued; see <see cref="EraseLocalAnalyticsData"/> for
        /// that. Persisted across launches. Idempotent. Does not affect
        /// <c>RecordTransactionAsync</c>, which runs under a different legal basis than
        /// consent (contract/financial-retention, not consent) — <c>LogException</c>,
        /// <c>LogError</c>, and <c>LogEvent</c> ARE gated, same as session/event
        /// tracking, since they carry player-identifiable data too.
        /// </summary>
        void SetConsent(bool granted);

        /// <summary>
        /// Deletes analytics events, session-end records, and log/crash events queued
        /// on-device but not yet sent to Flock's backend. Callable independent of
        /// <see cref="HasConsent"/>. Local-only — this does not delete analytics already
        /// ingested by the server for this player; there is currently no backend endpoint
        /// that could do that from the client SDK.
        /// </summary>
        void EraseLocalAnalyticsData();
    }
}
