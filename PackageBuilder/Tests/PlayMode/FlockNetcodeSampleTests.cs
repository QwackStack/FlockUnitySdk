#if FLOCK_NETCODE_FOR_GAMEOBJECTS
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Providers;
using Flock.Samples;
using Flock.Tests.Support;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The Play With Friends sample on real frames: this game hosts or joins through the sample, and the other player's game is a
    // plain Netcode for GameObjects host on loopback that answers waves.
    public partial class FlockNetcodeTests
    {
        private const string WaveMessage = "flock-sample-wave";

        // The sample lives under its NetworkManager's object, so the fixture's tear-down removes both.
        private FlockPlayWithFriendsSample FriendsSample(NetworkManager manager)
        {
            GameObject holder = new GameObject("Play With Friends sample");
            holder.transform.SetParent(manager.transform);
            FlockPlayWithFriendsSample sample = holder.AddComponent<FlockPlayWithFriendsSample>();
            sample.NetworkManagerInUse = manager;
            return sample;
        }

        // Another player's game hosting here: it approves whoever comes, and waves back at every wave it hears.
        private NetworkManager WavingHost(int port, List<string> heard, bool approves = true)
        {
            NetworkManager host = Manager(port);
            TransportOf(host).SetConnectionData("127.0.0.1", (ushort)port, "127.0.0.1");
            host.NetworkConfig.ConnectionApproval = true;
            host.ConnectionApprovalCallback = (request, response) =>
            {
                response.Approved = approves || request.ClientNetworkId == NetworkManager.ServerClientId;
                response.Reason = response.Approved ? null : "full_for_the_test";
            };
            Assert.IsTrue(host.StartHost(), "Precondition: the other player's game hosts here");
            host.CustomMessagingManager.RegisterNamedMessageHandler(WaveMessage, (sender, reader) =>
            {
                reader.ReadValueSafe(out string playerId);
                heard.Add(playerId);
                using (FastBufferWriter writer = new FastBufferWriter(64, Allocator.Temp))
                {
                    writer.WriteValueSafe(A);
                    host.CustomMessagingManager.SendNamedMessage(WaveMessage, sender, writer, NetworkDelivery.Reliable);
                }
            });
            return host;
        }

        [UnityTest]
        public IEnumerator PlayWithFriends_TheHost_ShowsItsCode_AndStartsAsHost()
        {
            SignedInAs(A);
            NetworkManager manager = Manager(FreePort());
            FlockPlayWithFriendsSample sample = FriendsSample(manager);

            Task hosting = sample.HostAsync();
            yield return Done(hosting, "hosted");
            Assert.IsTrue(sample.Playing, sample.Status);
            Assert.IsTrue(sample.Session.IsHost);
            StringAssert.Contains(sample.Session.JoinCode, sample.Status, "The code is shown for the friend");
            Assert.IsTrue(manager.IsHost, "The sample started the NetworkManager it was given");
            Assert.AreEqual(1, _server.Count(FakeSessionServer.Publish), "Where to reach the host was published");

            Task ending = sample.LeaveAsync();
            yield return Done(ending, "ended");
            Assert.AreEqual("ended", _server.ById(sample.Session.Id).Status, "The host's End Session ends it for everyone");
            Assert.AreEqual("ended_by_host", _server.ById(sample.Session.Id).EndedReason, "Ended, not left: leaving alone would close it too");
            yield return FlockTestWait.Until(() => !manager.IsListening && !manager.ShutdownInProgress, "the netcode stopped");
        }

        [UnityTest]
        public IEnumerator PlayWithFriends_AFriend_JoinsWithTheCode_WavesBothWays_AndLeaves()
        {
            SignedInAs(B);
            FakeSessionServer.Session served = _server.HostedBy(A);
            int port = FreePort();
            List<string> hostHeard = new List<string>();
            WavingHost(port, hostHeard);
            _server.Publishes(served, DirectOnThisMachine(port));
            NetworkManager manager = Manager(FreePort());
            FlockPlayWithFriendsSample sample = FriendsSample(manager);

            Task joining = sample.JoinAsync(served.Code);
            yield return Done(joining, "joined", 15f);
            Assert.IsTrue(sample.Playing, sample.Status);
            Assert.IsFalse(sample.Session.IsHost);

            sample.Wave();
            yield return FlockTestWait.Until(() => hostHeard.Count == 1 && sample.Heard.Contains(A + " waved."), "the waves went both ways");
            Assert.AreEqual("", hostHeard[0], "A player's wave names nobody: the host names it from the connection");

            Task leaving = sample.LeaveAsync();
            yield return Done(leaving, "left");
            Assert.AreEqual("left", served.Seats.Single(seat => seat.PlayerId == B).Status, "The seat is given up");
            yield return FlockTestWait.Until(() => !manager.IsListening && !manager.ShutdownInProgress, "the netcode stopped");
        }

        // Another player's game connecting to the sample's host with its join token, hearing waves.
        private NetworkManager FriendConnecting(int hostPort, FakeSessionServer.Session served, string playerId, List<string> heard)
        {
            NetworkManager friend = Manager(FreePort());
            TransportOf(friend).SetConnectionData("127.0.0.1", (ushort)hostPort);
            friend.NetworkConfig.ConnectionApproval = true;
            friend.NetworkConfig.ConnectionData = WithToken(SeatedWithToken(new Hosting { OnServer = served }, playerId));
            Assert.IsTrue(friend.StartClient(), "Precondition: the friend's game started connecting");
            friend.OnClientConnectedCallback += _ => friend.CustomMessagingManager.RegisterNamedMessageHandler(WaveMessage, (sender, reader) =>
            {
                reader.ReadValueSafe(out string waver);
                heard.Add(waver);
            });
            return friend;
        }

        private void WaveFrom(NetworkManager friend, string playerId)
        {
            using (FastBufferWriter writer = new FastBufferWriter(64, Allocator.Temp))
            {
                writer.WriteValueSafe(playerId);
                friend.CustomMessagingManager.SendNamedMessage(WaveMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
            }
        }

        [UnityTest]
        public IEnumerator PlayWithFriends_TheHost_PassesAPlayersWaveOnToTheOthers_AndItsOwnToAll()
        {
            SignedInAs(A);
            int port = FreePort();
            NetworkManager manager = Manager(port);
            FlockPlayWithFriendsSample sample = FriendsSample(manager);
            Task hosting = sample.HostAsync();
            yield return Done(hosting, "hosted");
            FakeSessionServer.Session served = _server.ById(sample.Session.Id);
            List<string> heardByB = new List<string>();
            List<string> heardByC = new List<string>();
            NetworkManager b = FriendConnecting(port, served, B, heardByB);
            NetworkManager c = FriendConnecting(port, served, C, heardByC);
            yield return FlockTestWait.Until(() => b.IsConnectedClient && c.IsConnectedClient && manager.ConnectedClientsIds.Count == 3, "both friends connected");

            // B's game claims to be someone else; the host names B from the connection Flock verified.
            WaveFrom(b, "someone-else");
            yield return FlockTestWait.Until(() => heardByC.Contains(B) && sample.Heard.Contains(B + " waved."), "B's wave reached the host and C");
            Assert.IsFalse(heardByC.Contains("someone-else") || sample.Heard.Contains("someone-else waved."), "A name a player claims is not passed on");
            Assert.IsFalse(heardByB.Contains(B), "Not sent back to the one who waved");

            sample.Wave();
            yield return FlockTestWait.Until(() => heardByB.Contains(A) && heardByC.Contains(A), "the host's wave reached both");
        }

        [UnityTest]
        public IEnumerator ASampleGivenNoNetworkManager_InASceneWithNone_MakesOne()
        {
            SignedInAs(A);
            GameObject holder = new GameObject("Play With Friends sample, nothing given");
            FlockPlayWithFriendsSample sample = holder.AddComponent<FlockPlayWithFriendsSample>();
            typeof(FlockMultiplayerSample).GetField("_port", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(sample, (ushort)FreePort());
            Assert.IsTrue(NetworkManager.Singleton == null, "Precondition: no NetworkManager in the scene");

            Task hosting = sample.HostAsync();
            yield return Done(hosting, "hosted");
            NetworkManager made = sample.NetworkManagerInUse;
            Assert.IsNotNull(made, sample.Status);
            _managers.Add(made);
            UnityEngine.Object.Destroy(holder);
            Assert.AreEqual("NetworkManager (Flock sample)", made.gameObject.name);
            Assert.IsTrue(made.IsHost, sample.Status);
        }

        [UnityTest]
        public IEnumerator ASampleDestroyed_InASession_GivesTheSeatUp()
        {
            SignedInAs(B);
            FakeSessionServer.Session served = _server.HostedBy(A);
            int port = FreePort();
            WavingHost(port, new List<string>());
            _server.Publishes(served, DirectOnThisMachine(port));
            NetworkManager manager = Manager(FreePort());
            FlockPlayWithFriendsSample sample = FriendsSample(manager);
            Task joining = sample.JoinAsync(served.Code);
            yield return Done(joining, "joined", 15f);
            Assert.IsTrue(sample.Playing, "Precondition: playing");

            UnityEngine.Object.Destroy(sample.gameObject);
            yield return FlockTestWait.Until(() => served.Seats.Single(seat => seat.PlayerId == B).Status == "left", "the seat was given up");
            yield return FlockTestWait.Until(() => !manager.IsListening && !manager.ShutdownInProgress, "the netcode stopped");
        }

        [UnityTest]
        public IEnumerator PlayWithFriends_AWrongCode_SaysSo_AndJoinsNothing()
        {
            SignedInAs(B);
            FlockPlayWithFriendsSample sample = FriendsSample(Manager(FreePort()));

            Task joining = sample.JoinAsync("NOPE42");
            yield return Done(joining, "refused");
            StringAssert.Contains("No session has the code NOPE42", sample.Status);
            Assert.IsNull(sample.Session);
        }

        [UnityTest]
        public IEnumerator PlayWithFriends_TheHostEndingTheSession_StopsTheFriendsNetcode_AndSaysWhy()
        {
            SignedInAs(B);
            _server.HeartbeatSeconds = 1;
            FakeSessionServer.Session served = _server.HostedBy(A);
            int port = FreePort();
            WavingHost(port, new List<string>());
            _server.Publishes(served, DirectOnThisMachine(port));
            NetworkManager manager = Manager(FreePort());
            FlockPlayWithFriendsSample sample = FriendsSample(manager);
            Task joining = sample.JoinAsync(served.Code);
            yield return Done(joining, "joined", 15f);
            Assert.IsTrue(sample.Playing, "Precondition: playing");

            _server.Ends(served);
            yield return FlockTestWait.Until(() => sample.Session.HasEnded, "the end was read", 10f);
            StringAssert.Contains("The session ended (ended_by_host)", sample.Status);
            yield return FlockTestWait.Until(() => !manager.IsListening && !manager.ShutdownInProgress, "the netcode stopped");
        }

        [UnityTest]
        public IEnumerator PlayWithFriends_ARefusedConnection_SaysWhy_AndGivesTheSeatUp()
        {
            SignedInAs(B);
            FakeSessionServer.Session served = _server.HostedBy(A);
            int port = FreePort();
            WavingHost(port, new List<string>(), approves: false);
            _server.Publishes(served, DirectOnThisMachine(port));
            FlockPlayWithFriendsSample sample = FriendsSample(Manager(FreePort()));

            Task joining = sample.JoinAsync(served.Code);
            yield return Done(joining, "refused", 15f);
            StringAssert.Contains("The host refused this player: full_for_the_test", sample.Status);
            Assert.IsFalse(sample.Playing);
            Assert.AreEqual("left", served.Seats.Single(seat => seat.PlayerId == B).Status, "No seat kept in a session the player cannot play");
        }

        [UnityTest]
        public IEnumerator ASample_WhoseNetworkManagerIsAlreadyRunning_GivesTheSeatUp()
        {
            SignedInAs(A);
            NetworkManager running = Manager(FreePort());
            Assert.IsTrue(running.StartHost(), "Precondition: the game's own NetworkManager is running");
            FlockPlayWithFriendsSample sample = FriendsSample(running);

            Task hosting = sample.HostAsync();
            yield return Done(hosting, "refused");
            StringAssert.Contains("already running", sample.Status);
            Assert.AreEqual("ended", _server.ById(sample.Session.Id).Status, "The session it could not play is given up");
        }

        [UnityTest]
        public IEnumerator ASample_SignsInADeviceFlockHasNeverSeen_ByRegisteringIt()
        {
            SignedInAs(A);
            _h.Client.Authentication.Logout();
            DeviceSignIn signIn = new DeviceSignIn(_server);
            FlockHttpClient.Configure(signIn);
            FlockPlayWithFriendsSample sample = FriendsSample(Manager(FreePort()));

            Task signingIn = sample.SignInAsync("device-never-seen");
            yield return Done(signingIn, "signed in");
            Assert.AreEqual(1, signIn.Logins, "Tried the device's sign-in first");
            Assert.AreEqual(1, signIn.Registrations, "Then registered it");
            Assert.AreEqual("player-new", _h.Client.CurrentPlayerId, sample.Status);
        }

        // Device sign-in in front of the session server: the login refuses a device Flock has never seen, and the register makes it.
        private sealed class DeviceSignIn : IFlockHttpAdapter
        {
            private readonly IFlockHttpAdapter _rest;
            internal int Logins;
            internal int Registrations;

            internal DeviceSignIn(IFlockHttpAdapter rest) => _rest = rest;

            public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
            {
                if (request.Url.EndsWith("/v1/player/login/device"))
                {
                    Logins++;
                    return Task.FromResult(FakeSessionServer.Refused(401, "player.invalid_login_credentials"));
                }
                if (request.Url.EndsWith("/v1/player/register/device"))
                {
                    Registrations++;
                    string body = "{\"player_id\":\"player-new\",\"access_token\":\"" + FlockTestClient.MakeJwt("player-new", 3600) + "\",\"refresh_token\":\"refresh-new\"}";
                    return Task.FromResult(new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 200, Body = body });
                }
                return _rest.SendAsync(request, cancellationToken);
            }
        }
    }
}
#endif
