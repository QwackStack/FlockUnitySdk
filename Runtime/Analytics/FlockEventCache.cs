using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace Flock.Analytics
{
    // Spool directory: one event = one JSON file. The filename starts with a
    // sortable timestamp so flushes go oldest-first, and the only commit step
    // is renaming a .tmp into place. A crash anywhere just leaves files on
    // disk for the next run to retry.
    internal class FlockEventCache<T> : IEventCache<T> where T : class
    {
        // Success: batch is gone from the server's perspective (Sent) — delete the files and move on.
        // Drop: batch is gone from the server's perspective (permanently rejected) — delete the files and move on.
        // Defer: leave the files on disk and stop the flush loop; try again on the next trigger.
        private enum FlushOutcome
        {
            Success,
            Drop, 
            Defer
        }
        private const string Extension = ".evt";
        private const string TmpExtension = ".evt.tmp";

        private readonly string _dir;
        private readonly int _maxEvents;
        private readonly int _batchSize;
        private readonly IFlockLogger _logger;

        private int _flushing;
        private int _pendingCount;

        // Paths the active flush has read but not yet dropped. Rewrite skips these so a login-time auth-id
        // rewrite can't modify a file that's already in flight (which DropBatch would then delete, losing the
        // rewrite). The lock makes ReadBatch's read+mark atomic against Rewrite — closing the read-then-mark gap.
        private readonly object _inFlightLock = new object();
        private HashSet<string> _inFlightPaths;

        public FlockEventCache(string directory, string subfolder, int maxEvents, int batchSize, IFlockLogger logger)
        {
            string root = string.IsNullOrEmpty(directory) ? Application.persistentDataPath : directory;
            _dir = Path.Combine(root, subfolder);
            _maxEvents = Math.Max(1, maxEvents);
            _batchSize = Math.Max(1, batchSize);
            _logger = logger;

            Directory.CreateDirectory(_dir);
            _pendingCount = SweepStaleTempFilesAndCount();
        }

        // Read directly from the field so the hot-path check (FlushCacheInBackground entry) doesn't syscall.
        public int PendingCount => Volatile.Read(ref _pendingCount);

        public string Enqueue(T evt)
        {
            if (evt == null)
                return null;

            string name = $"{DateTime.UtcNow.Ticks:D19}_{Guid.NewGuid():N}";
            string finalPath = Path.Combine(_dir, name + Extension);
            string tmpPath = Path.Combine(_dir, name + TmpExtension);

            try
            {
                // Write to tmp then rename so a crash mid-write can never expose a partial event file.
                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(evt));
                File.Move(tmpPath, finalPath);
                Interlocked.Increment(ref _pendingCount);
                TrimOldest();
                return finalPath;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Event cache write failed, event dropped: {ex.Message}");
                TryDelete(tmpPath);
                return null;
            }
        }

        public void Remove(string handle)
        {
            if (string.IsNullOrEmpty(handle))
                return;

            if (TryDelete(handle))
                Interlocked.Decrement(ref _pendingCount);
        }

        public void Rewrite(Func<T, bool> shouldRewrite, Action<T> setAuthID)
        {
            if (shouldRewrite == null || setAuthID == null)
                return;

            int rewritten = 0;
            foreach (string path in EnumerateFiles())
            {
                // Lock per file: skip-check + read + replace must be atomic against ReadBatch's read+mark so a
                // file can't be rewritten after the flush read it (which DropBatch would then delete).
                lock (_inFlightLock)
                {
                    try
                    {
                        if (_inFlightPaths != null && _inFlightPaths.Contains(path))
                            continue;

                        T evt = JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
                        if (evt == null || !shouldRewrite(evt))
                            continue;

                        setAuthID(evt);

                        // Atomic swap: write tmp, replace original. File.Replace is atomic on the local FS
                        // and avoids the empty-file window that File.WriteAllText would leave behind.
                        string tmpPath = path + TmpExtension;
                        File.WriteAllText(tmpPath, JsonConvert.SerializeObject(evt));
                        File.Replace(tmpPath, path, null);
                        rewritten++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"Cache rewrite failed for {Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            }

            if (rewritten > 0)
                _logger?.LogInfo($"Rewrote {rewritten} cached entry(ies)");
        }

        public async Task FlushAsync(
            Func<IReadOnlyList<T>, CancellationToken, Task> sender,
            CancellationToken cancellationToken)
        {
            if (sender == null)
                return;

            //  prevents two overlapping flushes from sending the same batch twice.
            if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0)
                return;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    List<CachedEvent> batch = ReadBatch();
                    if (batch.Count == 0)
                        return;

                    FlushOutcome outcome = await TrySendBatch(sender, batch, cancellationToken).ConfigureAwait(false);
                    if (outcome == FlushOutcome.Defer)
                        return;

                    DropBatch(batch);
                }
            }
            finally
            {
                lock (_inFlightLock)
                    _inFlightPaths = null;
                Interlocked.Exchange(ref _flushing, 0);
            }
        }

        private async Task<FlushOutcome> TrySendBatch(
            Func<IReadOnlyList<T>, CancellationToken, Task> sender,
            List<CachedEvent> batch,
            CancellationToken cancellationToken)
        {
            try
            {
                List<T> list = batch.Select(b => b.Event).ToList();
                await sender(list, cancellationToken).ConfigureAwait(false);

                _logger?.LogDebug($"Sent Pending {list.Count} events batch");
                return FlushOutcome.Success;
            }
            catch (OperationCanceledException)
            {
                return FlushOutcome.Defer;
            }
            catch (FlockValidationException ex)
            {
                _logger?.LogWarning($"Pending events batch dropped (validation): {ex.Message}");
                return FlushOutcome.Drop;
            }
            catch (FlockSerializationException ex)
            {
                _logger?.LogWarning($"Pending events batch dropped (unreadable response): {ex.Message}");
                return FlushOutcome.Drop;
            }
            catch (FlockNetworkException ex) when (FlockNetworkException.IsPermanentStatus(ex.StatusCode))
            {
                _logger?.LogWarning($"Pending events batch dropped (HTTP {ex.StatusCode}): {ex.Message}");
                return FlushOutcome.Drop;
            }
            catch (Exception ex)
            {
                // Transient (offline, 5xx, auth, timeout) — leave the files for the next attempt.
                _logger?.LogDebug($"Pending events flush deferred: {ex.Message}");
                return FlushOutcome.Defer;
            }
        }

        public void Clear()
        {
            foreach (string path in EnumerateFiles())
                TryDelete(path);

            Interlocked.Exchange(ref _pendingCount, 0);
        }

        private List<CachedEvent> ReadBatch()
        {
            // Read the batch and mark it in flight under one lock so a concurrent Rewrite can't slip between
            // reading a file and marking it (which would let it rewrite a file already captured for this send).
            lock (_inFlightLock)
            {
                List<CachedEvent> batch = new List<CachedEvent>(_batchSize);

                foreach (string path in EnumerateFiles().OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (batch.Count >= _batchSize)
                        break;

                    try
                    {
                        T evt = JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
                        if (evt != null)
                            batch.Add(new CachedEvent { Path = path, Event = evt });
                        else if (TryDelete(path))
                            Interlocked.Decrement(ref _pendingCount);
                    }
                    catch
                    {
                        // Corrupt file (rare since writes are atomic) — drop it and move on.
                        if (TryDelete(path))
                            Interlocked.Decrement(ref _pendingCount);
                    }
                }

                _inFlightPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (CachedEvent item in batch)
                    _inFlightPaths.Add(item.Path);
                return batch;
            }
        }

        private void DropBatch(List<CachedEvent> batch)
        {
            foreach (CachedEvent item in batch)
            {
                if (TryDelete(item.Path))
                    Interlocked.Decrement(ref _pendingCount);
            }
        }

        private void TrimOldest()
        {
            // Skip the sort+materialize when we're under the cap (the common case after Enqueue).
            List<string> files = EnumerateFiles().ToList();
            if (files.Count <= _maxEvents)
                return;

            files.Sort(StringComparer.Ordinal);
            int overflow = files.Count - _maxEvents;
            for (int i = 0; i < overflow; i++)
            {
                if (TryDelete(files[i]))
                    Interlocked.Decrement(ref _pendingCount);
            }
        }

        // One pass: deletes tmp files left by a crash between WriteAllText and Move, and counts the live .evt files.
        private int SweepStaleTempFilesAndCount()
        {
            int count = 0;
            try
            {
                foreach (string path in Directory.EnumerateFiles(_dir))
                {
                    if (path.EndsWith(TmpExtension, StringComparison.Ordinal))
                        TryDelete(path);
                    else if (path.EndsWith(Extension, StringComparison.Ordinal))
                        count++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Event cache sweep skipped: {ex.Message}");
            }
            return count;
        }

        // Explicit suffix filter — Win32 wildcard semantics for "*.evt" vary across runtimes.
        private IEnumerable<string> EnumerateFiles()
        {
            if (!Directory.Exists(_dir))
                return Enumerable.Empty<string>();

            return Directory.EnumerateFiles(_dir).Where(p => p.EndsWith(Extension, StringComparison.Ordinal));
        }

        private bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch
            {
                //nothing to do here
            }
            return false;
        }

        private class CachedEvent
        {
            public string Path;
            public T Event;
        }
    }
}
