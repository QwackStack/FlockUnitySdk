using System;
using System.IO;
using Flock.Analytics;

namespace Flock.Tests.Support
{
    /// <summary>Gives every test SDK in a run a folder of its own for its analytics files, so no test is one more launch of the game.</summary>
    // A test's SDK would otherwise take over the launches Play mode left in the project's persistentDataPath, send their
    // queued events into a fake transport and delete them. Each test assembly calls Use once, from a SetUpFixture.
    public static class FlockTestSavedFiles
    {
        /// <summary>The folder every test SDK keeps its analytics files in during this run.</summary>
        public static string AnalyticsFolder => FlockAnalyticsLaunches.FolderForTesting;

        public static void Use()
        {
            if (FlockAnalyticsLaunches.FolderForTesting != null)
                return;
            FlockAnalyticsLaunches.FolderForTesting = Path.Combine(Path.GetTempPath(), "flock_test_analytics_" + Guid.NewGuid().ToString("N"));
        }

        public static void Release()
        {
            string folder = FlockAnalyticsLaunches.FolderForTesting;
            FlockAnalyticsLaunches.FolderForTesting = null;
            if (folder == null)
                return;
            try
            {
                if (FlockClient.IsInitialized)
                    FlockClient.Shutdown();
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch (Exception)
            {
                // A temporary folder is cleaned up by the system in time.
            }
        }

        /// <summary>A folder of this test's own, for a test that counts what its launch sends; disposing it puts the run's folder back. Dispose after the test's SDK has shut down.</summary>
        public static IDisposable UseFolderOfItsOwn() => new FolderOfItsOwn();

        private sealed class FolderOfItsOwn : IDisposable
        {
            private readonly string _runFolder = FlockAnalyticsLaunches.FolderForTesting;
            private readonly string _folder = Path.Combine(Path.GetTempPath(), "flock_test_analytics_own_" + Guid.NewGuid().ToString("N"));

            public FolderOfItsOwn() => FlockAnalyticsLaunches.FolderForTesting = _folder;

            public void Dispose()
            {
                FlockAnalyticsLaunches.FolderForTesting = _runFolder;
                try
                {
                    if (Directory.Exists(_folder))
                        Directory.Delete(_folder, true);
                }
                catch (Exception)
                {
                    // A temporary folder is cleaned up by the system in time.
                }
            }
        }
    }
}
