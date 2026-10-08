using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The player's search for a match on real frames, against matchmaking, party and session servers in memory: one search per
    // sign-in, its ticket checked until it leaves the queue, the match's session handed over held, every way a search ends, the
    // game's own cancel leaving nothing behind, and a party member's game following the leader's search.
    public class FlockMatchmakingTests
    {
        private const string A = "player-a";
        private const string B = "player-b";
        private const string C = "player-c";

        private FlockTestClient _h;
        private FakeMatchmakingServer _server;

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

        private FlockMultiplayerProvider SignedInAs(string player, double partyRefreshSeconds = 0, double checkSeconds = 0.2, int retries = 0, int retryWaitMilliseconds = 50)
        {
            _server = new FakeMatchmakingServer();
            _h = FlockTestClient.Create(new FlockFakeTransport(), config =>
            {
                config.PartyRefreshInterval = TimeSpan.FromSeconds(partyRefreshSeconds);
                if (retries > 0)
                    config.RetryPolicy = new RetryPolicy { MaxRetries = retries, InitialDelay = TimeSpan.FromMilliseconds(retryWaitMilliseconds), UseJitter = false };
            });
            FlockHttpClient.Configure(_server);
            _h.LoginAs(player);
            FlockMultiplayerProvider multiplayer = _h.Client.Multiplayer;
            if (checkSeconds > 0)
                multiplayer.Matchmaking.SetCheckIntervalForTesting(TimeSpan.FromSeconds(checkSeconds));
            return multiplayer;
        }

        private static IEnumerator Done(Task task, string what) => FlockTestWait.Until(() => task.IsCompleted, what);

        private static IEnumerator RealSeconds(float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        private static T Failure<T>(Task task) where T : Exception
        {
            Assert.IsTrue(task.IsFaulted, "The call failed");
            T failure = task.Exception.InnerException as T;
            Assert.IsNotNull(failure, $"The call failed with {typeof(T).Name}, not {task.Exception.InnerException?.GetType().Name}");
            return failure;
        }

        private FakeMatchmakingServer.Ticket TicketOf(string player) => _server.TicketsOf(player).LastOrDefault();

        // ---- a search alone ----

        [UnityTest]
        public IEnumerator FindMatch_Alone_IsCheckedEveryFewSeconds_AndHandsBackTheMatchedSessionHeld()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, checkSeconds: 0);
            _server.QueuedBy(FakeMatchmakingServer.DuelId, B);
            DateTime started = DateTime.UtcNow;
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Duel);
            List<DateTime> checks = new List<DateTime>();
            while (checks.Count < 2 && DateTime.UtcNow < started.AddSeconds(10))
            {
                if (_server.Count(FakeMatchmakingServer.ReadTicket) > checks.Count)
                    checks.Add(DateTime.UtcNow);
                yield return null;
            }
            Assert.AreEqual(2, checks.Count, "Checked twice");
            double first = (checks[0] - started).TotalSeconds;
            double between = (checks[1] - checks[0]).TotalSeconds;
            Assert.That(first, Is.InRange(2.0, 4.0), "The first check, the interval from the start");
            Assert.That(between, Is.InRange(2.1, 3.9), "A check every 2.25 to 3.75 s");

            _server.FormMatches();
            yield return Done(found, "matched");
            FlockMatchmakingResult result = found.Result;
            Assert.IsTrue(result.IsMatched);
            Assert.AreEqual(FlockMatchmakingOutcome.Matched, result.Outcome);
            CollectionAssert.AreEqual(new[] { B, A }, result.PlayerIds, "The longest waiting first");
            Assert.IsNotNull(result.Session);
            Assert.AreEqual(B, result.Session.HostPlayerId, "The longest waiting hosts");
            Assert.AreSame(result.Session, multiplayer.Sessions.Held, "Held like any other session");
            Task<FlockMultiplayerSession> mine = multiplayer.GetMySessionAsync();
            yield return Done(mine, "my session");
            Assert.AreSame(result.Session, mine.Result);
        }

        [UnityTest]
        public IEnumerator FindMatch_SendsTheQueueItsNameNames_ItsAttributes_AndNoParty()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            CancellationTokenSource giveUp = new CancellationTokenSource();
            FlockMatchmakingOptions options = new FlockMatchmakingOptions { Attributes = new Dictionary<string, object> { { "mode", "ranked" } } };
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio, options, giveUp.Token);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 1, "The search was made");
            JObject body = JObject.Parse(_server.RequestsTo(FakeMatchmakingServer.Create)[0].JsonBody);
            Assert.AreEqual(FakeMatchmakingServer.TrioId, (string)body["queue_id"]);
            Assert.AreEqual("ranked", (string)body["attributes"]?["mode"]);
            Assert.IsFalse(body.ContainsKey("party_id"), "A search alone names no party");
            giveUp.Cancel();
            yield return Done(found, "cancelled");

            CancellationTokenSource giveUpAgain = new CancellationTokenSource();
            Task<FlockMatchmakingResult> plain = multiplayer.FindMatchAsync(FakeMatchmakingServer.Duel, null, giveUpAgain.Token);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 2, "The second search was made");
            Assert.IsFalse(JObject.Parse(_server.RequestsTo(FakeMatchmakingServer.Create)[1].JsonBody).ContainsKey("attributes"), "No attributes given, none sent");
            giveUpAgain.Cancel();
            yield return Done(plain, "cancelled");
        }

        [UnityTest]
        public IEnumerator ASearchNobodyFills_EndsAsExpired_NotAFailure()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 1, "The search was made");
            _server.ExpiresAll();
            yield return Done(found, "expired");
            Assert.IsFalse(found.IsFaulted || found.IsCanceled, "Not a failure");
            Assert.AreEqual(FlockMatchmakingOutcome.Expired, found.Result.Outcome);
            Assert.IsFalse(found.Result.IsMatched);
            Assert.IsNull(found.Result.Session);
            CollectionAssert.IsEmpty(found.Result.PlayerIds);
        }

        [UnityTest]
        public IEnumerator ASearchStoppedByAnotherGame_EndsAsCancelled()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 1, "The search was made");
            _server.CancelledElsewhere(TicketOf(A));
            yield return Done(found, "stopped");
            Assert.AreEqual(FlockMatchmakingOutcome.Cancelled, found.Result.Outcome);
            Assert.AreEqual(0, _server.Count(FakeMatchmakingServer.Cancel), "This game sent no cancel");
        }

        [UnityTest]
        public IEnumerator CancellingTheToken_CancelsTheSearchOnTheServer_ThenThrows_AndChecksNoMore()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio, null, giveUp.Token);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.ReadTicket) >= 1, "Checked once");
            giveUp.Cancel();
            yield return Done(found, "cancelled");
            Assert.IsTrue(found.IsCanceled, "Heard as a cancellation");
            Assert.AreEqual(1, _server.Count(FakeMatchmakingServer.Cancel));
            Assert.AreEqual("cancelled", TicketOf(A).Status, "Nothing left searching");
            int checks = _server.Count(FakeMatchmakingServer.ReadTicket);
            yield return RealSeconds(1f);
            Assert.AreEqual(checks, _server.Count(FakeMatchmakingServer.ReadTicket), "No check after the cancel");
        }

        [UnityTest]
        public IEnumerator AMatchLandingWhileTheCancelIsOnItsWay_GivesTheSeatUp_ThenTheCancelIsReported()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, checkSeconds: 30);
            _server.QueuedBy(FakeMatchmakingServer.DuelId, B);
            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Duel, null, giveUp.Token);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 1, "The search was made");
            FakeMatchmakingServer.Match match = _server.FormMatches().Single();
            giveUp.Cancel();
            yield return Done(found, "cancelled");
            Assert.IsTrue(found.IsCanceled, "The cancel is reported");
            FakeSessionServer.Session session = _server.Sessions.ById(match.SessionId);
            yield return FlockTestWait.Until(() => !session.Seated.Contains(A), "The seat the match gave was let go");
            Assert.IsTrue(session.Seated.Contains(B), "The other player keeps theirs");
        }

        [UnityTest]
        public IEnumerator CancellingWhileTheTicketIsOnItsWay_CancelsItOnceItLands()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeMatchmakingServer.Held making = _server.HoldNext(FakeMatchmakingServer.Create);
            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio, null, giveUp.Token);
            yield return FlockTestWait.Until(() => making.Arrived, "The search is on its way");
            giveUp.Cancel();
            yield return Done(found, "given up at once");
            Assert.IsTrue(found.IsCanceled);
            Assert.AreEqual(0, _server.Count(FakeMatchmakingServer.Cancel), "Nothing to cancel yet");

            making.Release();
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Cancel) == 1, "Cancelled once it landed");
            Assert.AreEqual("cancelled", TicketOf(A).Status);
            yield return RealSeconds(0.5f);
            Assert.AreEqual(0, _server.Count(FakeMatchmakingServer.ReadTicket), "Never checked");
        }

        [UnityTest]
        public IEnumerator ASearchGivenUpOnItsWay_ThatMatchedMeanwhile_GivesTheSeatUp()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _server.QueuedBy(FakeMatchmakingServer.DuelId, B);
            FakeMatchmakingServer.Held making = _server.HoldNext(FakeMatchmakingServer.Create);
            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Duel, null, giveUp.Token);
            yield return FlockTestWait.Until(() => making.Arrived, "The search is on its way");
            giveUp.Cancel();
            yield return Done(found, "given up at once");
            FakeMatchmakingServer.Match match = _server.FormMatches().Single();
            making.Release();
            FakeSessionServer.Session session = _server.Sessions.ById(match.SessionId);
            yield return FlockTestWait.Until(() => !session.Seated.Contains(A), "The seat the match gave was let go");
            Assert.IsTrue(found.IsCanceled);
        }

        [UnityTest]
        public IEnumerator ASearchGivenUpOnItsWay_ThatLandsAfterTheGamesNextSearch_NeverCancelsTheNextOne()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            // The given-up search reaches the server after the next one, so the server refuses it as already queued.
            _server.AnswerNext(FakeMatchmakingServer.Create, FakeMatchmakingServer.Refused(409, "matchmaking.already_queued"));
            FakeMatchmakingServer.Held givenUpMaking = _server.HoldNext(FakeMatchmakingServer.Create);
            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task<FlockMatchmakingResult> first = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio, null, giveUp.Token);
            yield return FlockTestWait.Until(() => givenUpMaking.Arrived, "The first search is on its way");
            giveUp.Cancel();
            yield return Done(first, "given up at once");

            Task<FlockMatchmakingResult> next = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.ReadTicket) >= 1, "The next search is checked");
            givenUpMaking.Release();
            yield return RealSeconds(0.5f);
            Assert.AreEqual(0, _server.Count(FakeMatchmakingServer.Cancel), "Nothing of the game's next search is cancelled");
            Assert.AreEqual("queued", TicketOf(A).Status);
            Assert.IsFalse(next.IsCompleted, "The next search still runs");

            _server.QueuedBy(FakeMatchmakingServer.TrioId, B);
            _server.QueuedBy(FakeMatchmakingServer.TrioId, C);
            _server.FormMatches();
            yield return Done(next, "matched");
            Assert.IsTrue(next.Result.IsMatched);
        }

        [UnityTest]
        public IEnumerator ASearchOfThePlayersOwnFromBefore_IsCancelledAndThisOneTakesItsPlace()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeMatchmakingServer.Ticket earlier = _server.QueuedBy(FakeMatchmakingServer.TrioId, A).Single();
            _server.QueuedBy(FakeMatchmakingServer.DuelId, B);
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Duel);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 2, "Made again after the refusal");
            Assert.AreEqual("cancelled", earlier.Status, "The earlier search was cancelled");
            Assert.AreEqual(FakeMatchmakingServer.DuelId, _server.QueuedTicketOf(A)?.QueueId, "This search took its place");
            _server.FormMatches();
            yield return Done(found, "matched");
            Assert.IsTrue(found.Result.IsMatched);
        }

        [UnityTest]
        public IEnumerator ASearchAlone_WhileThePartysSearchRuns_IsRefusedOnItsCode_AndLeavesThePartysSearchAlone()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            _server.Parties.Joins(party, A);
            _server.QueuedBy(FakeMatchmakingServer.TrioId, B, party);
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Duel);
            yield return Done(found, "refused");
            Assert.AreEqual(FlockErrorCode.MatchmakingAlreadyQueued, Failure<FlockException>(found).ErrorCode);
            Assert.AreEqual("queued", _server.QueuedTicketOf(A)?.Status, "The party's search goes on");
            Assert.AreEqual(0, _server.Count(FakeMatchmakingServer.Cancel), "Nothing cancelled");
        }

        [UnityTest]
        public IEnumerator ASecondSearchWhileOneRuns_IsRefusedWithoutARequest_AndARefusedOneLeavesNoneRunning()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task<FlockMatchmakingResult> first = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio, null, giveUp.Token);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 1, "The search was made");
            Task<FlockMatchmakingResult> second = multiplayer.FindMatchAsync(FakeMatchmakingServer.Duel);
            yield return Done(second, "refused");
            Failure<FlockValidationException>(second);
            Assert.AreEqual(1, _server.Count(FakeMatchmakingServer.Create), "No second search went out");
            giveUp.Cancel();
            yield return Done(first, "cancelled");

            Task<FlockMatchmakingResult> unknown = multiplayer.FindMatchAsync("no-such-queue");
            yield return Done(unknown, "refused");
            Assert.AreEqual(FlockErrorCode.MatchmakingQueueNotFound, Failure<FlockException>(unknown).ErrorCode);
            Task<FlockMatchmakingResult> after = multiplayer.FindMatchAsync(FakeMatchmakingServer.Duel);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 2, "A refused search left none running");
            Assert.IsFalse(after.IsFaulted);
        }

        [UnityTest]
        public IEnumerator SignedOut_IsRefusedWithoutARequest_AndASignInThatEndsMidSearch_EndsIt_SendingNothingMore()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _h.Client.Authentication.Logout();
            Task<FlockMatchmakingResult> signedOut = multiplayer.FindMatchAsync(FakeMatchmakingServer.Duel);
            yield return Done(signedOut, "refused");
            Failure<FlockAuthException>(signedOut);
            Assert.AreEqual(0, _server.Count(FakeMatchmakingServer.Create));

            _h.LoginAs(A);
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.ReadTicket) >= 1, "Checked once");
            _h.LoginAs(C);
            yield return Done(found, "ended with the sign-in");
            Assert.IsTrue(found.IsCanceled);
            int checks = _server.Count(FakeMatchmakingServer.ReadTicket);
            yield return RealSeconds(1f);
            Assert.AreEqual(checks, _server.Count(FakeMatchmakingServer.ReadTicket), "No check as the next player");
            Assert.AreEqual(0, _server.Count(FakeMatchmakingServer.Cancel), "Nothing sent as the next player");
        }

        [UnityTest]
        public IEnumerator AMatchWithoutASession_IsMatched_NamesThePlayers_AndWarnsOnce()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _server.MakesSessions = false;
            _server.QueuedBy(FakeMatchmakingServer.DuelId, B);
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Duel);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 1, "The search was made");
            _server.FormMatches();
            yield return Done(found, "matched");
            Assert.IsTrue(found.Result.IsMatched);
            Assert.IsNull(found.Result.Session);
            CollectionAssert.AreEqual(new[] { B, A }, found.Result.PlayerIds);
            Assert.AreEqual(1, _h.Logger.Warnings.Count(line => line.Contains("made no session")), "One warning names the setting");
        }

        [UnityTest]
        public IEnumerator Quitting_CancelsTheSearch_WithoutWaiting()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 1, "The search was made");

            FieldInfo field = typeof(FlockBehaviour).GetField("OnQuit", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "FlockBehaviour's quit event is where the test expects it");
            Action quit = (Action)field.GetValue(FlockBehaviour.Instance);
            Delegate[] handlers = quit == null ? new Delegate[0] : quit.GetInvocationList().Where(handler => handler.Target == multiplayer).ToArray();
            Assert.AreEqual(1, handlers.Length, "The provider listens for quitting");
            handlers[0].DynamicInvoke();

            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Cancel) == 1, "The cancel went out");
            Assert.AreEqual("cancelled", TicketOf(A).Status, "Nobody who has gone is matched");
        }

        [UnityTest]
        public IEnumerator ARetriedCheckAfterAPlayerSwitch_IsNotSentAsTheNewPlayer()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, retries: 1, retryWaitMilliseconds: 1000);
            _server.AnswerNext(FakeMatchmakingServer.ReadTicket, FakeMatchmakingServer.Refused(429, "request.rate_limited"));
            FakeMatchmakingServer.Held firstTry = _server.HoldNext(FakeMatchmakingServer.ReadTicket);
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio);
            yield return FlockTestWait.Until(() => firstTry.Arrived, "The first check is on its way");
            firstTry.Release();
            yield return RealSeconds(0.2f);

            // The switch and a long frame (a scene load) after it: the retry's wait ends before a frame could end the search.
            _h.LoginAs(C);
            Thread.Sleep(1200);
            yield return RealSeconds(0.5f);
            Assert.AreEqual(1, _server.Count(FakeMatchmakingServer.ReadTicket), "The retry did not go out as the new player");
            Assert.IsTrue(found.IsCanceled, "The search ended with its sign-in");
        }

        [UnityTest]
        public IEnumerator FlockShuttingDown_EndsTheSearchWithoutAWord()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 1, "The search was made");
            FlockClient.Shutdown();
            yield return Done(found, "ended");
            Assert.IsTrue(found.IsCanceled);
            Assert.AreEqual(0, _server.Count(FakeMatchmakingServer.Cancel), "Nothing sent while Flock shuts down");
        }

        // ---- a party ----

        [UnityTest]
        public IEnumerator TheLeader_QueuesTheParty_ChecksItsOwnTicket_AndHearsTheSearchStartAndEnd()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            _server.Parties.Joins(party, A);
            _server.Parties.Leads(party, A);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            List<string> heard = new List<string>();
            held.SearchStarted += () => heard.Add("started");
            held.SearchEnded += result => heard.Add("ended " + result.Outcome);
            string heardWhenTheCallReturned = null;
            // Awaited as a game does, so the await can resume inside the result being handed over.
            async Task<FlockMatchmakingResult> FindAndNoteWhatWasHeard()
            {
                FlockMatchmakingResult result = await held.FindMatchAsync(FakeMatchmakingServer.Trio);
                heardWhenTheCallReturned = string.Join(",", heard);
                return result;
            }

            Task<FlockMatchmakingResult> found = FindAndNoteWhatWasHeard();
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.ReadTicket) >= 1, "Checked once");
            Assert.AreEqual(party.Id, (string)JObject.Parse(_server.RequestsTo(FakeMatchmakingServer.Create)[0].JsonBody)["party_id"]);
            CollectionAssert.AreEqual(new[] { "started" }, heard);
            Assert.IsTrue(held.IsSearching);
            string own = _server.QueuedTicketOf(A).Id;
            Assert.IsTrue(_server.RequestsTo(FakeMatchmakingServer.ReadTicket).All(request => request.Url.EndsWith(own, StringComparison.Ordinal)),
                "The leader checks its own ticket, not the first the answer lists");

            _server.QueuedBy(FakeMatchmakingServer.TrioId, C);
            _server.FormMatches();
            yield return Done(found, "matched");
            CollectionAssert.AreEqual(new[] { B, A, C }, found.Result.PlayerIds, "The party first, never split");
            Assert.AreSame(found.Result.Session, multiplayer.Sessions.Held);
            CollectionAssert.AreEqual(new[] { "started", "ended matched" }, heard);
            Assert.AreEqual("started,ended matched", heardWhenTheCallReturned, "The party's handlers hear the end before the game's call returns");
            Assert.IsFalse(held.IsSearching);
        }

        [UnityTest]
        public IEnumerator ALeadersSearchMatchedBeforeTheMembersGameReadsAgain_IsStillToldToThatGame()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            Task<FlockParty> joined = multiplayer.JoinPartyAsync(party.InviteCode);
            yield return Done(joined, "joined");
            yield return TheLeadersEarlyMatchIsTold(multiplayer, party, joined.Result);
        }

        [UnityTest]
        public IEnumerator ALeadersSearchMatchedBeforeTheNextReading_AfterGetMyParty_IsStillToldToThatGame()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            _server.Parties.Joins(party, A);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            yield return TheLeadersEarlyMatchIsTold(multiplayer, party, mine.Result);
        }

        [UnityTest]
        public IEnumerator AJoinTheGameGivesUpDuringItsSearchReading_StillHandsOverTheParty()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            FakeMatchmakingServer.Held reading = _server.HoldNext(FakeMatchmakingServer.Current);
            reading.EndsWhenCancelled = true;
            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task<FlockParty> joined = multiplayer.JoinPartyAsync(party.InviteCode, giveUp.Token);
            yield return FlockTestWait.Until(() => reading.Arrived, "The player's search is being read");
            giveUp.Cancel();
            yield return Done(joined, "handed over once the game gave up");
            Assert.AreEqual(TaskStatus.RanToCompletion, joined.Status, "The join landed, so the party is the player's");
            Assert.AreSame(joined.Result, multiplayer.Parties.Held);
            reading.Release();
        }

        // The leader's search made and matched before this game reads again: the game holding the party is still told.
        private IEnumerator TheLeadersEarlyMatchIsTold(FlockMultiplayerProvider multiplayer, FakePartyServer.Party party, FlockParty held)
        {
            List<string> heard = new List<string>();
            held.SearchStarted += () => heard.Add("started");
            held.SearchEnded += result => heard.Add("ended " + result.Outcome);
            Task<FlockMatchmakingResult> found = held.FindMatchAsync(null);

            // Made and matched before this game's next reading, as when the engine forms matches right after the leader's search.
            _server.QueuedBy(FakeMatchmakingServer.TrioId, B, party);
            _server.QueuedBy(FakeMatchmakingServer.TrioId, C);
            _server.FormMatches();
            yield return Done(found, "matched");
            Assert.IsTrue(found.Result.IsMatched, "A match made after the game held the party is never taken for an old one");
            Assert.AreSame(found.Result.Session, multiplayer.Sessions.Held);
            CollectionAssert.AreEqual(new[] { "started", "ended matched" }, heard);
        }

        [UnityTest]
        public IEnumerator AMember_LearnsTheLeadersSearch_FromThePartyRefresh_AndGetsTheMatch()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, partyRefreshSeconds: 0.3);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            _server.Parties.Joins(party, A);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            List<FlockMatchmakingResult> ended = new List<FlockMatchmakingResult>();
            int started = 0;
            held.SearchStarted += () => started++;
            held.SearchEnded += ended.Add;
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Current) >= 1, "A refresh read the player's search");

            _server.QueuedBy(FakeMatchmakingServer.TrioId, B, party);
            yield return FlockTestWait.Until(() => started == 1, "The leader's search was seen");
            Assert.IsTrue(held.IsSearching);
            _server.QueuedBy(FakeMatchmakingServer.TrioId, C);
            _server.FormMatches();
            yield return FlockTestWait.Until(() => ended.Count == 1, "The match was seen");
            Assert.IsTrue(ended[0].IsMatched);
            Assert.AreSame(ended[0].Session, multiplayer.Sessions.Held, "The match's session is held here too");
            Assert.AreEqual(3, ended[0].Session.Players.Count);
            Assert.AreEqual(0, _server.Count(FakeMatchmakingServer.Create), "A member's game makes no search");
        }

        [UnityTest]
        public IEnumerator AMembersFindMatch_WaitsForTheLeadersSearch_AndReturnsItsMatch()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            _server.Parties.Joins(party, A);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            Task<FlockMatchmakingResult> found = held.FindMatchAsync(null);
            yield return RealSeconds(0.6f);
            Assert.IsFalse(found.IsCompleted, "Waits for the leader");
            Assert.AreEqual(0, _server.Count(FakeMatchmakingServer.Create), "A member's game makes no search");

            _server.QueuedBy(FakeMatchmakingServer.TrioId, B, party);
            yield return FlockTestWait.Until(() => held.IsSearching, "The leader's search was taken up");
            _server.QueuedBy(FakeMatchmakingServer.TrioId, C);
            _server.FormMatches();
            yield return Done(found, "matched");
            Assert.IsTrue(found.Result.IsMatched);
            Assert.AreSame(found.Result.Session, multiplayer.Sessions.Held);

            Task<FlockMatchmakingResult> second = held.FindMatchAsync(null);
            Task<FlockMatchmakingResult> third = held.FindMatchAsync(null);
            yield return Done(third, "refused");
            Failure<FlockValidationException>(third);
            Assert.IsFalse(second.IsCompleted, "The first call still waits");
        }

        [UnityTest]
        public IEnumerator ASearchAlreadyRunningWhenThePartyIsFirstRead_IsFollowedAndToldStarted()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, partyRefreshSeconds: 0.3);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            _server.Parties.Joins(party, A);
            _server.QueuedBy(FakeMatchmakingServer.TrioId, B, party);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            List<string> heard = new List<string>();
            held.SearchStarted += () => heard.Add("started");
            held.SearchEnded += result => heard.Add("ended " + result.Outcome);
            yield return FlockTestWait.Until(() => held.IsSearching, "The running search was followed");
            CollectionAssert.AreEqual(new[] { "started" }, heard, "Told like a new one");
            _server.QueuedBy(FakeMatchmakingServer.TrioId, C);
            _server.FormMatches();
            yield return FlockTestWait.Until(() => heard.Count == 2, "Its end was told");
            Assert.AreEqual("ended matched", heard[1]);
        }

        [UnityTest]
        public IEnumerator AMember_IsNotToldOfASearchFromBeforeThePartyWasRead_ButIsOfTheNextOne()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, partyRefreshSeconds: 0.3);
            FakeMatchmakingServer server = _server;
            FakePartyServer.Party party = server.Parties.MadeBy(B);
            server.Parties.Joins(party, A);
            server.QueuedBy(FakeMatchmakingServer.DuelId, B, party);
            server.FormMatches();
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            int started = 0;
            int ended = 0;
            held.SearchStarted += () => started++;
            held.SearchEnded += _ => ended++;
            yield return FlockTestWait.Until(() => server.Count(FakeMatchmakingServer.Current) >= 3, "Three refreshes read the player's search");
            Assert.AreEqual(0, started, "A search from before is not this launch's");
            Assert.AreEqual(0, ended);

            server.QueuedBy(FakeMatchmakingServer.TrioId, B, party);
            yield return FlockTestWait.Until(() => started == 1, "The next search is told");
        }

        [UnityTest]
        public IEnumerator APartyMatchedBetweenTwoRefreshes_IsStillToldAsStartedThenMatched()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, partyRefreshSeconds: 0.5);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            _server.Parties.Joins(party, A);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            List<string> heard = new List<string>();
            held.SearchStarted += () => heard.Add("started");
            held.SearchEnded += result => heard.Add("ended " + result.Outcome + (result.Session == null ? " without a session" : " with a session"));
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Current) >= 1, "A refresh read the player's search");
            int refreshes = _server.Count(FakeMatchmakingServer.Current);

            _server.QueuedBy(FakeMatchmakingServer.TrioId, B, party);
            _server.QueuedBy(FakeMatchmakingServer.TrioId, C);
            _server.FormMatches();
            Assert.AreEqual(refreshes, _server.Count(FakeMatchmakingServer.Current), "Matched before the next refresh");
            yield return FlockTestWait.Until(() => heard.Count >= 1, "The next refresh saw the search");
            int sawIt = _server.Count(FakeMatchmakingServer.Current);
            yield return FlockTestWait.Until(() => heard.Count == 2, "Its end was told too");
            Assert.AreEqual(sawIt, _server.Count(FakeMatchmakingServer.Current), "Told by the reading that saw the match, not a refresh later");
            CollectionAssert.AreEqual(new[] { "started", "ended matched with a session" }, heard);
        }

        [UnityTest]
        public IEnumerator APartysSearch_StoppedByAPlayerJoining_EndsAsPartyChanged()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party party = _server.Parties.MadeBy(A);
            _server.Parties.Joins(party, B);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            List<string> heard = new List<string>();
            held.SearchEnded += result => heard.Add(result.Outcome);
            Task<FlockMatchmakingResult> found = held.FindMatchAsync(FakeMatchmakingServer.Trio);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 1, "The search was made");
            _server.Parties.Joins(party, C);
            yield return Done(found, "stopped");
            Assert.AreEqual(FlockMatchmakingOutcome.PartyChanged, found.Result.Outcome);
            CollectionAssert.AreEqual(new[] { FlockMatchmakingOutcome.PartyChanged }, heard);
        }

        [UnityTest]
        public IEnumerator APartyThePlayerLeftMidSearch_HearsNothingMore_ThoughTheCallGetsHowItEnded()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            _server.Parties.Joins(party, A);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            int ended = 0;
            held.SearchEnded += _ => ended++;
            Task<FlockMatchmakingResult> found = held.FindMatchAsync(null);
            _server.QueuedBy(FakeMatchmakingServer.TrioId, B, party);
            yield return FlockTestWait.Until(() => held.IsSearching, "The leader's search was taken up");

            Task left = held.LeaveAsync();
            yield return Done(left, "left");
            yield return Done(found, "the search ended");
            Assert.AreEqual(FlockMatchmakingOutcome.PartyChanged, found.Result.Outcome, "The call hears how the search ended");
            Assert.AreEqual(0, ended, "A party that is no longer the player's raises nothing");
        }

        [UnityTest]
        public IEnumerator AFollowedSearchWhoseCheckFailed_IsFollowedAgainByTheNextRefresh_WithoutBeingToldTwice()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, partyRefreshSeconds: 0.4);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            _server.Parties.Joins(party, A);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            int started = 0;
            List<FlockMatchmakingResult> ended = new List<FlockMatchmakingResult>();
            held.SearchStarted += () => started++;
            held.SearchEnded += ended.Add;
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Current) >= 1, "A refresh read the player's search");

            _server.AnswerNext(FakeMatchmakingServer.ReadTicket, FakeMatchmakingServer.Refused(400, "request.validation_failed"));
            _server.QueuedBy(FakeMatchmakingServer.TrioId, B, party);
            yield return FlockTestWait.Until(() => _h.Logger.Warnings.Any(line => line.Contains("no longer followed")), "The check was refused");
            int checks = _server.Count(FakeMatchmakingServer.ReadTicket);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.ReadTicket) > checks, "Followed again");
            Assert.AreEqual(1, started, "Told once");
            _server.QueuedBy(FakeMatchmakingServer.TrioId, C);
            _server.FormMatches();
            yield return FlockTestWait.Until(() => ended.Count == 1, "The match was seen");
            Assert.IsTrue(ended[0].IsMatched);
        }

        [UnityTest]
        public IEnumerator AReadingSentBeforeASearchBegan_ChangesNothingWhenItLandsAfter()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, partyRefreshSeconds: 0.3);
            FakePartyServer.Party party = _server.Parties.MadeBy(A);
            _server.Parties.Joins(party, B);
            _server.QueuedBy(FakeMatchmakingServer.DuelId, A, party);
            _server.FormMatches();
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            List<string> heard = new List<string>();
            held.SearchStarted += () => heard.Add("started");
            held.SearchEnded += result => heard.Add("ended " + result.Outcome);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Current) >= 1, "A refresh set where the party's searches stood");

            FakeMatchmakingServer.Held older = _server.HoldNext(FakeMatchmakingServer.Current);
            yield return FlockTestWait.Until(() => older.Arrived, "A refresh's reading is on its way");
            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task<FlockMatchmakingResult> found = held.FindMatchAsync(FakeMatchmakingServer.Trio, null, giveUp.Token);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Create) == 1, "The search was made");
            giveUp.Cancel();
            yield return Done(found, "cancelled");
            older.Release();
            yield return RealSeconds(0.2f);
            CollectionAssert.AreEqual(new[] { "started", "ended cancelled" }, heard, "The older reading told nothing");
        }

        [UnityTest]
        public IEnumerator AnotherPlayerOfTheSameParty_StartsFromAReadingOfTheirOwn()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, partyRefreshSeconds: 0.3);
            FakePartyServer.Party party = _server.Parties.MadeBy(C);
            _server.Parties.Joins(party, A);
            _server.Parties.Joins(party, B);
            _server.QueuedBy(FakeMatchmakingServer.TrioId, C, party);
            _server.FormMatches();
            Task<FlockParty> mineAsA = multiplayer.GetMyPartyAsync();
            yield return Done(mineAsA, "A's party");
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Current) >= 1, "A's reading marked where the party's searches stood");

            _h.LoginAs(B);
            yield return null;
            Task<FlockParty> mineAsB = multiplayer.GetMyPartyAsync();
            yield return Done(mineAsB, "B's party");
            FlockParty held = mineAsB.Result;
            int started = 0;
            int ended = 0;
            held.SearchStarted += () => started++;
            held.SearchEnded += _ => ended++;
            int readings = _server.Count(FakeMatchmakingServer.Current);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Current) >= readings + 3, "Three of B's refreshes read B's search");
            Assert.AreEqual(0, started, "B's search from before is not this launch's");
            Assert.AreEqual(0, ended);
        }

        [UnityTest]
        public IEnumerator ASwitchWhileTheSearchIsBeingMade_EndsTheCallAsCancelled_NotAsABadAnswer()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakeMatchmakingServer.Held making = _server.HoldNext(FakeMatchmakingServer.Create);
            Task<FlockMatchmakingResult> found = multiplayer.FindMatchAsync(FakeMatchmakingServer.Trio);
            yield return FlockTestWait.Until(() => making.Arrived, "The search is on its way");
            _h.LoginAs(C);
            making.Release();
            yield return Done(found, "ended");
            Assert.IsTrue(found.IsCanceled, "Heard as a cancellation: the search was asked for by a sign-in that ended");
        }

        [UnityTest]
        public IEnumerator AFindMatchCallWhileThePartysSearchIsFinishing_WaitsForItsResult()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, partyRefreshSeconds: 0.3);
            FakePartyServer.Party party = _server.Parties.MadeBy(B);
            _server.Parties.Joins(party, A);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Current) >= 1, "A refresh read the player's search");
            _server.QueuedBy(FakeMatchmakingServer.TrioId, B, party);
            yield return FlockTestWait.Until(() => held.IsSearching, "The leader's search was followed");

            FakeSessionServer.Held reading = _server.Sessions.HoldNext(FakeSessionServer.Read);
            _server.QueuedBy(FakeMatchmakingServer.TrioId, C);
            _server.FormMatches();
            yield return FlockTestWait.Until(() => reading.Arrived, "The match's session is being read");
            Task<FlockMatchmakingResult> found = held.FindMatchAsync(null);
            yield return null;
            Assert.IsFalse(found.IsFaulted, "Not refused while the party's search finishes");
            reading.Release();
            yield return Done(found, "matched");
            Assert.IsTrue(found.Result.IsMatched);
        }

        [UnityTest]
        public IEnumerator ARefreshLandingWhileTheLeadersSearchIsBeingMade_FollowsNothing_AndTheSearchStartsOnce()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, partyRefreshSeconds: 0.3);
            FakePartyServer.Party party = _server.Parties.MadeBy(A);
            _server.Parties.Joins(party, B);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party");
            FlockParty held = mine.Result;
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Current) >= 1, "A refresh read the player's search");
            int started = 0;
            held.SearchStarted += () => started++;

            FakeMatchmakingServer.Held making = _server.HoldNext(FakeMatchmakingServer.Create);
            Task<FlockMatchmakingResult> found = held.FindMatchAsync(FakeMatchmakingServer.Trio);
            yield return FlockTestWait.Until(() => making.Arrived, "The search is on its way");
            int readings = _server.Count(FakeMatchmakingServer.Current);
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.Current) >= readings + 2, "Two refreshes read the search on its way");
            making.Release();
            yield return FlockTestWait.Until(() => _server.Count(FakeMatchmakingServer.ReadTicket) >= 1 || found.IsCompleted, "Checked");
            Assert.IsFalse(found.IsFaulted, "The search's own answer begins it: " + found.Exception?.InnerException?.Message);
            Assert.AreEqual(1, started, "Told once");
        }
    }
}
