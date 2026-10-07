using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Logging;

namespace Flock.Providers
{
    /// <summary>Finds a matchmaking queue's id by its name. The game's queue list is read once a launch, and read again only when a name is not in it.</summary>
    internal sealed class FlockMatchmakingQueueNames
    {
        /// <summary>The most queues one read of the list returns; the backend has no further pages.</summary>
        internal const int MostQueuesListed = 200;

        private const string QueueNotFoundCode = "matchmaking.queue_not_found";

        private readonly Func<CancellationToken, Task<List<MatchmakingQueue>>> _readQueues;
        private readonly IFlockLogger _logger;
        private readonly HashSet<string> _repeatedNamesWarnedAbout = new HashSet<string>(StringComparer.Ordinal);
        private List<MatchmakingQueue> _queues;
        private Task<QueueRead> _reading;

        internal FlockMatchmakingQueueNames(Func<CancellationToken, Task<List<MatchmakingQueue>>> readQueues, IFlockLogger logger)
        {
            _readQueues = readQueues ?? throw new ArgumentNullException(nameof(readQueues));
            _logger = logger;
        }

        /// <summary>The id of the queue named exactly <paramref name="queueName"/>; throws <see cref="FlockValidationException"/> coded matchmaking.queue_not_found when the game has none.</summary>
        internal async Task<string> FindIdAsync(string queueName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = FindIn(_queues, queueName);
            if (id != null)
                return id;

            List<MatchmakingQueue> queues = await WaitForReadAsync(cancellationToken);

            id = FindIn(queues, queueName);
            if (id != null)
                return id;
            throw NotFound(queueName, queues);
        }

        // A caller that gives up stops waiting at once; the read goes on for whoever else waits, and keeps what it reads.
        private async Task<List<MatchmakingQueue>> WaitForReadAsync(CancellationToken cancellationToken)
        {
            Task<QueueRead> reading = SharedRead();
            if (!reading.IsCompleted && cancellationToken.CanBeCanceled)
            {
                // No RunContinuationsAsynchronously: Task.WhenAny over such a source never finished in a WebGL player (measured).
                TaskCompletionSource<bool> givenUp = new TaskCompletionSource<bool>();
                using (cancellationToken.Register(() => givenUp.TrySetResult(true)))
                    await Task.WhenAny(reading, givenUp.Task);
                cancellationToken.ThrowIfCancellationRequested();
            }

            QueueRead read = await reading;
            if (read.Failure != null)
                ExceptionDispatchInfo.Capture(read.Failure).Throw();
            return read.Queues;
        }

        // One read at a time, shared by every caller, with no caller's token; a finished read is never handed out again.
        private Task<QueueRead> SharedRead()
        {
            if (_reading == null || _reading.IsCompleted)
                _reading = ReadAndKeepAsync();
            return _reading;
        }

        // Never faults: the failure is handed to each caller to throw, so none is left unobserved when every caller gave up.
        private async Task<QueueRead> ReadAndKeepAsync()
        {
            try
            {
                List<MatchmakingQueue> queues = await _readQueues(CancellationToken.None);
                _queues = queues;
                return new QueueRead(queues, null);
            }
            catch (Exception failure)
            {
                return new QueueRead(null, failure);
            }
        }

        // The list is oldest first, so the first match is the oldest queue of that name.
        private string FindIn(List<MatchmakingQueue> queues, string queueName)
        {
            if (queues == null)
                return null;

            MatchmakingQueue found = null;
            foreach (MatchmakingQueue queue in queues)
            {
                if (queue == null || !string.Equals(queue.Name, queueName, StringComparison.Ordinal))
                    continue;
                if (found == null)
                {
                    found = queue;
                    continue;
                }
                if (_repeatedNamesWarnedAbout.Add(queueName))
                    _logger?.LogWarning($"Two matchmaking queues in this game are named \"{queueName}\" ({found.Id} and {queue.Id}). Using the older one, {found.Id}. Give each queue its own name in the Flock dashboard.");
                break;
            }
            return found?.Id;
        }

        private static FlockValidationException NotFound(string queueName, List<MatchmakingQueue> queues)
        {
            string sameLettersOtherCase = null;
            foreach (MatchmakingQueue queue in queues)
            {
                if (queue != null && string.Equals(queue.Name, queueName, StringComparison.OrdinalIgnoreCase))
                {
                    sameLettersOtherCase = queue.Name;
                    break;
                }
            }

            string hint;
            if (sameLettersOtherCase != null)
                hint = $"This game has a queue named \"{sameLettersOtherCase}\". Queue names match letter case exactly.";
            else if (queues.Count >= MostQueuesListed)
                hint = $"The game's queue list returns only its {MostQueuesListed} oldest queues, so a newer queue may be missing from it. Delete queues the game no longer uses in the Flock dashboard.";
            else
                hint = "Create a queue with this name in the Flock dashboard, or check the name against the game's queues there. Names match exactly, letter case included.";

            return new FlockValidationException($"No matchmaking queue in this game is named \"{queueName}\"")
            {
                Code = QueueNotFoundCode,
                Hint = hint,
            };
        }

        // What one read of the list gave: the queues, or why there are none.
        private sealed class QueueRead
        {
            internal QueueRead(List<MatchmakingQueue> queues, Exception failure)
            {
                Queues = queues;
                Failure = failure;
            }

            internal List<MatchmakingQueue> Queues { get; }
            internal Exception Failure { get; }
        }
    }
}
