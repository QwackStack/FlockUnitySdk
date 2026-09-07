using System;
using System.Collections.Generic;
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

        /// <summary>Reserved first scope segment for STATE, as opposed to cache. Never pruned.</summary>
        /// <remarks>
        /// <para>
        /// Snapshots are scoped by game version and every other version is deleted at startup, which is right
        /// for a cached response and catastrophic for anything that is not re-fetchable. A queued offline write
        /// exists nowhere else — the server has never seen it — so deleting it does not cost a request, it
        /// loses the player's data. Nothing under this segment is touched by <see cref="PruneOtherVersions"/>.
        /// </para>
        /// <para>
        /// Compose a state scope with this first and the owning player last: <c>_state/command/{playerId}</c>.
        /// Cache keeps the version first, unchanged: <c>{gameVersionId}/leaderboard</c>.
        /// </para>
        /// <para>
        /// Safe as a reserved name because a game version id is a ULID (<c>[0-9A-Z]</c>) and can never sanitize
        /// to it, and an unset version sanitizes to <c>"_"</c> rather than to it.
        /// </para>
        /// </remarks>
        public const string StateScope = "_state";

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

        /// <summary>Persists a snapshot. Returns false if it did not reach disk, so callers holding data that only exists here can say so instead of reporting success.</summary>
        public bool Write<T>(string scope, string key, T value) where T : class
        {
            if (value == null)
                return false;

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
                // Atomic swap: File.Replace overwrites in one step so a crash can't leave the prior snapshot
                // deleted-but-not-yet-replaced (the delete-then-move window). Move covers the first write.
                if (File.Exists(path))
                    File.Replace(tmpPath, path, null);
                else
                    File.Move(tmpPath, path);

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Snapshot write failed for {scope}/{key}: {ex.Message}");
                TryDelete(tmpPath);
                return false;
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

        // Clears a scope except keys starting with one of these prefixes - a scope is shared by every player, so
        // per-player state cant survive a plain DeleteScope. Filenames are "{sanitized key}_{hash}".
        public void DeleteScopeExcept(string scope, params string[] keepKeyPrefixes)
        {
            try
            {
                string path = Path.Combine(_root, SanitizeScope(scope));
                if (!Directory.Exists(path))
                    return;

                List<string> keep = new List<string>();
                foreach (string prefix in keepKeyPrefixes ?? new string[0])
                {
                    if (!string.IsNullOrEmpty(prefix))
                        keep.Add(Sanitize(prefix));
                }

                foreach (string file in Directory.EnumerateFiles(path))
                {
                    string name = Path.GetFileName(file);
                    bool preserve = false;
                    foreach (string prefix in keep)
                    {
                        if (name.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            preserve = true;
                            break;
                        }
                    }

                    if (!preserve)
                        TryDelete(file);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Snapshot scope delete failed for {scope}: {ex.Message}");
            }
        }

        /// <summary>Moves pre-fix state out of the version-scoped tree and under <see cref="StateScope"/>. Returns how many files were rescued.</summary>
        /// <remarks>
        /// <para>
        /// One-time, legacy-only, and deliberately not a general mechanism. The offline write queue used to be
        /// stored at <c>{gameVersionId}/command/{playerId}</c>, so shipping a build with a new game version had
        /// <see cref="PruneOtherVersions"/> delete the player's unsent writes — silently, with no error and no
        /// log. State now lives outside that tree by construction, so nothing written from this version on
        /// needs rescuing.
        /// </para>
        /// <para>
        /// <b>It has to run before the prune</b>, which is why the client calls it rather than a provider
        /// rescuing its own queue when it loads: by then the directory is already gone.
        /// </para>
        /// <para>Delete this once no install predating the fix can still be upgraded.</para>
        /// </remarks>
        public int MigrateLegacyState(params string[] leaves)
        {
            if (leaves == null || leaves.Length == 0 || !Directory.Exists(_root))
                return 0;

            int moved = 0;
            try
            {
                foreach (string versionDir in Directory.EnumerateDirectories(_root))
                {
                    if (string.Equals(Path.GetFileName(versionDir), StateScope, StringComparison.Ordinal))
                        continue;

                    foreach (string leaf in leaves)
                    {
                        string sanitized = Sanitize(leaf);
                        string legacy = Path.Combine(versionDir, sanitized);
                        if (Directory.Exists(legacy))
                            moved += MoveTree(legacy, Path.Combine(_root, StateScope, sanitized));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Legacy state migration failed: {ex.Message}");
            }

            if (moved > 0)
                _logger?.LogWarning($"Rescued {moved} queued write file(s) into '{StateScope}' — they would " +
                                    "otherwise have been deleted by this build's game-version change.");
            return moved;
        }

        /// <summary>Moves every file under <paramref name="from"/> into <paramref name="to"/>, merging rather than replacing.</summary>
        /// <remarks>
        /// Merges because two game versions can each hold a queue for the same player — one from before the
        /// upgrade and one from a build that was rolled back — and losing either is the bug this exists to fix.
        /// A file already at the destination wins: it belongs to the newer layout.
        /// </remarks>
        private int MoveTree(string from, string to)
        {
            int moved = 0;
            try
            {
                Directory.CreateDirectory(to);

                foreach (string file in Directory.EnumerateFiles(from))
                {
                    string destination = Path.Combine(to, Path.GetFileName(file));
                    try
                    {
                        if (File.Exists(destination)) File.Delete(file);
                        else { File.Move(file, destination); moved++; }
                    }
                    catch { }
                }

                foreach (string dir in Directory.EnumerateDirectories(from))
                    moved += MoveTree(dir, Path.Combine(to, Path.GetFileName(dir)));

                // Only ever removes what it has just emptied, so a file it could not move keeps its
                // directory alive rather than being orphaned.
                try { Directory.Delete(from); } catch { }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Legacy state move failed for {from}: {ex.Message}");
            }
            return moved;
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
                        || string.Equals(name, BootstrapScope, StringComparison.Ordinal)
                        || string.Equals(name, StateScope, StringComparison.Ordinal))
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
