using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Flock
{
    /// <summary>How the SDK waits. WebGL has one thread, no thread pool and no timers, so there it resumes on the main thread and waits in frames.</summary>
    internal static class FlockWaiting
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>Pass to ConfigureAwait: true on WebGL, where a continuation handed to the thread pool never runs.</summary>
        internal const bool ResumeOnCallersThread = true;
#else
        /// <summary>Pass to ConfigureAwait: false off WebGL, so background work does not come back to the main thread.</summary>
        internal const bool ResumeOnCallersThread = false;
#endif

        /// <summary>Task.Delay, except on WebGL, where its timer never fires and it waits a frame at a time instead.</summary>
        internal static Task DelayAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return DelayInFramesAsync(wait, cancellationToken);
#else
            return Task.Delay(wait, cancellationToken);
#endif
        }

        /// <summary>Waits by yielding to the next frame until the time has passed; throws when cancelled.</summary>
        internal static async Task DelayInFramesAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            Stopwatch clock = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            while (clock.Elapsed < wait)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
