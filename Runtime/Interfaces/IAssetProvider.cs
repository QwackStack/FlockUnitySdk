using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Interfaces
{
    public interface IAssetProvider
    {
        Task<List<AssetSchema>> GetAllAsync(CancellationToken cancellationToken = default);
        Task<AssetSchema> GetByIdAsync(string assetId, CancellationToken cancellationToken = default);
        Task<AssetSchema> GetByNameAsync(string name, CancellationToken cancellationToken = default);

        string CacheDirectory { get; }
        void ClearCache();
        bool IsCached(string assetId, DateTime updatedAt);
        bool IsCached(AssetSchema asset);

        /// <summary>Returns assets from the given list that are not currently on disk for their current UpdatedAt.</summary>
        List<AssetSchema> GetUncached(IEnumerable<AssetSchema> assets);

        // Download — basic overloads
        Task<T> DownloadAsync<T>(string assetId, CancellationToken cancellationToken = default) where T : class;
        Task<T> DownloadAsync<T>(AssetSchema asset, CancellationToken cancellationToken = default) where T : class;
        Task<List<T>> DownloadAsync<T>(IEnumerable<string> assetIds, CancellationToken cancellationToken = default) where T : class;
        Task<List<T>> DownloadAsync<T>(IEnumerable<AssetSchema> assets, CancellationToken cancellationToken = default) where T : class;

        // Download — with progress (0→1 per individual asset)
        Task<T> DownloadAsync<T>(string assetId, IProgress<float> progress, CancellationToken cancellationToken = default) where T : class;
        Task<T> DownloadAsync<T>(AssetSchema asset, IProgress<float> progress, CancellationToken cancellationToken = default) where T : class;

        // Preload — warm disk cache without decoding
        Task PreloadAsync(string assetId, CancellationToken cancellationToken = default);
        Task PreloadAsync(AssetSchema asset, CancellationToken cancellationToken = default);

        /// <summary>
        /// Fetches all assets, filters by <paramref name="predicate"/>, then warms the disk
        /// cache for each match. Cache-hit assets are skipped. Progress (0→1) is reported
        /// across the whole filtered set if <paramref name="progress"/> is supplied.
        /// </summary>
        Task PreloadAsync(Func<AssetSchema, bool> predicate, IProgress<float> progress = null, CancellationToken cancellationToken = default);
    }
}
