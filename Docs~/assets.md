# Assets

[← Back to README](../README.md)

Assets are stand-alone files you upload via the Flock dashboard (images, audio,
JSON, raw bytes) and download at runtime — think "files on a CDN with metadata".
Good for content you want to swap without rebuilding the game (icons, sound effects,
art swaps). NOT a replacement for Unity Addressables: prefabs, scenes,
ScriptableObjects, materials and shaders still need Unity's own pipeline.

## Listing and finding assets

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock;
using Flock.Models;
using UnityEngine;

public static class AssetLookup
{
    public static async Task ListAsync()
    {
        List<AssetSchema> assets = await FlockClient.Instance.Asset.GetAllAsync();
        foreach (AssetSchema asset in assets)
            Debug.Log($"{asset.Name} ({asset.Id})");
    }

    public static Task<AssetSchema> FindByIdAsync(string assetId)
    {
        return FlockClient.Instance.Asset.GetByIdAsync(assetId);
    }

    // Filters the full list on the device; throws FlockException when no asset has that name.
    public static Task<AssetSchema> FindByNameAsync(string assetName)
    {
        return FlockClient.Instance.Asset.GetByNameAsync(assetName);
    }
}
```

An `AssetSchema` carries the asset's `Id`, `Name`, `SizeBytes`, `UpdatedAt` and `S3DownloadUrl` (the direct download
address), so you can also download it your own way.

## Downloading

`DownloadAsync<T>` downloads an asset and turns it into a Unity type. `T` is one of `Texture2D`, `Sprite`, `AudioClip`,
`string` or `byte[]`.

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock;
using Flock.Models;
using UnityEngine;

public class ShopIcons : MonoBehaviour
{
    [SerializeField] private SpriteRenderer iconRenderer;

    // By id: one extra call to look the asset up.
    public async Task ShowIconAsync(string assetId)
    {
        iconRenderer.sprite = await FlockClient.Instance.Asset.DownloadAsync<Sprite>(assetId);
    }

    // With an asset already read (skips the lookup), reporting progress from 0 to 1.
    public async Task ShowIconAsync(AssetSchema asset)
    {
        Progress<float> progress = new Progress<float>(done => Debug.Log($"Downloading {done:P0}"));
        iconRenderer.sprite = await FlockClient.Instance.Asset.DownloadAsync<Sprite>(asset, progress);
    }

    // Several at once, at most Asset Max Concurrent Downloads (4) at a time.
    public Task<List<Sprite>> DownloadAllAsync(List<string> assetIds)
    {
        return FlockClient.Instance.Asset.DownloadAsync<Sprite>(assetIds);
    }
}
```

The most downloads at once is `FlockInitConfig.AssetMaxConcurrentDownloads` (4 by default). It has no field in
**Flock > Settings**: set it in code, on a game that starts the SDK itself
([Code-Based Configuration](../README.md#code-based-configuration)).

## The disk cache

Downloads are kept on disk, by asset id and `UpdatedAt`, so an asset is downloaded again only after it changes in the
dashboard. Set the cache in **Flock > Settings**, **Advanced Settings** tab, **Asset Cache** card:

| Field | Default | What it does |
|---|---|---|
| **Enable Asset Cache** | on | Keep downloads on disk. Turn it off for WebGL (below). |
| **Asset Cache Directory** | empty: `Application.persistentDataPath/Flock/flock_assets` | Where the cache is kept (an absolute path). |
| **Asset Cache Max Size MB** | 100 | Past this size the least recently used entries are deleted; 0 means no limit. |
| **Asset Download Timeout Seconds** | 0 (none) | How long one download may take. |
| **Asset Download Retry Count** | 3 | How many times a failed download is tried again; only passing failures are. |

Each write goes to a temporary file first and is then moved into place, and an older copy of the same asset is deleted
when a newer one is kept.

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock;
using Flock.Models;
using UnityEngine;

public static class AssetCacheTools
{
    public static void Describe(AssetSchema asset, List<AssetSchema> assets)
    {
        Debug.Log($"Cache folder: {FlockClient.Instance.Asset.CacheDirectory}");

        // Is this version of the asset on disk already? No download.
        bool ready = FlockClient.Instance.Asset.IsCached(asset);

        // The assets in a list that are not on disk yet.
        List<AssetSchema> missing = FlockClient.Instance.Asset.GetUncached(assets);
        Debug.Log($"Ready: {ready}; still to download: {missing.Count}");
    }

    // Downloads into the cache without turning the file into a Unity type; a cached asset is not downloaded again.
    public static async Task WarmUpAsync(AssetSchema asset)
    {
        await FlockClient.Instance.Asset.PreloadAsync(asset);

        // Or every asset that matches: here, all under 1 MB.
        await FlockClient.Instance.Asset.PreloadAsync(a => a.SizeBytes.HasValue && a.SizeBytes.Value < 1_000_000);
    }

    // Deletes every cached download.
    public static void Clear()
    {
        FlockClient.Instance.Asset.ClearCache();
    }
}
```

> **WebGL:** the asset cache writes files synchronously, which a browser's storage does not support — untick **Enable
> Asset Cache** in **Flock > Settings**, **Advanced Settings** tab, for a WebGL build (or set
> `FlockInitConfig.EnableAssetCache = false` when you start the SDK yourself). See
> [Platform notes](../README.md#platform-notes).
