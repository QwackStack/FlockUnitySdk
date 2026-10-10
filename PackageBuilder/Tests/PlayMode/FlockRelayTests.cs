using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The relay client against a TURN relay in memory on loopback (FakeRelayServer, coturn's answers as measured): reserving an
    // address, opening it to the host's relay address, packets both ways, renewals, each failure on its own reason, and a session's
    // relay given back when the session ends.
    public class FlockRelayTests
    {
        private const string A = "player-a";
        private const string B = "player-b";

        private FakeRelayServer _relay;
        private RecordingFlockLogger _logger;
        private readonly List<FlockRelayConnection> _connections = new List<FlockRelayConnection>();
        private FlockTestClient _h;
        private FakeSessionServer _server;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            _relay = new FakeRelayServer();
            _logger = new RecordingFlockLogger();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (FlockRelayConnection connection in _connections)
                connection.Close();
            _connections.Clear();
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            _relay?.Dispose();
            _relay = null;
        }

        private static FlockRelayConnection.Timing Quick() => new FlockRelayConnection.Timing
        {
            AnswerWait = TimeSpan.FromSeconds(2),
            NameLookUpWait = TimeSpan.FromSeconds(1),
        };

        private FlockRelayLogin Login(string player) => new FlockRelayLogin("127.0.0.1", _relay.Port, "1760000000:game:" + player, "minted-for-the-test");

        private Task<FlockRelayConnection> Open(string player, FlockRelayConnection.Timing timing = null, CancellationToken cancellationToken = default)
            => FlockRelayConnection.OpenAsync(Login(player), timing ?? Quick(), _logger, cancellationToken);

        private static IEnumerator Done(Task task, string what, float seconds = 10f) => FlockTestWait.Until(() => task.IsCompleted, what, seconds);

        private static IEnumerator RealSeconds(float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        private static string ReasonOf(Task task)
        {
            Assert.IsTrue(task.IsFaulted, $"The call failed (it is {task.Status})");
            FlockRelayException failure = task.Exception.InnerException as FlockRelayException;
            Assert.IsNotNull(failure, $"Failed with a relay reason, not {task.Exception.InnerException?.GetType().Name}: {task.Exception.InnerException?.Message}");
            return failure.Reason;
        }

        private static bool Cancelled(Task task) => task.IsCanceled || (task.IsFaulted && task.Exception.InnerException is OperationCanceledException);

        private IEnumerator Opened(string player, List<FlockRelayConnection> into, FlockRelayConnection.Timing timing = null)
        {
            Task<FlockRelayConnection> opening = Open(player, timing);
            yield return Done(opening, $"{player}'s relay opened");
            Assert.IsFalse(opening.IsFaulted, $"Precondition: {player}'s relay opened ({opening.Exception?.InnerException?.Message})");
            into.Add(opening.Result);
            _connections.Add(opening.Result);
        }

        // A host and a player, each with a relay address, both opened to the host's.
        private IEnumerator HostAndPlayer(List<FlockRelayConnection> pair, FlockRelayConnection.Timing timing = null)
        {
            yield return Opened(A, pair, timing);
            yield return Opened(B, pair, timing);
            Task hostOpen = pair[0].OpenToHostAsync(pair[0].Address);
            Task playerOpen = pair[1].OpenToHostAsync(pair[0].Address);
            yield return Done(Task.WhenAll(hostOpen, playerOpen), "both opened to the host's relay address");
            Assert.IsFalse(hostOpen.IsFaulted || playerOpen.IsFaulted, "Precondition: both opened");
        }

        // Sends one packet and waits for it at the other end; the peer it was heard from.
        private static IEnumerator Delivered(FlockRelayConnection from, FlockRelayPeer to, FlockRelayConnection receiver, string text, List<FlockRelayPeer> heardFrom, float seconds = 3f)
        {
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(text);
            Assert.IsTrue(from.Send(to, payload, 0, payload.Length), $"Sent \"{text}\"");
            byte[] buffer = new byte[FlockRelayConnection.MostBytesInAPacket];
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (receiver.TryReceive(buffer, out int count, out FlockRelayPeer peer))
                {
                    if (System.Text.Encoding.UTF8.GetString(buffer, 0, count) == text)
                    {
                        heardFrom.Add(peer);
                        yield break;
                    }
                }
                yield return null;
            }
            Assert.Fail($"\"{text}\" did not arrive within {seconds} s");
        }

        // ---- reserving an address ----

        [UnityTest]
        public IEnumerator Open_ReservesAnAddress_AfterAnsweringTheLoginChallenge()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);

            Assert.IsTrue(opened[0].IsOpen);
            Assert.AreEqual($"{FakeRelayServer.RelayIp}:50000", opened[0].Address.ToString(), "The relay address the relay answered");
            List<FakeRelayServer.Seen> asked = _relay.SeenRequests(FakeRelayServer.MethodAllocate);
            Assert.AreEqual(2, asked.Count, "One unsigned reservation, challenged, then one signed with the login");
            Assert.IsFalse(asked[0].Signed);
            Assert.IsTrue(asked[1].Signed);
            Assert.AreEqual(1, _relay.Reservations);
        }

        [UnityTest]
        public IEnumerator Open_ALostRequest_IsSentAgainUnderItsTransaction()
        {
            _relay.DropNext(FakeRelayServer.MethodAllocate, 1);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);

            List<FakeRelayServer.Seen> signed = _relay.SeenRequests(FakeRelayServer.MethodAllocate).Where(seen => seen.Signed).ToList();
            Assert.AreEqual(2, signed.Count, "The signed reservation went twice");
            Assert.AreEqual(signed[0].Transaction, signed[1].Transaction, "Under the same transaction, so the relay knows it for a repeat");
            Assert.AreEqual(1, _relay.Reservations, "One reservation, not two");
        }

        [UnityTest]
        public IEnumerator Open_SilentRelay_FailsUnreachable_WithinTheAnswerWait()
        {
            _relay.Silent = true;
            Stopwatch clock = Stopwatch.StartNew();
            Task<FlockRelayConnection> opening = Open(A);
            yield return Done(opening, "the open gave up");

            Assert.AreEqual(FlockRelayFailure.Unreachable, ReasonOf(opening));
            Assert.That(clock.Elapsed.TotalSeconds, Is.InRange(1.8, 4.0), "Given up at the 2 s answer wait");
            List<FakeRelayServer.Seen> asked = _relay.SeenRequests(FakeRelayServer.MethodAllocate);
            Assert.AreEqual(3, asked.Count, "Sent at 0, 0.5 and 1.5 s before giving up at 2 s");
            Assert.AreEqual(1, asked.Select(seen => seen.Transaction).Distinct().Count(), "Every send under one transaction");
        }

        [UnityTest]
        public IEnumerator Open_NameLookUpThatNeverAnswers_FailsUnreachable_WithinItsWait()
        {
            FlockRelayConnection.Timing timing = Quick();
            timing.NameLookUpWait = TimeSpan.FromSeconds(0.5);
            timing.NameLookUpForTesting = host => new TaskCompletionSource<System.Net.IPAddress[]>().Task;
            Stopwatch clock = Stopwatch.StartNew();
            Task<FlockRelayConnection> opening = FlockRelayConnection.OpenAsync(new FlockRelayLogin("relay.test", _relay.Port, "u", "p"), timing, _logger, CancellationToken.None);
            yield return Done(opening, "the open gave up");

            Assert.AreEqual(FlockRelayFailure.Unreachable, ReasonOf(opening));
            Assert.Less(clock.Elapsed.TotalSeconds, 2.0, "Given up at the lookup's own 0.5 s wait");
        }

        [UnityTest]
        public IEnumerator Open_WrongPassword_FailsWrongLogin_WithoutAnotherTry()
        {
            _relay.Password = "not-what-flock-minted";
            Task<FlockRelayConnection> opening = Open(A);
            yield return Done(opening, "the open failed");

            Assert.AreEqual(FlockRelayFailure.WrongLogin, ReasonOf(opening));
            yield return RealSeconds(0.5f);
            List<FakeRelayServer.Seen> asked = _relay.SeenRequests(FakeRelayServer.MethodAllocate);
            Assert.AreEqual(1, asked.Count(seen => seen.Signed), "A refused login is not sent again");
            Assert.AreEqual(0, _relay.Reservations);
        }

        [UnityTest]
        public IEnumerator Open_RelayWithNoRoom_FailsFull()
        {
            _relay.RefuseReservationsWith = 486;
            Task<FlockRelayConnection> opening = Open(A);
            yield return Done(opening, "the open failed");
            Assert.AreEqual(FlockRelayFailure.Full, ReasonOf(opening));
        }

        [UnityTest]
        public IEnumerator Open_RefusedForAnotherReason_FailsRefused()
        {
            _relay.RefuseReservationsWith = 442;
            Task<FlockRelayConnection> opening = Open(A);
            yield return Done(opening, "the open failed");
            Assert.AreEqual(FlockRelayFailure.Refused, ReasonOf(opening));
        }

        [UnityTest]
        public IEnumerator Open_StaleNonce_IsSentAgainWithTheNewNonce()
        {
            _relay.AnswerStaleNonce(1);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);

            List<FakeRelayServer.Seen> signed = _relay.SeenRequests(FakeRelayServer.MethodAllocate).Where(seen => seen.Signed).ToList();
            Assert.AreEqual(2, signed.Count, "Signed once with the first nonce, once with the one the 438 gave");
            Assert.AreNotEqual(signed[0].Transaction, signed[1].Transaction, "A new request, not a repeat");
            Assert.AreEqual(2, opened[0].NoncesTakenForTesting, "The login challenge's nonce, then the 438's");
        }

        [UnityTest]
        public IEnumerator Open_StaleNonceEveryTime_GivesUp()
        {
            _relay.AnswerStaleNonce(-1);
            Task<FlockRelayConnection> opening = Open(A);
            yield return Done(opening, "the open gave up");

            Assert.AreEqual(FlockRelayFailure.Refused, ReasonOf(opening));
            yield return RealSeconds(0.5f);
            Assert.AreEqual(3, _relay.SeenRequests(FakeRelayServer.MethodAllocate).Count, "The challenge and two nonces, then no more");
        }

        [UnityTest]
        public IEnumerator Open_AnAnswerTheRelayDidNotSign_IsNotBelieved()
        {
            _relay.ForgeNextReservation = true;
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            Assert.AreEqual($"{FakeRelayServer.RelayIp}:50000", opened[0].Address.ToString(), "The address the relay signed, not the one that came first");
        }

        [UnityTest]
        public IEnumerator Open_Cancelled_GivesTheReservationBack()
        {
            _relay.HoldReservationAnswers = true;
            CancellationTokenSource cancel = new CancellationTokenSource();
            Task<FlockRelayConnection> opening = Open(A, cancellationToken: cancel.Token);
            yield return FlockTestWait.Until(() => _relay.SeenRequests(FakeRelayServer.MethodAllocate).Any(seen => seen.Signed), "the signed reservation reached the relay");
            Assert.AreEqual(1, _relay.Reservations, "Precondition: the relay holds an address whose answer is on hold");

            cancel.Cancel();
            yield return Done(opening, "the open ended");
            Assert.IsTrue(Cancelled(opening), $"The open ends as cancelled ({opening.Status})");
            yield return FlockTestWait.Until(() => _relay.Reservations == 0, "the address was given back", 2f);
            Assert.AreEqual(1, _relay.Releases);
            _relay.ReleaseHeldAnswers();
        }

        [UnityTest]
        public IEnumerator Open_TheCallerResumesOffTheRelaysThread()
        {
            _relay.HoldReservationAnswers = true;
            // Opened from a thread with no synchronization context, as a game's or the netcode's background work could: nothing
            // posts the rest of the caller back to the main thread, so only the relay's own hand-off keeps it off the relay's thread.
            FlockRelayConnection opened = null;
            string resumedOn = null;
            Task awaiting = Task.Run(async () =>
            {
                opened = await Open(A);
                resumedOn = Thread.CurrentThread.Name ?? "unnamed";
            });
            yield return FlockTestWait.Until(() => _relay.SeenRequests(FakeRelayServer.MethodAllocate).Any(seen => seen.Signed), "the signed reservation reached the relay");
            yield return RealSeconds(0.2f);
            Assert.IsFalse(awaiting.IsCompleted, "Precondition: the caller waits before the answer comes");

            _relay.ReleaseHeldAnswers();
            yield return Done(awaiting, "the caller resumed");
            Assert.IsFalse(awaiting.IsFaulted, awaiting.Exception?.InnerException?.Message);
            _connections.Add(opened);
            Assert.AreNotEqual(FlockRelayConnection.RelayThreadName, resumedOn, "Game code never runs on the relay's thread");
        }

        // ---- packets ----

        [UnityTest]
        public IEnumerator HostAndPlayer_TradePackets_OnChannelsOnceAsked()
        {
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostAndPlayer(pair);
            FlockRelayConnection host = pair[0];
            FlockRelayConnection player = pair[1];
            List<FlockRelayPeer> heard = new List<FlockRelayPeer>();

            yield return Delivered(player, host.Address, host, "hello host", heard);
            Assert.AreEqual(player.Address, heard[0], "The host hears the player from the player's relay address");
            yield return Delivered(host, heard[0], player, "hello player", heard);
            Assert.AreEqual(host.Address, heard[1], "The player hears the host from the host's relay address");
            yield return RealSeconds(0.3f);
            Assert.AreEqual(0, _relay.SeenRequests(FakeRelayServer.MethodChannelBind).Count, "Sending and hearing bind no channel");

            player.KeepChannelTo(host.Address);
            host.KeepChannelTo(player.Address);
            yield return FlockTestWait.Until(() => _relay.SeenRequests(FakeRelayServer.MethodChannelBind).Count >= 2, "each side bound a channel to the other", 3f);
            yield return RealSeconds(0.3f);
            int framesBefore = _relay.Count(0xFFFF, 0);
            for (int i = 0; i < 10; i++)
            {
                yield return Delivered(player, host.Address, host, "to host " + i, heard);
                yield return Delivered(host, player.Address, player, "to player " + i, heard);
            }
            Assert.AreEqual(20, _relay.Count(0xFFFF, 0) - framesBefore, "Once bound, every packet goes in a channel frame");
            Assert.AreEqual(0, host.PacketsDropped + player.PacketsDropped);
        }

        [UnityTest]
        public IEnumerator Send_ToAnAddressNeverOpened_IsRefused()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            FlockRelayConnection connection = opened[0];
            byte[] payload = { 1, 2, 3 };
            FlockRelayPeer.TryParse($"{FakeRelayServer.RelayIp}:50001", out FlockRelayPeer peer);

            Assert.IsFalse(connection.Send(peer, payload, 0, payload.Length), "Nothing goes to an IP never opened");
            Task opening = connection.OpenToHostAsync(connection.Address);
            yield return Done(opening, "opened");
            Assert.IsTrue(connection.Send(peer, payload, 0, payload.Length), "Control: once opened, it goes");
        }

        [UnityTest]
        public IEnumerator Send_EmptyOrOverTheMost_IsRefused()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            FlockRelayConnection connection = opened[0];
            Task opening = connection.OpenToHostAsync(connection.Address);
            yield return Done(opening, "opened");
            byte[] big = new byte[FlockRelayConnection.MostBytesInAPacket + 1];

            Assert.IsFalse(connection.Send(connection.Address, big, 0, 0), "An empty packet");
            Assert.IsFalse(connection.Send(connection.Address, big, 0, big.Length), "One byte over the most");
            Assert.IsTrue(connection.Send(connection.Address, big, 0, FlockRelayConnection.MostBytesInAPacket), "Control: the most");
            Assert.IsFalse(connection.Send(connection.Address, big, 2, big.Length - 1), "A range past the end of the array");
        }

        [UnityTest]
        public IEnumerator Arrived_WhenFull_DropsTheNewest()
        {
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostAndPlayer(pair);
            FlockRelayConnection host = pair[0];
            FlockRelayConnection player = pair[1];
            byte[] packet = new byte[8];
            for (int i = 0; i < 300; i++)
            {
                packet[0] = (byte)(i >> 8);
                packet[1] = (byte)i;
                Assert.IsTrue(player.Send(host.Address, packet, 0, packet.Length));
                if (i % 20 == 19)
                    yield return RealSeconds(0.02f);
            }
            yield return FlockTestWait.Until(() => host.PacketsDropped == 44, $"44 packets past the 256 held were dropped (dropped {host.PacketsDropped})", 5f);

            byte[] buffer = new byte[FlockRelayConnection.MostBytesInAPacket];
            List<int> numbers = new List<int>();
            while (host.TryReceive(buffer, out int count, out FlockRelayPeer from))
                numbers.Add((buffer[0] << 8) | buffer[1]);
            CollectionAssert.AreEqual(Enumerable.Range(0, 256).ToList(), numbers, "The oldest 256, in order");
        }

        [UnityTest]
        public IEnumerator Packets_MakeNoGarbage_OnTheGamesThread()
        {
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostAndPlayer(pair);
            FlockRelayConnection host = pair[0];
            FlockRelayConnection player = pair[1];
            List<FlockRelayPeer> heard = new List<FlockRelayPeer>();
            yield return Delivered(player, host.Address, host, "warm up", heard);
            yield return Delivered(host, player.Address, player, "warm up back", heard);
            player.KeepChannelTo(host.Address);
            host.KeepChannelTo(player.Address);
            yield return FlockTestWait.Until(() => _relay.SeenRequests(FakeRelayServer.MethodChannelBind).Count >= 2, "channels bound", 3f);
            yield return RealSeconds(0.3f);

            // Unity's own count of allocations on this thread, per frame (the count .NET offers reads 0 in Unity's Mono, measured).
            Recorder allocations = Recorder.Get("GC.Alloc");
            Assert.IsTrue(allocations.isValid, "Precondition: the allocation count can be read");
            allocations.FilterToCurrentThread();
            allocations.enabled = true;
            byte[] payload = new byte[200];
            byte[] buffer = new byte[FlockRelayConnection.MostBytesInAPacket];
            int received = 0;
            List<int> quiet = new List<int>();
            List<int> working = new List<int>();
            List<int> allocating = new List<int>();
            yield return null;
            for (int frame = 0; frame < 5; frame++)
            {
                yield return null;
                quiet.Add(allocations.sampleBlockCount);
            }
            for (int frame = 0; frame < 10; frame++)
            {
                for (int i = 0; i < 50; i++)
                {
                    player.Send(host.Address, payload, 0, payload.Length);
                    host.Send(player.Address, payload, 0, payload.Length);
                }
                while (host.TryReceive(buffer, out int count, out FlockRelayPeer from))
                    received++;
                while (player.TryReceive(buffer, out int count, out FlockRelayPeer from))
                    received++;
                yield return null;
                working.Add(allocations.sampleBlockCount);
            }
            List<byte[]> made = new List<byte[]>();
            for (int frame = 0; frame < 3; frame++)
            {
                for (int i = 0; i < 10; i++)
                    made.Add(new byte[100]);
                yield return null;
                allocating.Add(allocations.sampleBlockCount);
            }
            allocations.enabled = false;

            string counts = $"quiet frames {string.Join(",", quiet)}; sending frames {string.Join(",", working)}; allocating frames {string.Join(",", allocating)}";
            Assert.Greater(allocating.Min(), quiet.Max(), $"Control: the count sees {made.Count} allocations ({counts})");
            Assert.LessOrEqual(working.Max(), quiet.Max(), $"Sending and taking 100 packets a frame adds no allocation to the frame ({counts})");
            Assert.Greater(received, 100, "Control: packets were taken while measured");
        }

        // ---- renewals ----

        [UnityTest]
        public IEnumerator Reservation_IsRenewedBeforeItLapses()
        {
            _relay.ReservationLifetime = TimeSpan.FromSeconds(2);
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostAndPlayer(pair);
            yield return RealSeconds(4.5f);

            List<FlockRelayPeer> heard = new List<FlockRelayPeer>();
            yield return Delivered(pair[1], pair[0].Address, pair[0], "after two lifetimes", heard);
            Assert.IsNull(pair[0].FailureReason);
            Assert.GreaterOrEqual(_relay.SeenRequests(FakeRelayServer.MethodRefresh).Count(seen => seen.LifetimeAsked == 600), 6, "Each side renewed at half the 2 s it was granted");
        }

        [UnityTest]
        public IEnumerator Opening_IsRenewedBeforeItLapses()
        {
            _relay.OpeningLifetime = TimeSpan.FromSeconds(2);
            FlockRelayConnection.Timing timing = Quick();
            timing.RenewOpeningsEvery = TimeSpan.FromSeconds(1);
            timing.OpeningLasts = TimeSpan.FromSeconds(2);
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostAndPlayer(pair, timing);
            yield return RealSeconds(4.5f);

            List<FlockRelayPeer> heard = new List<FlockRelayPeer>();
            yield return Delivered(pair[1], pair[0].Address, pair[0], "after two openings' lifetimes", heard);
            Assert.GreaterOrEqual(_relay.SeenRequests(FakeRelayServer.MethodCreatePermission).Count, 8, "Each side's opening renewed every second");
        }

        [UnityTest]
        public IEnumerator Channel_IsRenewedBeforeItLapses()
        {
            _relay.ChannelLifetime = TimeSpan.FromSeconds(2);
            FlockRelayConnection.Timing timing = Quick();
            timing.RenewChannelsEvery = TimeSpan.FromSeconds(1);
            timing.ChannelLasts = TimeSpan.FromSeconds(2);
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostAndPlayer(pair, timing);
            List<FlockRelayPeer> heard = new List<FlockRelayPeer>();
            pair[1].KeepChannelTo(pair[0].Address);
            pair[0].KeepChannelTo(pair[1].Address);
            yield return Delivered(pair[1], pair[0].Address, pair[0], "bind", heard);
            yield return Delivered(pair[0], pair[1].Address, pair[1], "bind back", heard);
            yield return RealSeconds(4.5f);

            int framesBefore = _relay.Count(0xFFFF, 0);
            yield return Delivered(pair[1], pair[0].Address, pair[0], "after two channel lifetimes", heard);
            yield return Delivered(pair[0], pair[1].Address, pair[1], "and back", heard);
            Assert.AreEqual(2, _relay.Count(0xFFFF, 0) - framesBefore, "Both still go in channel frames");
            Assert.GreaterOrEqual(_relay.SeenRequests(FakeRelayServer.MethodChannelBind).Count, 8, "Each channel renewed every second");
        }

        [UnityTest]
        public IEnumerator Renewal_StaleNonce_IsSentAgain_AndTheRelayKept()
        {
            _relay.ReservationLifetime = TimeSpan.FromSeconds(2);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            int renewalsBefore = _relay.SeenRequests(FakeRelayServer.MethodRefresh).Count;
            _relay.AnswerStaleNonce(1);
            yield return RealSeconds(3f);

            Assert.IsNull(opened[0].FailureReason, "The relay is kept");
            Assert.IsTrue(opened[0].IsOpen);
            Assert.GreaterOrEqual(_relay.SeenRequests(FakeRelayServer.MethodRefresh).Count - renewalsBefore, 3, "The renewal went again with the new nonce, and renewals went on");
        }

        [UnityTest]
        public IEnumerator Renewal_Refused_LosesTheRelay_AtOnce()
        {
            // Renewed every half second of a 600 s reservation: only the refusal itself can lose it within the wait.
            FlockRelayConnection.Timing timing = Quick();
            timing.LongestBetweenReservationRenewals = TimeSpan.FromSeconds(0.5);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened, timing);
            _relay.ForgetEveryReservation();

            yield return FlockTestWait.Until(() => opened[0].HasStopped, "the relay was lost", 3f);
            Assert.AreEqual(FlockRelayFailure.Lost, opened[0].FailureReason);
            // The relay's thread says it once it has stopped everything.
            yield return FlockTestWait.Until(() => _logger.Warnings.ToArray().Any(warning => warning.Contains("was lost")), "the loss was said", 2f);
            yield return RealSeconds(0.5f);
            Assert.AreEqual(1, _logger.Warnings.ToArray().Count(warning => warning.Contains("was lost")), "Said once");
            Assert.IsFalse(opened[0].Send(opened[0].Address, new byte[] { 1 }, 0, 1), "Nothing more is sent");
        }

        [UnityTest]
        public IEnumerator Renewal_Unanswered_LosesTheRelay_OnlyOnceItLapses()
        {
            _relay.ReservationLifetime = TimeSpan.FromSeconds(3);
            FlockRelayConnection.Timing timing = Quick();
            timing.AnswerWait = TimeSpan.FromSeconds(0.5);
            timing.RetryRenewalAfter = TimeSpan.FromSeconds(0.3);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened, timing);
            Stopwatch clock = Stopwatch.StartNew();
            _relay.Silent = true;

            yield return RealSeconds(2.3f);
            Assert.IsNull(opened[0].FailureReason, "Renewals unanswered for a while lose nothing while the address lasts");
            yield return FlockTestWait.Until(() => opened[0].HasStopped, "the relay was lost", 3f);
            Assert.AreEqual(FlockRelayFailure.Lost, opened[0].FailureReason);
            Assert.That(clock.Elapsed.TotalSeconds, Is.InRange(2.5, 4.5), "Lost when the 3 s it was granted ran out");
            Assert.GreaterOrEqual(_relay.SeenRequests(FakeRelayServer.MethodRefresh).Count, 2, "A renewal unanswered was tried again");
        }

        [UnityTest]
        public IEnumerator KeepAlive_GoesOutWhenNothingElseDoes()
        {
            FlockRelayConnection.Timing timing = Quick();
            timing.KeepAliveAfter = TimeSpan.FromSeconds(0.5);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened, timing);
            yield return RealSeconds(2.2f);
            Assert.GreaterOrEqual(_relay.SeenRequests(FakeRelayServer.MethodBinding).Count, 3, "A binding request each half second of quiet");
        }

        [UnityTest]
        public IEnumerator Host_BindsNoChannel_ToAPeerItHearsOrAnswers_UntilAsked()
        {
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostAndPlayer(pair);
            List<FlockRelayPeer> heard = new List<FlockRelayPeer>();
            yield return Delivered(pair[1], pair[0].Address, pair[0], "from someone on the relay", heard);
            yield return Delivered(pair[0], heard[0], pair[1], "an answer", heard);
            yield return RealSeconds(0.5f);
            Assert.AreEqual(0, _relay.SeenRequests(FakeRelayServer.MethodChannelBind).Count, "Strangers on the relay get no channel: only a peer known to be a player");

            pair[0].KeepChannelTo(heard[0]);
            yield return FlockTestWait.Until(() => _relay.SeenRequests(FakeRelayServer.MethodChannelBind).Count >= 1, "the host bound a channel once asked", 3f);
            Assert.AreEqual(pair[0].LocalPortForTesting, _relay.SeenRequests(FakeRelayServer.MethodChannelBind)[0].ClientPort, "From the host");
        }

        [UnityTest]
        public IEnumerator Arrivals_HandedOver_ReachTheReceiver_AndAreKeptAgainOnceItStops()
        {
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostAndPlayer(pair);
            List<string> handed = new List<string>();
            Action<FlockRelayPeer, byte[], int, int> receiver = (from, packet, offset, count) =>
            {
                lock (handed)
                    handed.Add(from + " " + System.Text.Encoding.UTF8.GetString(packet, offset, count));
            };
            Action<FlockRelayPeer, byte[], int, int> another = (from, packet, offset, count) => { };
            Assert.IsTrue(pair[0].HandArrivalsTo(receiver));
            Assert.IsFalse(pair[0].HandArrivalsTo(another), "One receiver at a time");
            byte[] packet1 = System.Text.Encoding.UTF8.GetBytes("handed");
            Assert.IsTrue(pair[1].Send(pair[0].Address, packet1, 0, packet1.Length));
            yield return FlockTestWait.Until(() => { lock (handed) return handed.Count == 1; }, "the receiver was handed the packet", 3f);
            Assert.AreEqual(pair[1].Address + " handed", handed[0]);
            Assert.IsFalse(pair[0].TryReceive(new byte[FlockRelayConnection.MostBytesInAPacket], out _, out _), "Nothing kept while handed over");

            pair[0].StopHandingArrivalsTo(another);
            Assert.IsTrue(pair[0].HandsArrivalsOverForTesting, "Only the receiver itself stops it");
            Assert.IsFalse(pair[0].KeepsPacketsForTesting, "No packet store made while every arrival is handed over");
            pair[0].StopHandingArrivalsTo(receiver);
            List<FlockRelayPeer> heard = new List<FlockRelayPeer>();
            yield return Delivered(pair[1], pair[0].Address, pair[0], "kept again", heard);
            Assert.AreEqual(1, handed.Count);
            Assert.IsTrue(pair[0].KeepsPacketsForTesting, "Made when the first packet is kept");
        }

        // ---- opening to the host ----

        [UnityTest]
        public IEnumerator OpenToHost_Refused_FailsOpenRefused_AndKeepsTheAddress()
        {
            _relay.RefusedOpenings.Add(FakeRelayServer.RelayIp);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            Task opening = opened[0].OpenToHostAsync(opened[0].Address);
            yield return Done(opening, "the opening was answered");

            Assert.AreEqual(FlockRelayFailure.OpenRefused, ReasonOf(opening));
            Assert.IsTrue(opened[0].IsOpen, "The relay address stays");
        }

        [UnityTest]
        public IEnumerator OpenToHost_Unanswered_FailsUnreachable()
        {
            _relay.DropNext(FakeRelayServer.MethodCreatePermission, 10);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            Task opening = opened[0].OpenToHostAsync(opened[0].Address);
            yield return Done(opening, "the opening gave up");
            Assert.AreEqual(FlockRelayFailure.Unreachable, ReasonOf(opening));
        }

        // ---- closing ----

        [UnityTest]
        public IEnumerator Close_GivesTheReservationBack()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            Assert.AreEqual(1, _relay.Reservations, "Precondition: reserved");

            opened[0].Close();
            yield return FlockTestWait.Until(() => _relay.Reservations == 0, "the address was given back", 2f);
            Assert.AreEqual(1, _relay.Releases);
            Assert.IsTrue(opened[0].HasStopped);
            Assert.IsNull(opened[0].FailureReason, "A close is no failure");
            Assert.IsFalse(opened[0].Send(opened[0].Address, new byte[] { 1 }, 0, 1));
        }

        [UnityTest]
        public IEnumerator Send_AfterTheRelayStopped_IsRefused_WithoutGarbage()
        {
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostAndPlayer(pair);
            pair[1].Close();
            Recorder allocations = Recorder.Get("GC.Alloc");
            allocations.FilterToCurrentThread();
            allocations.enabled = true;
            byte[] payload = new byte[200];
            List<int> quiet = new List<int>();
            List<int> sending = new List<int>();
            yield return null;
            for (int frame = 0; frame < 5; frame++)
            {
                yield return null;
                quiet.Add(allocations.sampleBlockCount);
            }
            bool anySent = false;
            for (int frame = 0; frame < 5; frame++)
            {
                for (int i = 0; i < 100; i++)
                    anySent |= pair[1].Send(pair[0].Address, payload, 0, payload.Length);
                yield return null;
                sending.Add(allocations.sampleBlockCount);
            }
            allocations.enabled = false;

            Assert.IsFalse(anySent, "Nothing goes once the relay stopped");
            Assert.LessOrEqual(sending.Max(), quiet.Max(), $"A game still sending after the stop costs no garbage (quiet {string.Join(",", quiet)}; sending {string.Join(",", sending)})");
        }

        [UnityTest]
        public IEnumerator Close_WithAStaleNonce_SendsTheReleaseAgain()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            _relay.AnswerStaleNonce(1);
            // Later than one of the relay thread's waits, so only waiting for the release's answer hears the 438.
            _relay.AnswerDelay = TimeSpan.FromMilliseconds(150);

            opened[0].Close();
            yield return FlockTestWait.Until(() => _relay.Reservations == 0, "the address was given back", 2f);
            Assert.AreEqual(2, _relay.Releases, "Refused as stale, then sent again with the new nonce");
        }

        [UnityTest]
        public IEnumerator OpenToHost_ARefusalNamingNoCode_IsARefusal()
        {
            _relay.RefuseOpeningsNamingNoCode = true;
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            Task opening = opened[0].OpenToHostAsync(opened[0].Address);
            yield return Done(opening, "the opening was answered");

            Assert.AreEqual(FlockRelayFailure.Refused, ReasonOf(opening), "Never taken for the success 0 stands for");
            Assert.IsFalse(opened[0].Send(opened[0].Address, new byte[] { 1 }, 0, 1), "Nothing was opened");
        }

        [UnityTest]
        public IEnumerator OpenToHost_AfterClose_SaysClosed()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            opened[0].Close();
            Task opening = opened[0].OpenToHostAsync(opened[0].Address);
            yield return Done(opening, "refused");
            Assert.AreEqual(FlockRelayFailure.Closed, ReasonOf(opening), "Closed, not lost");
        }

        [UnityTest]
        public IEnumerator Sends_NeverHoldTheGamesThread()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            FlockRelaySocket socket = (FlockRelaySocket)typeof(FlockRelayConnection)
                .GetField("_socket", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(opened[0]);
            Assert.IsFalse(socket.BlocksForTesting, "A full send buffer drops a packet rather than holding the sender");
        }

        [UnityTest]
        public IEnumerator Close_EndsACallStillWaiting_AsCancelled()
        {
            _relay.DropNext(FakeRelayServer.MethodCreatePermission, 10);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            Task opening = opened[0].OpenToHostAsync(opened[0].Address);
            yield return RealSeconds(0.2f);
            Assert.IsFalse(opening.IsCompleted, "Precondition: still waiting");

            opened[0].Close();
            yield return Done(opening, "the waiting call ended", 1f);
            Assert.IsTrue(Cancelled(opening), $"Cancelled, not {opening.Status}");
        }

        // ---- through a session ----

        private FlockMultiplayerProvider SignedInAs(string player)
        {
            _server = new FakeSessionServer();
            _server.RelayUrls.Clear();
            _server.RelayUrls.Add(_relay.Url);
            _h = FlockTestClient.Create(new FlockFakeTransport(), config => config.PartyRefreshInterval = TimeSpan.Zero);
            FlockHttpClient.Configure(_server);
            _h.LoginAs(player);
            FlockMultiplayerProvider multiplayer = _h.Client.Multiplayer;
            multiplayer.Sessions.RelayTiming.AnswerWait = TimeSpan.FromSeconds(2);
            return multiplayer;
        }

        private IEnumerator Hosted(FlockMultiplayerProvider multiplayer, List<FlockMultiplayerSession> held)
        {
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "the session was hosted");
            Assert.IsFalse(hosted.IsFaulted, "Precondition: hosted");
            held.Add(hosted.Result);
        }

        [UnityTest]
        public IEnumerator OpenRelay_NamesTheSession_AndReservesWithFlocksLogin()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "the relay opened");
            Assert.IsFalse(opening.IsFaulted, opening.Exception?.InnerException?.Message);
            Assert.AreEqual(held[0].Id, (string)JObject.Parse(_server.RequestsTo(FakeSessionServer.RelayCredentials).Last().JsonBody)["session_id"], "The logins name the session");
            Assert.AreSame(opening.Result, held[0].Relay, "The session holds its relay");
            Assert.AreEqual(1, _relay.Reservations);
        }

        [UnityTest]
        public IEnumerator OpenRelay_TwoCallers_ShareOneReservation()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            Task<FlockRelayConnection> first = held[0].OpenRelayAsync();
            Task<FlockRelayConnection> second = held[0].OpenRelayAsync();
            yield return Done(Task.WhenAll(first, second), "both opened");
            Assert.AreSame(first.Result, second.Result);
            Assert.AreEqual(1, _server.Count(FakeSessionServer.RelayCredentials), "One request for logins");
            Assert.AreEqual(1, _relay.SeenRequests(FakeRelayServer.MethodAllocate).Count(seen => seen.Signed), "One reservation");

            Task<FlockRelayConnection> again = held[0].OpenRelayAsync();
            yield return Done(again, "asked again");
            Assert.AreSame(first.Result, again.Result, "An open relay is handed back as it is");
        }

        [UnityTest]
        public IEnumerator OpenRelayOn_TheHostsServer_ReplacesARelayOpenOnAnother_AndKeepsOneOnIt()
        {
            using (FakeRelayServer hosts = new FakeRelayServer())
            {
                FlockMultiplayerProvider multiplayer = SignedInAs(A);
                _server.RelayUrls.Add(hosts.Url);
                List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
                yield return Hosted(multiplayer, held);
                Task<FlockRelayConnection> anyServer = held[0].OpenRelayAsync();
                yield return Done(anyServer, "opened on the first server Flock lists");
                Assert.AreEqual(1, _relay.Reservations, "Precondition: on the first");

                string hostsServer = "127.0.0.1:" + hosts.Port;
                Task<FlockRelayConnection> onTheHosts = held[0].OpenRelayOnAsync(hostsServer);
                yield return Done(onTheHosts, "opened on the host's server");
                Assert.IsFalse(onTheHosts.IsFaulted, onTheHosts.Exception?.InnerException?.Message);
                Assert.AreEqual(hostsServer, onTheHosts.Result.Server);
                Assert.IsTrue(anyServer.Result.HasStopped, "The relay on the other server made way");
                yield return FlockTestWait.Until(() => _relay.Releases == 1, "and was given back", 3f);

                Task<FlockRelayConnection> again = held[0].OpenRelayOnAsync(hostsServer.ToUpperInvariant());
                yield return Done(again, "asked again");
                Assert.AreSame(onTheHosts.Result, again.Result, "One already on the host's server is handed back as it is");
                Task<FlockRelayConnection> anyAgain = held[0].OpenRelayAsync();
                yield return Done(anyAgain, "asked for any server");
                Assert.AreSame(onTheHosts.Result, anyAgain.Result, "Any server will do for a caller who names none");
                Assert.AreEqual(1, hosts.Reservations);
            }
        }

        [UnityTest]
        public IEnumerator OpenRelay_ACallerThatGivesUp_LeavesTheOpeningToTheOthers()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            _relay.HoldReservationAnswers = true;
            CancellationTokenSource cancel = new CancellationTokenSource();

            Task<FlockRelayConnection> givingUp = held[0].OpenRelayAsync(cancel.Token);
            Task<FlockRelayConnection> staying = held[0].OpenRelayAsync();
            yield return FlockTestWait.Until(() => _relay.SeenRequests(FakeRelayServer.MethodAllocate).Any(seen => seen.Signed), "the reservation reached the relay");
            cancel.Cancel();
            yield return Done(givingUp, "the caller stopped waiting", 1f);
            Assert.IsTrue(Cancelled(givingUp));

            _relay.ReleaseHeldAnswers();
            yield return Done(staying, "the other caller's relay opened");
            Assert.IsFalse(staying.IsFaulted, staying.Exception?.InnerException?.Message);
            Assert.IsTrue(staying.Result.IsOpen);
        }

        [UnityTest]
        public IEnumerator OpenRelay_TheFirstRelayDoesNotAnswer_OpensOnTheNext()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            using (FakeRelayServer silent = new FakeRelayServer { Silent = true })
            {
                _server.RelayUrls.Insert(0, silent.Url);
                multiplayer.Sessions.RelayTiming.AnswerWait = TimeSpan.FromSeconds(1);
                List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
                yield return Hosted(multiplayer, held);

                Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
                yield return Done(opening, "the relay opened");
                Assert.IsFalse(opening.IsFaulted, opening.Exception?.InnerException?.Message);
                Assert.Greater(silent.SeenRequests(FakeRelayServer.MethodAllocate).Count, 0, "Precondition: the first relay was asked");
                Assert.AreEqual(1, _relay.Reservations, "Reserved on the next relay Flock lists");
            }
        }

        [UnityTest]
        public IEnumerator OpenRelay_NoRelayOpens_TellsTheFirstRelaysReason()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            using (FakeRelayServer silent = new FakeRelayServer { Silent = true })
            {
                _server.RelayUrls.Insert(0, silent.Url);
                _relay.Password = "not-what-flock-minted";
                multiplayer.Sessions.RelayTiming.AnswerWait = TimeSpan.FromSeconds(1);
                List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
                yield return Hosted(multiplayer, held);

                Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
                yield return Done(opening, "refused");
                Assert.AreEqual(1, _relay.SeenRequests(FakeRelayServer.MethodAllocate).Count(seen => seen.Signed), "Precondition: the next relay was asked too");
                Assert.AreEqual(FlockRelayFailure.Unreachable, ReasonOf(opening), "Flock's main relay's reason, not the next one's wrong login");
            }
        }

        [UnityTest]
        public IEnumerator OpenRelay_RelayPaused_FailsPaused()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _server.RelayPaused = true;
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "refused");
            Assert.AreEqual(FlockRelayFailure.Paused, ReasonOf(opening));
            Assert.AreEqual(0, _relay.SeenRequests(FakeRelayServer.MethodAllocate).Count, "Nothing asked of the relay");
        }

        [UnityTest]
        public IEnumerator OpenRelay_NoRelayListed_FailsNotOffered()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _server.RelayUrls.Clear();
            _server.RelayUrls.Add("turn:relay.test:3479?transport=tcp");
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "refused");
            Assert.AreEqual(FlockRelayFailure.NotOffered, ReasonOf(opening), "A relay over TCP alone is none this client can use");
        }

        [UnityTest]
        public IEnumerator OpenRelay_TooManyLogins_FailsTooManyLogins()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            _server.AnswerNext(FakeSessionServer.RelayCredentials, FakeSessionServer.Refused(429, "multiplayer.mint_rate_limited"));
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "refused");
            Assert.AreEqual(FlockRelayFailure.TooManyLogins, ReasonOf(opening));
        }

        [UnityTest]
        public IEnumerator OpenRelay_FlockDoesNotAnswer_FailsFlockUnreachable()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            _server.AnswerNext(FakeSessionServer.RelayCredentials, new FlockHttpResponse { Result = FlockHttpResult.ConnectionError });
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "refused");
            Assert.AreEqual(FlockRelayFailure.FlockUnreachable, ReasonOf(opening));
        }

        [UnityTest]
        public IEnumerator OpenRelay_SessionFlockNoLongerSeatsThePlayerIn_FailsNotInSession()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            _server.Ends(_server.ById(held[0].Id));
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "refused");
            Assert.AreEqual(FlockRelayFailure.NotInSession, ReasonOf(opening));
        }

        [UnityTest]
        public IEnumerator OpenRelay_OnAnEndedSession_IsRefused()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            Task leaving = held[0].LeaveAsync();
            yield return Done(leaving, "left");
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "refused");
            Assert.IsTrue(opening.IsFaulted && opening.Exception.InnerException is FlockValidationException, "Refused as for any call on an ended session");
            Assert.AreEqual(0, _server.Count(FakeSessionServer.RelayCredentials), "No logins asked for");
        }

        [UnityTest]
        public IEnumerator SessionEnd_GivesTheRelayBack_BeforeEndedIsRaised()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "the relay opened");
            FlockRelayConnection relay = opening.Result;
            bool? stoppedWhenEnded = null;
            held[0].Ended += reason => stoppedWhenEnded = relay.HasStopped;

            Task ending = held[0].EndAsync();
            yield return Done(ending, "ended");
            Assert.AreEqual(true, stoppedWhenEnded, "The relay was closed before the game heard of the end");
            yield return FlockTestWait.Until(() => _relay.Reservations == 0, "the address was given back", 2f);
            Assert.AreEqual(1, _relay.Releases);
        }

        [UnityTest]
        public IEnumerator SessionEnd_WhileTheRelayOpens_CancelsItAndGivesItBack()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            _relay.HoldReservationAnswers = true;
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return FlockTestWait.Until(() => _relay.SeenRequests(FakeRelayServer.MethodAllocate).Any(seen => seen.Signed), "the reservation reached the relay");

            Task leaving = held[0].LeaveAsync();
            yield return Done(leaving, "left");
            yield return Done(opening, "the opening ended", 2f);
            Assert.IsTrue(Cancelled(opening), $"The opening ends as cancelled, not {opening.Status}");
            yield return FlockTestWait.Until(() => _relay.Reservations == 0, "the address was given back", 2f);
            _relay.ReleaseHeldAnswers();
        }

        [UnityTest]
        public IEnumerator SignOut_GivesTheRelayBack()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "the relay opened");

            _h.Client.Authentication.Logout();
            yield return FlockTestWait.Until(() => opening.Result.HasStopped, "the relay closed once the sign-in ended", 2f);
            Assert.AreEqual(FlockMultiplayerSessionEndReason.SignedOut, held[0].EndReason);
            yield return FlockTestWait.Until(() => _relay.Reservations == 0, "the address was given back", 2f);
        }

        [UnityTest]
        public IEnumerator OpenRelay_AfterTheRelayStopped_OpensANewOne()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            Task<FlockRelayConnection> first = held[0].OpenRelayAsync();
            yield return Done(first, "the relay opened");
            first.Result.Close();

            Task<FlockRelayConnection> second = held[0].OpenRelayAsync();
            yield return Done(second, "a new relay opened");
            Assert.IsFalse(second.IsFaulted, second.Exception?.InnerException?.Message);
            Assert.AreNotSame(first.Result, second.Result);
            Assert.IsTrue(second.Result.IsOpen);
            Assert.AreSame(second.Result, held[0].Relay);
        }
    }
}
