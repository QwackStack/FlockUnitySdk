using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Interfaces;
using Flock.Models;
using UnityEngine;
using UnityEngine.Networking;

namespace Flock.Providers
{
    public class FlockAssetProvider : FlockProviderBase, IAssetProvider
    {
        private const string SnapshotCategory = "asset";
        private const string IndexKey = "asset_index";

        private readonly FlockAssetCache _cache;
        private readonly Dictionary<string, AssetSchema> _assetsById = new Dictionary<string, AssetSchema>();
        private bool _allAssetsFetched;
        private bool _diskIndexLoaded;
        private Task<List<AssetSchema>> _allAssetsFetchTask;

        public FlockAssetProvider(FlockClient client) : base(client)
        {
            _cache = new FlockAssetCache(
                client.InitConfig.AssetCacheDirectory,
                client.InitConfig.AssetCacheMaxSizeMB);
        }

        public string CacheDirectory => _cache.Directory;

        public Task<List<AssetSchema>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            if (_allAssetsFetched)
                return Task.FromResult(new List<AssetSchema>(_assetsById.Values));
            if (_allAssetsFetchTask != null)
                return _allAssetsFetchTask;

            _allAssetsFetchTask = FetchAllAssetsAsync(cancellationToken);
            return _allAssetsFetchTask;
        }

        private async Task<List<AssetSchema>> FetchAllAssetsAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Application.internetReachability == NetworkReachability.NotReachable
                    && TryLoadDiskIndex())
                    return new List<AssetSchema>(_assetsById.Values);

                try
                {
                    List<AssetSchema> assets = await ExecuteAsync(async () =>
                    {
                        string url = $"{Client.GetVersionedApiUrl()}/asset";
                        GenericResponse<List<AssetSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<AssetSchema>>>(
                            url, Client.GetBaseHeaders(), cancellationToken);
                        ValidateResponse(response);
                        return response.Result;
                    }, "Fetch assets", cancellationToken);

                    _assetsById.Clear();
                    foreach (AssetSchema asset in assets)
                        IndexAsset(asset);
                    _allAssetsFetched = true;
                    _diskIndexLoaded = true;
                    PersistIndex();
                    return new List<AssetSchema>(_assetsById.Values);
                }
                catch (FlockNetworkException e)
                {
                    if (!FlockNetworkException.IsPermanentStatus(e.StatusCode) && TryLoadDiskIndex())
                    {
                        Client.Logger.LogWarning("Fetch assets: serving cached snapshot (network unavailable)");
                        return new List<AssetSchema>(_assetsById.Values);
                    }
                    throw;
                }
            }
            finally
            {
                _allAssetsFetchTask = null;
            }
        }

        public async Task<AssetSchema> GetByIdAsync(string assetId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(assetId, "Asset ID");
            if (_assetsById.TryGetValue(assetId, out AssetSchema indexed))
                return indexed;

            if (Application.internetReachability == NetworkReachability.NotReachable
                && TryLoadDiskIndex()
                && _assetsById.TryGetValue(assetId, out AssetSchema offline))
                return offline;

            try
            {
                AssetSchema asset = await ExecuteAsync(async () =>
                {
                    string url = $"{Client.GetVersionedApiUrl()}/asset/{assetId}";
                    GenericResponse<AssetSchema> response = await FlockHttpClient.GetAsync<GenericResponse<AssetSchema>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, $"Fetch asset {assetId}", cancellationToken);

                TryLoadDiskIndex();
                IndexAsset(asset);
                PersistIndex();
                return asset;
            }
            catch (FlockNetworkException e)
            {
                if (!FlockNetworkException.IsPermanentStatus(e.StatusCode)
                    && TryLoadDiskIndex()
                    && _assetsById.TryGetValue(assetId, out AssetSchema fallback))
                {
                    Client.Logger.LogWarning($"Fetch asset {assetId}: serving cached snapshot (network unavailable)");
                    return fallback;
                }
                throw;
            }
        }

        public async Task<AssetSchema> GetByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(name, "Asset Name");
            if (!_allAssetsFetched)
                await GetAllAsync(cancellationToken);

            foreach (AssetSchema asset in _assetsById.Values)
            {
                if (asset.Name == name)
                    return asset;
            }
            return null;
        }

        public async Task<T> DownloadAsync<T>(string assetId, CancellationToken cancellationToken = default) where T : class
        {
            AssetSchema asset = await GetByIdAsync(assetId, cancellationToken);
            return await DownloadAsync<T>(asset, cancellationToken);
        }

        public async Task<T> DownloadAsync<T>(AssetSchema asset, CancellationToken cancellationToken = default) where T : class
        {
            if (asset == null)
                throw new FlockValidationException("Asset cannot be null");
            if (string.IsNullOrEmpty(asset.S3DownloadUrl))
                throw new FlockValidationException($"Asset '{asset.Name ?? asset.Id}' has no download URL");

            bool cacheEnabled = Client.InitConfig.EnableAssetCache;
            if (cacheEnabled && _cache.MaxSizeBytes > 0
                && asset.SizeBytes.HasValue && asset.SizeBytes.Value > _cache.MaxSizeBytes)
            {
                Client.Logger.LogWarning(
                    $"Asset '{asset.Name ?? asset.Id}' ({asset.SizeBytes.Value} bytes) exceeds cache cap " +
                    $"({_cache.MaxSizeBytes} bytes); caching disabled for this asset.");
                cacheEnabled = false;
            }
            bool tryGetCachedFileUrl = _cache.TryGetCachedFileUrl(asset.Id, asset.UpdatedAt, out string cachedUrl);
            bool cacheHit = cacheEnabled && tryGetCachedFileUrl;
            string sourceUrl = cacheHit ? cachedUrl : asset.S3DownloadUrl;

            UnityWebRequest req = BuildRequest<T>(sourceUrl, asset);
            using (req)
            {
                await SendAsync(req, $"Download asset '{asset.Name}'", cancellationToken);

                if (!cacheHit && cacheEnabled)
                {
                    try
                    {
                        _cache.Write(asset.Id, asset.UpdatedAt, req.downloadHandler?.data);
                    }
                    catch (Exception ex)
                    {
                        Client.Logger.LogWarning($"Failed to write asset cache for '{asset.Name}': {ex.Message}");
                    }
                }

                return Extract<T>(req);
            }
        }

        public async Task<List<T>> DownloadAsync<T>(IEnumerable<string> assetIds, CancellationToken cancellationToken = default) where T : class
        {
            if (assetIds == null)
                return new List<T>();

            Task<T>[] tasks = assetIds.Select(id => DownloadAsync<T>(id, cancellationToken)).ToArray();
            T[] results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        public async Task<List<T>> DownloadAsync<T>(IEnumerable<AssetSchema> assets, CancellationToken cancellationToken = default) where T : class
        {
            if (assets == null)
                return new List<T>();

            Task<T>[] tasks = assets.Select(a => DownloadAsync<T>(a, cancellationToken)).ToArray();
            T[] results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        public void ClearCache()
        {
            try
            {
                _cache.Clear();
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Failed to clear asset cache: {ex.Message}");
            }

            _assetsById.Clear();
            _allAssetsFetched = false;
            _diskIndexLoaded = false;
            Client.SnapshotStore?.DeleteScope(GetSnapshotScope(SnapshotCategory));
        }

        private void IndexAsset(AssetSchema asset)
        {
            if (asset == null || string.IsNullOrEmpty(asset.Id))
                return;
            _assetsById[asset.Id] = asset;
        }

        // Merges last-known disk entries under live ones so a partial run never clobbers a fuller index.
        private bool TryLoadDiskIndex()
        {
            if (_diskIndexLoaded)
                return _assetsById.Count > 0;

            _diskIndexLoaded = true;
            FlockSnapshotStore store = Client.SnapshotStore;
            if (store == null)
                return false;

            if (!store.TryRead(GetSnapshotScope(SnapshotCategory), IndexKey, out List<AssetSchema> assets))
                return false;

            foreach (AssetSchema asset in assets)
            {
                if (asset != null && !string.IsNullOrEmpty(asset.Id) && !_assetsById.ContainsKey(asset.Id))
                    _assetsById[asset.Id] = asset;
            }
            return _assetsById.Count > 0;
        }

        private void PersistIndex()
        {
            Client.SnapshotStore?.Write(GetSnapshotScope(SnapshotCategory), IndexKey, new List<AssetSchema>(_assetsById.Values));
        }

        // Reports literal on-disk presence; does not consult EnableAssetCache.
        public bool IsCached(string assetId, DateTime updatedAt)
        {
            if (string.IsNullOrEmpty(assetId)) return false;
            return _cache.TryGetCachedFileUrl(assetId, updatedAt, out _);
        }

        public bool IsCached(AssetSchema asset)
        {
            return asset != null && IsCached(asset.Id, asset.UpdatedAt);
        }

        // Downloads bytes into the disk cache without decoding into a typed Unity
        // object. Cache hits short-circuit, so calling twice for an unchanged asset
        // is cheap.
        public Task PreloadAsync(string assetId, CancellationToken cancellationToken = default)
        {
            return DownloadAsync<byte[]>(assetId, cancellationToken);
        }

        public Task PreloadAsync(AssetSchema asset, CancellationToken cancellationToken = default)
        {
            return DownloadAsync<byte[]>(asset, cancellationToken);
        }

        
        private static UnityWebRequest BuildRequest<T>(string url, AssetSchema asset) where T : class
        {
            Type t = typeof(T);
            if (t == typeof(Texture2D) || t == typeof(Sprite))
                return UnityWebRequestTexture.GetTexture(url);
            if (t == typeof(AudioClip))
                return UnityWebRequestMultimedia.GetAudioClip(url, ResolveAudioType(asset?.ExtensionType));
            return UnityWebRequest.Get(url);
        }

        private static AudioType ResolveAudioType(string extensionType)
        {
            if (string.IsNullOrEmpty(extensionType)) return AudioType.UNKNOWN;
            switch (extensionType.Trim().TrimStart('.').ToLowerInvariant())
            {
                case "mp3": return AudioType.MPEG;
                case "wav": return AudioType.WAV;
                case "ogg": return AudioType.OGGVORBIS;
                case "aif":
                case "aiff": return AudioType.AIFF;
                default: return AudioType.UNKNOWN;
            }
        }

        private static T Extract<T>(UnityWebRequest req) where T : class
        {
            Type t = typeof(T);
            if (t == typeof(Texture2D))
                return DownloadHandlerTexture.GetContent(req) as T;
            if (t == typeof(Sprite))
            {
                Texture2D tex = DownloadHandlerTexture.GetContent(req);
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f)) as T;
            }
            if (t == typeof(AudioClip))
                return DownloadHandlerAudioClip.GetContent(req) as T;
            if (t == typeof(string))
                return req.downloadHandler.text as T;
            if (t == typeof(byte[]))
                return req.downloadHandler.data as T;

            throw new FlockValidationException($"Unsupported asset type: {t.Name}");
        }

        private static async Task SendAsync(UnityWebRequest req, string context, CancellationToken cancellationToken)
        {
            UnityWebRequestAsyncOperation op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    req.Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await Task.Yield();
            }

            if (req.result != UnityWebRequest.Result.Success)
                throw new FlockNetworkException($"{context} failed: {req.error}");
        }
    }
}
