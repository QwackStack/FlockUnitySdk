#if FLOCK_NETCODE_FOR_GAMEOBJECTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // A relay that gives each address an IP of its own, as Cloudflare's does (measured 2026-10-12): a relayed player publishes its relay
    // address for its seat before connecting, and the host, reading the session every 2 s while it takes players through its relay,
    // lets each seated player's relay IP in.
    public partial class FlockNetcodeTests
    {
        private const string OtherRelayIp = "203.0.113.51";

        private static JObject SeatOnTheRelay(FlockRelayPeer address) => new JObject { ["mode"] = "relay", ["relay_address"] = address.ToString() };

        // An Always host whose relay gives each address an IP of its own, reading the session often; its heartbeats come every 5 minutes,
        // so within a test only the often read can let a player in.
        private IEnumerator RelayedHostOnItsOwnIp(Hosting hosting)
        {
            _heartbeatSecondsForTheNextSignIn = 300;
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, manager =>
            {
                RelayOn();
                _relay.RelayIps.Add(OtherRelayIp);
                _h.Client.Multiplayer.Sessions.SetRelayedPlayersReadIntervalForTesting(TimeSpan.FromSeconds(0.2));
            });
        }

        // ---- a host ----

        [UnityTest]
        public IEnumerator AHost_LetsInAPlayerOnAnotherRelayIp_OnlyOnceThePlayersSeatSaysWhere()
        {
            Hosting hosting = new Hosting();
            yield return RelayedHostOnItsOwnIp(hosting);
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            List<NetworkManager> players = new List<NetworkManager>();
            yield return RelayedPlayer(hosting, B, WithToken(SeatedWithToken(hosting, B)), relays, players);
            Assert.AreNotEqual(hosting.Session.Relay.Address.Ip, relays[0].Address.Ip, "Precondition: the player's relay address is on an IP of its own");

            // A seat published under another mode names no relay address, whatever else it holds.
            JObject notOnTheRelay = SeatOnTheRelay(relays[0].Address);
            notOnTheRelay["mode"] = "direct";
            _server.PublishesSeat(hosting.OnServer, B, notOnTheRelay);
            yield return new UnityEngine.WaitForSecondsRealtime(1.5f);
            Assert.IsFalse(players[0].IsConnectedClient, "Unheard while its seat names no relay address");
            Assert.AreEqual(1, hosting.Manager.ConnectedClientsIds.Count, "Only the host");

            _server.PublishesSeat(hosting.OnServer, B, SeatOnTheRelay(relays[0].Address));
            yield return LetIn(hosting.Manager, players[0], 2);
            Assert.AreEqual(B, hosting.Session.PlayerForConnection(Newest(hosting.Manager)));
        }

        [UnityTest]
        public IEnumerator AHostsPlayersChanged_FindsTheRelayAlreadyOpeningToTheNewlySeatedPlayer()
        {
            const string C = "player-c";
            Hosting hosting = new Hosting();
            yield return RelayedHostOnItsOwnIp(hosting);
            FlockRelayConnection relay = hosting.Session.Relay;
            FlockRelayPeer joining = new FlockRelayPeer(0xCB007133, 5000);
            Assert.AreEqual(OtherRelayIp, joining.ToString().Split(':')[0], "Precondition: the joining player's relay address is on the relay's other IP");
            Assert.IsTrue(relay.OpeningsAskedForTesting == 0 && !relay.IsOpenTo(joining), "Precondition: nothing opened or asked yet");
            bool openingSeen = false;
            hosting.Session.PlayersChanged += () => openingSeen = relay.OpeningsAskedForTesting > 0 || relay.IsOpenTo(joining);

            // One reading brings a newly seated player whose seat already names its relay address.
            _server.Joins(hosting.OnServer, C);
            _server.PublishesSeat(hosting.OnServer, C, SeatOnTheRelay(joining));
            Task<FlockMultiplayerSession> read = _h.Client.Multiplayer.GetSessionAsync(hosting.Session.Id);
            yield return Done(read, "the session was read");
            Assert.IsTrue(hosting.Session.Players.Any(player => player.PlayerId == C), "Precondition: the reading seated the player");
            Assert.IsTrue(openingSeen, "Inside the handler the relay is already opening to the new player's relay address");
        }

        [UnityTest]
        public IEnumerator AHost_ReadsTheSessionOften_OnlyWhileItTakesPlayersThroughItsRelay()
        {
            Hosting hosting = new Hosting();
            yield return RelayedHostOnItsOwnIp(hosting);
            FlockMultiplayerSessions sessions = _h.Client.Multiplayer.Sessions;
            yield return FlockTestWait.Until(() => sessions.ReadsForRelayedPlayersForTesting, "the host reads the session for relayed players", 3f);
            int before = _server.Count(FakeSessionServer.Read);
            yield return new UnityEngine.WaitForSecondsRealtime(1f);
            Assert.GreaterOrEqual(_server.Count(FakeSessionServer.Read) - before, 3, "Read about every 0.2 s");

            FlockRelayBridge bridge = FlockJoinChecks.RelayBridgeForTesting(hosting.Session);
            hosting.Manager.Shutdown();
            yield return FlockTestWait.Until(() => bridge.IsClosed, "the bridge closed with the netcode", 5f);
            yield return FlockTestWait.Until(() => !sessions.ReadsForRelayedPlayersForTesting, "the reads stopped", 3f);
            yield return new UnityEngine.WaitForSecondsRealtime(0.3f);
            int after = _server.Count(FakeSessionServer.Read);
            yield return new UnityEngine.WaitForSecondsRealtime(1f);
            Assert.AreEqual(after, _server.Count(FakeSessionServer.Read), "No reads once no player can come through the relay");
        }

        [UnityTest]
        public IEnumerator AHostWhoseRelayRefusesAPlayersIp_SaysSoOnce_HoweverOftenItReads()
        {
            Hosting hosting = new Hosting();
            yield return RelayedHostOnItsOwnIp(hosting);
            _relay.RefusedOpenings.Add(OtherRelayIp);
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            List<NetworkManager> players = new List<NetworkManager>();
            yield return RelayedPlayer(hosting, B, WithToken(SeatedWithToken(hosting, B)), relays, players);
            _server.PublishesSeat(hosting.OnServer, B, SeatOnTheRelay(relays[0].Address));

            yield return FlockTestWait.Until(() => _h.Logger.Warnings.Any(warning => warning.Contains("cannot be let in")), "the refusal was said", 3f);
            yield return new UnityEngine.WaitForSecondsRealtime(1.5f);
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("cannot be let in")), "Said once across about seven readings");
        }

        [UnityTest]
        public IEnumerator AHostWhoseOftenReadIsRefused_DoesNotSendItAgainEveryFrame()
        {
            Hosting hosting = new Hosting();
            yield return RelayedHostOnItsOwnIp(hosting);
            FlockMultiplayerSessions sessions = _h.Client.Multiplayer.Sessions;
            yield return FlockTestWait.Until(() => sessions.ReadsForRelayedPlayersForTesting, "the host reads the session for relayed players", 3f);

            // Refused as out of the session while the server still seats it: the read that learns why finds the session going on.
            _server.AnswerNext(FakeSessionServer.Read, FakeSessionServer.Refused(404, "multiplayer.session_not_found"));
            yield return FlockTestWait.Until(() => hosting.Session.Relay.ReadForPlayersRefused, "the refused read stopped", 3f);
            int after = _server.Count(FakeSessionServer.Read);
            yield return new UnityEngine.WaitForSecondsRealtime(1f);
            Assert.LessOrEqual(_server.Count(FakeSessionServer.Read) - after, 1, "Not started again for this relay: at most the read that learns why");
            Assert.IsFalse(hosting.Session.HasEnded, "Precondition: the server still seats it");
        }

        // ---- a player ----

        [UnityTest]
        public IEnumerator ARelayedPlayer_PublishesItsRelayAddressForItsSeat_BeforeConnecting()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, (host, port) => RelayedOnThisMachine(host, port), started, held, relays);

            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome);
            List<FlockHttpRequest> seatPublishes = _server.RequestsTo(FakeSessionServer.SeatPublish);
            Assert.AreEqual(1, seatPublishes.Count, "Its relay address, published once");
            JObject connection = JObject.Parse(seatPublishes[0].JsonBody)["connection_info"] as JObject;
            Assert.AreEqual("relay", (string)connection["mode"]);
            Assert.AreEqual(held[0].Relay.Address.ToString(), (string)connection["relay_address"]);
            List<string> routes = _server.RoutesInOrder();
            Assert.Less(routes.IndexOf(FakeSessionServer.SeatPublish), routes.IndexOf(FakeSessionServer.JoinToken), "Published before the netcode connects");
            Assert.IsFalse(_h.Client.Multiplayer.Sessions.ReadsForRelayedPlayersForTesting, "A player does not read the session for relayed players");
        }

        [UnityTest]
        public IEnumerator APlayerWhoseSeatSaysItsRelayAddress_DoesNotPublishItAgain_WhenItsNetcodeStartsAgain()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, (host, port) => RelayedOnThisMachine(host, port), started, held, relays);
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome, "Precondition: connected");
            NetworkManager client = _managers.Last();
            client.Shutdown();
            yield return FlockTestWait.Until(() => !client.IsListening && !client.ShutdownInProgress, "the player's netcode stopped");

            Task<FlockNetcodeStartResult> again = held[0].StartNetcodeAsync(client, new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
            yield return Done(again, "connected again", 15f);
            Assert.IsFalse(again.IsFaulted, again.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, again.Result.Outcome);
            Assert.AreEqual(1, _server.Count(FakeSessionServer.SeatPublish), "The same relay address is not published again");
        }

        [UnityTest]
        public IEnumerator APlayerWhoseRelayAddressFlockWillNotTake_StillConnects_AndSaysWhy()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            // A Flock from before the seat route: its own 404, which names no code.
            yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, (host, port) =>
            {
                _server.AnswerNext(FakeSessionServer.SeatPublish, new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 404, Body = "{\"detail\":\"Not Found\"}" });
                return RelayedOnThisMachine(host, port);
            }, started, held, relays);

            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome, "A relay giving every address one IP lets it in all the same");
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("could not be given to the host")), "Said why once");
        }

        [UnityTest]
        public IEnumerator APlayerWhoseRelayAddressFlockCannotTakeNow_AsksOnce_AndConnects()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            // A client that retries a passing failure, so sending once is the publish's own choice.
            _retriesForTheNextSignIn = 2;
            yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, (host, port) =>
            {
                _server.AnswerNext(FakeSessionServer.SeatPublish, FakeSessionServer.Refused(503, "request.service_unavailable"));
                return RelayedOnThisMachine(host, port);
            }, started, held, relays);

            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome);
            Assert.AreEqual(1, _server.Count(FakeSessionServer.SeatPublish), "Asked once: the connect does not wait for retries");
        }
    }
}
#endif
