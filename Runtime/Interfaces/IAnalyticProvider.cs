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

        /// <summary>Records a gameplay event for the Game Metrics dashboards (surface: analytics): queued on disk, held until a player is signed in, and refused with false for no consent, an empty or reserved name, or a name or category the server cannot store.</summary>
        bool TrackEvent(
            string eventName,
            Dictionary<string, object> properties = null,
            string eventCategory = null);

        /// <summary>Records an exception you caught under Diagnostics → Errors (surface: log_event); unhandled ones are captured for you.</summary>
        void LogDiagnosticException(
            Exception exception,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null);

        /// <summary>Records an exception from its message and stack trace under Diagnostics → Errors (surface: log_event).</summary>
        void LogDiagnosticException(
            string message,
            string stackTrace,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null);

        /// <summary>Records a recoverable logic fault under Diagnostics → Errors (surface: log_event).</summary>
        void LogDiagnosticError(
            string message,
            string logicalExpression = null,
            string errorCode = null,
            string errorMessage = null,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null);

        /// <summary>Records a diagnostic message under Diagnostics → Events (surface: log_event); a gameplay event is <see cref="TrackEvent"/>.</summary>
        void LogDiagnosticEvent(
            string message,
            Dictionary<string, object> extraData = null);

        /// <summary>The former name of <see cref="LogDiagnosticException(Exception, Dictionary{string, object}, Dictionary{string, object})"/>, kept so existing code keeps working.</summary>
        [Obsolete(Flock.Providers.FlockAnalyticsProvider.ObsoleteDiagnosticsMessage + "LogDiagnosticException.")]
        void LogException(
            Exception exception,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null);

        /// <summary>The former name of <see cref="LogDiagnosticException(string, string, Dictionary{string, object}, Dictionary{string, object})"/>, kept so existing code keeps working.</summary>
        [Obsolete(Flock.Providers.FlockAnalyticsProvider.ObsoleteDiagnosticsMessage + "LogDiagnosticException.")]
        void LogException(
            string message,
            string stackTrace,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null);

        /// <summary>The former name of <see cref="LogDiagnosticError"/>, kept so existing code keeps working.</summary>
        [Obsolete(Flock.Providers.FlockAnalyticsProvider.ObsoleteDiagnosticsMessage + "LogDiagnosticError.")]
        void LogError(
            string message,
            string logicalExpression = null,
            string errorCode = null,
            string errorMessage = null,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null);

        /// <summary>The former name of <see cref="LogDiagnosticEvent"/>, kept so existing code keeps working.</summary>
        [Obsolete(Flock.Providers.FlockAnalyticsProvider.ObsoleteDiagnosticsMessage + "LogDiagnosticEvent.")]
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
