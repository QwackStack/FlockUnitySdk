using System;
using System.Diagnostics;

namespace Flock.Providers
{
    /// <summary>The relay client's clock: Stopwatch readings, which setting the system clock does not move.</summary>
    internal static class FlockRelayClock
    {
        internal static long Now() => Stopwatch.GetTimestamp();

        /// <summary>How many clock ticks <paramref name="span"/> lasts.</summary>
        internal static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);
    }
}
