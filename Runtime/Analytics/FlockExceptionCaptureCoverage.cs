namespace Flock.Analytics
{
    /// <summary>
    /// What the SDK's exception capture hears, so a game (or a tool such as the playtest's self-test) can tell what reaches
    /// Flock's log events before relying on it. Everything is false with analytics off, Capture Exceptions off in Flock > Settings,
    /// or before the SDK runs.
    /// </summary>
    public sealed class FlockExceptionCaptureCoverage
    {
        /// <summary>Nothing is captured.</summary>
        public static readonly FlockExceptionCaptureCoverage Nothing = new FlockExceptionCaptureCoverage(false, false);

        public FlockExceptionCaptureCoverage(bool enabled, bool loggingEnabled)
        {
            Enabled = enabled;
            LoggedExceptions = enabled && loggingEnabled;
            UnhandledExceptions = enabled;
            UnobservedTaskExceptions = enabled;
        }

        /// <summary>The capture is listening: analytics and Capture Exceptions are on and the SDK runs.</summary>
        public bool Enabled { get; }

        /// <summary>
        /// Exceptions Unity logs: one uncaught on the main thread, one handed to Debug.LogException, one thrown from an async void
        /// method. Not heard while Debug.unityLogger.logEnabled is false, which also silences the log callback.
        /// </summary>
        public bool LoggedExceptions { get; }

        /// <summary>Exceptions uncaught on another thread (AppDomain.UnhandledException).</summary>
        public bool UnhandledExceptions { get; }

        /// <summary>Faulted tasks nobody awaited (TaskScheduler.UnobservedTaskException), heard once the collector finalizes them.</summary>
        public bool UnobservedTaskExceptions { get; }

        /// <summary>True when every kind above is heard.</summary>
        public bool IsComplete => Enabled && LoggedExceptions && UnhandledExceptions && UnobservedTaskExceptions;
    }
}
