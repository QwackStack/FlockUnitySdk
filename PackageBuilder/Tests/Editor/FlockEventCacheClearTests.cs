using System;
using System.IO;
using Flock.Analytics;
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

        [Test]
        public void Clear_EmptyCache_IsNoOp()
        {
            FlockEventCache<Dummy> cache = new FlockEventCache<Dummy>(_tempDir, "sub", 100, 10, new NullFlockLogger());

            cache.Clear();

            Assert.AreEqual(0, cache.PendingCount);
        }
    }
}
