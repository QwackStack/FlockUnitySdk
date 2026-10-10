#if FLOCK_NETCODE_FOR_GAMEOBJECTS
using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Providers;
using Flock.Samples;
using Flock.Tests.Support;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The Quick Match sample on real frames, against the matchmaking routes in memory: a match starts the netcode, a queue the game
    // does not have says where to make it, and Cancel stops the search on the server.
    public class FlockQuickMatchSampleTests
    {
        private const string A = "player-a";
        private const string B = "player-b";

        private FlockTestClient _h;
        private FakeMatchmakingServer _server;
        private NetworkManager _manager;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_manager != null && _manager.IsListening)
                _manager.Shutdown();
            yield return FlockTestWait.Until(() => _manager == null || (!_manager.IsListening && !_manager.ShutdownInProgress), "Netcode stopped");
            if (_manager != null)
                UnityEngine.Object.Destroy(_manager.gameObject);
            _manager = null;
            yield return null;
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        private FlockQuickMatchSample SignedInSample(string player, string queueName)
        {
            _server = new FakeMatchmakingServer();
            // The relay switched off for the game: these tests are about matching, and the host publishes its direct address.
            _server.Sessions.RelayUrls.Clear();
            _h = FlockTestClient.Create(new FlockFakeTransport(), config => config.PartyRefreshInterval = TimeSpan.Zero);
            FlockHttpClient.Configure(_server);
            _h.LoginAs(player);
            _h.Client.Multiplayer.Matchmaking.SetCheckIntervalForTesting(TimeSpan.FromSeconds(0.2));
            GameObject holder = new GameObject("Quick Match sample");
            _manager = holder.AddComponent<NetworkManager>();
            UnityTransport transport = holder.AddComponent<UnityTransport>();
            using (UdpClient probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                transport.SetConnectionData("127.0.0.1", (ushort)((IPEndPoint)probe.Client.LocalEndPoint).Port);
            _manager.NetworkConfig = new NetworkConfig { NetworkTransport = transport };
            FlockQuickMatchSample sample = holder.AddComponent<FlockQuickMatchSample>();
            sample.NetworkManagerInUse = _manager;
            sample.QueueName = queueName;
            return sample;
        }

        private static IEnumerator Done(Task task, string what) => FlockTestWait.Until(() => task.IsCompleted, what, 15f);

        [UnityTest]
        public IEnumerator AMatch_StartsTheNetcode_TheLongestWaitingHosting()
        {
            FlockQuickMatchSample sample = SignedInSample(A, FakeMatchmakingServer.Duel);

            Task searching = sample.FindMatchAsync();
            yield return FlockTestWait.Until(() => _server.QueuedTicketOf(A) != null, "the search is queued");
            Assert.IsTrue(sample.Searching);
            _server.QueuedBy(FakeMatchmakingServer.DuelId, B);
            _server.FormMatches();
            yield return Done(searching, "matched and playing");
            Assert.IsTrue(sample.Playing, sample.Status);
            Assert.IsTrue(sample.Session.IsHost, "This player waited longest, so it hosts");
            Assert.IsTrue(_manager.IsHost);
            Assert.IsFalse(sample.Searching);
        }

        [UnityTest]
        public IEnumerator AQueueTheGameDoesNotHave_SaysWhereToMakeIt()
        {
            FlockQuickMatchSample sample = SignedInSample(A, "no-such-queue");

            Task searching = sample.FindMatchAsync();
            yield return Done(searching, "refused");
            StringAssert.Contains("No queue is named \"no-such-queue\"", sample.Status);
            StringAssert.Contains("Flock dashboard, under Matchmaking", sample.Status);
            Assert.IsNull(sample.Session);
        }

        [UnityTest]
        public IEnumerator Cancel_StopsTheSearch_OnTheServer()
        {
            FlockQuickMatchSample sample = SignedInSample(A, FakeMatchmakingServer.Duel);

            Task searching = sample.FindMatchAsync();
            yield return FlockTestWait.Until(() => _server.QueuedTicketOf(A) != null, "the search is queued");
            sample.CancelSearch();
            yield return Done(searching, "cancelled");
            StringAssert.Contains("Search cancelled", sample.Status);
            Assert.AreEqual("cancelled", _server.TicketsOf(A).Last().Status, "Cancelled on the server, not only here");
            Assert.IsFalse(sample.Searching);
        }

        [UnityTest]
        public IEnumerator ASearchThatExpires_SaysThereWasNoMatch()
        {
            FlockQuickMatchSample sample = SignedInSample(A, FakeMatchmakingServer.Duel);

            Task searching = sample.FindMatchAsync();
            yield return FlockTestWait.Until(() => _server.QueuedTicketOf(A) != null, "the search is queued");
            _server.ExpiresAll();
            yield return Done(searching, "expired");
            StringAssert.Contains("No match: " + FlockMatchmakingOutcome.Expired, sample.Status);
            Assert.IsNull(sample.Session);
        }
    }
}
#endif
