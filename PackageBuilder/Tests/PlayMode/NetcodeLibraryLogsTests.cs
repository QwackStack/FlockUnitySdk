#if FLOCK_NETCODE_FOR_GAMEOBJECTS && FLOCK_UNITY_TRANSPORT_1
using NUnit.Framework;
using UnityEngine;

namespace Flock.Tests.PlayMode
{
    // The libraries' own lines on Unity Transport 1.x are let through, and nothing else is.
    public class NetcodeLibraryLogsTests
    {
        private const string SocketLine = "All socket receive requests were marked as failed, likely because socket itself has failed.";
        private const string LateSynchronize = "KeyNotFoundException: The given key '1' was not present in the dictionary.";
        private const string SceneManagerFrame = "Unity.Netcode.NetworkSceneManager.HandleServerSceneEvent (System.UInt32 sceneEventId, System.UInt64 clientId)";

        [Test]
        public void TheTransportsSocketLine_IsLetThrough()
        {
            NetcodeLibraryLogs logs = NetcodeLibraryLogs.Start();
            Debug.LogError(SocketLine);
            Assert.DoesNotThrow(logs.Finish);
        }

        [Test]
        public void AnyOtherError_FailsTheTest()
        {
            NetcodeLibraryLogs logs = NetcodeLibraryLogs.Start();
            Debug.LogError("Flock: something went wrong");
            Assert.Throws<AssertionException>(logs.Finish);
        }

        [Test]
        public void TheLateSynchronizeException_FromTheSceneManager_IsLetThrough()
        {
            NetcodeLibraryLogs logs = NetcodeLibraryLogs.Start();
            logs.HeardForTesting(LateSynchronize, SceneManagerFrame, LogType.Exception);
            Assert.DoesNotThrow(logs.Finish);
        }

        [Test]
        public void TheSameException_FromAnywhereElse_FailsTheTest()
        {
            NetcodeLibraryLogs logs = NetcodeLibraryLogs.Start();
            logs.HeardForTesting(LateSynchronize, "Flock.Providers.FlockJoinChecks.Approve ()", LogType.Exception);
            Assert.Throws<AssertionException>(logs.Finish);
        }
    }
}
#endif
