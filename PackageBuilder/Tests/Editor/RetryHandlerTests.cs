using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using NUnit.Framework;

namespace Flock.Tests
{
    // Locks the retry contract that protects non-idempotent money mutations (AddGameFunds, shop
    // Purchase) from being silently re-sent after an ambiguous failure, while still retrying
    // failures the server provably didn't process (408/429).
    public class RetryHandlerTests
    {
        private static RetryHandler NoDelayHandler(int maxRetries)
        {
            RetryPolicy policy = new RetryPolicy
            {
                MaxRetries = maxRetries,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                UseJitter = false
            };
            return new RetryHandler(policy, null);
        }

        // Runs the handler off Unity's sync-context so blocking for the result can't deadlock the test thread.
        private static T Run<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();

        // Idempotent op: an ambiguous failure (no status — client timeout/dropped connection) retries to the cap.
        [Test]
        public void Idempotent_Retries_AmbiguousFailure()
        {
            int calls = 0;
            RetryHandler handler = NoDelayHandler(maxRetries: 3);

            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => { calls++; throw new FlockNetworkException("timeout"); },
                    CancellationToken.None, retryAmbiguousFailures: true)));

            Assert.AreEqual(4, calls); // initial attempt + 3 retries
        }

        // Money-mutation guarantee: an ambiguous failure (no status) surfaces after a single attempt.
        [Test]
        public void NonIdempotent_DoesNotRetry_AmbiguousFailure()
        {
            int calls = 0;
            RetryHandler handler = NoDelayHandler(maxRetries: 3);

            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => { calls++; throw new FlockNetworkException("timeout"); },
                    CancellationToken.None, retryAmbiguousFailures: false)));

            Assert.AreEqual(1, calls);
        }

        // Money-mutation guarantee: a 5xx is ambiguous (the server may have committed) — surfaced, not retried.
        [Test]
        public void NonIdempotent_DoesNotRetry_ServerError()
        {
            int calls = 0;
            RetryHandler handler = NoDelayHandler(maxRetries: 3);

            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => { calls++; throw new FlockNetworkException("server", 500); },
                    CancellationToken.None, retryAmbiguousFailures: false)));

            Assert.AreEqual(1, calls);
        }

        // Even a money mutation safely retries a 429 — the server rejected it before processing.
        [Test]
        public void NonIdempotent_Retries_NotProcessedStatus()
        {
            int calls = 0;
            RetryHandler handler = NoDelayHandler(maxRetries: 3);

            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => { calls++; throw new FlockNetworkException("rate limited", 429); },
                    CancellationToken.None, retryAmbiguousFailures: false)));

            Assert.AreEqual(4, calls); // 429 is provably not processed, so retried to the cap
        }

        // Permanent 4xx (e.g. 409) is an authoritative answer — never retried, even for an idempotent op.
        [Test]
        public void Idempotent_DoesNotRetry_PermanentStatus()
        {
            int calls = 0;
            RetryHandler handler = NoDelayHandler(maxRetries: 3);

            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => { calls++; throw new FlockNetworkException("conflict", 409); },
                    CancellationToken.None, retryAmbiguousFailures: true)));

            Assert.AreEqual(1, calls);
        }

        // A successful op runs exactly once and returns its value.
        [Test]
        public void ReturnsResult_OnSuccess()
        {
            int calls = 0;
            RetryHandler handler = NoDelayHandler(maxRetries: 3);

            int result = Run(() => handler.ExecuteAsync<int>(() => { calls++; return Task.FromResult(42); },
                CancellationToken.None, retryAmbiguousFailures: false));

            Assert.AreEqual(42, result);
            Assert.AreEqual(1, calls);
        }
    }
}
