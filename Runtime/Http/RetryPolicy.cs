using System;
using System.Text;
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

        public RetryHandler(RetryPolicy policy, IFlockLogger logger)
        {
            _policy = policy ?? new RetryPolicy();
            _logger = logger;
        }

        public async Task<T> ExecuteAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default, bool shouldRetryOnException = true)
        {
            int attempt = 0;
            TimeSpan delay = _policy.InitialDelay;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    attempt++;
                    if (attempt > 1)
                    {
                        _logger?.LogDebug(new StringBuilder().Append("Attempt ")
                            .Append(attempt)
                            .Append("/")
                            .Append(_policy.MaxRetries + 1)
                            .ToString());
                    }
                    return await operation();
                }
                catch (Exception ex) when (shouldRetryOnException && attempt <= _policy.MaxRetries)
                {
                    if (ex is FlockAuthException || ex is FlockValidationException)
                        throw;

                    _logger?.LogWarning(new StringBuilder().Append("Attempt ")
                        .Append(attempt)
                        .Append(" failed: ")
                        .Append(ex.Message)
                        .Append(". Retrying in ")
                        .Append(delay.TotalSeconds)
                        .Append("s...")
                        .ToString());
                    await Task.Delay(CalculateDelay(delay), cancellationToken);

                    delay = TimeSpan.FromSeconds(Math.Min(
                        delay.TotalSeconds * _policy.BackoffMultiplier,
                        _policy.MaxDelay.TotalSeconds
                    ));
                }
                catch (Exception ex)
                {
                    _logger?.LogError(
                        new StringBuilder().Append("Operation failed after ")
                            .Append(attempt)
                            .Append(" attempt(s)")
                            .ToString(), ex);
                    throw;
                }
            }
        }

        private TimeSpan CalculateDelay(TimeSpan baseDelay)
        {
            if (!_policy.UseJitter)
                return baseDelay;

            double jitterFactor = 0.75 + (_random.NextDouble() * 0.5); // ±25% , should I expose this?
            return TimeSpan.FromSeconds(baseDelay.TotalSeconds * jitterFactor);
        }
    }
}