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
    /// <summary>The file names every event queue uses, whatever it holds.</summary>
    internal static class FlockEventCacheNames
    {
        internal const string Extension = ".evt";

        private static long _lastStamp;

        /// <summary>Stands in for the clock's ticks, so a test can hold the clock still or step it back.</summary>
        internal static Func<long> ClockTicksForTesting;

        // The clock's ticks, but always later than the last stamp this launch handed out: the clock can show one tick for several
        // writes in a row (Windows' moves every millisecond or more) or step back, and the names must keep the order events came in.
        internal static long NextStamp()
        {
            long now = ClockTicksForTesting?.Invoke() ?? DateTime.UtcNow.Ticks;
            while (true)
            {
                long last = Interlocked.Read(ref _lastStamp);
                long next = Math.Max(now, last + 1);
                if (Interlocked.CompareExchange(ref _lastStamp, next, last) == last)
                    return next;
            }
        }
    }

    // Spool directory: one event = one JSON file. The filename starts with a
    // sortable timestamp so flushes go oldest-first, and the only commit step
    // is renaming a .tmp into place. A crash anywhere just leaves files on
    // disk for the next run to retry.
    internal class FlockEventCache<T> : IEventCache<T> where T : class
    {
        // Success: batch is gone from the server's perspective (Sent) — delete the files and move on.
        // Drop: batch is gone from the server's perspective (permanently rejected) — delete the files and move on.
        // RefusedForItsContent: refused for good for what it holds (400, 413, 422), which one entry can cause, so the batch is
        //   sent again one entry at a time and only the entries refused alone are dropped.
        // DeferUnanswered / DeferAnswered: leave the files on disk and stop the flush loop; try again on the next trigger.
        private enum FlushOutcome
        {
            Success,
            Drop, 
            RefusedForItsContent,
            DeferUnanswered,
            DeferAnswered
        }
        private const string Extension = FlockEventCacheNames.Extension;

        private readonly string _dir;
        private readonly int _maxEvents;
        private readonly int _batchSize;
        private readonly IFlockLogger _logger;
        // Entries sent together share this key (a gameplay event's player): the server refuses a whole batch for one entry
        // it cannot take, so one player's refusal must never cost another player's events. Null sends any entries together.
        private readonly Func<T, string> _sendTogetherKey;
        private int _lastDrainEnding;
        // The first refusal of the drain under way, for the flush's report; one drain runs at a time.
        private string _firstRefusalThisDrain;
        // Each queued file's send-together key, read once: a batch of one player's entries skips another player's without
        // parsing them again. Kept under _inFlightLock; a rewrite or a delete forgets the file's entry.
        private readonly Dictionary<string, string> _sendTogetherKeyByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        internal int FilesReadForTesting;

        // The flush now sending, or null: two flushes never send the same batch.
        private readonly object _flushLock = new object();
        private Task _runningFlush;
        private int _pendingCount;
        // Bumped by Clear(). A flush compares it before sending, so an erase abandons the batch in flight.
        private int _epoch;

        // Paths the active flush has read but not yet dropped. Rewrite skips these so a login-time auth-id
        // rewrite can't modify a file that's already in flight (which DropBatch would then delete, losing the
        // rewrite). The lock makes ReadBatch's read+mark atomic against Rewrite — closing the read-then-mark gap.
        private readonly object _inFlightLock = new object();
        private HashSet<string> _inFlightPaths;

        public FlockEventCache(string directory, string subfolder, int maxEvents, int batchSize, IFlockLogger logger,
            Func<T, string> sendTogetherKey = null)
        {
            _sendTogetherKey = sendTogetherKey;
            string root = string.IsNullOrEmpty(directory) ? Application.persistentDataPath : directory;
            _dir = Path.Combine(root, subfolder);
            _maxEvents = Math.Max(1, maxEvents);
            _batchSize = Math.Max(1, batchSize);
            _logger = logger;

            FlockSavedFiles.CreateFolder(_dir);
            _pendingCount = SweepStaleTempFilesAndCount();
        }

        // Read directly from the field so the hot-path check (FlushCacheInBackground entry) doesn't syscall.
        public int PendingCount => Volatile.Read(ref _pendingCount);

        public FlockDrainEnding LastDrainEnding => (FlockDrainEnding)Volatile.Read(ref _lastDrainEnding);

        public string Enqueue(T evt)
        {
            if (evt == null)
                return null;

            string name = $"{FlockEventCacheNames.NextStamp():D19}_{Guid.NewGuid():N}";
            string finalPath = Path.Combine(_dir, name + Extension);
            string tmpPath = FlockTemporaryFiles.MakePath(finalPath);

            try
            {
                string json = JsonConvert.SerializeObject(evt);
                // Write to tmp then rename so a crash mid-write can never expose a partial event file.
                try
                {
                    FlockSavedFiles.WriteText(tmpPath, json);
                }
                catch (DirectoryNotFoundException)
                {
                    // Made again if something deleted it while this launch runs, so the events that follow are not lost.
                    FlockSavedFiles.CreateFolder(_dir);
                    FlockSavedFiles.WriteText(tmpPath, json);
                }
                FlockSavedFiles.Move(tmpPath, finalPath);
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

                        // Replaced only while it is still there, so a rewrite never brings back an entry erased meanwhile.
                        FlockTemporaryFiles.Replace(path, JsonConvert.SerializeObject(evt));
                        // Its key may have changed with it (a player credited), so it is read again next time.
                        _sendTogetherKeyByPath.Remove(path);
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

        public async Task<FlockFlushResult> FlushAsync(
            Func<IReadOnlyList<T>, CancellationToken, Task> sender,
            CancellationToken cancellationToken,
            bool waitForARunningFlush = false)
        {
            if (sender == null)
                return FlockFlushResult.Nothing;

            while (true)
            {
                // No RunContinuationsAsynchronously: Task.WhenAny over such a source never finished in a WebGL player (measured).
                TaskCompletionSource<bool> ours = null;
                Task running;
                lock (_flushLock)
                {
                    if (_runningFlush == null)
                    {
                        ours = new TaskCompletionSource<bool>();
                        _runningFlush = ours.Task;
                    }
                    running = _runningFlush;
                }

                if (ours != null)
                {
                    try
                    {
                        return await SendEverythingQueuedAsync(sender, cancellationToken).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
                    }
                    finally
                    {
                        lock (_flushLock)
                            _runningFlush = null;
                        ours.SetResult(true);
                    }
                }

                // A trigger leaves the queue to the running flush; an awaited flush waits for it, then sends what is left.
                if (!waitForARunningFlush)
                    return FlockFlushResult.Nothing;
                await WaitForAsync(running, cancellationToken).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            }
        }

        // Waits for the task, or until the token is cancelled, which throws.
        private static async Task WaitForAsync(Task task, CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                TaskCompletionSource<bool> cancelled = new TaskCompletionSource<bool>();
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                    await Task.WhenAny(task, cancelled.Task).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            }
            else
            {
                await task.ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task<FlockFlushResult> SendEverythingQueuedAsync(Func<IReadOnlyList<T>, CancellationToken, Task> sender, CancellationToken cancellationToken)
        {
            int sent = 0;
            int refused = 0;
            _firstRefusalThisDrain = null;
            try
            {
                int epoch = Volatile.Read(ref _epoch);
                Volatile.Write(ref _lastDrainEnding, (int)FlockDrainEnding.Finished);

                while (!cancellationToken.IsCancellationRequested)
                {
                    List<CachedEvent> batch = ReadBatch();
                    if (batch.Count == 0)
                        break;

                    // Erased since this flush started -> do not put it on the wire. A Clear during the send itself
                    // cannot be recalled; this closes the read-to-wire window.
                    if (Volatile.Read(ref _epoch) != epoch)
                        break;

                    FlushOutcome outcome = await TrySendBatch(sender, batch, cancellationToken).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
                    if (StopsTheDrain(outcome))
                        break;

                    if (outcome == FlushOutcome.RefusedForItsContent && batch.Count > 1)
                    {
                        // One entry can make the server refuse the whole body. Sent apart, only that entry goes.
                        bool stopped = false;
                        foreach (CachedEvent entry in batch)
                        {
                            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _epoch) != epoch)
                            {
                                stopped = true;
                                break;
                            }
                            List<CachedEvent> alone = new List<CachedEvent> { entry };
                            FlushOutcome aloneOutcome = await TrySendBatch(sender, alone, cancellationToken).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);
                            if (StopsTheDrain(aloneOutcome))
                            {
                                stopped = true;
                                break;
                            }
                            if (aloneOutcome == FlushOutcome.Success)
                                sent++;
                            else
                                refused++;
                            DropBatch(alone);
                        }
                        if (stopped)
                            break;
                        continue;
                    }

                    if (outcome == FlushOutcome.Success)
                        sent += batch.Count;
                    else
                        refused += batch.Count;
                    DropBatch(batch);
                }
            }
            finally
            {
                lock (_inFlightLock)
                    _inFlightPaths = null;
            }
            return new FlockFlushResult(sent, refused, _firstRefusalThisDrain);
        }

        // A deferral stops the drain and records how it ended, for the periodic flush's wait.
        private bool StopsTheDrain(FlushOutcome outcome)
        {
            if (outcome == FlushOutcome.DeferAnswered)
                Volatile.Write(ref _lastDrainEnding, (int)FlockDrainEnding.StoppedAnswered);
            else if (outcome == FlushOutcome.DeferUnanswered)
                Volatile.Write(ref _lastDrainEnding, (int)FlockDrainEnding.StoppedUnanswered);
            else
                return false;
            return true;
        }

        private async Task<FlushOutcome> TrySendBatch(
            Func<IReadOnlyList<T>, CancellationToken, Task> sender,
            List<CachedEvent> batch,
            CancellationToken cancellationToken)
        {
            try
            {
                List<T> list = batch.Select(b => b.Event).ToList();
                await sender(list, cancellationToken).ConfigureAwait(FlockWaiting.ResumeOnCallersThread);

                _logger?.LogDebug($"Sent Pending {list.Count} events batch");
                return FlushOutcome.Success;
            }
            catch (OperationCanceledException)
            {
                return FlushOutcome.DeferUnanswered;
            }
            catch (FlockValidationException ex)
            {
                _logger?.LogWarning($"Pending events {(batch.Count > 1 ? "batch refused for what it holds, sent again one at a time" : "entry dropped")} (validation): {ex.Message}");
                if (batch.Count == 1)
                    _firstRefusalThisDrain = _firstRefusalThisDrain ?? ex.Message;
                return batch.Count > 1 ? FlushOutcome.RefusedForItsContent : FlushOutcome.Drop;
            }
            // A success that cannot be read was not the server's answer (a captive portal's page), so the batch is kept.
            catch (FlockSerializationException ex)
            {
                _logger?.LogDebug($"Pending events flush deferred (unreadable response): {ex.Message}");
                return FlushOutcome.DeferUnanswered;
            }
            catch (FlockNetworkException ex) when (FlockNetworkException.IsPermanentStatus(ex.StatusCode))
            {
                bool refusedForItsContent = ex.StatusCode == 400 || ex.StatusCode == 413 || ex.StatusCode == 422;
                if (refusedForItsContent && batch.Count > 1)
                {
                    _logger?.LogWarning($"Pending events batch refused for what it holds (HTTP {ex.StatusCode}), sent again one at a time: {ex.Message}");
                    return FlushOutcome.RefusedForItsContent;
                }
                _logger?.LogWarning($"Pending events {(batch.Count > 1 ? "batch" : "entry")} dropped (HTTP {ex.StatusCode}): {ex.Message}");
                _firstRefusalThisDrain = _firstRefusalThisDrain ?? ex.Message;
                return FlushOutcome.Drop;
            }
            // FlockAuthException is not a FlockNetworkException, so it falls past the permanent-status catch above.
            // A backend-coded 403 never clears and would stall the cache on every flush. 401 and a bare 403 defer.
            catch (FlockAuthException ex) when (ex.StatusCode == 403 && !string.IsNullOrEmpty(ex.Code))
            {
                _logger?.LogWarning($"Pending events batch dropped (forbidden: {ex.Code}): {ex.Message}");
                _firstRefusalThisDrain = _firstRefusalThisDrain ?? ex.Message;
                return FlushOutcome.Drop;
            }
            catch (Exception ex)
            {
                // Transient (offline, 5xx, 401, timeout) — leave the files for the next attempt. Only a status the server
                // answered with counts as answered: a 401 or a bare 403 (FlockAuthException) is about the sign-in, not the
                // server's health, and must not stretch the periodic flush's wait.
                _logger?.LogDebug($"Pending events flush deferred: {ex.Message}");
                bool answered = ex is FlockNetworkException network && (network.StatusCode ?? 0) > 0;
                return answered ? FlushOutcome.DeferAnswered : FlushOutcome.DeferUnanswered;
            }
        }

        public void Clear()
        {
            // Bumped before the delete so an in-flight flush abandons the batch it already read.
            Interlocked.Increment(ref _epoch);

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
                string batchKey = null;

                foreach (string path in EnumerateFiles().OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (batch.Count >= _batchSize)
                        break;

                    // An entry of another key, read on an earlier pass of this drain: skipped without reading it again.
                    if (batch.Count > 0 && _sendTogetherKeyByPath.TryGetValue(path, out string knownKey)
                        && !string.Equals(knownKey, batchKey, StringComparison.Ordinal))
                        continue;

                    try
                    {
                        FilesReadForTesting++;
                        T evt = JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
                        if (evt != null)
                        {
                            // Only entries sharing the first one's key go together; the rest wait for a batch of their own.
                            string key = _sendTogetherKey?.Invoke(evt);
                            _sendTogetherKeyByPath[path] = key;
                            if (batch.Count == 0)
                                batchKey = key;
                            else if (!string.Equals(key, batchKey, StringComparison.Ordinal))
                                continue;
                            batch.Add(new CachedEvent { Path = path, Event = evt });
                        }
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

        // Deletes temporary files a write that never finished left over a minute ago, and counts the live .evt files.
        private int SweepStaleTempFilesAndCount()
        {
            FlockTemporaryFiles.DeleteLeftOverFiles(_dir, false);
            int count = 0;
            try
            {
                foreach (string path in Directory.EnumerateFiles(_dir))
                {
                    if (path.EndsWith(Extension, StringComparison.Ordinal))
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
            // Its key goes with it (the lock is re-entrant, so a delete from under it is fine too).
            lock (_inFlightLock)
                _sendTogetherKeyByPath.Remove(path);
            try
            {
                if (File.Exists(path))
                {
                    FlockSavedFiles.Delete(path);
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
