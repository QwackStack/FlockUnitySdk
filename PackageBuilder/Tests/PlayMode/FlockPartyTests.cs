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
    // The player's party on real frames against a party server in memory: one held party per sign-in, changed only by the
    // newest reading, its events, its refresh at the game's setting, and every way it ends.
    public class FlockPartyTests
    {
        private const string A = "player-a";
        private const string B = "player-b";
        private const string C = "player-c";
        private const string D = "player-d";

        private FlockTestClient _h;
        private FakePartyServer _server;

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

        // The refresh interval goes in through the init config, the way Flock > Settings hands it over.
        private FlockMultiplayerProvider SignedInAs(string player, double refreshSeconds = 0, int retries = 0)
        {
            _server = new FakePartyServer();
            _h = FlockTestClient.Create(new FlockFakeTransport(), config =>
            {
                config.PartyRefreshInterval = TimeSpan.FromSeconds(refreshSeconds);
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

        private static string[] Ids(FlockParty party) => party.Members.Select(member => member.PlayerId).ToArray();

        private static T Failure<T>(Task task) where T : Exception
        {
            Assert.IsTrue(task.IsFaulted, "The call failed");
            T failure = task.Exception.InnerException as T;
            Assert.IsNotNull(failure, $"The call failed with {typeof(T).Name}, not {task.Exception.InnerException?.GetType().Name}");
            return failure;
        }

        // ---- getting a party ----

        [UnityTest]
        public IEnumerator CreateParty_HoldsThePlayerAsItsOnlyMemberAndLeader_AndSendsOnlyWhatWasGiven()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");

            FlockParty party = created.Result;
            CollectionAssert.AreEqual(new[] { A }, Ids(party));
            Assert.IsTrue(party.IsLeader);
            Assert.AreEqual(4, party.MaxSize, "The server's own default size");
            Assert.IsFalse(string.IsNullOrEmpty(party.InviteCode));
            Assert.AreEqual("{}", _server.RequestsTo(FakePartyServer.Create)[0].JsonBody, "Nothing given, nothing sent");
            Assert.AreEqual(0, _server.Count(FakePartyServer.Mine) + _server.Count(FakePartyServer.Read), "The new party needs no read");
            Assert.AreSame(party, multiplayer.Parties.Held);
        }

        [UnityTest]
        public IEnumerator CreateParty_SendsTheSizeAndSettingsAsGiven()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync(3, new Dictionary<string, object> { { "mode", "duo" } });
            yield return Done(created, "the party was made");

            JObject sent = JObject.Parse(_server.RequestsTo(FakePartyServer.Create)[0].JsonBody);
            Assert.AreEqual(3, (int)sent["max_size"]);
            Assert.AreEqual("duo", (string)sent["settings"]["mode"]);
            Assert.AreEqual(3, created.Result.MaxSize);
        }

        [UnityTest]
        public IEnumerator JoinParty_ReadsTheMembersBeforeHandingItOver()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party onServer = _server.MadeBy(B);
            _server.Joins(onServer, C);

            Task<FlockParty> joined = multiplayer.JoinPartyAsync("  " + onServer.InviteCode.ToLowerInvariant());
            yield return Done(joined, "the party was joined");

            FlockParty party = joined.Result;
            CollectionAssert.AreEqual(new[] { B, C, A }, Ids(party), "Every member, in the order they joined");
            Assert.AreEqual(B, party.LeaderPlayerId);
            Assert.IsFalse(party.IsLeader);
            Assert.AreEqual(1, _server.Count(FakePartyServer.Read), "One read of the party joined");
        }

        [UnityTest]
        public IEnumerator GetMyParty_IsNullInNone_AndTheSameObjectWhileThePartyLasts()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> none = multiplayer.GetMyPartyAsync();
            yield return Done(none, "the player's party was read");
            Assert.IsNull(none.Result);

            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the player's party was read again");

            Assert.AreSame(created.Result, mine.Result);
        }

        // ---- the newest reading wins ----

        [UnityTest]
        public IEnumerator AReadSentBeforeThisGamesKickFinished_ChangesNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 1);
            Task<FlockParty> created = multiplayer.CreatePartyAsync(8);
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            FakePartyServer.Party onServer = _server.PartyOf(A);
            _server.Joins(onServer, B);
            _server.Joins(onServer, C);
            yield return FlockTestWait.Until(() => party.Members.Count == 3, "A refresh showed B and C");

            int changes = 0;
            party.MembersChanged += () => changes++;
            _server.Joins(onServer, D);
            FakePartyServer.Held refreshBeforeTheKick = _server.HoldNext(FakePartyServer.Mine);
            yield return FlockTestWait.Until(() => refreshBeforeTheKick.Arrived, "A refresh that sees D is on its way");
            FakePartyServer.Held readAfterTheKick = _server.HoldNext(FakePartyServer.Mine);
            Task kick = party.KickAsync(C);
            yield return FlockTestWait.Until(() => readAfterTheKick.Arrived, "The kick went through and its read is on its way");

            refreshBeforeTheKick.Release();
            yield return null;
            yield return null;
            Assert.AreEqual(0, changes, "A read sent before the kick finished changes nothing");
            CollectionAssert.AreEqual(new[] { A, B, C }, Ids(party));

            readAfterTheKick.Release();
            yield return Done(kick, "the kick finished");
            Assert.IsFalse(kick.IsFaulted);
            CollectionAssert.AreEqual(new[] { A, B, D }, Ids(party));
            Assert.AreEqual(1, changes);
        }

        [UnityTest]
        public IEnumerator AnOlderReadLandingAfterANewerOne_ChangesNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 1);
            Task<FlockParty> created = multiplayer.CreatePartyAsync(8);
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            FakePartyServer.Party onServer = _server.PartyOf(A);
            _server.Joins(onServer, B);
            FakePartyServer.Held olderRefresh = _server.HoldNext(FakePartyServer.Mine);
            yield return FlockTestWait.Until(() => olderRefresh.Arrived, "A refresh that sees A and B is on its way");

            _server.Joins(onServer, C);
            int changes = 0;
            party.MembersChanged += () => changes++;
            Task<FlockParty> newer = multiplayer.GetMyPartyAsync();
            yield return Done(newer, "a newer read landed");
            CollectionAssert.AreEqual(new[] { A, B, C }, Ids(party));

            olderRefresh.Release();
            yield return null;
            yield return null;
            CollectionAssert.AreEqual(new[] { A, B, C }, Ids(party), "The older read did not take C away");
            Assert.AreEqual(1, changes);
        }

        [UnityTest]
        public IEnumerator AReadSentBeforeLeaving_DoesNotBringThePartyBack()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            _server.Joins(_server.PartyOf(A), B);
            int ended = 0;
            party.Ended += _ => ended++;

            FakePartyServer.Held readBeforeLeaving = _server.HoldNext(FakePartyServer.Mine);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return FlockTestWait.Until(() => readBeforeLeaving.Arrived, "A read that sees the party is on its way");
            Task leave = party.LeaveAsync();
            yield return Done(leave, "the player left");

            readBeforeLeaving.Release();
            yield return Done(mine, "the read landed");
            Assert.IsNull(mine.Result, "The player is in no party: the old read was read again");
            Assert.IsNull(multiplayer.Parties.Held);
            Assert.AreEqual(FlockPartyEndReason.Left, party.EndReason);
            Assert.AreEqual(1, ended);
        }

        // ---- sign-in ----

        [UnityTest]
        public IEnumerator SignOut_EndsThePartyAtOnce_RaisesEndedOnTheNextFrame_AndStopsTheRefresh()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0.1);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            List<FlockPartyEndReason> ended = new List<FlockPartyEndReason>();
            party.Ended += ended.Add;
            yield return FlockTestWait.Until(() => _server.Count(FakePartyServer.Mine) >= 1, "The party was refreshed");

            _h.Client.Authentication.Logout();
            Assert.IsTrue(party.HasEnded, "Ended in the same frame as the sign-out");
            Assert.AreEqual(FlockPartyEndReason.SignedOut, party.EndReason);
            Task refused = party.KickAsync(B);
            Assert.IsInstanceOf<FlockValidationException>(Failure<FlockValidationException>(refused));

            yield return FlockTestWait.Until(() => ended.Count == 1, "Ended was raised");
            int sent = _server.Count(FakePartyServer.Mine);
            yield return RealSeconds(0.5f);
            CollectionAssert.AreEqual(new[] { FlockPartyEndReason.SignedOut }, ended);
            Assert.AreEqual(sent, _server.Count(FakePartyServer.Mine), "Nothing is refreshed after the sign-out");
            Assert.AreEqual(0, _server.Count(FakePartyServer.Kick));
        }

        [UnityTest]
        public IEnumerator APlayerSwitch_EndsTheOldPlayersParty_AndTheNewPlayerGetsTheirOwn()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            FlockParty partyOfA = created.Result;
            _server.Joins(_server.PartyOf(A), B);
            List<FlockPartyEndReason> ended = new List<FlockPartyEndReason>();
            partyOfA.Ended += ended.Add;

            // B signs in with no sign-out between, and asks in the same frame, before the frame check runs.
            _h.LoginAs(B);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "B's party was read");

            Assert.AreNotSame(partyOfA, mine.Result, "B gets a party object of its own");
            Assert.IsFalse(mine.Result.HasEnded);
            Assert.IsFalse(mine.Result.IsLeader);
            CollectionAssert.AreEqual(new[] { FlockPartyEndReason.SignedOut }, ended);
        }

        [UnityTest]
        public IEnumerator ALeaveAnsweredAfterTheSignInEnded_EndsThePartyAsSignedOut()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            List<FlockPartyEndReason> ended = new List<FlockPartyEndReason>();
            created.Result.Ended += ended.Add;
            FakePartyServer.Held answer = _server.HoldNext(FakePartyServer.Leave);
            Task leave = created.Result.LeaveAsync();
            yield return FlockTestWait.Until(() => answer.Arrived, "A's leave is on its way");

            // B signs in, and A's leave lands before the frame check runs.
            _h.LoginAs(B);
            answer.Release();
            yield return Done(leave, "A's leave landed");
            yield return null;

            CollectionAssert.AreEqual(new[] { FlockPartyEndReason.SignedOut }, ended, "Ended once, with the reason it already read as");
            Assert.AreEqual(FlockPartyEndReason.SignedOut, created.Result.EndReason);
        }

        [UnityTest]
        public IEnumerator ALateChangeAnswerFromTheLastSignIn_DoesNotSendTheNewPlayersReadAgain()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            _server.Joins(_server.PartyOf(A), B);
            FakePartyServer.Held kick = _server.HoldNext(FakePartyServer.Kick);
            Task kicked = created.Result.KickAsync(B);
            yield return FlockTestWait.Until(() => kick.Arrived, "A's kick is on its way");

            _h.LoginAs(D);
            int readsBefore = _server.Count(FakePartyServer.Mine);
            FakePartyServer.Held read = _server.HoldNext(FakePartyServer.Mine);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return FlockTestWait.Until(() => read.Arrived, "D's read is on its way");
            kick.Release();
            yield return Done(kicked, "A's kick landed");
            read.Release();
            yield return Done(mine, "D's party was read");

            Assert.IsFalse(mine.IsFaulted, "D's read was taken");
            Assert.AreEqual(1, _server.Count(FakePartyServer.Mine) - readsBefore, "A's answer did not send D's read again");
        }

        [UnityTest]
        public IEnumerator ARetryAfterAPlayerSwitch_IsNotSentAsTheNewPlayer()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, retries: 1);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            _server.Joins(_server.PartyOf(A), B);

            _server.AnswerNext(FakePartyServer.Kick, FakePartyServer.Refused(429, "request.rate_limited"));
            FakePartyServer.Held firstTry = _server.HoldNext(FakePartyServer.Kick);
            Task kick = created.Result.KickAsync(B);
            yield return FlockTestWait.Until(() => firstTry.Arrived, "The kick's first try is on its way");
            _h.LoginAs(D);
            firstTry.Release();
            yield return Done(kick, "the kick gave up");

            Assert.IsTrue(kick.IsCanceled, "A request whose sign-in ended is cancelled");
            Assert.AreEqual(1, _server.Count(FakePartyServer.Kick), "The retry did not go out as the new player");
        }

        [UnityTest]
        public IEnumerator CreateParty_WhoseSignInEndedOnTheWay_IsCancelled_AndHoldsNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Held answer = _server.HoldNext(FakePartyServer.Create);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return FlockTestWait.Until(() => answer.Arrived, "The create is on its way");
            _h.LoginAs(B);
            answer.Release();
            yield return Done(created, "the create landed");

            Assert.IsTrue(created.IsCanceled);
            Assert.IsNull(multiplayer.Parties.Held);
        }

        [UnityTest]
        public IEnumerator SignedOut_EveryCallIsRefusedWithoutARequest()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _h.Client.Authentication.Logout();

            Failure<FlockAuthException>(multiplayer.CreatePartyAsync());
            Failure<FlockAuthException>(multiplayer.JoinPartyAsync("ABC123"));
            Failure<FlockAuthException>(multiplayer.GetMyPartyAsync());
            Assert.AreEqual(0, _server.TotalRequests);
            yield return null;
        }

        [UnityTest]
        public IEnumerator APlayerSwitch_ThenCreatingAParty_EndsTheOldPlayersPartyAsSignedOut()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "A's party was made");
            List<FlockPartyEndReason> ended = new List<FlockPartyEndReason>();
            created.Result.Ended += ended.Add;

            _h.LoginAs(B);
            Task<FlockParty> createdByB = multiplayer.CreatePartyAsync();
            yield return Done(createdByB, "B's party was made");

            CollectionAssert.AreEqual(new[] { FlockPartyEndReason.SignedOut }, ended, "A's sign-in ended; nobody removed A");
            Assert.AreSame(createdByB.Result, multiplayer.Parties.Held);
        }

        [UnityTest]
        public IEnumerator GetMyParty_WhoseSignInEndedOnTheWay_IsCancelled()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _server.MadeBy(A);
            FakePartyServer.Held answer = _server.HoldNext(FakePartyServer.Mine);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return FlockTestWait.Until(() => answer.Arrived, "The read is on its way");
            _h.LoginAs(B);
            answer.Release();
            yield return Done(mine, "the read landed");

            Assert.IsTrue(mine.IsCanceled);
            Assert.IsNull(multiplayer.Parties.Held);
        }

        [UnityTest]
        public IEnumerator JoinParty_WhoseSignInEndedOnTheWay_IsCancelled()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party onServer = _server.MadeBy(B);
            FakePartyServer.Held answer = _server.HoldNext(FakePartyServer.Read);
            Task<FlockParty> joined = multiplayer.JoinPartyAsync(onServer.InviteCode);
            yield return FlockTestWait.Until(() => answer.Arrived, "The read after joining is on its way");
            _h.LoginAs(C);
            answer.Release();
            yield return Done(joined, "the read landed");

            Assert.IsTrue(joined.IsCanceled);
            Assert.IsNull(multiplayer.Parties.Held);
        }

        [UnityTest]
        public IEnumerator ALateReadFromTheLastSignIn_LeavesTheNewPlayersPartyAlone()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "A's party was made");
            _server.Joins(_server.PartyOf(A), B);
            FakePartyServer.Held readAfterTheKick = _server.HoldNext(FakePartyServer.Mine);
            Task kick = created.Result.KickAsync(B);
            yield return FlockTestWait.Until(() => readAfterTheKick.Arrived, "A's kick went through and its read is on its way");

            _h.LoginAs(C);
            _server.MadeBy(C);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "C's party was read");
            readAfterTheKick.Release();
            yield return Done(kick, "A's kick finished");

            Assert.IsFalse(mine.Result.HasEnded, "A read A sent changes nothing for C");
            Assert.AreSame(mine.Result, multiplayer.Parties.Held);
        }

        [UnityTest]
        public IEnumerator AReadSentBeforeCreating_DoesNotEndTheNewParty()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Held readBeforeCreating = _server.HoldNext(FakePartyServer.Mine);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return FlockTestWait.Until(() => readBeforeCreating.Arrived, "A read that finds no party is on its way");
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");

            readBeforeCreating.Release();
            yield return Done(mine, "the read landed");
            Assert.IsFalse(created.Result.HasEnded);
            Assert.AreSame(created.Result, mine.Result, "The old read was read again and found the new party");
        }

        [UnityTest]
        public IEnumerator AReadSentBeforeJoining_DoesNotSayThePlayerIsInNoParty()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party onServer = _server.MadeBy(B);
            FakePartyServer.Held readBeforeJoining = _server.HoldNext(FakePartyServer.Mine);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return FlockTestWait.Until(() => readBeforeJoining.Arrived, "A read that finds no party is on its way");
            FakePartyServer.Held readAfterJoining = _server.HoldNext(FakePartyServer.Read);
            Task<FlockParty> joined = multiplayer.JoinPartyAsync(onServer.InviteCode);
            yield return FlockTestWait.Until(() => readAfterJoining.Arrived, "The join went through and its read is on its way");

            readBeforeJoining.Release();
            yield return Done(mine, "the old read landed");
            Assert.IsNotNull(mine.Result, "The player had joined before the answer came back");
            readAfterJoining.Release();
            yield return Done(joined, "the join finished");
            Assert.AreSame(joined.Result, mine.Result);
        }

        [UnityTest]
        public IEnumerator AGetMyPartySentBeforeAKick_ReadsAgain_AndShowsTheKick()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync(8);
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            _server.Joins(_server.PartyOf(A), B);
            _server.Joins(_server.PartyOf(A), C);

            FakePartyServer.Held readBeforeTheKick = _server.HoldNext(FakePartyServer.Mine);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return FlockTestWait.Until(() => readBeforeTheKick.Arrived, "A read that sees C is on its way");
            FakePartyServer.Held readAfterTheKick = _server.HoldNext(FakePartyServer.Mine);
            Task kick = party.KickAsync(C);
            yield return FlockTestWait.Until(() => readAfterTheKick.Arrived, "The kick went through and its read is on its way");

            readBeforeTheKick.Release();
            yield return Done(mine, "the old read landed");
            CollectionAssert.AreEqual(new[] { A, B }, Ids(mine.Result), "The call read again and shows the kick");
            readAfterTheKick.Release();
            yield return Done(kick, "the kick finished");
        }

        [UnityTest]
        public IEnumerator AnAnswerThatNamesNoParty_IsAFailure_AndHoldsNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _server.AnswerNext(FakePartyServer.Create, FakePartyServer.NamingNoParty());
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the create finished");

            Failure<FlockNetworkException>(created);
            Assert.IsNull(multiplayer.Parties.Held);
        }

        // ---- the refresh ----

        [UnityTest]
        public IEnumerator TheRefreshRunsAtTheGamesSetting()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0.2);
            Assert.AreEqual(TimeSpan.FromSeconds(0.2), multiplayer.Parties.RefreshInterval);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");

            yield return RealSeconds(1.5f);
            int reads = _server.Count(FakePartyServer.Mine);
            Assert.That(reads, Is.InRange(4, 11), "About one read every 0.2 s, jittered");
        }

        [UnityTest]
        public IEnumerator ARefreshSettingOfZero_SendsNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            Assert.IsFalse(created.IsFaulted, "Precondition: the party was made");
            Assert.AreSame(created.Result, multiplayer.Parties.Held);

            yield return RealSeconds(1f);
            Assert.AreEqual(0, _server.Count(FakePartyServer.Mine));
        }

        [UnityTest]
        public IEnumerator AKickedPlayersNextRefresh_EndsThePartyAsRemoved_AndTheRefreshStops()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0.1);
            FakePartyServer.Party onServer = _server.MadeBy(B);
            Task<FlockParty> joined = multiplayer.JoinPartyAsync(onServer.InviteCode);
            yield return Done(joined, "the party was joined");
            FlockParty party = joined.Result;
            List<FlockPartyEndReason> ended = new List<FlockPartyEndReason>();
            party.Ended += ended.Add;

            _server.Removes(onServer, A);
            yield return FlockTestWait.Until(() => ended.Count == 1, "The next refresh ended the party");
            Assert.AreEqual(FlockPartyEndReason.Removed, ended[0]);
            Assert.IsNull(multiplayer.Parties.Held);

            int sent = _server.Count(FakePartyServer.Mine);
            yield return RealSeconds(0.5f);
            Assert.AreEqual(sent, _server.Count(FakePartyServer.Mine), "Nothing more is refreshed");
            Assert.AreEqual(1, ended.Count);
        }

        [UnityTest]
        public IEnumerator ARefreshThatFindsAnotherParty_EndsTheHeldOne_AndHoldsNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0.1);
            FakePartyServer.Party first = _server.MadeBy(B);
            Task<FlockParty> joined = multiplayer.JoinPartyAsync(first.InviteCode);
            yield return Done(joined, "the party was joined");
            FlockParty party = joined.Result;

            // The player left and joined another party on another device.
            _server.Removes(first, A);
            _server.Joins(_server.MadeBy(C), A);
            yield return FlockTestWait.Until(() => party.HasEnded, "The refresh ended the held party");

            Assert.AreEqual(FlockPartyEndReason.Removed, party.EndReason);
            Assert.IsNull(multiplayer.Parties.Held, "Only a call the game makes takes up another party");
            int sent = _server.Count(FakePartyServer.Mine);
            yield return RealSeconds(0.5f);
            Assert.AreEqual(sent, _server.Count(FakePartyServer.Mine));
        }

        [UnityTest]
        public IEnumerator ARefreshStoppedByAFailure_WarnsOnce_AndGetMyPartyStartsItAgain()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0.1);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            _server.AnswerNext(FakePartyServer.Mine, FakePartyServer.Refused(422, "request.validation_failed"));
            yield return FlockTestWait.Until(() => _h.Logger.Warnings.Any(line => line.Contains("no longer refreshed")), "The stop was reported");

            int sent = _server.Count(FakePartyServer.Mine);
            yield return RealSeconds(0.5f);
            Assert.AreEqual(sent, _server.Count(FakePartyServer.Mine), "A permanent failure stops the refresh");
            Assert.AreEqual(1, _h.Logger.Warnings.Count(line => line.Contains("no longer refreshed")));
            Assert.IsFalse(created.Result.HasEnded, "The player is still in the party");

            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return Done(mine, "the party was read");
            int afterAsking = _server.Count(FakePartyServer.Mine);
            yield return FlockTestWait.Until(() => _server.Count(FakePartyServer.Mine) > afterAsking + 1, "The refresh runs again");
        }

        [UnityTest]
        public IEnumerator LeavingStopsTheRefresh()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0.1);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            yield return FlockTestWait.Until(() => _server.Count(FakePartyServer.Mine) >= 1, "The party was refreshed");

            Task leave = created.Result.LeaveAsync();
            yield return Done(leave, "the player left");
            int sent = _server.Count(FakePartyServer.Mine);
            yield return RealSeconds(0.5f);
            Assert.AreEqual(sent, _server.Count(FakePartyServer.Mine));
        }

        // ---- changes by others ----

        [UnityTest]
        public IEnumerator ChangesByOthers_RaiseTheirEvents_WithThePartyAlreadyUpdated()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0.1);
            FakePartyServer.Party onServer = _server.MadeBy(B);
            Task<FlockParty> joined = multiplayer.JoinPartyAsync(onServer.InviteCode);
            yield return Done(joined, "the party was joined");
            FlockParty party = joined.Result;
            IReadOnlyList<FlockPartyMember> before = party.Members;
            List<string[]> membersSeen = new List<string[]>();
            List<bool> leaderSeen = new List<bool>();
            party.MembersChanged += () => membersSeen.Add(Ids(party));
            party.LeaderChanged += () => leaderSeen.Add(party.IsLeader);

            // B leaves: A is the only member left and leads.
            _server.Removes(onServer, B);
            yield return FlockTestWait.Until(() => leaderSeen.Count == 1, "LeaderChanged was raised");

            Assert.AreEqual(1, membersSeen.Count);
            CollectionAssert.AreEqual(new[] { A }, membersSeen[0], "MembersChanged saw the new members");
            CollectionAssert.AreEqual(new[] { true }, leaderSeen, "LeaderChanged saw the player lead");
            CollectionAssert.AreEqual(new[] { B, A }, before.Select(member => member.PlayerId).ToArray(), "A list handed out earlier keeps its members");
        }

        [UnityTest]
        public IEnumerator AHandlerThatThrows_IsLogged_AndTheOtherEventsStillRun()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0.1);
            FakePartyServer.Party onServer = _server.MadeBy(B);
            Task<FlockParty> joined = multiplayer.JoinPartyAsync(onServer.InviteCode);
            yield return Done(joined, "the party was joined");
            FlockParty party = joined.Result;
            int leaderChanges = 0;
            party.MembersChanged += () => throw new InvalidOperationException("the game's handler broke");
            party.LeaderChanged += () => leaderChanges++;

            LogAssert.Expect(LogType.Error, new Regex("FlockParty.MembersChanged subscriber threw"));
            _server.Removes(onServer, B);
            yield return FlockTestWait.Until(() => leaderChanges == 1, "LeaderChanged still ran");
        }

        [UnityTest]
        public IEnumerator TryGetSetting_ReadsEachValueAsTheTypeAsked()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Dictionary<string, object> settings = new Dictionary<string, object>
            {
                { "mode", "duo" }, { "n", 7 }, { "ratio", 0.5 }, { "nested", new Dictionary<string, int> { { "x", 1 } } },
            };
            Task<FlockParty> created = multiplayer.CreatePartyAsync(settings: settings);
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;

            Assert.IsTrue(party.TryGetSetting("n", out int n));
            Assert.AreEqual(7, n);
            Assert.IsTrue(party.TryGetSetting("ratio", out double ratio));
            Assert.AreEqual(0.5, ratio);
            Assert.IsTrue(party.TryGetSetting("mode", out string mode));
            Assert.AreEqual("duo", mode);
            Assert.IsTrue(party.TryGetSetting("nested", out Dictionary<string, int> nested));
            Assert.AreEqual(1, nested["x"]);
            Assert.IsFalse(party.TryGetSetting("missing", out int _));
            Assert.IsFalse(party.TryGetSetting("mode", out int _), "A word is not a number");
        }

        // ---- changes by this game ----

        [UnityTest]
        public IEnumerator MakeLeaderAndUpdate_ReadThePartyAgain_AndOnlyWhatWasGivenIsSent()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync(4, new Dictionary<string, object> { { "mode", "duo" } });
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            _server.Joins(_server.PartyOf(A), B);

            Task update = party.UpdateAsync(settings: new Dictionary<string, object> { { "region", "eu" } });
            yield return Done(update, "the party was updated");
            JObject sent = JObject.Parse(_server.RequestsTo(FakePartyServer.Update)[0].JsonBody);
            Assert.IsNull(sent["max_size"], "A size not given is not sent");
            Assert.IsTrue(party.TryGetSetting("region", out string region) && region == "eu");
            Assert.IsFalse(party.TryGetSetting("mode", out string _), "New settings replace the old ones");
            CollectionAssert.AreEqual(new[] { A, B }, Ids(party), "The read after the change shows B");

            int leaderChanges = 0;
            party.LeaderChanged += () => leaderChanges++;
            Task handOver = party.MakeLeaderAsync(B);
            yield return Done(handOver, "leadership was handed over");
            Assert.AreEqual(1, leaderChanges);
            Assert.IsFalse(party.IsLeader);
            Assert.AreEqual(2, _server.Count(FakePartyServer.Mine), "One read after each change");

            Task refused = party.UpdateAsync(maxSize: 6);
            yield return Done(refused, "the update was refused");
            Assert.AreEqual(FlockErrorCode.PartyNotPartyLeader, Failure<FlockException>(refused).ErrorCode);
        }

        [UnityTest]
        public IEnumerator Disband_EndsThePartyAsDisbanded_AndLaterCallsAreRefusedWithoutARequest()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            List<FlockPartyEndReason> ended = new List<FlockPartyEndReason>();
            party.Ended += ended.Add;

            Task disband = party.DisbandAsync();
            yield return Done(disband, "the party was disbanded");
            CollectionAssert.AreEqual(new[] { FlockPartyEndReason.Disbanded }, ended);
            Assert.IsNull(multiplayer.Parties.Held);

            int sent = _server.TotalRequests;
            Failure<FlockValidationException>(party.KickAsync(B));
            Failure<FlockValidationException>(party.LeaveAsync());
            Assert.AreEqual(sent, _server.TotalRequests);
        }

        [UnityTest]
        public IEnumerator Leaving_WhenTheServerSaysThePlayerIsAlreadyOut_StillEndsAsLeft()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            FakePartyServer.Party onServer = _server.MadeBy(B);
            Task<FlockParty> joined = multiplayer.JoinPartyAsync(onServer.InviteCode);
            yield return Done(joined, "the party was joined");
            _server.Removes(onServer, A);

            Task leave = joined.Result.LeaveAsync();
            yield return Done(leave, "the leave finished");
            Assert.IsFalse(leave.IsFaulted, "Already out is what leaving asks for");
            Assert.AreEqual(FlockPartyEndReason.Left, joined.Result.EndReason);
        }

        [UnityTest]
        public IEnumerator ARefreshThatSeesThisGamesLeaveFirst_EndsThePartyAsLeft_Once()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0.1);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            _server.Joins(_server.PartyOf(A), B);
            List<FlockPartyEndReason> ended = new List<FlockPartyEndReason>();
            party.Ended += ended.Add;

            FakePartyServer.Held leaveAnswer = _server.HoldNext(FakePartyServer.Leave);
            Task leave = party.LeaveAsync();
            yield return FlockTestWait.Until(() => leaveAnswer.Arrived, "The server has the leave; its answer is held");
            yield return FlockTestWait.Until(() => ended.Count == 1, "A refresh found the player out first");

            leaveAnswer.Release();
            yield return Done(leave, "the leave finished");
            CollectionAssert.AreEqual(new[] { FlockPartyEndReason.Left }, ended, "The player left; nobody removed them, and Ended came once");
        }

        [UnityTest]
        public IEnumerator AChangeToAPartyThatIsGone_EndsItAsRemoved_AndSaysWhy()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            _server.Joins(_server.PartyOf(A), B);
            _server.Disbands(_server.PartyOf(A));

            Task kick = party.KickAsync(B);
            yield return Done(kick, "the kick was refused");
            Assert.AreEqual(FlockErrorCode.PartyNotFound, Failure<FlockException>(kick).ErrorCode);
            Assert.AreEqual(FlockPartyEndReason.Removed, party.EndReason);
        }

        [UnityTest]
        public IEnumerator EmptyInputs_AreRefusedWithoutARequest()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            int sent = _server.TotalRequests;

            Failure<FlockValidationException>(multiplayer.JoinPartyAsync(""));
            Failure<FlockValidationException>(party.KickAsync(""));
            Failure<FlockValidationException>(party.MakeLeaderAsync(null));
            Failure<FlockValidationException>(party.UpdateAsync());
            Assert.AreEqual(sent, _server.TotalRequests);
        }

        // ---- Flock going away ----

        [UnityTest]
        public IEnumerator FlockShuttingDown_EndsThePartyWithoutRaisingEnded()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, refreshSeconds: 0.1);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            int ended = 0;
            party.Ended += _ => ended++;

            FlockClient.Shutdown();
            yield return RealSeconds(0.3f);
            Assert.IsTrue(party.HasEnded);
            Assert.AreEqual(0, ended);
        }

        [UnityTest]
        public IEnumerator AnAnswerLandingAfterTheReset_HoldsNothing()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _server.MadeBy(A);
            FakePartyServer.Held answer = _server.HoldNext(FakePartyServer.Mine);
            Task<FlockParty> mine = multiplayer.GetMyPartyAsync();
            yield return FlockTestWait.Until(() => answer.Arrived, "The read is on its way");

            FlockClient client = _h.Client;
            MethodInfo reset = typeof(FlockClient).GetMethod("ResetStaticState", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(reset, "FlockClient's reset is where the test expects it");
            try
            {
                // The reset keeps the sign-in, as entering Play Mode does; only the owner's own stop can tell the answer is late.
                reset.Invoke(null, null);
                answer.Release();
                yield return Done(mine, "the read landed");

                Assert.IsTrue(mine.IsCanceled, "An answer for a Flock that has gone holds nothing");
                Assert.IsNull(multiplayer.Parties.Held);
            }
            finally
            {
                client.Commands?.UnsubscribeFlushTriggers();
                (client.Analytics as FlockAnalyticsProvider)?.StopForShutdown();
                FlockEvents.ClearAll();
            }
        }

        [UnityTest]
        public IEnumerator TheNoDomainReloadReset_EndsTheParty()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            Task<FlockParty> created = multiplayer.CreatePartyAsync();
            yield return Done(created, "the party was made");
            FlockParty party = created.Result;
            int ended = 0;
            party.Ended += _ => ended++;

            FlockClient client = _h.Client;
            MethodInfo reset = typeof(FlockClient).GetMethod("ResetStaticState", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(reset, "FlockClient's reset is where the test expects it");
            try
            {
                reset.Invoke(null, null);
                Assert.IsTrue(party.HasEnded, "The reset keeps the sign-in, so only the party's own end says it is over");
                Assert.AreEqual(0, ended);
            }
            finally
            {
                // The reset forgets the client without shutting it down, as entering Play Mode does; let go of what it left listening.
                client.Commands?.UnsubscribeFlushTriggers();
                (client.Analytics as FlockAnalyticsProvider)?.StopForShutdown();
                FlockEvents.ClearAll();
            }
        }
    }
}
