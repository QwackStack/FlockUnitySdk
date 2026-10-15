using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Flock.Providers
{
    internal class FlockAssetCache
    {
        private const string DefaultFolder = "flock_assets";
        private const string CacheExt = ".cache";

        public string Directory { get; }
        public long MaxSizeBytes { get; }

        public FlockAssetCache(string directory, int maxSizeMB = 0)
        {
            Directory = string.IsNullOrEmpty(directory)
                ? Path.Combine(FlockUtil.FlockFilePath, DefaultFolder)
                : directory;
            MaxSizeBytes = maxSizeMB > 0 ? (long)maxSizeMB * 1024 * 1024 : 0;
        }

        private string GetCachePath(string assetId, DateTime updatedAt) =>
            Path.Combine(Directory, $"{Sanitize(assetId)}_{updatedAt.Ticks}{CacheExt}");

        /// <summary>Whether this version of the asset is on disk. Only a question: unlike a read it does not count as a use, so a preload screen asking about every asset does not reorder what is evicted next.</summary>
        public bool Contains(string assetId, DateTime updatedAt) => File.Exists(GetCachePath(assetId, updatedAt));

        public bool TryGetCachedFileUrl(string assetId, DateTime updatedAt, out string fileUrl)
        {
            string path = GetCachePath(assetId, updatedAt);
            if (File.Exists(path))
            {
                try { FlockSavedFiles.SetLastWriteTime(path, DateTime.UtcNow); }
                catch { }

                fileUrl = new Uri(path).AbsoluteUri;
                return true;
            }
            fileUrl = null;
            return false;
        }

        /// <summary>Keeps the bytes for this version of the asset; false when nothing was kept. A file larger than the whole budget is not kept, and nothing is evicted for it.</summary>
        public bool Write(string assetId, DateTime updatedAt, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return false;
            if (MaxSizeBytes > 0 && bytes.Length > MaxSizeBytes)
                return false;

            FlockSavedFiles.CreateFolder(Directory);

            string finalPath = GetCachePath(assetId, updatedAt);

            DeleteOtherVersions(assetId, finalPath);

            // A temporary file of its own per write: two downloads of one asset never share one.
            FlockTemporaryFiles.Save(finalPath, bytes);

            EnforceMaxSize(finalPath);
            return true;
        }

        /// <summary>Deletes temporary files a download that never finished left over a minute ago.</summary>
        public int DeleteLeftOverFiles() => FlockTemporaryFiles.DeleteLeftOverFiles(Directory, false);

        public void Clear()
        {
            if (System.IO.Directory.Exists(Directory))
                FlockSavedFiles.DeleteFolder(Directory, true);
        }

        private void DeleteOtherVersions(string assetId, string keepPath)
        {
            string pattern = $"{Sanitize(assetId)}_*{CacheExt}";
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, pattern))
            {
                if (string.Equals(file, keepPath, StringComparison.Ordinal))
                    continue;
                try { FlockSavedFiles.Delete(file); }
                catch { }
            }
        }

        // Evicts the least recently used files until the cache fits, never the one just kept: it is what the caller is about to
        // read, and a file whose time a moved clock or a coarse timestamp puts behind the others would otherwise go first.
        private void EnforceMaxSize(string keepPath)
        {
            if (MaxSizeBytes <= 0)
                return;

            FileInfo[] files = new DirectoryInfo(Directory)
                .EnumerateFiles($"*{CacheExt}")
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToArray();

            long total = files.Sum(f => f.Length);
            int i = 0;
            while (total > MaxSizeBytes && i < files.Length)
            {
                if (string.Equals(files[i].FullName, Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    continue;
                }
                long len = files[i].Length;
                try
                {
                    FlockSavedFiles.Delete(files[i].FullName);
                    total -= len;
                }
                catch { }
                i++;
            }
        }

        // White list filename-safe chars so a malicious assetId can't escape the cache dir.
        //maybe not needed?
        private static string Sanitize(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "_";

            StringBuilder sb = new StringBuilder(id.Length);
            foreach (char c in id)
            {
                bool safe = (c >= 'a' && c <= 'z')
                            || (c >= 'A' && c <= 'Z')
                            || (c >= '0' && c <= '9')
                            || c == '-' || c == '_';
                sb.Append(safe ? c : '_');
            }
            return sb.ToString();
        }
    }
}
