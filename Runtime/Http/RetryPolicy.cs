using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Logging;
using Flock.Exceptions;

namespace Flock.Http
{
    /// <summary>
    /// Retry policy configuration for HTTP requests
    /// </summary>
    public class RetryPolicy
    {
        /// <summary>
        /// Maximum number of retry attempts
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Initial delay before first retry
        /// </summary>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Maximum delay between retries
        /// </summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Multiplier for exponential backoff
        /// </summary>
        public double BackoffMultiplier { get; set; } = 2.0;

        /// <summary>
        /// Whether to use jitter (randomization) in retry delays
        /// </summary>
        public bool UseJitter { get; set; } = true;
    }

    /// <summary>
    /// Retry handler with exponential backoff
    /// </summary>
    public class RetryHandler
    {
        private readonly RetryPolicy _policy;
        private readonly IFlockLogger _logger;
        private readonly Random _random = new Random();

        public RetryHandler(RetryPolicy policy, IFlockLogger logger)
        {
            _policy = policy ?? new RetryPolicy();
            _logger = logger;
        }

        /// <summary>
        /// Executes an async operation with retry logic
        /// </summary>
        public async Task<T> ExecuteAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default,
            bool shouldRetryOnException = true)
        {
            int attempt = 0;
            TimeSpan delay = _policy.InitialDelay;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    attempt++;
                    _logger?.LogDebug($"Attempt {attempt}/{_policy.MaxRetries + 1}");

                    return await operation();
                }
                catch (Exception ex) when (shouldRetryOnException && attempt <= _policy.MaxRetries)
                {
                    // Don't retry on auth exceptions or validation exceptions
                    if (ex is FlockAuthException || ex is FlockValidationException)
                    {
                        throw;
                    }

                    _logger?.LogWarning($"Attempt {attempt} failed: {ex.Message}. Retrying in {delay.TotalSeconds}s...");

                    // Wait with exponential backoff
                    await Task.Delay(CalculateDelay(delay), cancellationToken);

                    // Increase delay for next retry
                    delay = TimeSpan.FromSeconds(Math.Min(
                        delay.TotalSeconds * _policy.BackoffMultiplier,
                        _policy.MaxDelay.TotalSeconds
                    ));
                }
                catch (Exception ex)
                {
                    // Max retries exceeded or non-retryable exception
                    _logger?.LogError($"Operation failed after {attempt} attempt(s)", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Calculates delay with optional jitter
        /// </summary>
        private TimeSpan CalculateDelay(TimeSpan baseDelay)
        {
            if (!_policy.UseJitter)
            {
                return baseDelay;
            }

            // Add random jitter (±25%)
            double jitterFactor = 0.75 + (_random.NextDouble() * 0.5);
            return TimeSpan.FromSeconds(baseDelay.TotalSeconds * jitterFactor);
        }
    }
}
