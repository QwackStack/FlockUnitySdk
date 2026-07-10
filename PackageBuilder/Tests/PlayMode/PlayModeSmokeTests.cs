using System.Collections;
using Flock;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // Proves the PlayMode assembly + Support reference resolve and a client can be created in play mode.
    public class PlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator Create_InPlayMode_Initializes_ThenDisposes()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient harness = FlockTestClient.Create(transport))
            {
                Assert.IsTrue(FlockClient.IsInitialized);
                yield return null; // let one frame tick so FlockBehaviour spins up
                Assert.IsNotNull(harness.Client);
            }
            Assert.IsFalse(FlockClient.IsInitialized);
        }
    }
}
