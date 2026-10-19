#if FLOCK_NETCODE_FOR_GAMEOBJECTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // StartNetcodeAsync called again after the session's host changed or this device's relay was lost: one start at a time on a
    // NetworkManager, a shutdown under way waited for, the new role's part done, and a lost relay told once.
    public partial class FlockNetcodeTests
    {
        // ---- one start at a time ----

        [UnityTest]
        public IEnumerator ShuttingDownAndStartingAgain_InOneFrame_WaitsForTheShutdown()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            NetworkManager manager = Manager(FreePort());
            Task<FlockNetcodeStartResult> first = held[0].StartNetcodeAsync(manager);
            yield return Done(first, "hosting");
            Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, first.Result.Outcome, "Precondition: hosting");

            manager.Shutdown();
            Task<FlockNetcodeStartResult> again = held[0].StartNetcodeAsync(manager);
            yield return Done(again, "hosting again");
            Assert.IsFalse(again.IsFaulted, again.Exception?.InnerException?.Message);
            Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, again.Result.Outcome);
            Assert.IsTrue(manager.IsHost);
        }

        [UnityTest]
        public IEnumerator ASessionThatEndsWhileTheShutdownIsWaitedFor_EndsTheStart_AsSessionEnded()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            NetworkManager manager = Manager(FreePort());
            Task<FlockNetcodeStartResult> first = held[0].StartNetcodeAsync(manager);
            yield return Done(first, "hosting");
            Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, first.Result.Outcome, "Precondition: hosting");

            manager.Shutdown();
            Task<FlockNetcodeStartResult> again = held[0].StartNetcodeAsync(manager);
            Task ending = held[0].EndAsync();
            Assert.IsTrue(ending.IsCompleted && held[0].HasEnded, "Precondition: the session ended at once");
            Assert.IsTrue(manager.ShutdownInProgress && !again.IsCompleted, "Precondition: the start still waits for the shutdown");
            yield return Done(again, "the start ended");
            Assert.IsFalse(again.IsFaulted, again.Exception?.InnerException?.Message);
            Assert.AreEqual(FlockNetcodeStartOutcome.SessionEnded, again.Result.Outcome);
            Assert.IsFalse(manager.IsListening, "Nothing started for an ended session");
        }

        [UnityTest]
        public IEnumerator ASecondStart_EndsTheFirst_AsRestarted_AndGoesOn()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            NetworkManager manager = Manager(FreePort());
            // The host has published nothing, so each start waits for it.
            Task<FlockNetcodeStartResult> first = held[0].StartNetcodeAsync(manager);
            yield return null;
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                Task<FlockNetcodeStartResult> second = held[0].StartNetcodeAsync(manager, null, stop.Token);
                yield return Done(first, "the first start ended");
                Assert.IsTrue(first.Status == TaskStatus.RanToCompletion, $"Ended with an outcome, not {first.Status}");
                Assert.AreEqual(FlockNetcodeStartOutcome.Restarted, first.Result.Outcome);
                yield return null;
                Assert.IsFalse(second.IsCompleted, "The second still waits for the host");

                stop.Cancel();
                yield return Done(second, "the second start ended");
                Assert.IsTrue(second.IsCanceled, $"Cancelled by its caller, not {second.Status}");
            }
        }

        [UnityTest]
        public IEnumerator AStartItsCallerCancelled_IsCancelled_EvenWhenANewerStartReplacesIt()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            NetworkManager manager = Manager(FreePort());
            // An answer that does not stop for cancelling, so the caller's cancel and the newer start are both waiting when it lands.
            FakeSessionServer.Held publish = _server.HoldNext(FakeSessionServer.Publish);
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                Task<FlockNetcodeStartResult> first = held[0].StartNetcodeAsync(manager, null, stop.Token);
                yield return FlockTestWait.Until(() => _server.Count(FakeSessionServer.Publish) == 1, "the first start published");
                stop.Cancel();
                Task<FlockNetcodeStartResult> second = held[0].StartNetcodeAsync(manager);
                yield return null;
                Assert.IsFalse(first.IsCompleted, "Precondition: both wait for the answer");
                publish.Release();
                yield return Done(first, "the first start ended");
                Assert.IsTrue(first.IsCanceled, $"The caller's cancel is theirs, not {first.Status}");
                yield return Done(second, "the second start ended", 15f);
                Assert.IsFalse(second.IsFaulted, second.Exception?.InnerException?.Message);
                Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, second.Result.Outcome, "The first stopped what it started for the newer start");
            }
        }

        [UnityTest]
        public IEnumerator AThirdStart_StillEndsTheSecond_AfterTheFirstHasEnded()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            NetworkManager manager = Manager(FreePort());
            Task<FlockNetcodeStartResult> first = held[0].StartNetcodeAsync(manager);
            yield return null;
            Task<FlockNetcodeStartResult> second = held[0].StartNetcodeAsync(manager);
            yield return Done(first, "the first start ended");
            Assert.AreEqual(FlockNetcodeStartOutcome.Restarted, first.Result.Outcome, "Precondition: the second replaced the first");

            // The first's end gives back only its own place, so the second is still the start under way.
            Task<FlockNetcodeStartResult> third = held[0].StartNetcodeAsync(manager);
            yield return Done(second, "the second start ended");
            Assert.IsTrue(second.Status == TaskStatus.RanToCompletion, $"Ended with an outcome, not {second.Status}");
            Assert.AreEqual(FlockNetcodeStartOutcome.Restarted, second.Result.Outcome);
            Assert.IsFalse(third.IsCompleted);
        }

        [UnityTest]
        public IEnumerator AReplacedStartWhoseAnswerIsSlow_IsWaitedFor_AndTheNewerStartHosts()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            NetworkManager manager = Manager(FreePort());
            // A publish whose answer is slow and does not stop for cancelling, as a slow server's.
            FakeSessionServer.Held publish = _server.HoldNext(FakeSessionServer.Publish);
            Task<FlockNetcodeStartResult> first = held[0].StartNetcodeAsync(manager);
            yield return FlockTestWait.Until(() => _server.Count(FakeSessionServer.Publish) == 1, "the first start published");
            Assert.IsTrue(manager.IsHost, "Precondition: the first start's netcode runs");

            Task<FlockNetcodeStartResult> second = held[0].StartNetcodeAsync(manager);
            // Longer than a shutdown is waited for: only waiting for the first start itself keeps the second from being refused.
            yield return new WaitForSecondsRealtime(6f);
            Assert.IsFalse(second.IsCompleted, $"The second waits for the first, not {second.Status}");
            publish.Release();
            yield return Done(second, "the second start ended", 15f);

            Assert.AreEqual(FlockNetcodeStartOutcome.Restarted, first.Result.Outcome, "The first stopped what it started once its answer came");
            Assert.IsFalse(second.IsFaulted, second.Exception?.InnerException?.Message);
            Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, second.Result.Outcome);
            Assert.IsTrue(manager.IsHost);
        }

        // ---- the new role's part after a host change ----

        [UnityTest]
        public IEnumerator AfterTheHostChanges_ThePlayerWhoHostsNow_HostsOnTheRelayItHad_AndPublishes()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            FlockNetcodeOptions always = new FlockNetcodeOptions { Relay = FlockRelayUse.Always };
            yield return PlayerAgainstARelayedHost(always, (host, port) => RelayedOnThisMachine(host, port), started, held, relays);
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome, "Precondition: playing through the relay");
            FlockRelayConnection mine = held[0].Relay;
            FlockRelayPeer oldHost = relays[0].Address;
            NetworkManager manager = _managers.Last();
            int changes = 0;
            held[0].HostChanged += () => changes++;

            _server.MakesHost(_server.ById(held[0].Id), B);
            Task<FlockMultiplayerSession> read = _h.Client.Multiplayer.GetSessionAsync(held[0].Id);
            yield return Done(read, "the session was read");
            Assert.AreEqual(1, changes, "Precondition: the game heard the host change");
            Assert.IsTrue(held[0].IsHost);

            manager.Shutdown();
            Task<FlockNetcodeStartResult> hosting = held[0].StartNetcodeAsync(manager, always);
            yield return Done(hosting, "hosting", 15f);
            Assert.IsFalse(hosting.IsFaulted, hosting.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, hosting.Result.Outcome);
            Assert.IsTrue(hosting.Result.ThroughRelay);
            Assert.AreSame(mine, held[0].Relay, "Hosts on the relay address it had as a player");
            Assert.AreEqual(2, _relay.Reservations, "No new reservation: the old host's and this one's");
            Assert.AreEqual(mine.Address, RelayAddressIn(Published()), "Publishes its own relay address");
            Assert.IsFalse(mine.KeepsChannelToForTesting(oldHost), "The channel to the old host is let go");
        }

        [UnityTest]
        public IEnumerator AHostChangeLandingWhileTheShutdownIsWaitedFor_StartsTheNewRole()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            FlockNetcodeOptions always = new FlockNetcodeOptions { Relay = FlockRelayUse.Always };
            yield return PlayerAgainstARelayedHost(always, (host, port) => RelayedOnThisMachine(host, port), started, held, relays);
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome, "Precondition: playing through the relay");
            NetworkManager manager = _managers.Last();

            // The game restarts first, and the reading that makes this player the host lands while its netcode is still stopping.
            manager.Shutdown();
            Task<FlockNetcodeStartResult> again = held[0].StartNetcodeAsync(manager, always);
            _server.MakesHost(_server.ById(held[0].Id), B);
            Task<FlockMultiplayerSession> read = _h.Client.Multiplayer.GetSessionAsync(held[0].Id);
            Assert.IsTrue(read.IsCompleted && held[0].IsHost, "Precondition: the host change was read at once");
            Assert.IsTrue(manager.ShutdownInProgress && !again.IsCompleted, "Precondition: the start still waits for the shutdown");
            yield return Done(again, "the start ended", 15f);
            Assert.IsFalse(again.IsFaulted, again.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.StartedAsHost, again.Result.Outcome, "The part the session gives this device once the waits are over");
        }

        [UnityTest]
        public IEnumerator AfterTheHostChanges_APlayer_ConnectsToTheNewHost()
        {
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            FlockNetcodeOptions always = new FlockNetcodeOptions { Relay = FlockRelayUse.Always };
            yield return PlayerAgainstARelayedHost(always, (host, port) => RelayedOnThisMachine(host, port), started, held, relays);
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome, "Precondition: playing through the relay");
            NetworkManager manager = _managers.Last();
            FlockRelayPeer oldHost = relays[0].Address;

            // A third player's game hosting on this machine through a relay address of its own.
            int newPort = FreePort();
            NetworkManager newHost = Manager(newPort);
            TransportOf(newHost).SetConnectionData("127.0.0.1", (ushort)newPort, "127.0.0.1");
            newHost.NetworkConfig.ConnectionApproval = true;
            newHost.ConnectionApprovalCallback = (request, response) => response.Approved = true;
            Assert.IsTrue(newHost.StartHost(), "Precondition: the new host's game hosts here");
            List<FlockRelayConnection> newHostsRelay = new List<FlockRelayConnection>();
            yield return OtherRelay("player-c", newHostsRelay);
            OtherBridge(FlockRelayBridge.ForTheHost(newHostsRelay[0], newPort, _h.Logger));

            FakeSessionServer.Session session = _server.ById(held[0].Id);
            _server.MakesHost(session, "player-c");
            Task<FlockMultiplayerSession> read = _h.Client.Multiplayer.GetSessionAsync(held[0].Id);
            yield return Done(read, "the session was read");
            Assert.IsNull(held[0].Connection, "Precondition: the old host's address is gone");

            manager.Shutdown();
            Task<FlockNetcodeStartResult> again = held[0].StartNetcodeAsync(manager, always);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsFalse(again.IsCompleted, "Waits for the new host to say where it is");
            _server.Publishes(session, RelayedOnThisMachine(newHostsRelay[0].Address, newPort));
            yield return Done(again, "connected to the new host", 15f);
            Assert.IsFalse(again.IsFaulted, again.Exception?.InnerException?.ToString());
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, again.Result.Outcome, again.Result.RefusedReason);
            Assert.IsTrue(again.Result.ThroughRelay);
            Assert.IsTrue(held[0].Relay.KeepsChannelToForTesting(newHostsRelay[0].Address), "A channel to the new host");
            Assert.IsFalse(held[0].Relay.KeepsChannelToForTesting(oldHost), "None kept to the old one");
        }

        // ---- a relay lost mid-session ----

        [UnityTest]
        public IEnumerator ALostRelay_IsToldOnce_WithItsReason()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            RelayOn();
            // Renewed every second, so a relay that forgot the reservation answers the next renewal within the test.
            multiplayer.Sessions.RelayTiming.LongestBetweenReservationRenewals = TimeSpan.FromSeconds(1);
            List<string> told = new List<string>();
            held[0].RelayLost += reason => told.Add(reason);
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "the relay opened");
            Assert.IsFalse(opening.IsFaulted, "Precondition: the relay opened");

            _relay.ForgetEveryReservation();
            yield return FlockTestWait.Until(() => told.Count > 0, "the loss was told", 10f);
            yield return new WaitForSecondsRealtime(1f);
            CollectionAssert.AreEqual(new[] { FlockRelayFailure.Lost }, told, "Once, with its reason");
        }

        [UnityTest]
        public IEnumerator ARelayFlockGaveBack_IsNotToldAsLost()
        {
            Hosting hosting = new Hosting();
            Task<FlockNetcodeStartResult> started = null;
            List<string> told = new List<string>();
            yield return HostedWithNetcode(hosting, null, manager =>
            {
                RelayOn();
                _relay.RefusedOpenings.Add(FakeRelayServer.RelayIp);
                // Before the start, so a report on any frame after the relay was given back is heard.
                hosting.Session.RelayLost += reason => told.Add(reason);
            }, result => started = result);
            Assert.AreEqual(FlockRelayFailure.OpenRefused, started.Result.RelayFailureReason, "Precondition: the relay could not be opened to");
            Assert.IsTrue(hosting.Session.Relay != null && hosting.Session.Relay.HasStopped, "Precondition: the session still holds the relay it gave back");

            yield return new WaitForSecondsRealtime(1f);
            Assert.IsEmpty(told, "Given back by Flock, not lost");
        }
    }
}
#endif
