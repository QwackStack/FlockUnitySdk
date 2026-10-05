using System;
using System.Collections;
using NUnit.Framework;

namespace Flock.Tests.Support
{
    /// <summary>Waits in real time while the editor or player loop keeps ticking, which a held request and a UnityWebRequest both need.</summary>
    public static class FlockTestWait
    {
        /// <summary>Yields frames until <paramref name="done"/> holds, and fails with <paramref name="what"/> after <paramref name="seconds"/>.</summary>
        public static IEnumerator Until(Func<bool> done, string what, float seconds = 10f)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(done(), what);
        }
    }
}
