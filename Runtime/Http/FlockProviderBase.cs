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

        /// <summary>Runs <paramref name="operation"/> via the retry handler. Pass idempotent=false for non-idempotent mutations (e.g. currency grants): ambiguous failures surface instead of being re-sent, and only provably-not-processed failures (408/429) are retried.</summary>
        protected async Task<T> ExecuteAsync<T>(
            Func<Task<T>> operation,
            string context,
            CancellationToken cancellationToken,
            bool idempotent = true,
            int? maxRetriesOverride = null)
        {
            try
            {
                return await Client.RetryHandler.ExecuteAsync(operation, cancellationToken, retryAmbiguousFailures: idempotent, maxRetriesOverride: maxRetriesOverride);
            }
            //only try refresh if the auth is even successful
            catch (FlockAuthException) when (Client.IsAuthenticated)
            {
                Client.Logger.LogDebug("Access token expired, attempting silent refresh");
                bool refreshed = await Client.TryRefreshTokenAsync(cancellationToken);
                if (!refreshed)
                    throw;

                try
                {
                    return await Client.RetryHandler.ExecuteAsync(operation, cancellationToken, retryAmbiguousFailures: idempotent, maxRetriesOverride: maxRetriesOverride);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (FlockException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Client.Logger.LogError($"{context} failed", ex);
                    throw new FlockNetworkException($"{context} failed", ex);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Client.Logger.LogError($"{context} failed", ex);
                throw new FlockNetworkException($"{context} failed", ex);
            }
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
            CancellationToken cancellationToken) where T : class
        {
            string snapshotScope = GetSnapshotScope(category);
            return FetchAtScopeAsync(snapshotScope, key, operation, context, cancellationToken);
        }

        // Raw-scope escape hatch for the rare caller that can't use a plain category — e.g. FlockGameProvider's
        // by-name version lookup, which stays on BootstrapScope (not nested under GameVersionId) on purpose.
        protected async Task<T> FetchAtScopeAsync<T>(
            string scope,
            string key,
            Func<Task<T>> operation,
            string context,
            CancellationToken cancellationToken) where T : class
        {
            FlockSnapshotStore store = Client.SnapshotStore;
            if (store == null)
                return await ExecuteAsync(operation, context, cancellationToken);

            bool hasCache = store.TryRead(scope, key, out T cached);

            // No connection and a cached copy in hand — skip the network entirely.
            if (hasCache && !this.IsServerReachable())
            {
                Client.Logger.LogWarning($"{context}: serving cached snapshot (no connectivity)");
                return cached;
            }

            try
            {
                // With a cache to fall back on, don't burn the full retry backoff — one attempt, then serve cache.
                int? retryBudget = hasCache ? 0 : (int?)null;
                T result = await ExecuteAsync(operation, context, cancellationToken, maxRetriesOverride: retryBudget);
                store.Write(scope, key, result);
                return result;
            }
            catch (FlockNetworkException e)
            {
                if (!FlockNetworkException.IsPermanentStatus(e.StatusCode) && hasCache)
                {
                    Client.Logger.LogWarning($"{context}: serving cached snapshot (couldn't reach server)");
                    return cached;
                }
                throw;
            }
        }

        protected bool IsServerReachable()
        {
            return  Application.internetReachability != NetworkReachability.NotReachable;
        }
        protected void RequireNotEmpty(string value, string name)
        {
            // for cases that require params not to be null
            if (string.IsNullOrEmpty(value))
                throw new FlockValidationException($"{name} cannot be null or empty");
        }

        protected void ValidateResponse<T>(GenericResponse<T> response) where T : class
        {
            if (response?.Result == null)
                throw new FlockNetworkException("Invalid response from server");
        }
    }
}
