using System;
using System.Collections.Generic;
using Flock.Models;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    public class FlockAssetCacheGetUncachedTests
    {
        private FlockAssetCache _cache;
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "flock_test_" + Guid.NewGuid().ToString("N"));
            _cache = new FlockAssetCache(_tempDir, 0);
        }

        [TearDown]
        public void TearDown()
        {
            if (System.IO.Directory.Exists(_tempDir))
                System.IO.Directory.Delete(_tempDir, true);
        }

        [Test]
        public void GetUncached_NullInput_ReturnsEmptyList()
        {
            List<AssetSchema> result = FilterUncached(null, _cache);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void GetUncached_AllUncached_ReturnsAll()
        {
            AssetSchema a = new AssetSchema { Id = "a1", UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            AssetSchema b = new AssetSchema { Id = "b1", UpdatedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) };

            List<AssetSchema> result = FilterUncached(new List<AssetSchema> { a, b }, _cache);

            Assert.AreEqual(2, result.Count);
        }

        [Test]
        public void GetUncached_OneCached_ExcludesCachedEntry()
        {
            DateTime updatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            AssetSchema cached = new AssetSchema
            {
                Id = "cached1",
                UpdatedAt = updatedAt,
                S3DownloadUrl = "https://example.com/fake"
            };
            AssetSchema uncached = new AssetSchema
            {
                Id = "uncached1",
                UpdatedAt = updatedAt
            };

            System.IO.Directory.CreateDirectory(_tempDir);
            _cache.Write(cached.Id, cached.UpdatedAt, new byte[] { 1, 2, 3 });

            List<AssetSchema> result = FilterUncached(new List<AssetSchema> { cached, uncached }, _cache);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("uncached1", result[0].Id);
        }

        // Mirrors GetUncached logic without requiring a live FlockClient
        private static List<AssetSchema> FilterUncached(IEnumerable<AssetSchema> assets, FlockAssetCache cache)
        {
            List<AssetSchema> result = new List<AssetSchema>();
            if (assets == null)
                return result;
            foreach (AssetSchema asset in assets)
            {
                if (asset != null && !cache.Contains(asset.Id, asset.UpdatedAt))
                    result.Add(asset);
            }
            return result;
        }
    }

    public class FlockAssetCacheWriteTests
    {
        private string _tempDir;
        private FlockAssetCache _cache;

        [SetUp]
        public void SetUp()
        {
            _tempDir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "flock_cache_test_" + Guid.NewGuid().ToString("N"));
            _cache = new FlockAssetCache(_tempDir, 0);
        }

        [TearDown]
        public void TearDown()
        {
            if (System.IO.Directory.Exists(_tempDir))
                System.IO.Directory.Delete(_tempDir, true);
        }

        [Test]
        public void Write_ThenTryGet_ReturnsTrue()
        {
            DateTime ts = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            _cache.Write("asset-xyz", ts, new byte[] { 10, 20, 30 });

            bool hit = _cache.TryGetCachedFileUrl("asset-xyz", ts, out string url);

            Assert.IsTrue(hit);
            Assert.IsNotNull(url);
        }

        [Test]
        public void Write_DifferentUpdatedAt_Miss()
        {
            DateTime writtenAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime queriedAt = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            _cache.Write("asset-xyz", writtenAt, new byte[] { 1 });

            bool hit = _cache.TryGetCachedFileUrl("asset-xyz", queriedAt, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void Write_NewVersion_DeletesOldVersion()
        {
            DateTime v1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime v2 = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            _cache.Write("asset-abc", v1, new byte[] { 1 });
            _cache.Write("asset-abc", v2, new byte[] { 2 });

            Assert.IsFalse(_cache.TryGetCachedFileUrl("asset-abc", v1, out _));
            Assert.IsTrue(_cache.TryGetCachedFileUrl("asset-abc", v2, out _));
        }

        // ---- Asking whether an asset is cached does not count as a use ----
        // A preload screen asks about every asset; if asking stamped them, the least recently used would no longer go first.
        [Test]
        public void IsCached_DoesNotChangeWhatIsEvictedNext()
        {
            DateTime v = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using (FlockTestClient h = FlockTestClient.Create(new FlockFakeTransport(), config =>
            {
                config.AssetCacheDirectory = _tempDir;
                config.AssetCacheMaxSizeMB = 1;
            }))
            {
                // Another holder of the same folder fills it, the way an earlier download would have.
                FlockAssetCache cache = new FlockAssetCache(_tempDir, 1);
                Assert.IsTrue(cache.Write("old", v, new byte[400 * 1024]));
                Assert.IsTrue(cache.Write("newer", v, new byte[400 * 1024]));
                foreach (System.IO.FileInfo file in new System.IO.DirectoryInfo(_tempDir).GetFiles("*.cache"))
                    file.LastWriteTimeUtc = file.Name.StartsWith("old") ? DateTime.UtcNow.AddHours(-2) : DateTime.UtcNow.AddHours(-1);

                Assert.IsTrue(h.Client.Asset.IsCached("old", v), "Precondition: it is there.");
                Assert.IsTrue(cache.Write("third", v, new byte[400 * 1024]));

                Assert.IsFalse(cache.Contains("old", v), "The least recently used went first, though it was asked about.");
                Assert.IsTrue(cache.Contains("newer", v));
                Assert.IsTrue(cache.Contains("third", v));
            }
        }

        // ---- A read, unlike a question, does count as a use ----
        [Test]
        public void TryGetCachedFileUrl_CountsAsAUse()
        {
            FlockAssetCache cache = new FlockAssetCache(_tempDir, 1);
            DateTime v = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            cache.Write("old", v, new byte[400 * 1024]);
            cache.Write("newer", v, new byte[400 * 1024]);
            foreach (System.IO.FileInfo file in new System.IO.DirectoryInfo(_tempDir).GetFiles("*.cache"))
                file.LastWriteTimeUtc = file.Name.StartsWith("old") ? DateTime.UtcNow.AddHours(-2) : DateTime.UtcNow.AddHours(-1);

            Assert.IsTrue(cache.TryGetCachedFileUrl("old", v, out _));
            cache.Write("third", v, new byte[400 * 1024]);

            Assert.IsTrue(cache.Contains("old", v), "Read just now, so kept.");
            Assert.IsFalse(cache.Contains("newer", v), "The least recently used went instead.");
        }

        // ---- A file larger than the whole budget is not kept, and nothing is evicted for it ----
        [Test]
        public void Write_LargerThanTheBudget_IsNotKept()
        {
            FlockAssetCache cache = new FlockAssetCache(_tempDir, 1);
            DateTime v = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.IsTrue(cache.Write("small", v, new byte[] { 1 }));

            Assert.IsFalse(cache.Write("big", v, new byte[2 * 1024 * 1024]), "Not kept.");
            Assert.IsFalse(cache.Contains("big", v));
            Assert.IsTrue(cache.Contains("small", v), "Nothing else was evicted for it.");
        }

        // ---- Making room never evicts the file just kept, whatever the other files' times say ----
        [Test]
        public void Write_MakingRoomNeverEvictsTheFileJustKept()
        {
            FlockAssetCache cache = new FlockAssetCache(_tempDir, 1);
            DateTime v = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.IsTrue(cache.Write("other", v, new byte[600 * 1024]));
            // The other file reads as used later than the next one: a clock moved back.
            foreach (System.IO.FileInfo file in new System.IO.DirectoryInfo(_tempDir).GetFiles("*.cache"))
                file.LastWriteTimeUtc = DateTime.UtcNow.AddHours(1);

            Assert.IsTrue(cache.Write("taken", v, new byte[600 * 1024]));

            Assert.IsTrue(cache.Contains("taken", v), "The file just kept is still there to read.");
            Assert.IsFalse(cache.Contains("other", v), "The other made room for it.");
        }
    }
}
