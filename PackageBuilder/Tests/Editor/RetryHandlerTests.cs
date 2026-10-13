using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Tests.Support;
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

        // RTRY-03: a 408 is provably not processed, so even a money mutation retries it (like 429).
        [Test]
        public void NonIdempotent_Retries_RequestTimeoutStatus()
        {
            int calls = 0;
            RetryHandler handler = NoDelayHandler(maxRetries: 3);

            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => { calls++; throw new FlockNetworkException("request timeout", 408); },
                    CancellationToken.None, retryAmbiguousFailures: false)));

            Assert.AreEqual(4, calls);
        }

        // RTRY-07: validation/auth/serialization errors are authoritative — never retried, even for an idempotent op.
        [Test]
        public void NeverRetries_ValidationError()
        {
            int calls = 0;
            RetryHandler handler = NoDelayHandler(maxRetries: 3);

            Assert.Throws<FlockValidationException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => { calls++; throw new FlockValidationException("bad request"); },
                    CancellationToken.None, retryAmbiguousFailures: true)));

            Assert.AreEqual(1, calls);
        }

        // RTRY-08: cancellation is not a failure — rethrown, never retried.
        [Test]
        public void Cancellation_IsRethrown_NotRetried()
        {
            int calls = 0;
            RetryHandler handler = NoDelayHandler(maxRetries: 3);

            Assert.Catch<OperationCanceledException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => { calls++; throw new OperationCanceledException(); },
                    CancellationToken.None, retryAmbiguousFailures: true)));

            Assert.AreEqual(1, calls);
        }

        // RTRY-11: an explicit maxRetriesOverride of 0 forces a single attempt (the snapshot fast-path).
        [Test]
        public void MaxRetriesOverrideZero_SingleAttempt()
        {
            int calls = 0;
            RetryHandler handler = NoDelayHandler(maxRetries: 3);

            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => { calls++; throw new FlockNetworkException("timeout"); },
                    CancellationToken.None, retryAmbiguousFailures: true, maxRetriesOverride: 0)));

            Assert.AreEqual(1, calls);
        }

        private static RetryHandler NoDelayHandler(int maxRetries, RecordingFlockLogger logger)
            => new RetryHandler(new RetryPolicy { MaxRetries = maxRetries, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, UseJitter = false }, logger);

        // A failure that is never retried is the caller's to report: with retries off it is not warned about, as with them on.
        [Test]
        public void ANeverRetriedFailure_IsNotWarnedAbout_WithRetriesOff()
        {
            RecordingFlockLogger logger = new RecordingFlockLogger();
            RetryHandler handler = NoDelayHandler(0, logger);

            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => throw new FlockNetworkException("not found", 404), CancellationToken.None)));
            Assert.Throws<FlockValidationException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => throw new FlockValidationException("bad request"), CancellationToken.None)));
            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => throw new FlockNetworkException("server error", 500), CancellationToken.None, retryAmbiguousFailures: false)));

            CollectionAssert.IsEmpty(logger.Warnings);
        }

        // A refusal landing on the last try is not warned about either; only the retry before it is.
        [Test]
        public void ANeverRetriedFailure_OnTheLastTry_IsNotWarnedAbout()
        {
            RecordingFlockLogger logger = new RecordingFlockLogger();
            RetryHandler handler = NoDelayHandler(1, logger);
            int calls = 0;

            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => { calls++; throw calls == 1 ? new FlockNetworkException("server error", 500) : new FlockNetworkException("not found", 404); },
                    CancellationToken.None)));

            Assert.AreEqual(2, calls);
            Assert.AreEqual(1, logger.Warnings.Count, "Only the retry: " + string.Join(" | ", logger.Warnings));
            StringAssert.StartsWith("Attempt 1 failed", logger.Warnings[0]);
        }

        // The control: a failure it retries and ran out of tries for is warned about once.
        [Test]
        public void ARetriedFailure_ThatRanOutOfTries_IsWarnedAboutOnce()
        {
            RecordingFlockLogger logger = new RecordingFlockLogger();
            RetryHandler handler = NoDelayHandler(0, logger);

            Assert.Throws<FlockNetworkException>(() => Run(() =>
                handler.ExecuteAsync<int>(() => throw new FlockNetworkException("timeout"), CancellationToken.None)));

            Assert.AreEqual(1, logger.Warnings.Count);
            StringAssert.StartsWith("Operation failed after 1 attempt(s)", logger.Warnings[0]);
        }
    }
}
