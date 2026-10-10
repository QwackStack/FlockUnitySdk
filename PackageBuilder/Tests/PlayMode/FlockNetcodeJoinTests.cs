#if FLOCK_NETCODE_FOR_GAMEOBJECTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // Join verification on real frames: this game hosts through StartNetcodeAsync, and other players' games are plain Netcode for
    // GameObjects clients on loopback sending what a game would (a token from the session server in memory, or none).
    public partial class FlockNetcodeTests
    {
        private const string C = "player-c";

        private sealed class Hosting
        {
            public FlockMultiplayerSession Session;
            public FakeSessionServer.Session OnServer;
            public NetworkManager Manager;
            public int Port;
        }

        private IEnumerator HostedWithNetcode(Hosting hosting, FlockNetcodeOptions options = null, Action<NetworkManager> before = null, Action<Task<FlockNetcodeStartResult>> startedWith = null)
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            hosting.Session = held[0];
            hosting.OnServer = _server.ById(held[0].Id);
            hosting.Port = FreePort();
            hosting.Manager = Manager(hosting.Port);
            before?.Invoke(hosting.Manager);
            Task<FlockNetcodeStartResult> started = hosting.Session.StartNetcodeAsync(hosting.Manager, options);
            yield return Done(started, "hosting");
            Assert.IsFalse(started.IsFaulted, started.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, started.Result.Outcome, "Precondition: hosting");
            startedWith?.Invoke(started);
        }

        // Another player's game: Netcode for GameObjects alone, with the approval switch a host checking players needs.
        private NetworkManager PlayerConnecting(int hostPort, byte[] payload)
        {
            NetworkManager player = Manager(FreePort());
            TransportOf(player).SetConnectionData("127.0.0.1", (ushort)hostPort);
            player.NetworkConfig.ConnectionApproval = true;
            player.NetworkConfig.ConnectionData = payload;
            Assert.IsTrue(player.StartClient(), "Precondition: the player's game started connecting");
            return player;
        }

        private string SeatedWithToken(Hosting hosting, string playerId)
        {
            _server.Joins(hosting.OnServer, playerId);
            return _server.MintsJoinToken(hosting.OnServer, playerId);
        }

        private static byte[] WithToken(string token, byte[] gamesBytes = null) => FlockJoinTokenPayload.Wrap(token, gamesBytes);

        private static IEnumerator RefusedWith(NetworkManager player, string reason)
        {
            yield return FlockTestWait.Until(() => !player.IsListening && !player.ShutdownInProgress, "the player's game was turned away", 15f);
            Assert.IsFalse(player.IsConnectedClient);
            Assert.AreEqual(reason, player.DisconnectReason, "Refused for its own reason");
        }

        private static IEnumerator LetIn(NetworkManager host, NetworkManager player, int clients)
        {
            float until = Time.realtimeSinceStartup + 15f;
            while (!(player.IsConnectedClient && host.ConnectedClientsIds.Count == clients) && Time.realtimeSinceStartup < until)
                yield return null;
            Assert.IsTrue(player.IsConnectedClient && host.ConnectedClientsIds.Count == clients,
                $"the player was let in: player listening {player.IsListening}, connected {player.IsConnectedClient}, reason \"{player.DisconnectReason}\"; " +
                $"host clients {host.ConnectedClientsIds.Count}, waiting {host.PendingClients.Count}; scenes loaded: " +
                string.Join(", ", Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount).Select(index => UnityEngine.SceneManagement.SceneManager.GetSceneAt(index).name)));
        }

        private static ulong Newest(NetworkManager host) => host.ConnectedClientsIds.Max();

        private string TokenSentToVerify(int index) => (string)JObject.Parse(_server.RequestsTo(FakeSessionServer.Verify)[index].JsonBody)["token"];

        // ---- the host lets in only players Flock vouches for ----

        [UnityTest]
        public IEnumerator APlayerWithTheirToken_IsLetIn_AndKnownByTheirConnection()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            Assert.IsTrue(hosting.Manager.NetworkConfig.ConnectionApproval, "The host checks every player");
            string token = SeatedWithToken(hosting, B);

            NetworkManager player = PlayerConnecting(hosting.Port, WithToken(token));
            yield return LetIn(hosting.Manager, player, 2);
            Assert.AreEqual(1, _server.Count(FakeSessionServer.Verify));
            Assert.AreEqual(token, TokenSentToVerify(0), "The token the player sent was checked with Flock");
            Assert.AreEqual(B, hosting.Session.PlayerForConnection(Newest(hosting.Manager)));
            Assert.AreEqual(A, hosting.Session.PlayerForConnection(NetworkManager.ServerClientId), "The host's own connection is its own player");
            Assert.IsNull(hosting.Session.PlayerForConnection(999), "A connection that is not here");
        }

        [UnityTest]
        public IEnumerator APlayerWithNoToken_IsRefused_WithoutAskingFlock()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);

            NetworkManager player = PlayerConnecting(hosting.Port, new byte[] { 1, 2, 3 });
            yield return RefusedWith(player, FlockNetcodeRefusedReason.NoJoinToken);
            Assert.AreEqual(0, _server.Count(FakeSessionServer.Verify));
            Assert.AreEqual(1, hosting.Manager.ConnectedClientsIds.Count, "Only the host");
        }

        [UnityTest]
        public IEnumerator AMadeUpToken_AnotherSessionsToken_AndAnExpiredOne_AreRefusedByFlock()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            FakeSessionServer.Session another = _server.HostedBy(C);
            string anotherSessions = _server.MintsJoinToken(another, C);

            NetworkManager madeUp = PlayerConnecting(hosting.Port, WithToken("made-up"));
            yield return RefusedWith(madeUp, FlockNetcodeRefusedReason.JoinTokenInvalid);
            NetworkManager elsewhere = PlayerConnecting(hosting.Port, WithToken(anotherSessions));
            yield return RefusedWith(elsewhere, FlockNetcodeRefusedReason.JoinTokenInvalid);
            string expiring = SeatedWithToken(hosting, B);
            _server.ExpireJoinTokens();
            NetworkManager late = PlayerConnecting(hosting.Port, WithToken(expiring));
            yield return RefusedWith(late, FlockNetcodeRefusedReason.JoinTokenInvalid);

            Assert.AreEqual(new[] { "made-up", anotherSessions, expiring }, Enumerable.Range(0, 3).Select(TokenSentToVerify).ToArray(), "Each refused on Flock's answer to it");
            Assert.AreEqual(1, hosting.Manager.ConnectedClientsIds.Count, "Only the host");
        }

        [UnityTest]
        public IEnumerator TheSamePlayerTwice_TheSecondIsRefused_AndTheFirstStays()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            string token = SeatedWithToken(hosting, B);
            NetworkManager first = PlayerConnecting(hosting.Port, WithToken(token));
            yield return LetIn(hosting.Manager, first, 2);
            ulong firstsConnection = Newest(hosting.Manager);

            // The same token checks out again within its 60 s, so only the host can turn the second connection away.
            NetworkManager second = PlayerConnecting(hosting.Port, WithToken(token));
            yield return RefusedWith(second, FlockNetcodeRefusedReason.AlreadyConnected);
            Assert.AreEqual(2, _server.Count(FakeSessionServer.Verify), "Flock vouched for the token both times");
            Assert.IsTrue(first.IsConnectedClient, "The first connection keeps its place");
            Assert.AreEqual(B, hosting.Session.PlayerForConnection(firstsConnection));
        }

        [UnityTest]
        public IEnumerator APlayerWhoLeft_CanConnectAgain()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            string token = SeatedWithToken(hosting, B);
            NetworkManager first = PlayerConnecting(hosting.Port, WithToken(token));
            yield return LetIn(hosting.Manager, first, 2);
            ulong firstsConnection = Newest(hosting.Manager);
            first.Shutdown();
            yield return FlockTestWait.Until(() => hosting.Manager.ConnectedClientsIds.Count == 1, "the host saw the player go");
            Assert.IsNull(hosting.Session.PlayerForConnection(firstsConnection), "A connection that left is nobody's");

            NetworkManager again = PlayerConnecting(hosting.Port, WithToken(token));
            yield return LetIn(hosting.Manager, again, 2);
            Assert.AreEqual(B, hosting.Session.PlayerForConnection(Newest(hosting.Manager)));
        }

        [UnityTest]
        public IEnumerator APlayerJoiningASessionThatEnded_IsRefused_SessionEnded()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            string token = SeatedWithToken(hosting, B);
            _server.Ends(hosting.OnServer);

            NetworkManager player = PlayerConnecting(hosting.Port, WithToken(token));
            yield return RefusedWith(player, FlockNetcodeRefusedReason.SessionEnded);
            Assert.IsTrue(hosting.Session.HasEnded, "The host learned why from Flock");
        }

        [UnityTest]
        public IEnumerator APlayerReachingADeviceNoLongerHosting_IsRefused_NotHost()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            SeatedWithToken(hosting, B);
            string token = SeatedWithToken(hosting, C);
            _server.MakesHost(hosting.OnServer, B);

            NetworkManager player = PlayerConnecting(hosting.Port, WithToken(token));
            yield return RefusedWith(player, FlockNetcodeRefusedReason.NotHost);
        }

        // ---- when Flock cannot answer, nobody unchecked gets in ----

        [UnityTest]
        public IEnumerator FlockAnsweringSomethingElse_RefusesThePlayer_AsUnreachable()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            string token = SeatedWithToken(hosting, B);
            _server.AnswerNext(FakeSessionServer.Verify, FakeSessionServer.Refused(422, "request.validation_failed"));

            NetworkManager player = PlayerConnecting(hosting.Port, WithToken(token));
            yield return RefusedWith(player, FlockNetcodeRefusedReason.FlockUnreachable);
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("Flock could not check their join token")), "Said why");
        }

        [UnityTest]
        public IEnumerator AFlockCheckThatNeverAnswers_RefusesWithAReason_BeforeNetcodeDropsThePlayer()
        {
            Hosting hosting = new Hosting();
            // Netcode for GameObjects drops a player waiting longer than this without a word; the check ends 2 s before.
            yield return HostedWithNetcode(hosting, null, manager => manager.NetworkConfig.ClientConnectionBufferTimeout = 4);
            string token = SeatedWithToken(hosting, B);
            FakeSessionServer.Held checking = _server.HoldNext(FakeSessionServer.Verify);

            float started = Time.realtimeSinceStartup;
            NetworkManager player = PlayerConnecting(hosting.Port, WithToken(token));
            yield return FlockTestWait.Until(() => checking.Arrived, "the check was sent");
            yield return RefusedWith(player, FlockNetcodeRefusedReason.FlockUnreachable);
            float took = Time.realtimeSinceStartup - started;
            Assert.Less(took, 4f, "Refused before the netcode's own drop");
            Assert.Greater(took, 1.5f, "Given its time first");
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("did not answer the check of their join token within 2 s")));

            checking.Release();
            yield return null;
            yield return null;
            Assert.AreEqual(1, hosting.Manager.ConnectedClientsIds.Count, "A late yes lets nobody in");
        }

        [UnityTest]
        public IEnumerator AHostWaitingLongerThanItsPlayers_StillRefusesBeforeThePlayerGivesUp()
        {
            Hosting hosting = new Hosting();
            // The host waits 30 s for a player; the player gives up after its own 10 s, Netcode for GameObjects' default.
            yield return HostedWithNetcode(hosting, null, manager => manager.NetworkConfig.ClientConnectionBufferTimeout = 30);
            string token = SeatedWithToken(hosting, B);
            FakeSessionServer.Held checking = _server.HoldNext(FakeSessionServer.Verify);

            float started = Time.realtimeSinceStartup;
            NetworkManager player = PlayerConnecting(hosting.Port, WithToken(token));
            Assert.AreEqual(10, player.NetworkConfig.ClientConnectionBufferTimeout, "Precondition: the player's own timeout is the default");
            yield return FlockTestWait.Until(() => checking.Arrived, "the check was sent");
            yield return RefusedWith(player, FlockNetcodeRefusedReason.FlockUnreachable);
            Assert.Less(Time.realtimeSinceStartup - started, 10f, "Told why before its own timeout");
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("within 8 s")));
            checking.Release();
        }

        [UnityTest]
        public IEnumerator TenChecksWaitingOnFlock_TheHostsFramesKeepTheirPace()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            List<FakeSessionServer.Held> checks = Enumerable.Range(0, 10).Select(_ => _server.HoldNext(FakeSessionServer.Verify)).ToList();
            List<NetworkManager.ConnectionApprovalResponse> responses = new List<NetworkManager.ConnectionApprovalResponse>();
            List<string> tokens = Enumerable.Range(0, 10).Select(index => SeatedWithToken(hosting, "player-" + index)).ToList();
            // Ten players waiting on the host, as Netcode for GameObjects lists them while it asks the check about each.
            for (int index = 0; index < 10; index++)
                hosting.Manager.PendingClients[1000 + (ulong)index] = new PendingClient();
            float asking = Time.realtimeSinceStartup;
            for (int index = 0; index < 10; index++)
            {
                NetworkManager.ConnectionApprovalResponse response = new NetworkManager.ConnectionApprovalResponse();
                hosting.Manager.ConnectionApprovalCallback(new NetworkManager.ConnectionApprovalRequest { ClientNetworkId = 1000 + (ulong)index, Payload = WithToken(tokens[index]) }, response);
                responses.Add(response);
            }
            Assert.Less(Time.realtimeSinceStartup - asking, 0.25f, "Netcode for GameObjects asks inside a frame: the ten questions came back at once");
            Assert.IsTrue(responses.All(response => response.Pending), "Each waits for Flock without holding the frame");
            Assert.IsTrue(checks.All(check => check.Arrived), "Precondition: ten checks on their way");

            float slowest = 0f;
            for (int frame = 0; frame < 30; frame++)
            {
                float before = Time.realtimeSinceStartup;
                yield return null;
                slowest = Mathf.Max(slowest, Time.realtimeSinceStartup - before);
            }
            Assert.Less(slowest, 0.25f, "No frame waited on a check");
            Assert.IsTrue(responses.All(response => response.Pending), "Still waiting on Flock");

            foreach (FakeSessionServer.Held check in checks)
                check.Release();
            yield return FlockTestWait.Until(() => responses.All(response => !response.Pending), "every answer was taken");
            Assert.IsTrue(responses.All(response => response.Approved), "Each let in on Flock's answer");
            Assert.AreEqual("player-7", hosting.Session.PlayerForConnection(1007));
            for (int index = 0; index < 10; index++)
                hosting.Manager.PendingClients.Remove(1000 + (ulong)index);
        }

        [UnityTest]
        public IEnumerator AConnectionTheGameRefused_HoldsNoPlace_EvenBeforeNetcodeDropsIt()
        {
            int calls = 0;
            FlockNetcodeOptions options = new FlockNetcodeOptions
            {
                ApproveJoiningPlayer = (joining, response) =>
                {
                    if (++calls == 2)
                        response.Approved = false;
                },
            };
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, options);
            string token = SeatedWithToken(hosting, B);
            // Both still listed as waiting: Netcode for GameObjects drops a refused one only when it next reads the answers.
            hosting.Manager.PendingClients[2000] = new PendingClient();
            hosting.Manager.PendingClients[2001] = new PendingClient();
            try
            {
                NetworkManager.ConnectionApprovalResponse refused = new NetworkManager.ConnectionApprovalResponse();
                hosting.Manager.ConnectionApprovalCallback(new NetworkManager.ConnectionApprovalRequest { ClientNetworkId = 2000, Payload = WithToken(token) }, refused);
                yield return FlockTestWait.Until(() => !refused.Pending, "the game refused the first");
                Assert.IsFalse(refused.Approved);
                Assert.IsNull(hosting.Session.PlayerForConnection(2000), "A refused connection is nobody's");

                NetworkManager.ConnectionApprovalResponse next = new NetworkManager.ConnectionApprovalResponse();
                hosting.Manager.ConnectionApprovalCallback(new NetworkManager.ConnectionApprovalRequest { ClientNetworkId = 2001, Payload = WithToken(token) }, next);
                yield return FlockTestWait.Until(() => !next.Pending, "the second was answered");
                Assert.IsTrue(next.Approved, next.Reason);
                Assert.AreEqual(B, hosting.Session.PlayerForConnection(2001));
            }
            finally
            {
                hosting.Manager.PendingClients.Remove(2000);
                hosting.Manager.PendingClients.Remove(2001);
            }
        }

        // ---- the game's own check, after Flock's ----

        [UnityTest]
        public IEnumerator TheGamesOwnCheck_RunsAfterFlocks_WithThePlayerAndTheGamesOwnBytes()
        {
            List<FlockJoiningPlayer> asked = new List<FlockJoiningPlayer>();
            FlockNetcodeOptions options = new FlockNetcodeOptions { ApproveJoiningPlayer = (joining, response) => asked.Add(joining) };
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, options);
            Assert.AreEqual(1, asked.Count, "Asked about the host's own connection");
            Assert.AreEqual(A, asked[0].PlayerId);
            Assert.AreEqual(NetworkManager.ServerClientId, asked[0].ClientId);

            NetworkManager madeUp = PlayerConnecting(hosting.Port, WithToken("made-up"));
            yield return RefusedWith(madeUp, FlockNetcodeRefusedReason.JoinTokenInvalid);
            Assert.AreEqual(1, asked.Count, "Never asked about a player Flock refused");

            NetworkManager player = PlayerConnecting(hosting.Port, WithToken(SeatedWithToken(hosting, B), new byte[] { 4, 5 }));
            yield return LetIn(hosting.Manager, player, 2);
            Assert.AreEqual(2, asked.Count);
            Assert.AreEqual(B, asked[1].PlayerId, "Flock's verified player");
            Assert.AreEqual(Newest(hosting.Manager), asked[1].ClientId);
            CollectionAssert.AreEqual(new byte[] { 4, 5 }, asked[1].ConnectionData, "The game's own bytes, without the token");
        }

        [UnityTest]
        public IEnumerator TheHostsOwnBytes_HandedToTheGamesCheck_AreACopy()
        {
            byte[] hostsBytes = { 1, 2 };
            FlockNetcodeOptions options = new FlockNetcodeOptions { ApproveJoiningPlayer = (joining, response) => joining.ConnectionData[0] = 99 };
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, options, manager => manager.NetworkConfig.ConnectionData = hostsBytes);
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, hosting.Manager.NetworkConfig.ConnectionData, "The game's check changed its own copy only");
        }

        [UnityTest]
        public IEnumerator TheGamesOwnRefusal_StandsWithItsReason_AndKeepsNoPlace()
        {
            int calls = 0;
            FlockNetcodeOptions options = new FlockNetcodeOptions
            {
                ApproveJoiningPlayer = (joining, response) =>
                {
                    calls++;
                    if (calls == 2)
                    {
                        response.Approved = false;
                        response.Reason = "banned";
                    }
                    else if (calls == 3)
                    {
                        response.Approved = false;
                    }
                    else if (calls == 4)
                    {
                        throw new InvalidOperationException("the game's check broke");
                    }
                },
            };
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, options);
            string token = SeatedWithToken(hosting, B);

            NetworkManager banned = PlayerConnecting(hosting.Port, WithToken(token));
            yield return RefusedWith(banned, "banned");
            NetworkManager noReason = PlayerConnecting(hosting.Port, WithToken(token));
            yield return RefusedWith(noReason, FlockNetcodeRefusedReason.RefusedByTheGame);
            NetworkManager broken = PlayerConnecting(hosting.Port, WithToken(token));
            yield return RefusedWith(broken, FlockNetcodeRefusedReason.GameCheckFailed);
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("the game's own check threw")));

            NetworkManager allowed = PlayerConnecting(hosting.Port, WithToken(token));
            yield return LetIn(hosting.Manager, allowed, 2);
            Assert.AreEqual(B, hosting.Session.PlayerForConnection(Newest(hosting.Manager)), "A refusal kept no place for the player");
        }

        [UnityTest]
        public IEnumerator AnApprovedPlayer_GetsThePlayerObjectTheConfigNames()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            GameObject prefab = new GameObject("player prefab for a test");
            NetworkManager.ConnectionApprovalResponse withPrefab = new NetworkManager.ConnectionApprovalResponse();
            NetworkManager.ConnectionApprovalResponse without = new NetworkManager.ConnectionApprovalResponse();
            try
            {
                hosting.Manager.NetworkConfig.PlayerPrefab = prefab;
                hosting.Manager.ConnectionApprovalCallback(new NetworkManager.ConnectionApprovalRequest { ClientNetworkId = NetworkManager.ServerClientId }, withPrefab);
                hosting.Manager.NetworkConfig.PlayerPrefab = null;
                hosting.Manager.ConnectionApprovalCallback(new NetworkManager.ConnectionApprovalRequest { ClientNetworkId = NetworkManager.ServerClientId }, without);
            }
            finally
            {
                hosting.Manager.NetworkConfig.PlayerPrefab = null;
                UnityEngine.Object.Destroy(prefab);
            }
            Assert.IsTrue(withPrefab.Approved && withPrefab.CreatePlayerObject, "As with approval off: a player object when the config names one");
            Assert.IsTrue(without.Approved);
            Assert.IsFalse(without.CreatePlayerObject);
        }

        // ---- the game's NetworkManager, before and after ----

        [UnityTest]
        public IEnumerator AGameWithItsOwnApprovalCallback_IsToldToMoveIt_AndNothingStarts()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            NetworkManager manager = Manager(FreePort());
            manager.ConnectionApprovalCallback = (request, response) => response.Approved = true;

            StringAssert.Contains("ApproveJoiningPlayer", Assert.Throws<FlockValidationException>(() => held[0].StartNetcodeAsync(manager)).Message);
            Assert.IsFalse(manager.IsListening);
            Assert.AreEqual(0, _server.Count(FakeSessionServer.Publish));
        }

        [UnityTest]
        public IEnumerator WhenTheNetcodeStops_TheNetworkManagerIsAsTheGameHadIt_AndCanHostAgain()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            hosting.Manager.Shutdown();
            yield return FlockTestWait.Until(() => !hosting.Manager.IsListening && !hosting.Manager.ShutdownInProgress, "stopped");
            Assert.IsNull(hosting.Manager.ConnectionApprovalCallback, "Flock's check removed");
            Assert.IsFalse(hosting.Manager.NetworkConfig.ConnectionApproval, "The switch as the game had it");
            Assert.IsNull(hosting.Session.PlayerForConnection(NetworkManager.ServerClientId), "Nothing remembered");

            Task<FlockNetcodeStartResult> again = hosting.Session.StartNetcodeAsync(hosting.Manager);
            yield return Done(again, "hosting again");
            Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, again.Result.Outcome);
            NetworkManager player = PlayerConnecting(hosting.Port, WithToken(SeatedWithToken(hosting, B)));
            yield return LetIn(hosting.Manager, player, 2);
        }

        [UnityTest]
        public IEnumerator AnEarlierStartStopping_LeavesTheNewerStartForTheSessionInPlace()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            NetworkManager newer = Manager(FreePort());
            Task<FlockNetcodeStartResult> again = hosting.Session.StartNetcodeAsync(newer);
            yield return Done(again, "hosting on a second NetworkManager");
            Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, again.Result.Outcome, "Precondition: the newer start");

            hosting.Manager.Shutdown();
            yield return FlockTestWait.Until(() => !hosting.Manager.IsListening && !hosting.Manager.ShutdownInProgress, "the earlier one stopped");
            Assert.AreEqual(A, hosting.Session.PlayerForConnection(NetworkManager.ServerClientId), "The newer start still answers");
            NetworkManager player = PlayerConnecting(again.Result.Port, WithToken(SeatedWithToken(hosting, B)));
            yield return LetIn(newer, player, 2);
            Assert.AreEqual(B, hosting.Session.PlayerForConnection(Newest(newer)));
        }

        [UnityTest]
        public IEnumerator ACheckTheGameSetWhileHosting_IsLeftInPlace_WhenTheNetcodeStops()
        {
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting);
            Action<NetworkManager.ConnectionApprovalRequest, NetworkManager.ConnectionApprovalResponse> games = (request, response) => response.Approved = true;
            hosting.Manager.ConnectionApprovalCallback = games;
            hosting.Manager.Shutdown();
            yield return FlockTestWait.Until(() => !hosting.Manager.IsListening && !hosting.Manager.ShutdownInProgress, "stopped");
            Assert.AreSame(games, hosting.Manager.ConnectionApprovalCallback, "The game's own check stays");
            Assert.IsTrue(hosting.Manager.NetworkConfig.ConnectionApproval, "With the switch it needs");
            hosting.Manager.ConnectionApprovalCallback = null;
        }

        [UnityTest]
        public IEnumerator AnAnswerLandingAfterThePlayerLeft_LetsNobodyIn_AndRemembersNothing()
        {
            List<FlockJoiningPlayer> asked = new List<FlockJoiningPlayer>();
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { ApproveJoiningPlayer = (joining, response) => asked.Add(joining) });
            string token = SeatedWithToken(hosting, B);
            FakeSessionServer.Held checking = _server.HoldNext(FakeSessionServer.Verify);
            NetworkManager player = PlayerConnecting(hosting.Port, WithToken(token));
            yield return FlockTestWait.Until(() => checking.Arrived, "the check was sent");
            ulong waiting = hosting.Manager.PendingClients.Keys.Single();

            player.Shutdown();
            yield return FlockTestWait.Until(() => hosting.Manager.PendingClients.Count == 0, "the host saw the player go");
            using (UnityLogLines logged = new UnityLogLines())
            {
                checking.Release();
                yield return new WaitForSecondsRealtime(0.5f);
                Assert.IsEmpty(logged.WarningsAndErrors, "Netcode for GameObjects was handed nothing it complains about");
            }
            Assert.AreEqual(1, asked.Count, "The game was asked about its own connection only");
            Assert.IsNull(hosting.Session.PlayerForConnection(waiting));
            Assert.AreEqual(1, hosting.Manager.ConnectedClientsIds.Count);
        }

        // Warnings and errors Unity logs while it is held, Netcode for GameObjects' own included.
        private sealed class UnityLogLines : IDisposable
        {
            internal readonly List<string> WarningsAndErrors = new List<string>();

            internal UnityLogLines() => Application.logMessageReceived += Heard;

            private void Heard(string text, string stack, LogType type)
            {
                if (type != LogType.Log)
                    WarningsAndErrors.Add(type + ": " + text);
            }

            public void Dispose() => Application.logMessageReceived -= Heard;
        }

        [UnityTest]
        public IEnumerator AnAnswerLandingAfterTheNetcodeStopped_ChangesNothing()
        {
            List<FlockJoiningPlayer> asked = new List<FlockJoiningPlayer>();
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { ApproveJoiningPlayer = (joining, response) => asked.Add(joining) });
            FakeSessionServer.Held checking = _server.HoldNext(FakeSessionServer.Verify);
            PlayerConnecting(hosting.Port, WithToken(SeatedWithToken(hosting, B)));
            yield return FlockTestWait.Until(() => checking.Arrived, "the check was sent");

            hosting.Manager.Shutdown();
            yield return FlockTestWait.Until(() => !hosting.Manager.IsListening && !hosting.Manager.ShutdownInProgress, "stopped");
            using (UnityLogLines logged = new UnityLogLines())
            {
                checking.Release();
                yield return new WaitForSecondsRealtime(0.5f);
                Assert.IsEmpty(logged.WarningsAndErrors);
            }
            Assert.AreEqual(1, asked.Count, "The game is not asked once its netcode has stopped");
            Assert.IsNull(hosting.Manager.ConnectionApprovalCallback);
        }

        [UnityTest]
        public IEnumerator AnAnswerLandingAfterTheNetcodeStopped_WithThePlayerStillListed_ChangesNothing()
        {
            List<FlockJoiningPlayer> asked = new List<FlockJoiningPlayer>();
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { ApproveJoiningPlayer = (joining, response) => asked.Add(joining) });
            FakeSessionServer.Held checking = _server.HoldNext(FakeSessionServer.Verify);
            // A player the stopped netcode still lists as waiting, so only the stop itself can turn the late answer away.
            hosting.Manager.PendingClients[3000] = new PendingClient();
            try
            {
                NetworkManager.ConnectionApprovalResponse response = new NetworkManager.ConnectionApprovalResponse();
                hosting.Manager.ConnectionApprovalCallback(new NetworkManager.ConnectionApprovalRequest { ClientNetworkId = 3000, Payload = WithToken(SeatedWithToken(hosting, B)) }, response);
                yield return FlockTestWait.Until(() => checking.Arrived, "the check was sent");

                hosting.Manager.Shutdown();
                yield return FlockTestWait.Until(() => !hosting.Manager.IsListening && !hosting.Manager.ShutdownInProgress, "stopped");
                Assert.IsTrue(hosting.Manager.PendingClients.ContainsKey(3000), "Precondition: still listed");
                checking.Release();
                yield return new WaitForSecondsRealtime(0.5f);
                Assert.AreEqual(1, asked.Count, "The game is not asked once its netcode has stopped");
                Assert.IsFalse(response.Approved, "Nobody let in");
                Assert.AreEqual(0, _h.Logger.Warnings.Count(warning => warning.Contains("Refused a joining player")), "Nobody is being refused");
            }
            finally
            {
                hosting.Manager.PendingClients.Remove(3000);
            }
        }

        [UnityTest]
        public IEnumerator PlayerForConnection_OnASessionNotHostingThroughFlock_IsNobody()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            Assert.IsNull(held[0].PlayerForConnection(NetworkManager.ServerClientId));
            FlockMultiplayerSession nothing = null;
            Assert.Throws<FlockValidationException>(() => nothing.PlayerForConnection(0));
        }

        // ---- the token's payload ----

        [Test]
        public void TheTokenRidesInFrontOfTheGamesBytes_AndOnlyAWholeOneIsRead()
        {
            Assert.IsTrue(FlockJoinTokenPayload.TryRead(FlockJoinTokenPayload.Wrap("abc", new byte[] { 9, 8 }), out string token, out byte[] gamesBytes));
            Assert.AreEqual("abc", token);
            CollectionAssert.AreEqual(new byte[] { 9, 8 }, gamesBytes);
            Assert.IsTrue(FlockJoinTokenPayload.TryRead(FlockJoinTokenPayload.Wrap("abc", null), out _, out byte[] none));
            Assert.AreEqual(0, none.Length, "No game bytes reads as none");

            byte[] whole = FlockJoinTokenPayload.Wrap("abc", null);
            Assert.IsFalse(FlockJoinTokenPayload.TryRead(null, out _, out _));
            Assert.IsFalse(FlockJoinTokenPayload.TryRead(new byte[0], out _, out _));
            Assert.IsFalse(FlockJoinTokenPayload.TryRead(whole.Take(whole.Length - 1).ToArray(), out _, out _), "A token cut short");
            byte[] otherMarker = (byte[])whole.Clone();
            otherMarker[0] = (byte)'X';
            Assert.IsFalse(FlockJoinTokenPayload.TryRead(otherMarker, out _, out _), "A game's own bytes that are not Flock's");
            byte[] emptyToken = (byte[])whole.Clone();
            emptyToken[5] = 0;
            emptyToken[6] = 0;
            Assert.IsFalse(FlockJoinTokenPayload.TryRead(emptyToken, out _, out _), "A token of no length");
            string longest = new string('t', 300);
            Assert.IsTrue(FlockJoinTokenPayload.TryRead(FlockJoinTokenPayload.Wrap(longest, null), out string longRead, out _), "A length past one byte");
            Assert.AreEqual(longest, longRead);
        }

        // ---- the joining player is told why ----

        private IEnumerator ConnectingToAHostThat(Action<NetworkManager.ConnectionApprovalRequest, NetworkManager.ConnectionApprovalResponse> approves, List<Task<FlockNetcodeStartResult>> outcome, NetworkManager client = null)
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            int port = FreePort();
            NetworkManager host = Manager(port);
            TransportOf(host).SetConnectionData("127.0.0.1", (ushort)port, "127.0.0.1");
            host.NetworkConfig.ConnectionApproval = true;
            host.ConnectionApprovalCallback = approves;
            Assert.IsTrue(host.StartHost(), "Precondition: the other player's game hosts here");
            _server.Publishes(served[0], DirectOnThisMachine(port));
            outcome.Add(held[0].StartNetcodeAsync(client ?? Manager(FreePort())));
            yield return Done(outcome[0], "the connect ended", 15f);
            Assert.IsFalse(outcome[0].IsFaulted, outcome[0].Exception?.InnerException?.ToString());
        }

        [UnityTest]
        public IEnumerator APlayerTheHostRefuses_IsToldWhy_AndGetsTheirBytesBack()
        {
            List<Task<FlockNetcodeStartResult>> outcome = new List<Task<FlockNetcodeStartResult>>();
            NetworkManager client = Manager(FreePort());
            byte[] gamesBytes = { 1 };
            client.NetworkConfig.ConnectionData = gamesBytes;
            yield return ConnectingToAHostThat((request, response) =>
            {
                response.Approved = request.ClientNetworkId == NetworkManager.ServerClientId;
                response.Reason = response.Approved ? null : FlockNetcodeRefusedReason.JoinTokenInvalid;
            }, outcome, client);
            Assert.AreEqual(FlockNetcodeStartOutcome.Refused, outcome[0].Result.Outcome);
            Assert.AreEqual(FlockNetcodeRefusedReason.JoinTokenInvalid, outcome[0].Result.RefusedReason);
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("refused this player (join_token_invalid)")));
            Assert.AreSame(gamesBytes, client.NetworkConfig.ConnectionData);
            Assert.IsFalse(client.NetworkConfig.ConnectionApproval);
        }

        [UnityTest]
        public IEnumerator AHostThatGivesNoReason_IsCouldNotConnect_NotARefusal()
        {
            List<Task<FlockNetcodeStartResult>> outcome = new List<Task<FlockNetcodeStartResult>>();
            yield return ConnectingToAHostThat((request, response) => response.Approved = request.ClientNetworkId == NetworkManager.ServerClientId, outcome);
            Assert.AreEqual(FlockNetcodeStartOutcome.CouldNotConnect, outcome[0].Result.Outcome, "Netcode's own disconnect text is not a reason the host gave");
            Assert.IsNull(outcome[0].Result.RefusedReason);
        }

        [UnityTest]
        public IEnumerator APlayerFlockGivesNoToken_DoesNotConnect_AndIsToldWhy()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            _server.Publishes(served[0], DirectOnThisMachine(FreePort()));
            _server.AnswerNext(FakeSessionServer.JoinToken, FakeSessionServer.Refused(404, "multiplayer.not_a_participant"));
            NetworkManager client = Manager(FreePort());

            Task<FlockNetcodeStartResult> started = held[0].StartNetcodeAsync(client);
            yield return Done(started, "refused by Flock");
            Assert.IsTrue(started.IsFaulted);
            Assert.AreEqual(FlockErrorCode.MultiplayerNotAParticipant, ((FlockException)started.Exception.InnerException).ErrorCode);
            Assert.IsFalse(client.IsListening, "Nothing started without a token");
        }
    }
}
#endif
