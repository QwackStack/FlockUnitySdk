using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // Finding a matchmaking queue by name while the queue list is still on its way: PlayMode, so a held read's answer reaches
    // its callers through the player loop.
    public class FlockMatchmakingQueueNamesTests
    {
        private const string Queues = "matchmaking/queues";
        private const string List =
            "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":["
            + "{\"id\":\"01M46J2MG533AXD49SBPTEPBRM\",\"name\":\"spike-three\",\"game_id\":\"01KVCSANMD9V0CRW85ADE1WJA1\",\"min_players\":3,\"max_players\":3,\"allowed_segments\":[],\"criteria\":{},\"data\":{},\"created_at\":\"2026-10-05T17:34:09.925902\",\"updated_at\":\"2026-10-05T17:34:09.925907\"},"
            + "{\"id\":\"01M4AFMDXH35GY8S6MWXQ7AYBD\",\"name\":\"casual\",\"game_id\":\"01KVCSANMD9V0CRW85ADE1WJA1\",\"min_players\":2,\"max_players\":4,\"allowed_segments\":[],\"criteria\":{},\"data\":{},\"created_at\":\"2026-10-07T06:08:25.009368\",\"updated_at\":\"2026-10-07T06:08:25.009372\"}]}";

        private FlockTestClient _h;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Transport.ReleaseGate();
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        private FlockFakeTransport HeldList()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(List));
            transport.GateNext(Queues);
            _h = FlockTestClient.Create(transport);
            return transport;
        }

        [UnityTest]
        public IEnumerator TwoFindsAtOnce_ShareOneRead()
        {
            FlockFakeTransport transport = HeldList();
            Task<string> casual = _h.Client.Multiplayer.FindQueueIdAsync("casual", CancellationToken.None);
            Task<string> spikeThree = _h.Client.Multiplayer.FindQueueIdAsync("spike-three", CancellationToken.None);
            yield return FlockTestWait.Until(() => transport.CountTo(Queues) == 1, "The list was asked for");
            yield return null;
            Assert.AreEqual(1, transport.CountTo(Queues), "The second find waits on the first one's read");

            transport.ReleaseGate();
            yield return FlockTestWait.Until(() => casual.IsCompleted && spikeThree.IsCompleted, "Both finds ended");

            Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", casual.Result);
            Assert.AreEqual("01M46J2MG533AXD49SBPTEPBRM", spikeThree.Result);
            Assert.AreEqual(1, transport.CountTo(Queues));
        }

        [UnityTest]
        public IEnumerator AFindGivenUp_StopsWaitingAtOnce_AndTheReadIsStillKept()
        {
            FlockFakeTransport transport = HeldList();
            CancellationTokenSource givenUp = new CancellationTokenSource();
            Task<string> abandoned = _h.Client.Multiplayer.FindQueueIdAsync("casual", givenUp.Token);
            yield return FlockTestWait.Until(() => transport.CountTo(Queues) == 1, "The list was asked for");

            givenUp.Cancel();
            yield return FlockTestWait.Until(() => abandoned.IsCompleted, "The find stopped waiting while the list was still on its way", 2f);
            Assert.IsTrue(abandoned.IsCanceled, "A find given up ends as cancelled");

            transport.ReleaseGate();
            yield return null;
            yield return null;
            Task<string> later = _h.Client.Multiplayer.FindQueueIdAsync("casual", CancellationToken.None);
            yield return FlockTestWait.Until(() => later.IsCompleted, "The later find ended");

            Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", later.Result);
            Assert.AreEqual(1, transport.CountTo(Queues), "The read the first find gave up on was kept for the next");
            givenUp.Dispose();
        }
    }
}
