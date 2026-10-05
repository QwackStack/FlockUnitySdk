using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Flock.Analytics;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Flock.Tests.PlayMode
{
    // The path a game takes: an exception thrown from a game script's Update, taken by the Flock SDK's own frame tick.
    public class FlockExceptionCapturePlayModeTests
    {
        private const string Message = "FlockExceptionCapturePlayModeTests thrown from Update";

        private sealed class ThrowsOnceFromUpdate : MonoBehaviour
        {
            private bool _thrown;

            private void Update()
            {
                if (_thrown)
                    return;
                _thrown = true;
                throw new InvalidOperationException(Message);
            }
        }

        [UnityTest]
        public IEnumerator AnExceptionAGameScriptThrowsIsQueuedByTheSdksOwnFrameTick()
        {
            // The project starts the Flock SDK when Play begins; this test brings its own.
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            using (FlockTestSavedFiles.UseFolderOfItsOwn())
            using (FlockTestClient sdk = FlockTestClient.Create(new FlockFakeTransport()))
            {
                string logQueue = Path.Combine(sdk.Client.AnalyticsLaunches.Folder, FlockAnalyticsLaunches.LogEventsQueueName);
                LogAssert.Expect(LogType.Exception, new Regex(Message));
                GameObject thrower = new GameObject("ThrowsOnceFromUpdate");
                thrower.AddComponent<ThrowsOnceFromUpdate>();
                for (int frame = 0; frame < 5; frame++)
                    yield return null;
                Object.Destroy(thrower);

                string[] queued = FlockAnalyticsLaunches.QueuedEntries(logQueue).Select(File.ReadAllText).ToArray();
                Assert.AreEqual(1, queued.Count(entry => entry.Contains(Message)), "Queued with nothing but the SDK's own frames driving it");
            }
        }

        [UnityTest]
        public IEnumerator AnExceptionNoFrameTakesIsQueuedWhenTheSdksObjectGoesAway()
        {
            const string lastMessage = "FlockExceptionCapturePlayModeTests thrown after the last frame";
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            using (FlockTestSavedFiles.UseFolderOfItsOwn())
            using (FlockTestClient sdk = FlockTestClient.Create(new FlockFakeTransport()))
            {
                string logQueue = Path.Combine(sdk.Client.AnalyticsLaunches.Folder, FlockAnalyticsLaunches.LogEventsQueueName);
                yield return null;
                // As a script's OnApplicationQuit throws at quit: Unity disables the SDK's object next, and no frame follows.
                LogAssert.Expect(LogType.Exception, new Regex(lastMessage));
                Debug.LogException(new InvalidOperationException(lastMessage));
                Object.DestroyImmediate(FlockBehaviour.Instance.gameObject);

                string[] queued = FlockAnalyticsLaunches.QueuedEntries(logQueue).Select(File.ReadAllText).ToArray();
                Assert.AreEqual(1, queued.Count(entry => entry.Contains(lastMessage)), "Queued as the SDK's object went away");
            }
        }
    }
}
