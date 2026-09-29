using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Flock.Logging;
using UnityEngine;

namespace Flock.Analytics
{
    /// <summary>Hears the game's exceptions on every thread and keeps them, up to a limit, until the main thread takes them.</summary>
    internal sealed class FlockExceptionCapture
    {
        /// <summary>Beyond this many exceptions waiting for the main thread, new ones are counted as lost rather than kept.</summary>
        internal const int MostWaiting = 256;

        private readonly ConcurrentQueue<FlockCapturedException> _waiting = new ConcurrentQueue<FlockCapturedException>();
        private int _waitingCount;
        private int _lost;
        private bool _listening;

        // Anything a hook does while keeping an exception could log again; the second call on the same thread is ignored.
        [ThreadStatic] private static bool _keepingOnThisThread;

        internal bool IsListening => _listening;

        /// <summary>Starts listening; a second call does nothing. Main thread.</summary>
        internal void Start()
        {
            if (_listening)
                return;
            Application.logMessageReceivedThreaded += HandleLog;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTask;
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            _listening = true;
        }

        /// <summary>Stops listening; what is waiting stays to be taken.</summary>
        internal void Stop()
        {
            if (!_listening)
                return;
            Application.logMessageReceivedThreaded -= HandleLog;
            TaskScheduler.UnobservedTaskException -= HandleUnobservedTask;
            AppDomain.CurrentDomain.UnhandledException -= HandleUnhandledException;
            _listening = false;
        }

        /// <summary>The next exception waiting, oldest first. Main thread.</summary>
        internal bool TryTake(out FlockCapturedException captured)
        {
            if (!_waiting.TryDequeue(out captured))
                return false;
            Interlocked.Decrement(ref _waitingCount);
            return true;
        }

        /// <summary>How many were lost to the limit since the last call.</summary>
        internal int TakeLost() => Interlocked.Exchange(ref _lost, 0);

        // Unity's log hands over every exception it logs, on the thread that logged it; its error lines are not exceptions.
        internal void HandleLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Exception || UnityFlockLogger.IsLoggingAnExceptionOnThisThread)
                return;
            Keep(message, stackTrace, FlockRepeatedExceptionCounter.SourceLog);
        }

        // A faulted task nobody awaited leaves no line in Unity's log; this is raised when its finalizer runs.
        internal void HandleUnobservedTask(object sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception == null)
                return;
            foreach (Exception inner in args.Exception.Flatten().InnerExceptions)
                Keep(Describe(inner), inner.StackTrace ?? string.Empty, FlockRepeatedExceptionCounter.SourceUnobservedTask);
        }

        // Unity's log also hands over an exception no thread caught, so this keeps it only while that log is switched off.
        internal void HandleUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            if (Debug.unityLogger.logEnabled || !(args.ExceptionObject is Exception exception))
                return;
            Keep(Describe(exception), exception.StackTrace ?? string.Empty, FlockRepeatedExceptionCounter.SourceUnhandled);
        }

        // As Unity's log names an exception: its type, then its message.
        private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";

        private void Keep(string message, string stackTrace, string source)
        {
            if (_keepingOnThisThread)
                return;
            _keepingOnThisThread = true;
            try
            {
                if (Interlocked.Increment(ref _waitingCount) > MostWaiting)
                {
                    Interlocked.Decrement(ref _waitingCount);
                    Interlocked.Increment(ref _lost);
                    return;
                }
                // A player set to log exceptions with no stack still has the frames that logged this one, the game's from its call.
                if (string.IsNullOrEmpty(stackTrace) && source == FlockRepeatedExceptionCounter.SourceLog)
                    stackTrace = FlockRepeatedExceptionCounter.FromTheFirstFrameOfTheGame(StackTraceUtility.ExtractStackTrace());
                _waiting.Enqueue(new FlockCapturedException { Message = message, StackTrace = stackTrace, Source = source });
            }
            catch (Exception)
            {
                // Inside Unity's log callback: a throw here would be logged, and heard, again.
            }
            finally
            {
                _keepingOnThisThread = false;
            }
        }
    }
}
