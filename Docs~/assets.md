# Assets

[← Back to README](../README.md)

Assets are stand-alone files you upload via the Flock dashboard (images, audio,
JSON, raw bytes) and download at runtime — think "files on a CDN with metadata".
Good for content you want to swap without rebuilding the game (icons, sound effects,
art swaps), and for content shared with the Unreal SDK. NOT a replacement for Unity
Addressables: prefabs, scenes, ScriptableObjects, materials and shaders still need
Unity's own pipeline.

```csharp
// Assets — list / lookup
var assets = await FlockClient.Instance.Asset.GetAllAsync();
var asset = await FlockClient.Instance.Asset.GetByIdAsync("asset-id");
var asset = await FlockClient.Instance.Asset.GetByNameAsync("iconTest"); // O(N) client-side filter; throws FlockException if not found
// asset.S3DownloadUrl is the direct download URL

// Assets — generic typed download. Supported T: Texture2D, Sprite, AudioClip, string, byte[]
// By id (extra lookup round-trip):
Sprite sprite = await FlockClient.Instance.Asset.DownloadAsync<Sprite>("asset-id");
// By already-fetched schema (skips the lookup):
Sprite sprite = await FlockClient.Instance.Asset.DownloadAsync<Sprite>(asset);
// With download progress (0 → 1):
var progress = new Progress<float>(p => Debug.Log($"Downloading {p:P0}"));
Sprite sprite = await FlockClient.Instance.Asset.DownloadAsync<Sprite>(asset, progress);

// Batch downloads — throttled to FlockInitConfig.AssetMaxConcurrentDownloads (default 4)
List<Sprite> sprites = await FlockClient.Instance.Asset.DownloadAsync<Sprite>(new[] { "id1", "id2", "id3" });
List<Sprite> sprites = await FlockClient.Instance.Asset.DownloadAsync<Sprite>(assets);

// Asset cache — downloads are cached on disk, keyed by asset ID + UpdatedAt.
// Default location: Application.persistentDataPath/flock_assets
// Override via FlockInitConfig.AssetCacheDirectory; disable via EnableAssetCache=false.
// Cap total size with FlockInitConfig.AssetCacheMaxSizeMB (default 100 MB; 0 = unlimited).
// When the cap is exceeded the oldest entries are evicted (LRU by last access).
// Writes are atomic (.tmp + move) and previous versions of the same asset
// are deleted automatically when a newer UpdatedAt is cached.
string cacheDir = FlockClient.Instance.Asset.CacheDirectory; // resolved absolute path
FlockClient.Instance.Asset.ClearCache();

// Warm the disk cache at boot without decoding into a Unity type.
// Cache-hit short-circuits, so re-calling for an unchanged asset is cheap.
await FlockClient.Instance.Asset.PreloadAsync(asset);
// Predicate-based bulk preload — e.g. warm all assets under 1 MB at startup:
await FlockClient.Instance.Asset.PreloadAsync(
    a => a.SizeBytes.HasValue && a.SizeBytes.Value < 1_000_000, progress);

// Ask "is this asset already on disk for this UpdatedAt?" without downloading.
bool ready = FlockClient.Instance.Asset.IsCached(asset);

// Filter a list to only the entries not yet on disk:
List<AssetSchema> missing = FlockClient.Instance.Asset.GetUncached(assets);
```

> **WebGL:** the asset cache is backed by synchronous file writes, which WebGL's IndexedDB-backed storage doesn't support — set `FlockInitConfig.EnableAssetCache = false` there. See [Platform notes](../README.md#platform-notes).
