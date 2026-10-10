#if FLOCK_NETCODE_FOR_GAMEOBJECTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
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
    // StartNetcodeAsync through Flock's relay, a relay in memory between real loopback sockets: this game hosts or plays through the
    // SDK, and the other side is Netcode for GameObjects with a relay address and a bridge of its own, as another player's game has.
    public partial class FlockNetcodeTests
    {
        private const string OffTheHostsNetwork = "198.51.100.9";

        private FakeRelayServer _relay;
        private readonly List<FlockRelayConnection> _otherRelays = new List<FlockRelayConnection>();
        private readonly List<FlockRelayBridge> _otherBridges = new List<FlockRelayBridge>();

        // Lists the relay in memory for this game, as Flock lists the studio's relay.
        private void RelayOn()
        {
            _relay = _relay ?? new FakeRelayServer();
            _server.RelayUrls.Clear();
            _server.RelayUrls.Add(_relay.Url);
            _h.Client.Multiplayer.Sessions.RelayTiming.AnswerWait = TimeSpan.FromSeconds(2);
        }

        private string RelayServer => "127.0.0.1:" + _relay.Port;

        private void RelayTearDown()
        {
            FlockNetcode.TryOnTheHostsNetworkRanOutForTesting = null;
            foreach (FlockRelayBridge bridge in _otherBridges)
                bridge.Close();
            _otherBridges.Clear();
            foreach (FlockRelayConnection relay in _otherRelays)
                relay.Close();
            _otherRelays.Clear();
            _relay?.Dispose();
            _relay = null;
        }

        // Another player's relay address, opened to the host's relay IP as its game opens it.
        private IEnumerator OtherRelay(string player, List<FlockRelayConnection> into, Func<FlockRelayPeer> openTo = null)
        {
            FlockRelayLogin login = new FlockRelayLogin("127.0.0.1", _relay.Port, "1760000000:game:" + player, "minted-for-the-test");
            Task<FlockRelayConnection> opening = FlockRelayConnection.OpenAsync(login, new FlockRelayConnection.Timing { AnswerWait = TimeSpan.FromSeconds(2) }, _h.Logger, default);
            yield return Done(opening, $"{player}'s relay opened");
            Assert.IsFalse(opening.IsFaulted, $"Precondition: {player}'s relay opened ({opening.Exception?.InnerException?.Message})");
            _otherRelays.Add(opening.Result);
            into.Add(opening.Result);
            Task openedTo = opening.Result.OpenToHostAsync(openTo == null ? opening.Result.Address : openTo());
            yield return Done(openedTo, $"{player}'s relay opened to the host");
            Assert.IsFalse(openedTo.IsFaulted, $"Precondition: {player} opened to the host");
        }

        private FlockRelayBridge OtherBridge(FlockRelayBridge bridge)
        {
            _otherBridges.Add(bridge);
            return bridge;
        }

        private JObject Published() => JObject.Parse(_server.RequestsTo(FakeSessionServer.Publish).Last().JsonBody)["connection_info"] as JObject;

        private static FlockRelayPeer RelayAddressIn(JObject published)
        {
            Assert.IsTrue(FlockRelayPeer.TryParse((string)published["relay_address"], out FlockRelayPeer peer), "A relay address published");
            return peer;
        }

        private int ChannelsBoundBy(FlockRelayConnection relay)
            => _relay.SeenRequests(FakeRelayServer.MethodChannelBind).Count(seen => seen.ClientPort == relay.LocalPortForTesting);

        // A relayed player's game reaching this host: its relay address, a bridge standing in for the host, and Netcode for GameObjects on it.
        private IEnumerator RelayedPlayer(Hosting hosting, string player, byte[] payload, List<FlockRelayConnection> relays, List<NetworkManager> players)
        {
            FlockRelayPeer host = RelayAddressIn(Published());
            yield return OtherRelay(player, relays, () => host);
            FlockRelayConnection relay = relays.Last();
            relay.KeepChannelTo(host);
            FlockRelayBridge bridge = OtherBridge(FlockRelayBridge.ToTheHost(relay, host, _h.Logger));
            players.Add(PlayerConnecting(bridge.LocalPort, payload));
        }

        // ---- a host ----

        [UnityTest]
        public IEnumerator AnAlwaysHost_ListensOnItsLoopbackOnly_AndPublishesItsRelayAddressAlone()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, manager => RelayOn());

            Assert.AreEqual("127.0.0.1", TransportOf(hosting.Manager).ConnectionData.ServerListenAddress, "Nobody reaches it but through the relay");
            JObject published = Published();
            Assert.AreEqual("relay", (string)published["mode"]);
            Assert.AreEqual(hosting.Session.Relay.Address, RelayAddressIn(published));
            Assert.AreEqual(RelayServer, (string)published["relay_server"]);
            Assert.IsNull(published["address"] ?? published["port"] ?? published["lan_address"] ?? published["public_address"], "No address of its own");
            Assert.AreEqual(1, _relay.Reservations);
        }

        [UnityTest]
        public IEnumerator AWhenNeededHost_PublishesItsRelayAddress_AndItsDirectOnes()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, null, manager => RelayOn());

            Assert.AreEqual("0.0.0.0", TransportOf(hosting.Manager).ConnectionData.ServerListenAddress, "Reachable on its network too");
            JObject published = Published();
            Assert.AreEqual("relay", (string)published["mode"]);
            Assert.AreEqual(hosting.Session.Relay.Address, RelayAddressIn(published));
            Assert.AreEqual(hosting.Port, (int)published["port"]);
            Assert.AreEqual(HostsPublicAddress, (string)published["public_address"], "Players sharing it connect on the LAN");
            Assert.IsNotNull(published["lan_address"]);
        }

        [UnityTest]
        public IEnumerator ARelayedPlayer_IsLetIn_KnownByItsConnection_AndGivenAChannel()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, manager => RelayOn());
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            List<NetworkManager> players = new List<NetworkManager>();
            yield return RelayedPlayer(hosting, B, WithToken(SeatedWithToken(hosting, B)), relays, players);

            yield return LetIn(hosting.Manager, players[0], 2);
            Assert.AreEqual(B, hosting.Session.PlayerForConnection(Newest(hosting.Manager)));
            yield return FlockTestWait.Until(() => ChannelsBoundBy(hosting.Session.Relay) >= 1, "the host bound a channel to the player let in", 3f);
            Assert.AreEqual(1, FlockJoinChecks.RelayBridgeForTesting(hosting.Session).PeersForTesting);
        }

        [UnityTest]
        public IEnumerator AnOutsiderOnTheRelay_IsRefused_AndGetsNoChannel()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, manager => RelayOn());
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            List<NetworkManager> players = new List<NetworkManager>();
            yield return RelayedPlayer(hosting, C, new byte[] { 1, 2, 3 }, relays, players);

            yield return RefusedWith(players[0], FlockNetcodeRefusedReason.NoJoinToken);
            yield return new UnityEngine.WaitForSecondsRealtime(0.3f);
            Assert.AreEqual(0, ChannelsBoundBy(hosting.Session.Relay), "A stranger on the relay is bound no channel");
            Assert.AreEqual(1, hosting.Manager.ConnectedClientsIds.Count, "Only the host");
        }

        [UnityTest]
        public IEnumerator ARelayedPlayerWhoLeaves_IsForgottenByTheBridge()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, manager => RelayOn());
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            List<NetworkManager> players = new List<NetworkManager>();
            yield return RelayedPlayer(hosting, B, WithToken(SeatedWithToken(hosting, B)), relays, players);
            yield return LetIn(hosting.Manager, players[0], 2);
            FlockRelayBridge bridge = FlockJoinChecks.RelayBridgeForTesting(hosting.Session);
            Assert.AreEqual(1, bridge.PeersForTesting, "Precondition: the player has a place");

            players[0].Shutdown();
            yield return FlockTestWait.Until(() => hosting.Manager.ConnectedClientsIds.Count == 1, "the host saw the player go", 15f);
            yield return FlockTestWait.Until(() => bridge.PeersForTesting == 0, "the bridge forgot the player", 3f);
        }

        [UnityTest]
        public IEnumerator AHostWhoseRelayIsSwitchedOff_PublishesDirectly_WithoutAWarning()
        {
            Hosting hosting = new Hosting();
            Task<FlockNetcodeStartResult> started = null;
            yield return HostedWithNetcode(hosting, null, manager => _server.RelayUrls.Clear(), result => started = result);

            Assert.AreEqual("direct", (string)Published()["mode"]);
            Assert.IsFalse(started.Result.ThroughRelay);
            Assert.AreEqual(FlockRelayFailure.NotOffered, started.Result.RelayFailureReason);
            Assert.AreEqual(0, _h.Logger.Warnings.Count(warning => warning.Contains("relay")), "The studio's choice is not worth a warning");
        }

        [UnityTest]
        public IEnumerator AHostWhoseRelayIsPaused_PublishesDirectly_AndSaysWhy()
        {
            Hosting hosting = new Hosting();
            Task<FlockNetcodeStartResult> started = null;
            yield return HostedWithNetcode(hosting, null, manager =>
            {
                RelayOn();
                _server.RelayPaused = true;
            }, result => started = result);

            Assert.AreEqual("direct", (string)Published()["mode"]);
            Assert.AreEqual(FlockRelayFailure.Paused, started.Result.RelayFailureReason);
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("cannot join through Flock's relay")), "Said why once");
        }

        [UnityTest]
        public IEnumerator AnAlwaysHostWhoseRelayFails_StopsHosting_AndPublishesNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            RelayOn();
            _server.RelayPaused = true;
            NetworkManager manager = Manager(FreePort());

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(manager, new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
            yield return Done(started, "gave up");
            Assert.IsFalse(started.IsFaulted, started.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.RelayFailed, started.Result.Outcome);
            Assert.AreEqual(FlockRelayFailure.Paused, started.Result.RelayFailureReason);
            Assert.AreEqual(0, _server.Count(FakeSessionServer.Publish));
            yield return FlockTestWait.Until(() => !manager.IsListening && !manager.ShutdownInProgress, "the netcode stopped");
        }

        [UnityTest]
        public IEnumerator AHostWhoseNetcodeStopsWhileTheRelayOpens_PublishesNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            RelayOn();
            NetworkManager manager = Manager(FreePort());
            FakeSessionServer.Held logins = _server.HoldNext(FakeSessionServer.RelayCredentials);

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(manager, new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
            yield return FlockTestWait.Until(() => logins.Arrived, "the relay logins were asked for");
            Assert.IsTrue(manager.IsListening, "Precondition: hosting while the relay opens");
            manager.Shutdown();
            yield return FlockTestWait.Until(() => !manager.IsListening && !manager.ShutdownInProgress, "the game stopped the netcode");
            logins.Release();
            yield return Done(started, "the start ended");
            Assert.IsFalse(started.IsFaulted, started.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.CouldNotStart, started.Result.Outcome);
            Assert.AreEqual(0, _server.Count(FakeSessionServer.Publish), "A host that is gone is not published");
            Assert.IsFalse(held[0].Relay.HandsArrivalsOverForTesting, "Its bridge closed");
        }

        [UnityTest]
        public IEnumerator AHostWhoseRelayOpeningIsRefused_GivesTheRelayBack_AndPublishesDirectly()
        {
            Hosting hosting = new Hosting();
            Task<FlockNetcodeStartResult> started = null;
            yield return HostedWithNetcode(hosting, null, manager =>
            {
                RelayOn();
                _relay.RefusedOpenings.Add(FakeRelayServer.RelayIp);
            }, result => started = result);

            Assert.AreEqual("direct", (string)Published()["mode"]);
            Assert.AreEqual(FlockRelayFailure.OpenRefused, started.Result.RelayFailureReason);
            yield return FlockTestWait.Until(() => _relay.Releases == 1, "the relay address no player could reach was given back", 3f);
            Assert.IsTrue(hosting.Session.Relay == null || hosting.Session.Relay.HasStopped);
        }

        [UnityTest]
        public IEnumerator StoppingTheHostsNetcode_ClosesItsBridge_AndTheRelayStaysTheSessions()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, manager => RelayOn());
            FlockRelayBridge bridge = FlockJoinChecks.RelayBridgeForTesting(hosting.Session);

            hosting.Manager.Shutdown();
            yield return FlockTestWait.Until(() => bridge.IsClosed, "the bridge closed with the netcode", 5f);
            Assert.IsTrue(hosting.Session.Relay.IsOpen, "The relay is the session's, open until it ends");
            Assert.IsFalse(hosting.Session.Relay.HandsArrivalsOverForTesting, "Arrivals are no longer handed to the closed bridge");
        }

        [UnityTest]
        public IEnumerator TheSessionsEnd_ClosesTheHostsBridge_AndItsRelay()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, manager => RelayOn());
            FlockRelayBridge bridge = FlockJoinChecks.RelayBridgeForTesting(hosting.Session);
            FlockRelayConnection relay = hosting.Session.Relay;

            Task ending = hosting.Session.EndAsync();
            yield return Done(ending, "ended");
            Assert.IsTrue(bridge.IsClosed, "Closed as the session ended");
            Assert.IsTrue(relay.HasStopped);
        }

        // ---- a player ----

        // Another player's game hosting on this machine with a relay address and a bridge, as StartNetcodeAsync hosts with Relay Always.
        private IEnumerator RelayedHostOnThisMachine(int port, List<FlockRelayConnection> relays, Action<NetworkManager.ConnectionApprovalRequest, NetworkManager.ConnectionApprovalResponse> approve = null)
        {
            NetworkManager host = Manager(port);
            TransportOf(host).SetConnectionData("127.0.0.1", (ushort)port, "127.0.0.1");
            host.NetworkConfig.ConnectionApproval = true;
            host.ConnectionApprovalCallback = approve ?? ((request, response) => response.Approved = true);
            Assert.IsTrue(host.StartHost(), "Precondition: the other player's game hosts here");
            yield return OtherRelay(A, relays);
            OtherBridge(FlockRelayBridge.ForTheHost(relays[0], port, _h.Logger));
        }

        private JObject RelayedOnThisMachine(FlockRelayPeer host, int port, string publicAddress = null, string server = null)
        {
            JObject connection = new JObject
            {
                ["mode"] = "relay",
                ["relay_address"] = host.ToString(),
                ["relay_server"] = server ?? RelayServer,
            };
            if (publicAddress != null)
            {
                connection["address"] = publicAddress;
                connection["port"] = port;
                connection["lan_address"] = "127.0.0.1";
                connection["public_address"] = publicAddress;
            }
            return connection;
        }

        private IEnumerator PlayerAgainstARelayedHost(FlockNetcodeOptions options, Func<FlockRelayPeer, int, JObject> published, List<Task<FlockNetcodeStartResult>> started,
            List<FlockMultiplayerSession> held, List<FlockRelayConnection> relays, Action<NetworkManager.ConnectionApprovalRequest, NetworkManager.ConnectionApprovalResponse> approve = null)
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            int port = FreePort();
            yield return RelayedHostOnThisMachine(port, relays, approve);
            NetworkManager client = Manager(FreePort());
            _server.Publishes(served[0], published(relays[0].Address, port));
            started.Add(held[0].StartNetcodeAsync(client, options));
            yield return Done(started[0], "the start ended", 15f);
            Assert.IsFalse(started[0].IsFaulted, started[0].Exception?.InnerException?.ToString());
        }

        [UnityTest]
        public IEnumerator APlayer_ConnectsThroughTheRelay_ToARelayedHost()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, (host, port) => RelayedOnThisMachine(host, port), started, held, relays);

            FlockNetcodeStartResult result = started[0].Result;
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, result.Outcome);
            Assert.IsTrue(result.ThroughRelay);
            Assert.AreEqual(relays[0].Address.Ip, result.Address, "The host's relay IP");
            Assert.AreEqual(relays[0].Address.Port, result.Port);
            Assert.AreEqual(1, _otherBridges[0].PeersForTesting, "The host heard the player through its relay");
            Assert.IsTrue(_otherBridges[0].TryGetPeer(LoopbackPortOfTheOnlyPeer(_otherBridges[0]), out FlockRelayPeer heard));
            Assert.AreEqual(held[0].Relay.Address, heard, "From the player's own relay address");
            yield return FlockTestWait.Until(() => ChannelsBoundBy(held[0].Relay) >= 1, "the player bound a channel to the host", 3f);
        }

        private static int LoopbackPortOfTheOnlyPeer(FlockRelayBridge bridge) => bridge.PeerPortsForTesting.Single();

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_OnTheHostsNetwork_ConnectsDirectly_WithoutTheRelay()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(null, (host, port) => RelayedOnThisMachine(host, port, HostsPublicAddress), started, held, relays);

            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome);
            Assert.IsFalse(started[0].Result.ThroughRelay, "Sharing the host's public address: its network");
            Assert.AreEqual("127.0.0.1", started[0].Result.Address);
            Assert.IsNull(held[0].Relay, "No relay opened");
            Assert.AreEqual(1, _relay.Reservations, "Only the host's");
        }

        // ---- a WhenNeeded player whose try on the host's network is not answered ----

        // Another loopback address, where the host does not listen: the host's address on a network that keeps its devices apart.
        private const string UnansweringLanAddress = "127.0.0.2";

        private JObject OnTheHostsNetworkAt(FlockRelayPeer host, int port, string lanAddress)
        {
            JObject published = RelayedOnThisMachine(host, port, HostsPublicAddress);
            published["lan_address"] = lanAddress;
            return published;
        }

        // Delegates the Flock netcode assembly left on a transport's events.
        private static int FlockListenersOn(UnityTransport transport)
        {
            Delegate listeners = (Delegate)typeof(NetworkTransport).GetField("OnTransportEvent", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(transport);
            return listeners == null ? 0 : listeners.GetInvocationList().Count(listener => listener.Method.DeclaringType.Assembly == typeof(FlockNetcode).Assembly);
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_WhoseHostsNetworkAddressDoesNotAnswer_ConnectsThroughTheRelay()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            List<UdpClient> silent = new List<UdpClient>();
            System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                yield return PlayerAgainstARelayedHost(null, (host, port) =>
                {
                    // Takes every packet and answers none, as an address a network keeps apart does.
                    silent.Add(new UdpClient(new IPEndPoint(IPAddress.Parse(UnansweringLanAddress), port)));
                    took.Restart();
                    return OnTheHostsNetworkAt(host, port, UnansweringLanAddress);
                }, started, held, relays);

                FlockNetcodeStartResult result = started[0].Result;
                Assert.AreEqual(FlockNetcodeStartOutcome.Connected, result.Outcome, result.RefusedReason);
                Assert.IsTrue(result.ThroughRelay, "The relay after the host's network did not answer");
                Assert.Greater(silent[0].Available, 0, "The host's address on its network was tried first");
                Assert.GreaterOrEqual(took.Elapsed.TotalSeconds, 2.9, "For the whole try");
                Assert.AreEqual(2, _relay.Reservations, "The host's and this player's");
                Assert.IsTrue(_managers.Last().IsConnectedClient);
                Assert.AreEqual(0, FlockListenersOn(TransportOf(_managers.Last())), "Nothing of Flock's left listening on the game's transport");
            }
            finally
            {
                foreach (UdpClient socket in silent)
                    socket.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_WhoseTransportGivesUpOnTheHostsNetwork_GoesThroughTheRelayAtOnce()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            int port = FreePort();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return RelayedHostOnThisMachine(port, relays);
            using (UdpClient silent = new UdpClient(new IPEndPoint(IPAddress.Parse(UnansweringLanAddress), port)))
            {
                _server.Publishes(served[0], OnTheHostsNetworkAt(relays[0].Address, port, UnansweringLanAddress));
                NetworkManager client = Manager(FreePort());
                // A game whose transport makes one short connect attempt, so the transport gives up before Flock's own 3 s.
                TransportOf(client).ConnectTimeoutMS = 500;
                TransportOf(client).MaxConnectAttempts = 1;
                LogAssert.Expect(LogType.Error, "Failed to connect to server.");
                System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();
                Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client);
                yield return Done(started, "the start ended", 15f);

                Assert.IsFalse(started.IsFaulted, started.Exception?.InnerException?.ToString());
                Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started.Result.Outcome, started.Result.RefusedReason);
                Assert.IsTrue(started.Result.ThroughRelay, "The relay once the transport gave up");
                Assert.Greater(silent.Available, 0, "The host's address on its network was tried first");
                Assert.Less(took.Elapsed.TotalSeconds, 2.5, "Without waiting out Flock's own try");
            }
        }

        // Windows answers a closed loopback port with nothing Unity Transport hears (measured), so this ends with Flock's try too.
        [UnityTest]
        public IEnumerator AWhenNeededPlayer_WhoseHostsNetworkAddressIsClosed_ConnectsThroughTheRelay()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();
            yield return PlayerAgainstARelayedHost(null, (host, port) => OnTheHostsNetworkAt(host, port, UnansweringLanAddress), started, held, relays);

            FlockNetcodeStartResult result = started[0].Result;
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, result.Outcome, result.RefusedReason);
            Assert.IsTrue(result.ThroughRelay, $"The relay after a closed port on the host's network (took {took.Elapsed.TotalSeconds:0.0} s)");
            Assert.AreEqual(2, _relay.Reservations);
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_RefusedByTheHostOnItsNetwork_IsNotTriedThroughTheRelay()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(null, (host, port) => OnTheHostsNetworkAt(host, port, "127.0.0.1"), started, held, relays, (request, response) =>
            {
                response.Approved = request.ClientNetworkId == NetworkManager.ServerClientId;
                response.Reason = response.Approved ? null : "full";
            });

            Assert.AreEqual(FlockNetcodeStartOutcome.Refused, started[0].Result.Outcome);
            Assert.AreEqual("full", started[0].Result.RefusedReason);
            Assert.IsFalse(started[0].Result.ThroughRelay);
            Assert.IsNull(held[0].Relay, "A host that answered and refused is not asked again through the relay");
            Assert.AreEqual(1, _relay.Reservations, "Only the host's");
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_WhoseHostChecksSlowly_StaysOnTheHostsNetwork()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            int port = FreePort();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            List<NetworkManager.ConnectionApprovalResponse> waiting = new List<NetworkManager.ConnectionApprovalResponse>();
            yield return RelayedHostOnThisMachine(port, relays, (request, response) =>
            {
                if (request.ClientNetworkId == NetworkManager.ServerClientId)
                {
                    response.Approved = true;
                    return;
                }
                response.Pending = true;
                waiting.Add(response);
            });
            _server.Publishes(served[0], OnTheHostsNetworkAt(relays[0].Address, port, "127.0.0.1"));
            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(Manager(FreePort()));
            yield return FlockTestWait.Until(() => waiting.Count == 1, "the host heard the player and checks it", 5f);

            // Longer than the try on the host's network: the host answered, so its check is waited for.
            yield return new WaitForSecondsRealtime(5f);
            Assert.IsFalse(started.IsCompleted, "Still waiting for the host's check");
            waiting[0].Approved = true;
            waiting[0].Pending = false;
            yield return Done(started, "connected", 10f);
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started.Result.Outcome, started.Result.RefusedReason);
            Assert.IsFalse(started.Result.ThroughRelay, "On the host's network");
            Assert.IsNull(held[0].Relay);
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_WhoseGameStopsTheNetcodeOnTheHostsNetwork_DoesNotGoThroughTheRelay()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            int port = FreePort();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return RelayedHostOnThisMachine(port, relays);
            using (UdpClient silent = new UdpClient(new IPEndPoint(IPAddress.Parse(UnansweringLanAddress), port)))
            {
                _server.Publishes(served[0], OnTheHostsNetworkAt(relays[0].Address, port, UnansweringLanAddress));
                NetworkManager client = Manager(FreePort());
                Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client);
                yield return FlockTestWait.Until(() => silent.Available > 0, "the try on the host's network began", 5f);
                client.Shutdown();
                yield return Done(started, "ended", 10f);

                Assert.AreEqual(FlockNetcodeStartOutcome.CouldNotConnect, started.Result.Outcome);
                Assert.IsNull(held[0].Relay, "The game stopped it: nothing started through the relay");
                yield return new WaitForSecondsRealtime(1f);
                Assert.IsFalse(client.IsListening, "And the netcode stays stopped");
                Assert.AreEqual(1, _relay.Reservations, "Only the host's");
            }
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_WhoseGameStopsTheNetcodeAsTheTryRunsOut_DoesNotGoThroughTheRelay()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            int port = FreePort();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return RelayedHostOnThisMachine(port, relays);
            using (UdpClient silent = new UdpClient(new IPEndPoint(IPAddress.Parse(UnansweringLanAddress), port)))
            {
                _server.Publishes(served[0], OnTheHostsNetworkAt(relays[0].Address, port, UnansweringLanAddress));
                NetworkManager client = Manager(FreePort());
                int stops = 0;
                // The game's own shutdown landing in the very frame the try runs out.
                FlockNetcode.TryOnTheHostsNetworkRanOutForTesting = manager =>
                {
                    if (stops++ == 0)
                        manager.Shutdown();
                };
                Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client);
                yield return Done(started, "ended", 15f);

                Assert.AreEqual(1, stops, "Precondition: the try ran out with the game's shutdown under way");
                Assert.AreEqual(FlockNetcodeStartOutcome.CouldNotConnect, started.Result.Outcome);
                Assert.IsNull(held[0].Relay, "The game stopped it: nothing started through the relay");
                Assert.IsFalse(client.IsListening);
            }
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_WhoseSessionEndsOnTheHostsNetwork_IsToldItEnded_WithoutTheRelay()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            int port = FreePort();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return RelayedHostOnThisMachine(port, relays);
            using (UdpClient silent = new UdpClient(new IPEndPoint(IPAddress.Parse(UnansweringLanAddress), port)))
            {
                _server.Publishes(served[0], OnTheHostsNetworkAt(relays[0].Address, port, UnansweringLanAddress));
                Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(Manager(FreePort()));
                yield return FlockTestWait.Until(() => silent.Available > 0, "the try on the host's network began", 5f);
                Task leaving = held[0].LeaveAsync();
                yield return Done(leaving, "left");
                Assert.IsTrue(held[0].HasEnded, "Precondition: the session ended during the try");
                yield return Done(started, "ended", 15f);

                Assert.IsFalse(started.IsFaulted, started.Exception?.InnerException?.ToString());
                Assert.AreEqual(FlockNetcodeStartOutcome.SessionEnded, started.Result.Outcome);
                Assert.IsNull(held[0].Relay, "No relay for an ended session");
            }
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_WhoseSessionsRelayIsOpen_GoesThroughItOnceTheDirectTryHasStopped()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            int port = FreePort();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            int playersAsked = 0;
            yield return RelayedHostOnThisMachine(port, relays, (request, response) =>
            {
                bool player = request.ClientNetworkId != NetworkManager.ServerClientId;
                if (player)
                    playersAsked++;
                response.Approved = !player || playersAsked > 1;
                response.Reason = response.Approved ? null : "full";
            });
            using (UdpClient silent = new UdpClient(new IPEndPoint(IPAddress.Parse(UnansweringLanAddress), port)))
            {
                _server.Publishes(served[0], OnTheHostsNetworkAt(relays[0].Address, port, UnansweringLanAddress));
                // A first start through the relay the host refuses leaves the session's relay open and opened to the host.
                Task<FlockNetcodeStartResult> refused = held[0].StartNetcodeAsync(Manager(FreePort()), new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
                yield return Done(refused, "refused", 15f);
                Assert.AreEqual(FlockNetcodeStartOutcome.Refused, refused.Result.Outcome, "Precondition: the first start is refused");
                Assert.IsTrue(held[0].Relay.IsOpen, "Precondition: the session's relay stays open");

                // Nothing left to open, so only waiting for the direct try to stop keeps the relay's start from being refused.
                Task<FlockNetcodeStartResult> again = held[0].StartNetcodeAsync(Manager(FreePort()));
                yield return Done(again, "connected", 15f);
                Assert.IsFalse(again.IsFaulted, again.Exception?.InnerException?.ToString());
                Assert.AreEqual(FlockNetcodeStartOutcome.Connected, again.Result.Outcome, again.Result.RefusedReason);
                Assert.IsTrue(again.Result.ThroughRelay);
                Assert.Greater(silent.Available, 0, "The host's address on its network was tried first");
            }
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_OfAHostThatKnewOnlyItsLanAddress_GoesThroughTheRelay()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(null, (host, port) =>
            {
                JObject published = RelayedOnThisMachine(host, port);
                published["address"] = "127.0.0.1";
                published["port"] = port;
                published["lan_address"] = "127.0.0.1";
                return published;
            }, started, held, relays);

            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome);
            Assert.IsTrue(started[0].Result.ThroughRelay, "A LAN address alone says nothing about this player's network");
        }

        [Test]
        public void TheTryOnTheHostsNetwork_IsTwoConnectAttempts_AndAtLeast3Seconds()
        {
            UnityTransport transport = TransportOf(Manager(FreePort()));
            transport.ConnectTimeoutMS = 1000;
            Assert.AreEqual(TimeSpan.FromSeconds(3), FlockNetcode.TryOnTheHostsNetwork(transport), "Unity Transport's default");
            transport.ConnectTimeoutMS = 4000;
            Assert.AreEqual(TimeSpan.FromSeconds(8), FlockNetcode.TryOnTheHostsNetwork(transport), "A game's longer connect attempts");
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_OffTheHostsNetwork_ConnectsThroughTheRelay()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(null, (host, port) => RelayedOnThisMachine(host, port, OffTheHostsNetwork), started, held, relays);

            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome);
            Assert.IsTrue(started[0].Result.ThroughRelay, "Another public address: not the host's network");
            Assert.AreEqual(2, _relay.Reservations);
        }

        [UnityTest]
        public IEnumerator APlayer_ReservesOnTheRelayServerTheHostUses()
        {
            using (FakeRelayServer first = new FakeRelayServer())
            {
                List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
                List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
                List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
                yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, (host, port) =>
                {
                    // Flock lists another server first; the host's is the second.
                    _server.RelayUrls.Insert(0, first.Url);
                    return RelayedOnThisMachine(host, port);
                }, started, held, relays);

                Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome);
                Assert.AreEqual(0, first.Reservations, "Not on the server Flock lists first");
                Assert.AreEqual(2, _relay.Reservations, "On the host's");
            }
        }

        [UnityTest]
        public IEnumerator APlayerWhoseLoginsLackTheHostsServer_RelayFailed_ServerNotListed()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, (host, port) => RelayedOnThisMachine(host, port, null, "relay.elsewhere.test:3479"),
                started, held, relays);

            Assert.AreEqual(FlockNetcodeStartOutcome.RelayFailed, started[0].Result.Outcome);
            Assert.AreEqual(FlockRelayFailure.ServerNotListed, started[0].Result.RelayFailureReason);
            Assert.AreEqual(1, _relay.Reservations, "Only the host's");
            Assert.AreEqual(0, _server.Count(FakeSessionServer.JoinToken), "No token asked for a host it cannot reach");
        }

        [UnityTest]
        public IEnumerator AnAlwaysPlayer_ADirectOnlyHost_RelayFailed_WithoutOpeningARelay()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            NetworkManager client = Manager(FreePort());
            _server.Publishes(served[0], DirectOnThisMachine(FreePort()));

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client, new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
            yield return Done(started, "gave up");
            Assert.AreEqual(FlockNetcodeStartOutcome.RelayFailed, started.Result.Outcome);
            Assert.AreEqual(FlockRelayFailure.NotOfferedByTheHost, started.Result.RelayFailureReason);
            Assert.AreEqual(0, _relay.Reservations);
            Assert.IsFalse(client.IsListening);
        }

        [UnityTest]
        public IEnumerator ANeverPlayer_ARelayOnlyHost_CouldNotConnect_AndSaysWhy()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Never }, (host, port) => RelayedOnThisMachine(host, port), started, held, relays);

            Assert.AreEqual(FlockNetcodeStartOutcome.CouldNotConnect, started[0].Result.Outcome);
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("takes players through Flock's relay only")));
            Assert.AreEqual(1, _relay.Reservations, "Only the host's");
        }

        [UnityTest]
        public IEnumerator ARelayedPlayerRefused_ClosesItsBridge_AndKeepsTheSessionsRelay()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            int port = FreePort();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return RelayedHostOnThisMachine(port, relays, (request, response) =>
            {
                response.Approved = request.ClientNetworkId == NetworkManager.ServerClientId;
                response.Reason = response.Approved ? null : "full";
            });
            _server.Publishes(served[0], RelayedOnThisMachine(relays[0].Address, port));

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(Manager(FreePort()), new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
            yield return Done(started, "refused", 15f);
            Assert.AreEqual(FlockNetcodeStartOutcome.Refused, started.Result.Outcome);
            Assert.AreEqual("full", started.Result.RefusedReason);
            Assert.IsTrue(started.Result.ThroughRelay);
            Assert.IsFalse(held[0].Relay.HandsArrivalsOverForTesting, "The player's bridge closed");
            Assert.IsTrue(held[0].Relay.IsOpen, "The relay stays the session's");
        }

        [UnityTest]
        public IEnumerator ARelayedPlayerRefused_StartsAgainOverTheSessionsRelay_AndConnects()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            int port = FreePort();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            int playersAsked = 0;
            yield return RelayedHostOnThisMachine(port, relays, (request, response) =>
            {
                bool player = request.ClientNetworkId != NetworkManager.ServerClientId;
                if (player)
                    playersAsked++;
                response.Approved = !player || playersAsked > 1;
                response.Reason = response.Approved ? null : "full";
            });
            _server.Publishes(served[0], RelayedOnThisMachine(relays[0].Address, port));
            Task<FlockNetcodeStartResult> refused = held[0].StartNetcodeAsync(Manager(FreePort()), new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
            yield return Done(refused, "refused", 15f);
            Assert.AreEqual(FlockNetcodeStartOutcome.Refused, refused.Result.Outcome, "Precondition: the first start is refused");

            // A new netcode on the same session: a bridge of its own, the same relay address, and the host's loopback for that address again.
            Task<FlockNetcodeStartResult> again = held[0].StartNetcodeAsync(Manager(FreePort()), new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
            yield return Done(again, "connected", 15f);
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, again.Result.Outcome, again.Result.RefusedReason);
            Assert.IsTrue(again.Result.ThroughRelay);
            Assert.AreEqual(2, playersAsked, "The host's game was asked about each start");
            Assert.IsTrue(_managers.Last().IsConnectedClient);
        }

        [UnityTest]
        public IEnumerator ASecondNetcodeOverTheSameSessionsRelay_IsRefused_AndTheFirstKeepsItsConnection()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, (host, port) => RelayedOnThisMachine(host, port), started, held, relays);
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome, "Precondition: connected through the relay");
            NetworkManager first = _managers.Last();

            // Two netcodes behind one relay address would reach the host as one connection.
            Task<FlockNetcodeStartResult> second = held[0].StartNetcodeAsync(Manager(FreePort()), new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
            yield return Done(second, "refused", 15f);
            Assert.IsTrue(second.IsFaulted, $"Refused, not {second.Status}");
            Assert.IsInstanceOf<Flock.Exceptions.FlockValidationException>(second.Exception.InnerException);
            StringAssert.Contains("already runs through this session's relay", second.Exception.InnerException.Message);
            Assert.IsTrue(first.IsConnectedClient, "The first connection keeps its place");
            Assert.IsTrue(held[0].Relay.HandsArrivalsOverForTesting, "And its bridge");
        }

        [UnityTest]
        public IEnumerator APlayersBridge_ClosesWhenItsNetcodeStops()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, (host, port) => RelayedOnThisMachine(host, port), started, held, relays);
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome);
            Assert.IsTrue(held[0].Relay.HandsArrivalsOverForTesting, "Precondition: the bridge takes the relay's arrivals");

            NetworkManager client = _managers.Last();
            client.Shutdown();
            yield return FlockTestWait.Until(() => !held[0].Relay.HandsArrivalsOverForTesting, "the bridge closed with the netcode", 5f);
        }
    }
}
#endif
