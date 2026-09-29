namespace Flock.Analytics
{
    public class FlockAnalyticsConfig
    {
        //TODO add summary for all
        public bool Enabled { get; set; } = true;

        // When true, no session/event tracking happens until SetConsent(true) is called at
        // least once. When false (default), analytics behaves as it always has for backward
        // compatibility; SetConsent(false) can still revoke it at runtime.
        public bool RequireExplicitConsent { get; set; } = false;
        public bool AutoStartSession { get; set; } = true;
        public bool AutoEndSessionOnQuit { get; set; } = true;
        public float SessionTimeoutSeconds { get; set; } = 30f;
        public float HeartbeatIntervalSeconds { get; set; } = 60f;
        public float BounceThresholdSeconds { get; set; } = 10f;
        public bool PersistSessionOnDisk { get; set; } = true;
        public bool TrackFps { get; set; } = true;
        public float FpsSampleIntervalSeconds { get; set; } = 1f;

        /// <summary>Reports the game's exceptions on their own, from every thread and from start-up; a manual LogDiagnosticException records either way.</summary>
        public bool CaptureExceptions { get; set; } = true;

        /// <summary>Repeats of one exception within this many seconds are counted and sent as one summary; 0 or less reports every occurrence.</summary>
        public float ExceptionRepeatWindowSeconds { get; set; } = 60f;

        public bool CacheFailedEvents { get; set; } = true;
        public int MaxCachedEvents { get; set; } = 1000;
        public int CacheFlushBatchSize { get; set; } = 50;
        public float EventBufferFlushIntervalSeconds { get; set; } = 10f;
    }
}
