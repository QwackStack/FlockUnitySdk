using System;
using System.Collections.Generic;
using Flock.Models;
using Flock.Providers;
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
                if (asset != null && !cache.TryGetCachedFileUrl(asset.Id, asset.UpdatedAt, out _))
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
    }
}
