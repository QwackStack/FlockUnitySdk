using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Logging;
using static System.FormattableString;

namespace Flock.Http
{
    public class RetryPolicy
    {
        /// <summary>How many times to retry after the initial attempt fails.</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>Wait time before the first retry.</summary>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Upper bound on delay between retries, regardless of backoff growth, jitter or a Retry-After hint.</summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Each retry multiplies the previous delay by this factor.</summary>
        public double BackoffMultiplier { get; set; } = 2.0;

        /// <summary>Adds ±25% randomness to each delay to avoid thundering herd.</summary>
        public bool UseJitter { get; set; } = true;

        /// <summary>A separate copy with every setting, including any added later.</summary>
        internal RetryPolicy Copy() => (RetryPolicy)MemberwiseClone();
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
                catch (Exception ex)
                {
                    // A failure that is never retried is the caller's to report, so it is not warned about whatever the retry setting.
                    if (!IsWorthRetrying(ex, retryAmbiguousFailures))
                        throw;
                    if (attempt > maxRetries)
                    {
                        string failedNote = ex is FlockNetworkException ? " (couldn't reach the server)" : string.Empty;
                        _logger?.LogWarning($"Operation failed after {attempt} attempt(s){failedNote}: {ex.Message}");
                        throw;
                    }

                    TimeSpan wait = ResolveDelay(ex, delay);
                    string note = ex is FlockNetworkException ? " Can't reach the server (connectivity) — retries keep failing until it's back." : string.Empty;
                    _logger?.LogWarning(Invariant($"Attempt {attempt} failed: {ex.Message}.{note} Retrying in {wait.TotalSeconds:F1}s..."));
                    await FlockWaiting.DelayAsync(wait, cancellationToken);

                    delay = TimeSpan.FromSeconds(Math.Min(
                        delay.TotalSeconds * _policy.BackoffMultiplier,
                        _policy.MaxDelay.TotalSeconds
                    ));
                }
            }
        }

        private static bool IsWorthRetrying(Exception ex, bool retryAmbiguousFailures)
        {
            if (ex is FlockAuthException || ex is FlockValidationException || ex is FlockSerializationException)
                return false;

            // Don't retry permanent 4xx (404, 409, etc.) — the server's answer won't change.
            if (ex is FlockNetworkException net && FlockNetworkException.IsPermanentStatus(net.StatusCode))
                return false;

            // Non-idempotent callers only retry failures the server provably didn't process (408/429);
            // ambiguous ones (client timeout, dropped connection, 5xx) might have committed, so surface them.
            return retryAmbiguousFailures || IsNotProcessed(ex);
        }

        // 408/429 mean the server rejected the request before processing it, so a retry can't double-apply.
        private static bool IsNotProcessed(Exception ex)
            => ex is FlockNetworkException net && (net.StatusCode == 408 || net.StatusCode == 429);

        // Honors a server Retry-After hint when present (bounded by MaxDelay), else jittered backoff.
        internal TimeSpan ResolveDelay(Exception ex, TimeSpan baseDelay)
        {
            if (ex is FlockNetworkException net && net.RetryAfter.HasValue)
            {
                TimeSpan hint = net.RetryAfter.Value;
                return hint < _policy.MaxDelay ? hint : _policy.MaxDelay;
            }
            return CalculateDelay(baseDelay);
        }

        // Jitter is applied after the backoff reached MaxDelay, so the jittered wait is capped again.
        private TimeSpan CalculateDelay(TimeSpan baseDelay)
        {
            TimeSpan delay = baseDelay;
            if (_policy.UseJitter)
            {
                double roll;
                lock (_randomLock)
                    roll = _random.NextDouble();
                double jitterFactor = 0.75 + (roll * 0.5); // ±25%
                delay = TimeSpan.FromSeconds(baseDelay.TotalSeconds * jitterFactor);
            }
            return delay < _policy.MaxDelay ? delay : _policy.MaxDelay;
        }
    }
}
