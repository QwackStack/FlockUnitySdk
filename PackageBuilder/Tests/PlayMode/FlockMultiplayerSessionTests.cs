using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The player's multiplayer session on real frames against a session server in memory: one held session per sign-in, changed
    // only by the newest answer, its events, every way it ends, and its calls. (Heartbeats wait for the backend to say how often.)
    public class FlockMultiplayerSessionTests
    {
        private const string A = "player-a";
        private const string B = "player-b";
        private const string C = "player-c";

        private FlockTestClient _h;
        private FakeSessionServer _server;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        private FlockMultiplayerProvider SignedInAs(string player, int retries = 0)
        {
            _server = new FakeSessionServer();
            _h = FlockTestClient.Create(new FlockFakeTransport(), config =>
            {
                config.PartyRefreshInterval = TimeSpan.Zero;
                if (retries > 0)
                    config.RetryPolicy = new RetryPolicy { MaxRetries = retries, InitialDelay = TimeSpan.FromMilliseconds(50), UseJitter = false };
            });
            FlockHttpClient.Configure(_server);
            _h.LoginAs(player);
            return _h.Client.Multiplayer;
        }

        private static IEnumerator Done(Task task, string what) => FlockTestWait.Until(() => task.IsCompleted, what);

        private static IEnumerator RealSeconds(float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        private static string[] Ids(FlockMultiplayerSession session) => session.Players.Select(player => player.PlayerId).ToArray();

        private static T Failure<T>(Task task) where T : Exception
        {
            Assert.IsTrue(task.IsFaulted, "The call failed");
            T failure = task.Exception.InnerException as T;
            Assert.IsNotNull(failure, $"The call failed with {typeof(T).Name}, not {task.Exception.InnerException?.GetType().Name}");
            return failure;
        }

        // A session A hosts with B seated, held by this game as A.
        private IEnumerator HostWithB(FlockMultiplayerProvider multiplayer, List<FlockMultiplayerSession> held)
        {
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "the session was hosted");
            Assert.IsFalse(hosted.IsFaulted, "Precondition: hosted");
            _server.Joins(_server.ById(hosted.Result.Id), B);
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(hosted.Result.Id);
            yield return Done(read, "the session was read");
            CollectionAssert.AreEqual(new[] { A, B }, Ids(hosted.Result), "Precondition: B is seated");
            held.Add(hosted.Result);
        }

        // ---- getting a session ----

        [UnityTest]
        public IEnumerator HostSession_HoldsThePlayerAsHostAndOnlyPlayer_AndSendsOnlyWhatWasGiven()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "the session was hosted");

            FlockMultiplayerSession session = hosted.Result;
            Assert.IsTrue(session.IsHost);
            CollectionAssert.AreEqual(new[] { A }, Ids(session));
            Assert.AreEqual(8, session.MaxPlayers, "The game's default size");
            Assert.IsNull(session.Connection, "Nothing published yet");
            Assert.IsFalse(string.IsNullOrEmpty(session.JoinCode));
            Assert.AreEqual("{}", _server.RequestsTo(FakeSessionServer.Host)[0].JsonBody, "Nothing given, nothing sent");
            Assert.AreSame(session, multiplayer.Sessions.Held);
        }

        [UnityTest]
        public IEnumerator HostSession_SendsTheSizeAndData_AndTryGetDataReadsThem()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync(4, new Dictionary<string, object> { { "map", "dust" }, { "rounds", 3 } });
            yield return Done(hosted, "the session was hosted");

            JObject sent = JObject.Parse(_server.RequestsTo(FakeSessionServer.Host)[0].JsonBody);
            Assert.AreEqual(4, (int)sent["max_players"]);
            Assert.AreEqual(4, hosted.Result.MaxPlayers);
            Assert.IsTrue(hosted.Result.TryGetData("rounds", out int rounds) && rounds == 3);
            Assert.IsTrue(hosted.Result.TryGetData("map", out string map) && map == "dust");
            Assert.IsFalse(hosted.Result.TryGetData("map", out int _), "A word is not a number");
        }

        [UnityTest]
        public IEnumerator JoinSession_ByCode_ShowsEveryPlayerLongestSeatedFirst()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeSessionServer.Session onServer = _server.HostedBy(B);
            _server.Joins(onServer, C);

            Task<FlockMultiplayerSession> joined = multiplayer.JoinSessionAsync(" " + onServer.Code.ToLowerInvariant() + " ");
            yield return Done(joined, "the session was joined");

            CollectionAssert.AreEqual(new[] { B, C, A }, Ids(joined.Result));
            Assert.AreEqual(B, joined.Result.HostPlayerId);
            Assert.IsFalse(joined.Result.IsHost);
        }

        [UnityTest]
        public IEnumerator GetSession_ReturnsTheHeldObject_AndRefusesASessionNeverSeatedIn()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "the session was hosted");
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(hosted.Result.Id);
            yield return Done(read, "the session was read");
            Assert.AreSame(hosted.Result, read.Result);

            Task<FlockMultiplayerSession> stranger = multiplayer.GetSessionAsync(_server.HostedBy(C).Id);
            yield return Done(stranger, "a stranger's session was asked for");
            Assert.AreEqual(FlockErrorCode.MultiplayerSessionNotFound, Failure<FlockException>(stranger).ErrorCode);
        }

        [UnityTest]
        public IEnumerator GetSession_OfASessionThePlayerLeft_ComesBackEnded_AndIsNotHeld()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeSessionServer.Session onServer = _server.HostedBy(B);
            _server.Joins(onServer, A);
            _server.Leaves(onServer, A);

            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(onServer.Id);
            yield return Done(read, "the past session was read");
            Assert.AreEqual(FlockMultiplayerSessionEndReason.Left, read.Result.EndReason);
            Assert.IsNull(multiplayer.Sessions.Held);
        }

        // ---- one seat at a time ----

        [UnityTest]
        public IEnumerator HostingAnotherSession_EndsTheHeldOneAtOnce_AsMovedToAnotherSession()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> first = multiplayer.HostSessionAsync();
            yield return Done(first, "the first session was hosted");
            List<string> ended = new List<string>();
            first.Result.Ended += ended.Add;

            Task<FlockMultiplayerSession> second = multiplayer.HostSessionAsync();
            yield return Done(second, "the second session was hosted");
            CollectionAssert.AreEqual(new[] { FlockMultiplayerSessionEndReason.MovedToAnotherSession }, ended);
            Assert.AreSame(second.Result, multiplayer.Sessions.Held);
        }

        [UnityTest]
        public IEnumerator JoiningAnotherSession_EndsTheHeldOneAtOnce_AsMovedToAnotherSession()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> first = multiplayer.HostSessionAsync();
            yield return Done(first, "the first session was hosted");
            List<string> ended = new List<string>();
            first.Result.Ended += ended.Add;

            Task<FlockMultiplayerSession> second = multiplayer.JoinSessionAsync(_server.HostedBy(B).Code);
            yield return Done(second, "another session was joined");
            CollectionAssert.AreEqual(new[] { FlockMultiplayerSessionEndReason.MovedToAnotherSession }, ended);
        }

        // ---- the newest answer wins ----

        [UnityTest]
        public IEnumerator AReadSentBeforeThisGamesChangeFinished_IsReadAgain_AndChangesNothingBack()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            FlockMultiplayerSession session = held[0];
            int hostChanges = 0;
            session.HostChanged += () => hostChanges++;

            FakeSessionServer.Held readBeforeTheChange = _server.HoldNext(FakeSessionServer.Read);
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(session.Id);
            yield return FlockTestWait.Until(() => readBeforeTheChange.Arrived, "A read that sees A hosting is on its way");
            Task handOver = session.MakeHostAsync(B);
            yield return Done(handOver, "hosting was handed over");
            Assert.AreEqual(1, hostChanges);

            readBeforeTheChange.Release();
            yield return Done(read, "the old read landed");
            Assert.IsFalse(session.IsHost, "The read sent before the change did not put A back");
            Assert.AreEqual(1, hostChanges);
            Assert.AreEqual(2, _server.Count(FakeSessionServer.Read) - 1, "The overtaken read was read again");
        }

        [UnityTest]
        public IEnumerator AnOlderReadLandingAfterANewerOne_ChangesNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            FlockMultiplayerSession session = held[0];
            int playerChanges = 0;
            session.PlayersChanged += () => playerChanges++;

            FakeSessionServer.Held older = _server.HoldNext(FakeSessionServer.Read);
            Task<FlockMultiplayerSession> olderRead = multiplayer.GetSessionAsync(session.Id);
            yield return FlockTestWait.Until(() => older.Arrived, "A read that sees A and B is on its way");
            _server.Joins(_server.ById(session.Id), C);
            Task<FlockMultiplayerSession> newer = multiplayer.GetSessionAsync(session.Id);
            yield return Done(newer, "a newer read landed");
            CollectionAssert.AreEqual(new[] { A, B, C }, Ids(session));

            older.Release();
            yield return Done(olderRead, "the older read landed");
            CollectionAssert.AreEqual(new[] { A, B, C }, Ids(session), "The older read did not take C away");
            Assert.AreEqual(1, playerChanges);
        }

        // ---- events ----

        [UnityTest]
        public IEnumerator Events_SeeTheSessionAlreadyUpdated()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            FlockMultiplayerSession session = held[0];
            List<string> seen = new List<string>();
            session.PlayersChanged += () => seen.Add("players " + string.Join(",", Ids(session)));
            session.HostChanged += () => seen.Add("host " + session.HostPlayerId + " isHost=" + session.IsHost);
            session.ConnectionChanged += () => seen.Add("connection " + session.Connection?.Mode);
            IReadOnlyList<FlockMultiplayerSessionPlayer> before = session.Players;

            Task publish = session.PublishConnectionAsync("direct", new Dictionary<string, object> { { "port", 7777 } });
            yield return Done(publish, "the connection was published");
            _server.Joins(_server.ById(session.Id), C);
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(session.Id);
            yield return Done(read, "the session was read");
            Task handOver = session.MakeHostAsync(B);
            yield return Done(handOver, "hosting was handed over");

            CollectionAssert.AreEqual(new[] { "connection direct", "players " + A + "," + B + "," + C, "host " + B + " isHost=False" }, seen);
            CollectionAssert.AreEqual(new[] { A, B }, before.Select(player => player.PlayerId).ToArray(), "A list handed out earlier keeps its players");
        }

        [UnityTest]
        public IEnumerator HostChanged_FollowsTheHostEpoch_EvenWhenHostingCameBack()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            FlockMultiplayerSession session = held[0];
            int hostChanges = 0;
            session.HostChanged += () => hostChanges++;

            FakeSessionServer.Session onServer = _server.ById(session.Id);
            _server.MakesHost(onServer, B);
            _server.MakesHost(onServer, A);
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(session.Id);
            yield return Done(read, "the session was read");
            Assert.AreEqual(1, hostChanges, "Hosting moved twice; the same host, a new term");
            Assert.IsTrue(session.IsHost);
        }

        [UnityTest]
        public IEnumerator ConnectionChanged_FollowsTheConnectionEpoch_EvenWithTheSameValues()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "the session was hosted");
            FlockMultiplayerSession session = hosted.Result;
            int connectionChanges = 0;
            session.ConnectionChanged += () => connectionChanges++;

            Dictionary<string, object> values = new Dictionary<string, object> { { "address", "192.168.1.20" }, { "port", 7777 } };
            Task first = session.PublishConnectionAsync("direct", values);
            yield return Done(first, "published");
            Task again = session.PublishConnectionAsync("direct", values);
            yield return Done(again, "published again");
            Assert.AreEqual(2, connectionChanges, "Each publish is a new connection for the players to take up");
        }

        [UnityTest]
        public IEnumerator AHandlerThatThrows_IsLogged_AndTheOtherEventsStillRun()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            FlockMultiplayerSession session = held[0];
            int hostChanges = 0;
            session.PlayersChanged += () => throw new InvalidOperationException("the game's handler broke");
            session.HostChanged += () => hostChanges++;

            LogAssert.Expect(LogType.Error, new Regex("FlockMultiplayerSession.PlayersChanged subscriber threw"));
            _server.Leaves(_server.ById(session.Id), A);
            _server.Joins(_server.ById(session.Id), A);
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(session.Id);
            yield return Done(read, "the session was read");
            Assert.AreEqual(1, hostChanges, "HostChanged still ran");
        }

        // ---- the host's calls and join tokens ----

        [UnityTest]
        public IEnumerator PublishConnection_SendsTheModeWithItsValues_AndRefusesWhatIsNotAllowed()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "the session was hosted");
            FlockMultiplayerSession session = hosted.Result;

            Task publish = session.PublishConnectionAsync("direct", new Dictionary<string, object> { { "address", "192.168.1.20" }, { "port", 7777 } });
            yield return Done(publish, "published");
            JObject sent = JObject.Parse(_server.RequestsTo(FakeSessionServer.Publish)[0].JsonBody);
            Assert.AreEqual("direct", (string)sent["connection_info"]["mode"]);
            Assert.AreEqual(7777, (int)sent["connection_info"]["port"]);
            Assert.AreEqual("direct", session.Connection.Mode);
            Assert.IsTrue(session.Connection.TryGetValue("port", out int port) && port == 7777);

            int sentSoFar = _server.TotalRequests;
            Failure<FlockValidationException>(session.PublishConnectionAsync(""));
            Assert.AreEqual(sentSoFar, _server.TotalRequests, "An empty mode is refused before sending");
            Task tooLong = session.PublishConnectionAsync(new string('m', 33));
            yield return Done(tooLong, "a long mode was refused");
            Assert.AreEqual("request.validation_failed", Failure<FlockException>(tooLong).Code, "The server's rule, kept as the server's");
        }

        [UnityTest]
        public IEnumerator MakeHost_HandsHostingOver_AndTheServersRefusalsKeepTheirCodes()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            FlockMultiplayerSession session = held[0];
            int readsBefore = _server.Count(FakeSessionServer.Read);

            Task outsider = session.MakeHostAsync(C);
            yield return Done(outsider, "a player without a seat was named");
            Assert.AreEqual(FlockErrorCode.MultiplayerTargetNotAParticipant, Failure<FlockException>(outsider).ErrorCode);

            Task handOver = session.MakeHostAsync(B);
            yield return Done(handOver, "hosting was handed over");
            Assert.IsFalse(session.IsHost);

            Task again = session.MakeHostAsync(B);
            yield return Done(again, "a member tried to hand hosting over");
            Assert.AreEqual(FlockErrorCode.MultiplayerNotHost, Failure<FlockException>(again).ErrorCode);
            Task end = session.EndAsync();
            yield return Done(end, "a member tried to end the session");
            Assert.AreEqual(FlockErrorCode.MultiplayerNotHost, Failure<FlockException>(end).ErrorCode);
            Assert.IsFalse(session.HasEnded, "A refusal for not hosting does not end the seat");
            Assert.AreEqual(readsBefore, _server.Count(FakeSessionServer.Read), "Only a refusal saying the player is out reads the session to learn why");
        }

        [UnityTest]
        public IEnumerator JoinTokens_AMemberAsks_TheHostVerifies_AndAWrongOneIsRefused()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            FakeSessionServer.Session onServer = _server.HostedBy(A);
            Task<FlockMultiplayerSession> joined = multiplayer.JoinSessionAsync(onServer.Code);
            yield return Done(joined, "B joined");
            Task<FlockMultiplayerSessionJoinToken> asked = joined.Result.RequestJoinTokenAsync();
            yield return Done(asked, "B asked for a token");
            Assert.AreEqual(60, asked.Result.ExpiresInSeconds);
            Task<string> notHost = joined.Result.VerifyJoinTokenAsync(asked.Result.Token);
            yield return Done(notHost, "B tried to verify");
            Assert.AreEqual(FlockErrorCode.MultiplayerNotHost, Failure<FlockException>(notHost).ErrorCode);

            _h.LoginAs(A);
            Task<FlockMultiplayerSession> hostView = multiplayer.GetSessionAsync(onServer.Id);
            yield return Done(hostView, "A read its session");
            Task<string> verified = hostView.Result.VerifyJoinTokenAsync(asked.Result.Token);
            yield return Done(verified, "A verified B's token");
            Assert.AreEqual(B, verified.Result);
            Task<string> garbage = hostView.Result.VerifyJoinTokenAsync("garbage");
            yield return Done(garbage, "A verified garbage");
            Assert.AreEqual(FlockErrorCode.MultiplayerInvalidJoinToken, Failure<FlockException>(garbage).ErrorCode);
            Assert.IsFalse(hostView.Result.HasEnded, "A refused token does not end the seat");
        }

        // ---- every way a session ends ----

        [UnityTest]
        public IEnumerator Leaving_EndsAsLeftOnce_AndLaterCallsAreRefusedWithoutARequest()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeSessionServer.Session onServer = _server.HostedBy(B);
            Task<FlockMultiplayerSession> joined = multiplayer.JoinSessionAsync(onServer.Code);
            yield return Done(joined, "joined");
            List<string> ended = new List<string>();
            joined.Result.Ended += ended.Add;

            Task leave = joined.Result.LeaveAsync();
            yield return Done(leave, "left");
            CollectionAssert.AreEqual(new[] { FlockMultiplayerSessionEndReason.Left }, ended);
            Assert.IsNull(multiplayer.Sessions.Held);
            int sent = _server.TotalRequests;
            Failure<FlockValidationException>(joined.Result.RequestJoinTokenAsync());
            Failure<FlockValidationException>(joined.Result.LeaveAsync());
            Assert.AreEqual(sent, _server.TotalRequests);
        }

        [UnityTest]
        public IEnumerator Leaving_WhenTheServerSaysThePlayerIsAlreadyOut_StillEndsAsLeft()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeSessionServer.Session onServer = _server.HostedBy(B);
            Task<FlockMultiplayerSession> joined = multiplayer.JoinSessionAsync(onServer.Code);
            yield return Done(joined, "joined");
            _server.Drops(onServer, A);

            Task leave = joined.Result.LeaveAsync();
            yield return Done(leave, "the leave finished");
            Assert.IsFalse(leave.IsFaulted);
            Assert.AreEqual(FlockMultiplayerSessionEndReason.Left, joined.Result.EndReason);
        }

        [UnityTest]
        public IEnumerator TheHostLeavingAlone_EndsAsLeft_NotAsHostLeft()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "hosted");

            Task leave = hosted.Result.LeaveAsync();
            yield return Done(leave, "left");
            Assert.AreEqual("host_left", _server.ById(hosted.Result.Id).EndedReason, "Precondition: the server closed it as host_left");
            Assert.AreEqual(FlockMultiplayerSessionEndReason.Left, hosted.Result.EndReason, "The game left it");
        }

        [UnityTest]
        public IEnumerator Ending_EndsAsEndedByHost()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "hosted");
            List<string> ended = new List<string>();
            hosted.Result.Ended += ended.Add;

            Task end = hosted.Result.EndAsync();
            yield return Done(end, "ended");
            CollectionAssert.AreEqual(new[] { FlockMultiplayerSessionEndReason.EndedByHost }, ended);
        }

        [UnityTest]
        public IEnumerator ACallThatFindsTheSessionOver_ReadsWhy_AndEndsWithTheServersReason()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeSessionServer.Session onServer = _server.HostedBy(B);
            Task<FlockMultiplayerSession> joined = multiplayer.JoinSessionAsync(onServer.Code);
            yield return Done(joined, "joined");
            _server.Ends(onServer, "expired");
            int readsBefore = _server.Count(FakeSessionServer.Read);

            Task<FlockMultiplayerSessionJoinToken> asked = joined.Result.RequestJoinTokenAsync();
            yield return Done(asked, "the call found the session over");
            Assert.AreEqual(FlockErrorCode.MultiplayerSessionNotFound, Failure<FlockException>(asked).ErrorCode);
            Assert.AreEqual(FlockMultiplayerSessionEndReason.Expired, joined.Result.EndReason);
            Assert.AreEqual(readsBefore + 1, _server.Count(FakeSessionServer.Read), "Read once to learn why");
        }

        [UnityTest]
        public IEnumerator ACallThatFindsThePlayerDropped_EndsAsDropped()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeSessionServer.Session onServer = _server.HostedBy(B);
            Task<FlockMultiplayerSession> joined = multiplayer.JoinSessionAsync(onServer.Code);
            yield return Done(joined, "joined");
            _server.Drops(onServer, A);

            Task<FlockMultiplayerSessionJoinToken> asked = joined.Result.RequestJoinTokenAsync();
            yield return Done(asked, "the call found the seat gone");
            Assert.AreEqual(FlockMultiplayerSessionEndReason.Dropped, joined.Result.EndReason);
        }

        [UnityTest]
        public IEnumerator ACallWhoseSessionCannotBeReadAgain_EndsAsDropped()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "hosted");
            _server.AnswerNext(FakeSessionServer.JoinToken, FakeSessionServer.Refused(404, "multiplayer.session_not_found"));
            _server.AnswerNext(FakeSessionServer.Read, FakeSessionServer.Refused(404, "multiplayer.session_not_found"));

            Task<FlockMultiplayerSessionJoinToken> asked = hosted.Result.RequestJoinTokenAsync();
            yield return Done(asked, "the call found the session gone");
            Assert.AreEqual(FlockMultiplayerSessionEndReason.Dropped, hosted.Result.EndReason);
        }

        // ---- sign-in ----

        [UnityTest]
        public IEnumerator SignOut_EndsTheSessionAtOnce_RaisesEndedOnTheNextFrame_AndRefusesItsCalls()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "hosted");
            List<string> ended = new List<string>();
            hosted.Result.Ended += ended.Add;

            _h.Client.Authentication.Logout();
            Assert.AreEqual(FlockMultiplayerSessionEndReason.SignedOut, hosted.Result.EndReason, "Ended in the same frame as the sign-out");
            int sent = _server.TotalRequests;
            Failure<FlockValidationException>(hosted.Result.EndAsync());
            Assert.AreEqual(sent, _server.TotalRequests);
            yield return FlockTestWait.Until(() => ended.Count == 1, "Ended was raised");
            yield return RealSeconds(0.2f);
            CollectionAssert.AreEqual(new[] { FlockMultiplayerSessionEndReason.SignedOut }, ended);
        }

        [UnityTest]
        public IEnumerator APlayerSwitch_ThenGettingTheSession_EndsTheOldPlayersSessionAsSignedOut()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            List<string> ended = new List<string>();
            held[0].Ended += ended.Add;

            _h.LoginAs(B);
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(held[0].Id);
            yield return Done(read, "B read the session");
            Assert.AreNotSame(held[0], read.Result, "B gets a session object of its own");
            Assert.IsFalse(read.Result.HasEnded);
            CollectionAssert.AreEqual(new[] { FlockMultiplayerSessionEndReason.SignedOut }, ended);
        }

        [UnityTest]
        public IEnumerator ALeaveRefusedAfterTheSignInEnded_EndsTheSessionAsSignedOut()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            List<string> ended = new List<string>();
            held[0].Ended += ended.Add;
            _server.Leaves(_server.ById(held[0].Id), A);
            FakeSessionServer.Held answer = _server.HoldNext(FakeSessionServer.Leave);
            Task leave = held[0].LeaveAsync();
            yield return FlockTestWait.Until(() => answer.Arrived, "A's leave is on its way, to be refused as already out");

            // B signs in, and the refusal lands before the frame check runs.
            _h.LoginAs(B);
            answer.Release();
            yield return Done(leave, "A's leave landed");
            yield return null;

            CollectionAssert.AreEqual(new[] { FlockMultiplayerSessionEndReason.SignedOut }, ended, "Ended once, with the reason it already read as");
            Assert.AreEqual(FlockMultiplayerSessionEndReason.SignedOut, held[0].EndReason);
        }

        [UnityTest]
        public IEnumerator TwoPublishesAnsweredOutOfOrder_KeepTheNewerConnection()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            FakeSessionServer.Held first = _server.HoldNext(FakeSessionServer.Publish);
            Task relay = held[0].PublishConnectionAsync("relay");
            yield return FlockTestWait.Until(() => first.Arrived, "The first publish is on its way");
            Task direct = held[0].PublishConnectionAsync("direct");
            yield return Done(direct, "the second publish landed first");
            Assert.AreEqual("direct", held[0].Connection.Mode, "Precondition: the second publish was taken");
            int changes = 0;
            held[0].ConnectionChanged += () => changes++;

            first.Release();
            yield return Done(relay, "the first publish landed last");
            Assert.IsFalse(relay.IsFaulted);
            Assert.AreEqual("direct", held[0].Connection.Mode, "An older answer changes nothing");
            Assert.AreEqual(0, changes);
        }

        [UnityTest]
        public IEnumerator AChangeAnswerOlderThanAReadAlreadyTaken_ChangesNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            FakeSessionServer.Held handOver = _server.HoldNext(FakeSessionServer.MakeHost);
            Task makeB = held[0].MakeHostAsync(B);
            yield return FlockTestWait.Until(() => handOver.Arrived, "A's hand-over to B is on its way, its answer showing B hosting");
            _server.MakesHost(_server.ById(held[0].Id), A);
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(held[0].Id);
            yield return Done(read, "the session was read");
            Assert.IsTrue(held[0].IsHost, "Precondition: the read shows B handed hosting back");
            int changes = 0;
            held[0].HostChanged += () => changes++;

            handOver.Release();
            yield return Done(makeB, "the hand-over's answer landed last");
            Assert.IsFalse(makeB.IsFaulted);
            Assert.IsTrue(held[0].IsHost, "An answer older than the read changes nothing");
            Assert.AreEqual(0, changes);
        }

        [UnityTest]
        public IEnumerator ALateChangeAnswerFromTheLastSignIn_DoesNotSendTheNewPlayersReadAgain()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            FakeSessionServer.Held publish = _server.HoldNext(FakeSessionServer.Publish);
            Task published = held[0].PublishConnectionAsync("direct");
            yield return FlockTestWait.Until(() => publish.Arrived, "A's publish is on its way");

            _h.LoginAs(B);
            int readsBefore = _server.Count(FakeSessionServer.Read);
            FakeSessionServer.Held read = _server.HoldNext(FakeSessionServer.Read);
            Task<FlockMultiplayerSession> seenByB = multiplayer.GetSessionAsync(held[0].Id);
            yield return FlockTestWait.Until(() => read.Arrived, "B's read is on its way");
            publish.Release();
            yield return Done(published, "A's publish landed");
            read.Release();
            yield return Done(seenByB, "B read the session");

            Assert.IsFalse(seenByB.IsFaulted, "B's read was taken");
            Assert.AreEqual(1, _server.Count(FakeSessionServer.Read) - readsBefore, "A's answer did not send B's read again");
        }

        [UnityTest]
        public IEnumerator ALateChangeAnswerFromTheLastSignIn_LeavesTheNewPlayersSessionAlone()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            FakeSessionServer.Held answer = _server.HoldNext(FakeSessionServer.Publish);
            Task publish = held[0].PublishConnectionAsync("direct");
            yield return FlockTestWait.Until(() => answer.Arrived, "A's publish is on its way, its answer showing A and B");

            _h.LoginAs(B);
            _server.Joins(_server.ById(held[0].Id), C);
            Task<FlockMultiplayerSession> seenByB = multiplayer.GetSessionAsync(held[0].Id);
            yield return Done(seenByB, "B read the session");
            int changes = 0;
            seenByB.Result.PlayersChanged += () => changes++;

            answer.Release();
            yield return Done(publish, "A's publish landed");
            CollectionAssert.AreEqual(new[] { A, B, C }, Ids(seenByB.Result), "An answer A asked for changes nothing for B");
            Assert.AreEqual(0, changes);
        }

        [UnityTest]
        public IEnumerator HostSession_WhoseSignInEndedOnTheWay_IsCancelled_AndHoldsNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeSessionServer.Held answer = _server.HoldNext(FakeSessionServer.Host);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return FlockTestWait.Until(() => answer.Arrived, "The host request is on its way");
            _h.LoginAs(B);
            answer.Release();
            yield return Done(hosted, "the answer landed");
            Assert.IsTrue(hosted.IsCanceled);
            Assert.IsNull(multiplayer.Sessions.Held);
        }

        [UnityTest]
        public IEnumerator JoinSession_WhoseSignInEndedOnTheWay_IsCancelled_AndHoldsNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeSessionServer.Session onServer = _server.HostedBy(B);
            FakeSessionServer.Held answer = _server.HoldNext(FakeSessionServer.Join);
            Task<FlockMultiplayerSession> joined = multiplayer.JoinSessionAsync(onServer.Code);
            yield return FlockTestWait.Until(() => answer.Arrived, "The join is on its way");
            _h.LoginAs(C);
            answer.Release();
            yield return Done(joined, "the answer landed");
            Assert.IsTrue(joined.IsCanceled);
            Assert.IsNull(multiplayer.Sessions.Held);
        }

        [UnityTest]
        public IEnumerator GetSession_WhoseSignInEndedOnTheWay_IsCancelled_AndHoldsNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeSessionServer.Session onServer = _server.HostedBy(A);
            FakeSessionServer.Held answer = _server.HoldNext(FakeSessionServer.Read);
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(onServer.Id);
            yield return FlockTestWait.Until(() => answer.Arrived, "The read is on its way");
            _h.LoginAs(B);
            answer.Release();
            yield return Done(read, "the answer landed");
            Assert.IsTrue(read.IsCanceled);
            Assert.IsNull(multiplayer.Sessions.Held);
        }

        [UnityTest]
        public IEnumerator ARetryAfterAPlayerSwitch_IsNotSentAsTheNewPlayer()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, retries: 1);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return HostWithB(multiplayer, held);
            _server.AnswerNext(FakeSessionServer.MakeHost, FakeSessionServer.Refused(429, "request.rate_limited"));
            FakeSessionServer.Held firstTry = _server.HoldNext(FakeSessionServer.MakeHost);
            Task handOver = held[0].MakeHostAsync(B);
            yield return FlockTestWait.Until(() => firstTry.Arrived, "The first try is on its way");
            _h.LoginAs(C);
            firstTry.Release();
            yield return Done(handOver, "the call gave up");
            Assert.IsTrue(handOver.IsCanceled);
            Assert.AreEqual(1, _server.Count(FakeSessionServer.MakeHost), "The retry did not go out as the new player");
        }

        [UnityTest]
        public IEnumerator SignedOut_AndEmptyInputs_AreRefusedWithoutARequest()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "hosted");
            int sent = _server.TotalRequests;
            Failure<FlockValidationException>(multiplayer.JoinSessionAsync(""));
            Failure<FlockValidationException>(multiplayer.GetSessionAsync(null));
            Failure<FlockValidationException>(hosted.Result.MakeHostAsync(""));
            Failure<FlockValidationException>(hosted.Result.VerifyJoinTokenAsync(""));
            Assert.AreEqual(sent, _server.TotalRequests);

            _h.Client.Authentication.Logout();
            Failure<FlockAuthException>(multiplayer.HostSessionAsync());
            Failure<FlockAuthException>(multiplayer.JoinSessionAsync("ABC123"));
            Failure<FlockAuthException>(multiplayer.GetSessionAsync("01SESSION"));
            Assert.AreEqual(sent, _server.TotalRequests);
        }

        [UnityTest]
        public IEnumerator AnAnswerThatNamesNoSession_IsAFailure_AndHoldsNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _server.AnswerNext(FakeSessionServer.Host, FlockFakeTransport.Ok("{\"result\":null}"));
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "the host call finished");
            Failure<FlockNetworkException>(hosted);
            Assert.IsNull(multiplayer.Sessions.Held);
        }

        // ---- Flock going away, and quitting ----

        [UnityTest]
        public IEnumerator FlockShuttingDown_EndsTheSessionWithoutRaisingEnded()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "hosted");
            int ended = 0;
            hosted.Result.Ended += _ => ended++;

            FlockClient.Shutdown();
            yield return RealSeconds(0.2f);
            Assert.IsTrue(hosted.Result.HasEnded);
            Assert.AreEqual(0, ended);
        }

        [UnityTest]
        public IEnumerator TheNoDomainReloadReset_EndsTheSession_AndAnAnswerLandingAfterItHoldsNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "hosted");
            FakeSessionServer.Session other = _server.HostedBy(C);
            _server.Joins(other, A);
            FakeSessionServer.Held answer = _server.HoldNext(FakeSessionServer.Read);
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(other.Id);
            yield return FlockTestWait.Until(() => answer.Arrived, "A read is on its way");

            FlockClient client = _h.Client;
            MethodInfo reset = typeof(FlockClient).GetMethod("ResetStaticState", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(reset, "FlockClient's reset is where the test expects it");
            try
            {
                // The reset keeps the sign-in, as entering Play Mode does; only the owner's own stop can tell.
                reset.Invoke(null, null);
                Assert.IsTrue(hosted.Result.HasEnded);
                answer.Release();
                yield return Done(read, "the read landed");
                Assert.IsTrue(read.IsCanceled, "An answer for a Flock that has gone holds nothing");
                Assert.IsNull(multiplayer.Sessions.Held);
            }
            finally
            {
                client.Commands?.UnsubscribeFlushTriggers();
                (client.Analytics as FlockAnalyticsProvider)?.StopForShutdown();
                FlockEvents.ClearAll();
            }
        }

        [UnityTest]
        public IEnumerator Quitting_GivesTheSeatUpWithoutWaiting_AndRaisesNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeSessionServer.Session onServer = _server.HostedBy(B);
            Task<FlockMultiplayerSession> joined = multiplayer.JoinSessionAsync(onServer.Code);
            yield return Done(joined, "joined");
            int ended = 0;
            joined.Result.Ended += _ => ended++;

            FieldInfo field = typeof(FlockBehaviour).GetField("OnQuit", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "FlockBehaviour's quit event is where the test expects it");
            Action quit = (Action)field.GetValue(FlockBehaviour.Instance);
            Delegate[] handlers = quit == null ? new Delegate[0] : quit.GetInvocationList().Where(handler => handler.Target == multiplayer).ToArray();
            Assert.AreEqual(1, handlers.Length, "The provider listens for quitting");
            handlers[0].DynamicInvoke();

            Assert.AreEqual(FlockMultiplayerSessionEndReason.Left, joined.Result.EndReason);
            yield return FlockTestWait.Until(() => _server.Count(FakeSessionServer.Leave) == 1, "The leave went out");
            Assert.IsFalse(onServer.Seated.Contains(A), "The seat is given up on the server");
            Assert.AreEqual(0, ended, "Nothing is raised while the game quits");
        }
    }
}
