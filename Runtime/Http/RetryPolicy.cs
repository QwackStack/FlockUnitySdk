using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Logging;

namespace Flock.Http
{
    public class RetryPolicy
    {
        /// <summary>How many times to retry after the initial attempt fails.</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>Wait time before the first retry.</summary>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Upper bound on delay between retries, regardless of backoff growth.</summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Each retry multiplies the previous delay by this factor.</summary>
        public double BackoffMultiplier { get; set; } = 2.0;

        /// <summary>Adds ±25% randomness to each delay to avoid thundering herd.</summary>
        public bool UseJitter { get; set; } = true;
    }

    public class RetryHandler
    {
        private readonly RetryPolicy _policy;
        private readonly IFlockLogger _logger;
        private readonly Random _random = new Random();
        private readonly object _randomLock = new object();

        public RetryHandler(RetryPolicy policy, IFlockLogger logger)
        {
            _policy = policy ?? new RetryPolicy();
            _logger = logger;
        }

        public async Task<T> ExecuteAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default, bool retryAmbiguousFailures = true,
            int? maxRetriesOverride = null)
        {
            int maxRetries = maxRetriesOverride ?? _policy.MaxRetries;
            int attempt = 0;
            TimeSpan delay = _policy.InitialDelay;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    attempt++;
                    if (attempt > 1)
                        _logger?.LogDebug($"Attempt {attempt}/{maxRetries + 1}");

                    return await operation();
                }
                catch (OperationCanceledException)
                {
                    // Cancellation isn't a failure — never retry it, never log it as one.
                    throw;
                }
                catch (Exception ex) when (attempt <= maxRetries)
                {
                    if (ex is FlockAuthException || ex is FlockValidationException || ex is FlockSerializationException)
                        throw;

                    // Don't retry permanent 4xx (404, 409, etc.) — the server's answer won't change.
                    if (ex is FlockNetworkException net && FlockNetworkException.IsPermanentStatus(net.StatusCode))
                        throw;

                    // Non-idempotent callers only retry failures the server provably didn't process (408/429);
                    // ambiguous ones (client timeout, dropped connection, 5xx) might have committed, so surface them.
                    if (!retryAmbiguousFailures && !IsNotProcessed(ex))
                        throw;

                    TimeSpan wait = ResolveDelay(ex, delay);
                    string note = ex is FlockNetworkException ? " Can't reach the server (connectivity) — retries keep failing until it's back." : string.Empty;
                    _logger?.LogWarning($"Attempt {attempt} failed: {ex.Message}.{note} Retrying in {wait.TotalSeconds:F1}s...");
                    await Task.Delay(wait, cancellationToken);

                    delay = TimeSpan.FromSeconds(Math.Min(
                        delay.TotalSeconds * _policy.BackoffMultiplier,
                        _policy.MaxDelay.TotalSeconds
                    ));
                }
                catch (Exception ex)
                {
                    string note = ex is FlockNetworkException ? " (couldn't reach the server)" : string.Empty;
                    _logger?.LogError($"Operation failed after {attempt} attempt(s){note}", ex);
                    throw;
                }
            }
        }

        // 408/429 mean the server rejected the request before processing it, so a retry can't double-apply.
        private static bool IsNotProcessed(Exception ex)
            => ex is FlockNetworkException net && (net.StatusCode == 408 || net.StatusCode == 429);

        // Honors a server Retry-After hint when present (bounded by MaxDelay), else jittered backoff.
        private TimeSpan ResolveDelay(Exception ex, TimeSpan baseDelay)
        {
            if (ex is FlockNetworkException net && net.RetryAfter.HasValue)
            {
                TimeSpan hint = net.RetryAfter.Value;
                return hint < _policy.MaxDelay ? hint : _policy.MaxDelay;
            }
            return CalculateDelay(baseDelay);
        }

        private TimeSpan CalculateDelay(TimeSpan baseDelay)
        {
            if (!_policy.UseJitter)
                return baseDelay;

            double roll;
            lock (_randomLock)
                roll = _random.NextDouble();
            double jitterFactor = 0.75 + (roll * 0.5); // ±25%
            return TimeSpan.FromSeconds(baseDelay.TotalSeconds * jitterFactor);
        }
    }
}
