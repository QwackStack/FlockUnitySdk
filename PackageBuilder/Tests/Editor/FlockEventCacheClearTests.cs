using System;
using System.IO;
using System.Threading;
using Flock.Analytics;
using Flock.Exceptions;
using Flock.Logging;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // First-time coverage for Clear() — EraseLocalAnalyticsData (consent feature) becomes
    // its first real caller, so its on-disk + in-memory behavior needs to be locked down.
    public class FlockEventCacheClearTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "flock_cache_test_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        private class Dummy
        {
            public string Value { get; set; }
        }

        [Test]
        public void Clear_WithPendingEvents_RemovesAllFilesAndResetsCount()
        {
            FlockEventCache<Dummy> cache = new FlockEventCache<Dummy>(_tempDir, "sub", 100, 10, new NullFlockLogger());
            cache.Enqueue(new Dummy { Value = "a" });
            cache.Enqueue(new Dummy { Value = "b" });
            Assert.AreEqual(2, cache.PendingCount);

            cache.Clear();

            Assert.AreEqual(0, cache.PendingCount);
            string subDir = Path.Combine(_tempDir, "sub");
            Assert.AreEqual(0, Directory.GetFiles(subDir).Length);
        }

        // ---- A coded 403 must drop the batch, not defer it forever ----
        // FlockAuthException derives from FlockException, not FlockNetworkException, so it falls past the
        // permanent-status catch and lands in the generic "transient" one. An authoritative 403 then parks the
        // batch at the head of the cache and re-sends it on every flush — two POSTs plus a refresh each time —
        // until TrimOldest evicts it at the cap.
        [Test]
        public void Flush_CodedForbidden_DropsBatch()
        {
            FlockEventCache<Dummy> cache = new FlockEventCache<Dummy>(_tempDir, "sub", 100, 10, new NullFlockLogger());
            cache.Enqueue(new Dummy { Value = "a" });

            cache.FlushAsync((batch, ct) => throw new FlockAuthException("Authentication failed (HTTP 403)")
            {
                StatusCode = 403,
                Code = "player.forbidden"
            }, CancellationToken.None).GetAwaiter().GetResult();

            Assert.AreEqual(0, cache.PendingCount, "A backend-coded 403 will never succeed on replay — drop it rather than stalling the cache.");
        }

        // ---- 401 and a bare 403 must still be kept ----
        // 401 clears on re-login; an uncoded 403 is a proxy or WAF, not the backend's answer. Dropping either
        // would discard telemetry over a condition that resolves itself.
        [TestCase(401, null, TestName = "Flush_Unauthorized_KeepsBatch")]
        [TestCase(403, null, TestName = "Flush_BareForbidden_KeepsBatch")]
        public void Flush_RecoverableAuthFailure_KeepsBatch(int status, string code)
        {
            FlockEventCache<Dummy> cache = new FlockEventCache<Dummy>(_tempDir, "sub", 100, 10, new NullFlockLogger());
            cache.Enqueue(new Dummy { Value = "a" });

            cache.FlushAsync((batch, ct) => throw new FlockAuthException($"Authentication failed (HTTP {status})")
            {
                StatusCode = status,
                Code = code
            }, CancellationToken.None).GetAwaiter().GetResult();

            Assert.AreEqual(1, cache.PendingCount, "Recoverable auth failures must leave the batch for the next attempt.");
        }

        [Test]
        public void Clear_EmptyCache_IsNoOp()
        {
            FlockEventCache<Dummy> cache = new FlockEventCache<Dummy>(_tempDir, "sub", 100, 10, new NullFlockLogger());

            cache.Clear();

            Assert.AreEqual(0, cache.PendingCount);
        }
    }
}
