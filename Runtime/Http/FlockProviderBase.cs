using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Models;
using Flock.Providers;
using UnityEngine;

namespace Flock.Http
{
    public abstract class FlockProviderBase
    {
        protected readonly FlockClient Client;

        protected FlockProviderBase(FlockClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>The sign-in a call that acts for the signed-in player hands to <see cref="ExecuteAsync{T}"/>; null while nobody is signed in.</summary>
        protected int? SignInToActFor => Client.IsAuthenticated ? Client.SignInNumber : (int?)null;

        /// <summary>Runs an <paramref name="operation"/> that has nothing to return, with the same retry, refresh and error rules as <see cref="ExecuteAsync{T}"/>.</summary>
        protected Task ExecuteWithoutResultAsync(
            Func<Task> operation,
            string context,
            CancellationToken cancellationToken,
            bool idempotent = true,
            int? maxRetriesOverride = null,
            int? actsForSignIn = null)
        {
            return ExecuteAsync(async () =>
            {
                await operation();
                return true;
            }, context, cancellationToken, idempotent, maxRetriesOverride, actsForSignIn);
        }

        /// <summary>Runs <paramref name="operation"/> via the retry handler. Pass idempotent=false for non-idempotent mutations (e.g. currency grants): ambiguous failures surface instead of being re-sent, and only provably-not-processed failures (408/429) are retried.</summary>
        /// <param name="actsForSignIn">For a call that acts as the signed-in player through its token (<see cref="SignInToActFor"/>): each attempt, retries included, is cancelled once that sign-in has ended.</param>
        /// <param name="spendsOrGrants">For a call that moves money or items (a purchase, a fund grant, a consume, a redemption): a failure that leaves it unknown whether it was done sets <see cref="FlockException.OutcomeUnknown"/>.</param>
        protected async Task<T> ExecuteAsync<T>(
            Func<Task<T>> operation,
            string context,
            CancellationToken cancellationToken,
            bool idempotent = true,
            int? maxRetriesOverride = null,
            int? actsForSignIn = null,
            bool spendsOrGrants = false)
        {
            // The sign-in this request goes out under; once it ends, the request is never re-sent as whoever signed in next.
            int signInNumber = Client.SignInNumber;
            Func<Task<T>> send = actsForSignIn.HasValue ? () => SendForSignIn(operation, actsForSignIn.Value) : operation;
            try
            {
                return await Client.RetryHandler.ExecuteAsync(send, cancellationToken, retryAmbiguousFailures: idempotent, maxRetriesOverride: maxRetriesOverride);
            }
            // A 403 means "not allowed", never a lapsed sign-in (that is a 401), so a new token would change nothing.
            catch (FlockAuthException refused) when (Client.IsAuthenticated && refused.StatusCode != 403)
            {
                Client.Logger.LogDebug("Access token expired, attempting silent refresh");
                bool refreshed = await Client.TryRefreshTokenAsync(signInNumber, cancellationToken);
                if (!refreshed)
                    throw;

                try
                {
                    return await Client.RetryHandler.ExecuteAsync(send, cancellationToken, retryAmbiguousFailures: idempotent, maxRetriesOverride: maxRetriesOverride);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (FlockException ex)
                {
                    StampOperation(ex, context);
                    if (spendsOrGrants && IsOutcomeUnknown(ex))
                        MarkOutcomeUnknown(ex);
                    throw;
                }
                catch (Exception ex)
                {
                    Client.Logger.LogError($"{context} failed", ex);
                    throw new FlockNetworkException(ex.Message, ex) { Operation = context, Hint = FlockErrorHints.ReportSdkFault };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FlockException ex)
            {
                StampOperation(ex, context);
                if (spendsOrGrants && IsOutcomeUnknown(ex))
                    MarkOutcomeUnknown(ex);
                throw;
            }
            catch (Exception ex)
            {
                Client.Logger.LogError($"{context} failed", ex);
                throw new FlockNetworkException(ex.Message, ex) { Operation = context, Hint = FlockErrorHints.ReportSdkFault };
            }
        }

        // A call that acts for one sign-in goes out as it or not at all, so a retry never carries the next player's token.
        private Task<T> SendForSignIn<T>(Func<Task<T>> operation, int signInNumber)
        {
            if (signInNumber != Client.SignInNumber)
                throw new OperationCanceledException("The player signed out or changed, so the request was not sent");
            return operation();
        }

        // A change that may have been made leaves the player's cached data unsure (the wallet above all), so it is dropped for the next read to ask the server.
        private protected void MarkOutcomeUnknown(FlockException failure)
        {
            failure.OutcomeUnknown = true;
            ForgetThePlayersData();
        }

        // Drops the signed-in player's cached player data, so the next read asks the server; writes still queued are laid over it again.
        private protected void ForgetThePlayersData()
        {
#if !FLOCK_NO_PLAYER
            Client.Player?.ForgetPlayerData(Client.CurrentPlayerId);
#endif
        }

        // No answer, a server error that names no cause, or an answer that could not be read: the change may have been made.
        private static bool IsOutcomeUnknown(FlockException failure)
            => failure is FlockSerializationException
               || (failure is FlockNetworkException && (failure.StatusCode == null || failure.StatusCode >= 500)
                   && string.IsNullOrEmpty(FlockErrorHints.For(failure.ErrorCode)));

        // Names the failing SDK call on the exception; first writer wins so the innermost, most specific context survives.
        private static void StampOperation(FlockException ex, string context)
        {
            if (string.IsNullOrEmpty(ex.Operation))
                ex.Operation = context;
        }

        // Kept protected (not folded into the wrappers below) — FlockCommandProvider's player-scoped
        // write queue needs a scope nested under a category, not just the category itself.
        protected string GetSnapshotScope(string category)
        {
            return $"{Client.GameVersionId}/{category}";
        }

        protected void DeleteSnapshotCategory(string category)
        {
            string snapshotScope = GetSnapshotScope(category);
            Client.SnapshotStore?.DeleteScope(snapshotScope);
        }

        protected bool TryReadSnapshot<T>(string category, string key, out T value) where T : class
        {
            FlockSnapshotStore store = Client.SnapshotStore;
            if (store == null)
            {
                value = null;
                return false;
            }

            string snapshotScope = GetSnapshotScope(category);
            return store.TryRead(snapshotScope, key, out value);
        }

        protected void WriteSnapshot<T>(string category, string key, T value) where T : class
        {
            string snapshotScope = GetSnapshotScope(category);
            Client.SnapshotStore?.Write(snapshotScope, key, value);
        }

        protected Task<T> FetchWithSnapshotAsync<T>(
            string category,
            string key,
            Func<Task<T>> operation,
            string context,
            CancellationToken cancellationToken,
            int? actsForSignIn = null) where T : class
        {
            string snapshotScope = GetSnapshotScope(category);
            return FetchAtScopeAsync(snapshotScope, key, operation, context, cancellationToken, actsForSignIn);
        }

        // Raw-scope escape hatch for the rare caller that can't use a plain category — e.g. FlockGameProvider's
        // by-name version lookup, which stays on BootstrapScope (not nested under GameVersionId) on purpose.
        protected async Task<T> FetchAtScopeAsync<T>(
            string scope,
            string key,
            Func<Task<T>> operation,
            string context,
            CancellationToken cancellationToken,
            int? actsForSignIn = null) where T : class
        {
            FlockSnapshotStore store = Client.SnapshotStore;
            if (store == null)
                return await ExecuteAsync(operation, context, cancellationToken, actsForSignIn: actsForSignIn);

            bool hasCache = store.TryRead(scope, key, out T cached);

            // No connection and a cached copy in hand — skip the network entirely.
            if (hasCache && !this.IsServerReachable())
            {
                Client.Logger.LogDebug($"{context}: serving cached snapshot (no connectivity)");
                return cached;
            }

            try
            {
                // With a cache to fall back on, don't burn the full retry backoff — one attempt, then serve cache.
                int? retryBudget = hasCache ? 0 : (int?)null;
                T result = await ExecuteAsync(operation, context, cancellationToken, maxRetriesOverride: retryBudget, actsForSignIn: actsForSignIn);
                store.Write(scope, key, result);
                return result;
            }
            catch (FlockNetworkException e)
            {
                if (!FlockNetworkException.IsPermanentStatus(e.StatusCode) && hasCache)
                {
                    Client.Logger.LogDebug($"{context}: serving cached snapshot (couldn't reach server)");
                    return cached;
                }
                throw;
            }
        }

        protected bool IsServerReachable()
        {
            return Client.IsReachable();
        }
        // Refuses an empty argument; fix says what to do about it, and without one the failure asks for the argument by name.
        protected void RequireNotEmpty(string value, string name, string fix = null)
        {
            if (string.IsNullOrEmpty(value))
                throw new FlockValidationException($"{name} is empty") { Hint = fix ?? $"Pass a {name}; this call cannot run without one." };
        }

        protected void ValidateResponse<T>(GenericResponse<T> response) where T : class
        {
            if (response?.Result == null)
                throw new FlockNetworkException("The server's answer has no result in it") { Hint = FlockErrorHints.NotFlocksAnswer };
        }
    }
}
