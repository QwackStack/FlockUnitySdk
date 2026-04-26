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
        private const string TempExt = ".tmp";

        public string Directory { get; }
        public long MaxSizeBytes { get; }

        public FlockAssetCache(string directory, int maxSizeMB = 0)
        {
            Directory = string.IsNullOrEmpty(directory)
                ? Path.Combine(Application.persistentDataPath, DefaultFolder)
                : directory;
            MaxSizeBytes = maxSizeMB > 0 ? (long)maxSizeMB * 1024 * 1024 : 0;
        }

        private string GetCachePath(string assetId, DateTime updatedAt) =>
            Path.Combine(Directory, $"{Sanitize(assetId)}_{updatedAt.Ticks}{CacheExt}");

        public bool TryGetCachedFileUrl(string assetId, DateTime updatedAt, out string fileUrl)
        {
            string path = GetCachePath(assetId, updatedAt);
            if (File.Exists(path))
            {
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
                catch { }

                fileUrl = new Uri(path).AbsoluteUri;
                return true;
            }
            fileUrl = null;
            return false;
        }

        public void Write(string assetId, DateTime updatedAt, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return;

            System.IO.Directory.CreateDirectory(Directory);

            string finalPath = GetCachePath(assetId, updatedAt);

            DeleteOtherVersions(assetId, finalPath);

            string tmpPath = finalPath + TempExt;
            File.WriteAllBytes(tmpPath, bytes);
            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(tmpPath, finalPath);

            EnforceMaxSize();
        }

        public void Clear()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, true);
        }

        private void DeleteOtherVersions(string assetId, string keepPath)
        {
            string pattern = $"{Sanitize(assetId)}_*{CacheExt}";
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, pattern))
            {
                if (string.Equals(file, keepPath, StringComparison.Ordinal))
                    continue;
                try { File.Delete(file); }
                catch { }
            }
        }

        private void EnforceMaxSize()
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
                long len = files[i].Length;
                try
                {
                    files[i].Delete();
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
