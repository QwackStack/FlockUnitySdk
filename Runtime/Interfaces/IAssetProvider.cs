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

        Task<T> DownloadAsync<T>(string assetId, CancellationToken cancellationToken = default) where T : class;
        Task<T> DownloadAsync<T>(AssetSchema asset, CancellationToken cancellationToken = default) where T : class;
        Task<List<T>> DownloadAsync<T>(IEnumerable<string> assetIds, CancellationToken cancellationToken = default) where T : class;
        Task<List<T>> DownloadAsync<T>(IEnumerable<AssetSchema> assets, CancellationToken cancellationToken = default) where T : class;
    }
}
