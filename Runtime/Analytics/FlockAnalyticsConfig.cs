namespace Flock.Analytics
{
    public class FlockAnalyticsConfig
    {
        //Should be turned to false based on consent
        public bool Enabled { get; set; } = true;
        public bool AutoStartSession { get; set; } = true;
        public bool AutoEndSessionOnQuit { get; set; } = true;
        public float SessionTimeoutSeconds { get; set; } = 30f;
        public float HeartbeatIntervalSeconds { get; set; } = 60f;
        public float BounceThresholdSeconds { get; set; } = 10f;
        public bool PersistSessionOnDisk { get; set; } = true;
        public bool TrackFps { get; set; } = true;
        public float FpsSampleIntervalSeconds { get; set; } = 1f;
    }
}
