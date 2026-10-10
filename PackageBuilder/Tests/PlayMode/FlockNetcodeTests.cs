#if FLOCK_NETCODE_FOR_GAMEOBJECTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // StartNetcodeAsync on real frames with Netcode for GameObjects on this machine's loopback, against a session server in memory
    // and a STUN server on loopback: the host listens then publishes, a player waits for it and connects, and every way it stops.
    public partial class FlockNetcodeTests
    {
        private const string A = "player-a";
        private const string B = "player-b";
        private const string HostsPublicAddress = "203.0.113.7";

        private FlockTestClient _h;
        private FakeSessionServer _server;
        private FakeStunServer _stun;
        private readonly List<NetworkManager> _managers = new List<NetworkManager>();

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (NetworkManager manager in _managers)
            {
                if (manager != null && manager.IsListening)
                    manager.Shutdown();
            }
            yield return FlockTestWait.Until(() => _managers.All(manager => manager == null || (!manager.IsListening && !manager.ShutdownInProgress)), "Netcode stopped");
            foreach (NetworkManager manager in _managers)
            {
                if (manager != null)
                    UnityEngine.Object.Destroy(manager.gameObject);
            }
            _managers.Clear();
            yield return null;
            RelayTearDown();
            _h?.Dispose();
            _h = null;
            _stun?.Dispose();
            _stun = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        private FlockMultiplayerProvider SignedInAs(string player)
        {
            _server = new FakeSessionServer();
            // The relay switched off for the game, so a host publishes its direct address; the relay tests list one (RelayOn).
            _server.RelayUrls.Clear();
            _stun = new FakeStunServer { PublicAddress = HostsPublicAddress };
            _server.StunUrls.Add(_stun.Url);
            _h = FlockTestClient.Create(new FlockFakeTransport(), config => config.PartyRefreshInterval = TimeSpan.Zero);
            FlockHttpClient.Configure(_server);
            _h.LoginAs(player);
            FlockMultiplayerProvider multiplayer = _h.Client.Multiplayer;
            multiplayer.Sessions.SetConnectionWaitIntervalForTesting(TimeSpan.FromSeconds(0.2));
            multiplayer.Sessions.DirectAddresses.SetStunWaitForTesting(TimeSpan.FromSeconds(0.5));
            return multiplayer;
        }

        private static IEnumerator Done(Task task, string what, float seconds = 10f) => FlockTestWait.Until(() => task.IsCompleted, what, seconds);

        private static int FreePort()
        {
            using (UdpClient probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        }

        private NetworkManager Manager(int port, bool withTransport = true)
        {
            GameObject holder = new GameObject("NetworkManager for a test");
            NetworkManager manager = holder.AddComponent<NetworkManager>();
            _managers.Add(manager);
            if (!withTransport)
            {
                manager.NetworkConfig = new NetworkConfig();
                return manager;
            }
            UnityTransport transport = holder.AddComponent<UnityTransport>();
            transport.SetConnectionData("127.0.0.1", (ushort)port);
            manager.NetworkConfig = new NetworkConfig { NetworkTransport = transport };
            return manager;
        }

        private static UnityTransport TransportOf(NetworkManager manager) => (UnityTransport)manager.NetworkConfig.NetworkTransport;

        private IEnumerator Hosted(FlockMultiplayerProvider multiplayer, List<FlockMultiplayerSession> held)
        {
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "the session was hosted");
            Assert.IsFalse(hosted.IsFaulted, "Precondition: hosted");
            held.Add(hosted.Result);
        }

        private IEnumerator JoinedAsB(FlockMultiplayerProvider multiplayer, List<FlockMultiplayerSession> held, List<FakeSessionServer.Session> served)
        {
            FakeSessionServer.Session session = _server.HostedBy(A);
            Task<FlockMultiplayerSession> joined = multiplayer.JoinSessionAsync(session.Code);
            yield return Done(joined, "the session was joined");
            Assert.IsFalse(joined.IsFaulted, "Precondition: joined");
            held.Add(joined.Result);
            served.Add(session);
        }

        // Published as a host on this machine would be: reached on loopback by a player sharing its public address.
        private static JObject DirectOnThisMachine(int port) => new JObject
        {
            ["mode"] = "direct",
            ["address"] = HostsPublicAddress,
            ["port"] = port,
            ["lan_address"] = "127.0.0.1",
            ["public_address"] = HostsPublicAddress,
        };

        // ---- the host ----

        [UnityTest]
        public IEnumerator TheHost_ListensFirst_ThenPublishesItsDirectConnectionOnTheTransportsPort()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            int port = FreePort();
            NetworkManager manager = Manager(port);
            FakeSessionServer.Held publishing = _server.HoldNext(FakeSessionServer.Publish);

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(manager);
            yield return FlockTestWait.Until(() => publishing.Arrived || started.IsCompleted, "the publish was sent");
            Assert.IsTrue(publishing.Arrived, started.Exception?.InnerException?.ToString());
            Assert.IsTrue(manager.IsListening && manager.IsHost, "Listening before players are told where");
            Assert.AreEqual("0.0.0.0", TransportOf(manager).ConnectionData.ServerListenAddress, "Reachable on every address");
            publishing.Release();
            yield return Done(started, "started");
            Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, started.Result.Outcome);
            Assert.IsTrue(started.Result.IsHost);
            Assert.AreEqual(port, started.Result.Port);
            Assert.AreEqual(HostsPublicAddress, started.Result.Address);
            JObject published = JObject.Parse(_server.RequestsTo(FakeSessionServer.Publish)[0].JsonBody)["connection_info"] as JObject;
            Assert.AreEqual(port, (int)published["port"]);
        }

        [UnityTest]
        public IEnumerator AHostWhosePublishIsRefused_StopsNetcode_AndThrows()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            NetworkManager manager = Manager(FreePort());
            _server.AnswerNext(FakeSessionServer.Publish, FakeSessionServer.Refused(400, "request.validation_failed"));

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(manager);
            yield return Done(started, "refused");
            Assert.IsTrue(started.IsFaulted);
            Assert.IsInstanceOf<FlockException>(started.Exception.InnerException);
            yield return FlockTestWait.Until(() => !manager.IsListening && !manager.ShutdownInProgress, "Netcode stopped: nobody can reach a host that did not say where");
        }

        [UnityTest]
        public IEnumerator AHostWhosePortIsTaken_CouldNotStart_AndPublishesNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            int port = FreePort();
            using (UdpClient taken = new UdpClient(new IPEndPoint(IPAddress.Any, port)))
            {
                NetworkManager manager = Manager(port);
                LogAssert.ignoreFailingMessages = true;
                Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(manager);
                yield return Done(started, "refused");
                LogAssert.ignoreFailingMessages = false;
                Assert.IsFalse(started.IsFaulted, started.Exception?.InnerException?.ToString());
                Assert.AreEqual(FlockNetcodeStartOutcome.CouldNotStart, started.Result.Outcome);
                Assert.AreEqual(0, _server.Count(FakeSessionServer.Publish), "Nothing published for a host that is not listening");
                Assert.IsNull(manager.ConnectionApprovalCallback, "Flock's check taken off again");
                Assert.IsFalse(manager.NetworkConfig.ConnectionApproval, "The approval switch as the game had it");
            }
        }

        // ---- a player ----

        [UnityTest]
        public IEnumerator APlayer_WaitsForTheHost_ThenConnectsToIt()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            int port = FreePort();
            NetworkManager host = Manager(port);
            TransportOf(host).SetConnectionData("127.0.0.1", (ushort)port, "127.0.0.1");
            // The other player's game hosts here with Flock's check on, which this one stands in for.
            List<byte[]> payloads = new List<byte[]>();
            host.NetworkConfig.ConnectionApproval = true;
            host.ConnectionApprovalCallback = (request, response) =>
            {
                payloads.Add(request.Payload);
                response.Approved = true;
            };
            Assert.IsTrue(host.StartHost(), "Precondition: the other player's game hosts here");
            NetworkManager client = Manager(FreePort());
            byte[] gamesBytes = { 7, 8, 9 };
            client.NetworkConfig.ConnectionData = gamesBytes;

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsFalse(started.IsCompleted, "Waits for the host to publish");
            Assert.IsFalse(client.IsListening, "Netcode not started before there is a host to reach");
            Assert.AreEqual(0, _server.Count(FakeSessionServer.JoinToken), "No token asked for before there is a host: it lasts 60 s");
            _server.Publishes(served[0], DirectOnThisMachine(port));
            yield return Done(started, "connected", 15f);
            Assert.IsFalse(started.IsFaulted, started.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started.Result.Outcome);
            Assert.IsFalse(started.Result.IsHost);
            Assert.IsNull(started.Result.RefusedReason);
            Assert.AreEqual("127.0.0.1", started.Result.Address, "The LAN address: this device shares the host's public address");
            Assert.AreEqual(port, started.Result.Port);
            Assert.IsTrue(client.IsConnectedClient);
            yield return FlockTestWait.Until(() => host.ConnectedClientsIds.Count == 2, "the host sees the player");

            Assert.AreEqual(2, payloads.Count, "The host's own connection and the player's");
            Assert.IsTrue(FlockJoinTokenPayload.TryRead(payloads[1], out string token, out byte[] sentBytes), "The player's connection carries a join token");
            Assert.AreEqual("token-" + served[0].Id + "-" + B + "-1", token, "The token Flock gave this player for this session");
            CollectionAssert.AreEqual(gamesBytes, sentBytes, "The game's own bytes ride behind it unchanged");
            Assert.AreSame(gamesBytes, client.NetworkConfig.ConnectionData, "The game's own bytes are put back once connected");
            Assert.IsFalse(client.NetworkConfig.ConnectionApproval, "The game's own approval switch is put back too");
        }

        [UnityTest]
        public IEnumerator APlayerWhoseHostNeverPublishes_GivesUp_WithoutStartingNetcode()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            NetworkManager client = Manager(FreePort());

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client, new FlockNetcodeOptions { WaitForHost = TimeSpan.FromSeconds(0.6) });
            yield return Done(started, "gave up");
            Assert.AreEqual(FlockNetcodeStartOutcome.HostNeverPublished, started.Result.Outcome);
            Assert.IsNull(started.Result.Connection);
            Assert.IsFalse(client.IsListening);
        }

        [UnityTest]
        public IEnumerator APlayerWhoseSessionEndsWhileWaiting_IsToldTheSessionEnded()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            NetworkManager client = Manager(FreePort());

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client);
            _server.Ends(served[0]);
            yield return Done(started, "the end was read");
            Assert.AreEqual(FlockNetcodeStartOutcome.SessionEnded, started.Result.Outcome);
            Assert.IsFalse(client.IsListening);
        }

        [UnityTest]
        public IEnumerator AHostOfTheGamesOwnMode_IsHandedBack_NotConnectedTo()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            NetworkManager client = Manager(FreePort());
            _server.Publishes(served[0], new JObject { ["mode"] = "steam", ["steam_id"] = "76561198000000000" });

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client);
            yield return Done(started, "handed back");
            Assert.AreEqual(FlockNetcodeStartOutcome.NotDirect, started.Result.Outcome);
            Assert.AreEqual("steam", started.Result.Connection.Mode);
            Assert.IsTrue(started.Result.Connection.TryGetValue("steam_id", out string steamId));
            Assert.AreEqual("76561198000000000", steamId);
            Assert.IsFalse(client.IsListening);
        }

        [UnityTest]
        public IEnumerator APlayerWithNothingListeningAtTheAddress_CouldNotConnect_AndNetcodeStops()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            NetworkManager client = Manager(FreePort());
            TransportOf(client).ConnectTimeoutMS = 100;
            TransportOf(client).MaxConnectAttempts = 3;
            _server.Publishes(served[0], DirectOnThisMachine(FreePort()));
            // Netcode for GameObjects' own line for the failure this test is about.
            LogAssert.Expect(LogType.Error, "Failed to connect to server.");

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client);
            yield return Done(started, "gave up connecting", 15f);
            Assert.IsFalse(started.IsFaulted, started.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.CouldNotConnect, started.Result.Outcome);
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("Could not connect to the host at 127.0.0.1:")), "Said where it tried");
            yield return FlockTestWait.Until(() => !client.IsListening && !client.ShutdownInProgress, "Netcode stopped");
        }

        [UnityTest]
        public IEnumerator FlockShuttingDownWhileTheNetcodeConnects_StillHandsBackTheOutcome()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            NetworkManager client = Manager(FreePort());
            TransportOf(client).ConnectTimeoutMS = 300;
            TransportOf(client).MaxConnectAttempts = 3;
            _server.Publishes(served[0], DirectOnThisMachine(FreePort()));
            LogAssert.Expect(LogType.Error, "Failed to connect to server.");

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client);
            yield return FlockTestWait.Until(() => client.IsListening, "Precondition: connecting");
            FlockClient.Shutdown();
            yield return Done(started, "the connection gave up", 15f);
            Assert.IsFalse(started.IsFaulted, started.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.CouldNotConnect, started.Result.Outcome);
        }

        [UnityTest]
        public IEnumerator TheGameShuttingNetcodeDownWhileConnecting_EndsTheCall_AsCouldNotConnect()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            NetworkManager client = Manager(FreePort());
            TransportOf(client).ConnectTimeoutMS = 5000;
            TransportOf(client).MaxConnectAttempts = 10;
            _server.Publishes(served[0], DirectOnThisMachine(FreePort()));

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client);
            yield return FlockTestWait.Until(() => client.IsListening, "Precondition: connecting");
            // Netcode for GameObjects raises no disconnect on a client the game shuts down, so only the stopped check ends the call.
            client.Shutdown();
            yield return Done(started, "ended once the netcode stopped", 5f);
            Assert.IsFalse(started.IsFaulted, started.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.CouldNotConnect, started.Result.Outcome);
        }

        [UnityTest]
        public IEnumerator TheGamesToken_WhileConnecting_StopsNetcode_AndCancels()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            NetworkManager client = Manager(FreePort());
            TransportOf(client).ConnectTimeoutMS = 5000;
            TransportOf(client).MaxConnectAttempts = 10;
            _server.Publishes(served[0], DirectOnThisMachine(FreePort()));

            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client, null, giveUp.Token);
            yield return FlockTestWait.Until(() => client.IsListening, "Precondition: connecting");
            giveUp.Cancel();
            yield return Done(started, "cancelled");
            Assert.IsTrue(started.IsCanceled);
            yield return FlockTestWait.Until(() => !client.IsListening && !client.ShutdownInProgress, "Netcode stopped");
        }

        // ---- what a game gets wrong is refused at once ----

        [UnityTest]
        public IEnumerator WhatTheGameGotWrong_IsRefusedAtOnce()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            StringAssert.Contains("NetworkManager", Assert.Throws<FlockValidationException>(() => held[0].StartNetcodeAsync(null)).Message);
            NetworkManager noTransport = Manager(0, withTransport: false);
            StringAssert.Contains("Unity Transport", Assert.Throws<FlockValidationException>(() => held[0].StartNetcodeAsync(noTransport)).Message);
            NetworkManager running = Manager(FreePort());
            Assert.IsTrue(running.StartHost(), "Precondition: already running");
            StringAssert.Contains("already running", Assert.Throws<FlockValidationException>(() => held[0].StartNetcodeAsync(running)).Message);
            FlockMultiplayerSession nothing = null;
            StringAssert.Contains("needs the session", Assert.Throws<FlockValidationException>(() => nothing.StartNetcodeAsync(running)).Message);

            Task leaving = held[0].LeaveAsync();
            yield return Done(leaving, "left");
            NetworkManager idle = Manager(FreePort());
            StringAssert.Contains("has ended", Assert.Throws<FlockValidationException>(() => held[0].StartNetcodeAsync(idle)).Message);
            Assert.AreEqual(0, _server.Count(FakeSessionServer.Publish), "Nothing sent for any of them");
        }
    }
}
#endif
