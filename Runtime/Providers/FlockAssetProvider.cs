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
        private readonly FlockAssetCache _cache;

        public FlockAssetProvider(FlockClient client) : base(client)
        {
            _cache = new FlockAssetCache(
                client.InitConfig.AssetCacheDirectory,
                client.InitConfig.AssetCacheMaxSizeMB);
        }

        public string CacheDirectory => _cache.Directory;

        public async Task<List<AssetSchema>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/asset";
                GenericResponse<List<AssetSchema>> response = await FlockHttpClient.GetAsync<GenericResponse<List<AssetSchema>>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Fetch assets", cancellationToken);
        }

        public async Task<AssetSchema> GetByIdAsync(string assetId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(assetId, "Asset ID");

            return await ExecuteAsync(async () =>
            {
                string url = $"{Client.GetApiUrl()}/v1/asset/{assetId}";
                GenericResponse<AssetSchema> response = await FlockHttpClient.GetAsync<GenericResponse<AssetSchema>>(
                    url, Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, $"Fetch asset {assetId}", cancellationToken);
        }

        public async Task<AssetSchema> GetByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(name, "Asset Name");
            List<AssetSchema> all = await GetAllAsync(cancellationToken);
            return all?.Find(a => a.Name == name);
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
            bool tryGetCachedFileUrl = _cache.TryGetCachedFileUrl(asset.Id, asset.UpdatedAt, out string cachedUrl);
            bool cacheHit = cacheEnabled && tryGetCachedFileUrl;
            string sourceUrl = cacheHit ? cachedUrl : asset.S3DownloadUrl;

            UnityWebRequest req = BuildRequest<T>(sourceUrl);
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
        }

        
        private static UnityWebRequest BuildRequest<T>(string url) where T : class
        {
            Type t = typeof(T);
            if (t == typeof(Texture2D) || t == typeof(Sprite))
                return UnityWebRequestTexture.GetTexture(url);
            if (t == typeof(AudioClip))
                return UnityWebRequestMultimedia.GetAudioClip(url, AudioType.UNKNOWN);
            return UnityWebRequest.Get(url);
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
