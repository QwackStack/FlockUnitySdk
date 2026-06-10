using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Flock.Logging;
using Newtonsoft.Json;

namespace Flock.Providers
{
    internal class FlockSnapshotStore
    {
        public const string BootstrapScope = "bootstrap";

        private const int EnvelopeVersion = 1;
        private const string DefaultFolder = "snapshots";
        private const string Extension = ".json";

        private readonly string _root;
        private readonly IFlockLogger _logger;

        private class Envelope<T>
        {
            [JsonProperty("v")] public int Version { get; set; }
            [JsonProperty("sdk")] public string Sdk { get; set; }
            [JsonProperty("stored_at_utc")] public string StoredAtUtc { get; set; }
            [JsonProperty("etag")] public string ETag { get; set; }
            [JsonProperty("data")] public T Data { get; set; }
        }

        public FlockSnapshotStore(string rootDirectory, IFlockLogger logger)
        {
            _root = string.IsNullOrEmpty(rootDirectory)
                ? Path.Combine(FlockUtil.FlockFilePath, DefaultFolder)
                : rootDirectory;
            _logger = logger;
        }

        public bool TryRead<T>(string scope, string key, out T value) where T : class
        {
            value = null;
            string path = BuildPath(scope, key);
            if (!File.Exists(path))
                return false;

            try
            {
                Envelope<T> envelope = JsonConvert.DeserializeObject<Envelope<T>>(File.ReadAllText(path));
                if (envelope == null || envelope.Version != EnvelopeVersion || envelope.Data == null)
                {
                    TryDelete(path);
                    return false;
                }

                value = envelope.Data;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Snapshot read failed for {scope}/{key}: {ex.Message}");
                TryDelete(path);
                return false;
            }
        }

        public void Write<T>(string scope, string key, T value) where T : class
        {
            if (value == null)
                return;

            string path = BuildPath(scope, key);
            string tmpPath = path + ".tmp";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                Envelope<T> envelope = new Envelope<T>
                {
                    Version = EnvelopeVersion,
                    Sdk = FlockSdkVersion.Current,
                    StoredAtUtc = DateTime.UtcNow.ToString("o"),
                    Data = value
                };

                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(envelope));
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmpPath, path);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Snapshot write failed for {scope}/{key}: {ex.Message}");
                TryDelete(tmpPath);
            }
        }

        public void DeleteScope(string scope)
        {
            try
            {
                string path = Path.Combine(_root, SanitizeScope(scope));
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Snapshot scope delete failed for {scope}: {ex.Message}");
            }
        }

        public void PruneOtherVersions(string keepGameVersionId)
        {
            if (string.IsNullOrEmpty(keepGameVersionId) || !Directory.Exists(_root))
                return;

            string keep = Sanitize(keepGameVersionId);
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(_root))
                {
                    string name = Path.GetFileName(dir);
                    if (string.Equals(name, keep, StringComparison.Ordinal)
                        || string.Equals(name, BootstrapScope, StringComparison.Ordinal))
                        continue;

                    try { Directory.Delete(dir, true); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Snapshot prune failed: {ex.Message}");
            }
        }

        private string BuildPath(string scope, string key)
        {
            return Path.Combine(_root, SanitizeScope(scope), $"{Sanitize(key)}_{Hash8(key)}{Extension}");
        }

        private static string SanitizeScope(string scope)
        {
            string[] segments = scope.Split('/');
            for (int i = 0; i < segments.Length; i++)
                segments[i] = Sanitize(segments[i]);
            return Path.Combine(segments);
        }

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
            return sb.Length <= 64 ? sb.ToString() : sb.ToString(0, 64);
        }

        private static string Hash8(string key)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? string.Empty));
                StringBuilder sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
