using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The relay over TCP and TLS, for a network that blocks UDP to it, against the relay in memory with its streams on: a reservation,
    // packets both ways with channel frames padded as on Cloudflare's relay (measured 2026-10-25), a request sent once, a stream the relay
    // closes told lost at once, sends that never wait on a stalled stream, and the session trying UDP, then TCP, then TLS.
    public class FlockRelayStreamTests
    {
        private const string A = "player-a";
        private const string B = "player-b";
        private const int EngineSamplesAFrameVaryBy = 50;
        private const int AllocationsAControlFrameMakes = 100;

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
            _relay = new FakeRelayServer(withStreams: true);
            _logger = new RecordingFlockLogger();
        }

        [TearDown]
        public void TearDown()
        {
            FlockRelayStreamSocket.CertificateCheckForTesting = null;
            FlockRelayWays.AfterAWayOpenedForTesting = null;
            FlockRelayRequests.AfterASignedReservationWentForTesting = null;
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

        private static Task<IPAddress[]> Loopback(string host) => Task.FromResult(new[] { IPAddress.Loopback });

        private FlockRelayLogin Login(string player, FlockRelayTransport transport)
        {
            switch (transport)
            {
                case FlockRelayTransport.Tcp: return new FlockRelayLogin("127.0.0.1", _relay.TcpPort, "1760000000:game:" + player, "minted-for-the-test", transport);
                case FlockRelayTransport.Tls: return new FlockRelayLogin("relay.test", _relay.TlsPort, "1760000000:game:" + player, "minted-for-the-test", transport);
                default: return new FlockRelayLogin("127.0.0.1", _relay.Port, "1760000000:game:" + player, "minted-for-the-test");
            }
        }

        private Task<FlockRelayConnection> Open(string player, FlockRelayTransport transport, FlockRelayConnection.Timing timing = null)
        {
            timing = timing ?? new FlockRelayConnection.Timing();
            timing.NameLookUpForTesting = Loopback;
            return FlockRelayConnection.OpenAsync(Login(player, transport), timing, _logger, CancellationToken.None);
        }

        // The test's own check lets the in-memory relay's certificate through, and nothing else.
        private static void TrustTheTestCertificate()
        {
            X509Certificate2 ours = new X509Certificate2(Convert.FromBase64String(FakeRelayCertificate.Pfx), FakeRelayCertificate.Password);
            FlockRelayStreamSocket.CertificateCheckForTesting = (sender, certificate, chain, errors)
                => certificate != null && certificate.GetCertHashString() == ours.GetCertHashString();
        }

        private static IEnumerator Done(Task task, string what, float seconds = 10f) => FlockTestWait.Until(() => task.IsCompleted, what, seconds);

        private static Task<double> SecondsWhenDone(Task task, Stopwatch clock) => task.ContinueWith(_ => clock.Elapsed.TotalSeconds, TaskContinuationOptions.ExecuteSynchronously);

        private static IEnumerator RealSeconds(float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        private static FlockRelayException FailureOf(Task task)
        {
            Assert.IsTrue(task.IsFaulted, $"The call failed (it is {task.Status})");
            FlockRelayException failure = task.Exception.InnerException as FlockRelayException;
            Assert.IsNotNull(failure, $"Failed with a relay reason, not {task.Exception.InnerException?.GetType().Name}: {task.Exception.InnerException?.Message}");
            return failure;
        }

        private IEnumerator Opened(string player, FlockRelayTransport transport, List<FlockRelayConnection> into)
        {
            Task<FlockRelayConnection> opening = Open(player, transport);
            yield return Done(opening, $"{player}'s relay opened over {transport}");
            Assert.IsFalse(opening.IsFaulted, $"Precondition: {player}'s relay opened over {transport} ({opening.Exception?.InnerException?.Message})");
            into.Add(opening.Result);
            _connections.Add(opening.Result);
        }

        // A host over UDP and a player over the transport given, both opened to the host's relay address.
        private IEnumerator HostOnUdpAndPlayerOn(FlockRelayTransport transport, List<FlockRelayConnection> pair)
        {
            yield return Opened(A, FlockRelayTransport.Udp, pair);
            yield return Opened(B, transport, pair);
            Task hostOpen = pair[0].OpenToHostAsync(pair[0].Address);
            Task playerOpen = pair[1].OpenToHostAsync(pair[0].Address);
            yield return Done(Task.WhenAll(hostOpen, playerOpen), "both opened to the host's relay address");
            Assert.IsFalse(hostOpen.IsFaulted || playerOpen.IsFaulted, "Precondition: both opened");
        }

        private static IEnumerator Delivered(FlockRelayConnection from, FlockRelayPeer to, FlockRelayConnection receiver, string text, float seconds = 10f)
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
                        yield break;
                }
                yield return null;
            }
            Assert.Fail($"\"{text}\" did not arrive within {seconds} s");
        }

        // ---- one connection over a stream ----

        [UnityTest]
        public IEnumerator Open_OverTcp_ReservesAnAddress_AfterTheLoginChallenge()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, FlockRelayTransport.Tcp, opened);

            Assert.IsTrue(opened[0].IsOpen);
            Assert.AreEqual(FlockRelayTransport.Tcp, opened[0].Transport);
            Assert.AreEqual($"{FakeRelayServer.RelayIp}:50000", opened[0].Address.ToString(), "The relay address the relay answered");
            Assert.AreEqual(1, _relay.Reservations);
            Assert.AreEqual(1, _relay.StreamConnections, "One connection to the relay");
            Assert.IsTrue(_relay.SeenRequests(FakeRelayServer.MethodAllocate).Any(seen => !seen.Signed), "Challenged first, as over UDP");
        }

        [UnityTest]
        public IEnumerator Open_OverAStream_ARequestGoesOnce_EvenWhileItsAnswerIsSlow()
        {
            // Over UDP the reservation would go again at 0.5 and 1.5 s; the stream delivers it or ends.
            _relay.HoldReservationAnswers = true;
            Task<FlockRelayConnection> opening = Open(A, FlockRelayTransport.Tcp);
            yield return FlockTestWait.Until(() => _relay.SeenRequests(FakeRelayServer.MethodAllocate).Any(seen => seen.Signed), "the signed reservation reached the relay");
            yield return RealSeconds(2f);
            _relay.ReleaseHeldAnswers();
            yield return Done(opening, "the relay opened");
            Assert.IsFalse(opening.IsFaulted, opening.Exception?.InnerException?.Message);
            _connections.Add(opening.Result);

            Assert.AreEqual(1, _relay.SeenRequests(FakeRelayServer.MethodAllocate).Count(seen => seen.Signed), "The signed reservation went once");
        }

        [UnityTest]
        public IEnumerator Packets_GoBothWays_BetweenUdpAndTcp_WithDataNotAMultipleOfFour()
        {
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostOnUdpAndPlayerOn(FlockRelayTransport.Tcp, pair);
            FlockRelayConnection host = pair[0];
            FlockRelayConnection player = pair[1];

            yield return Delivered(player, host.Address, host, "as a send indication, 13");
            yield return Delivered(host, player.Address, player, "as a data indication 1");
            player.KeepChannelTo(host.Address);
            host.KeepChannelTo(player.Address);
            yield return FlockTestWait.Until(() => player.ChannelNumberForTesting(host.Address) != 0 && host.ChannelNumberForTesting(player.Address) != 0, "channels bound");
            for (int i = 0; i < 5; i++)
            {
                // 13 to 17 bytes: every frame on the stream but one needs padding, and the next frame is misread without it.
                yield return Delivered(player, host.Address, host, "on a channel" + new string('!', i + 1));
                yield return Delivered(host, player.Address, player, "on a channel" + new string('?', i + 1));
            }
            Assert.AreEqual(0, _relay.UnreadableStreamBytes, "Every frame the player sent was whole and padded");
            Assert.Greater(_relay.ChannelFramesFrom(player.LocalPortForTesting), 4, "The player's packets went on its channel");
        }

        [UnityTest]
        public IEnumerator Open_OverTls_ThePlatformRefusesACertificateItDoesNotTrust()
        {
            Task<FlockRelayConnection> opening = Open(A, FlockRelayTransport.Tls);
            yield return Done(opening, "the open ended");

            FlockRelayException failure = FailureOf(opening);
            Assert.AreEqual(FlockRelayFailure.Unreachable, failure.Reason);
            StringAssert.Contains("certificate", failure.Message, "Says the certificate was refused");
            StringAssert.Contains("over TLS", failure.Message, "Names the way");
            Assert.AreEqual(0, _relay.Reservations, "Nothing was sent to a relay the platform did not trust");
        }

        [UnityTest]
        public IEnumerator Packets_GoBothWays_BetweenUdpAndTls()
        {
            TrustTheTestCertificate();
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostOnUdpAndPlayerOn(FlockRelayTransport.Tls, pair);
            Assert.AreEqual(FlockRelayTransport.Tls, pair[1].Transport);
            pair[1].KeepChannelTo(pair[0].Address);
            pair[0].KeepChannelTo(pair[1].Address);
            yield return FlockTestWait.Until(() => pair[1].ChannelNumberForTesting(pair[0].Address) != 0 && pair[0].ChannelNumberForTesting(pair[1].Address) != 0, "channels bound");
            yield return Delivered(pair[1], pair[0].Address, pair[0], "over TLS to the host, 25 b");
            yield return Delivered(pair[0], pair[1].Address, pair[1], "back over UDP and TLS, 26 b");
            Assert.AreEqual(0, _relay.UnreadableStreamBytes);
        }

        [UnityTest]
        public IEnumerator Open_OverAStream_ARelayThatTakesTheConnectionAndAnswersNothing_IsGivenUp_WithinItsWaits()
        {
            _relay.StreamsSilent = true;
            Stopwatch clock = Stopwatch.StartNew();
            Task<FlockRelayConnection> opening = Open(A, FlockRelayTransport.Tcp, new FlockRelayConnection.Timing { ConnectWait = TimeSpan.FromSeconds(1), AnswerWait = TimeSpan.FromSeconds(1) });
            Task<double> tcpGaveUpAt = SecondsWhenDone(opening, clock);
            yield return Done(tcpGaveUpAt, "the TCP open gave up");
            Assert.AreEqual(FlockRelayFailure.Unreachable, FailureOf(opening).Reason, "Over TCP, the reservation's own wait");

            clock.Restart();
            Task<FlockRelayConnection> overTls = Open(A, FlockRelayTransport.Tls, new FlockRelayConnection.Timing { ConnectWait = TimeSpan.FromSeconds(1) });
            Task<double> gaveUpAt = SecondsWhenDone(overTls, clock);
            yield return Done(gaveUpAt, "the TLS open gave up");
            Assert.AreEqual(FlockRelayFailure.Unreachable, FailureOf(overTls).Reason);
            Assert.Less(gaveUpAt.Result, 4.0, "Given up at the 1 s connect wait, not left hanging on a handshake that never ends");
            StringAssert.Contains("over TLS", FailureOf(overTls).Message);
        }

        [UnityTest]
        public IEnumerator TheRelayClosingTheStream_IsToldLost_AtOnce()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, FlockRelayTransport.Tcp, opened);
            Stopwatch clock = Stopwatch.StartNew();
            _relay.CloseEveryStream();
            yield return FlockTestWait.Until(() => opened[0].HasStopped, "the relay was told lost", 10f);

            Assert.AreEqual(FlockRelayFailure.Lost, opened[0].FailureReason);
            Assert.Less(clock.Elapsed.TotalSeconds, 5.0, "Told at once, not after the relay's 45 s of silence");
            Assert.IsTrue(_logger.Warnings.Any(line => line.Contains("closed the connection") && line.Contains("Fix:")), "Warned with the cause and the fix");
        }

        [UnityTest]
        public IEnumerator TheRelayResettingTheStream_IsToldLost_InOneLine_ThenItsFix()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, FlockRelayTransport.Tcp, opened);
            _relay.ResetEveryStream();
            yield return FlockTestWait.Until(() => opened[0].HasStopped, "the relay was told lost", 10f);

            Assert.AreEqual(FlockRelayFailure.Lost, opened[0].FailureReason);
            string warning = _logger.Warnings.Single(line => line.Contains("was lost"));
            int fix = warning.IndexOf("\nFix: ", StringComparison.Ordinal);
            Assert.Greater(fix, 0, $"The cause, then the fix ({warning})");
            // The platform's words for a reset end in a line break on Windows; quoted as they are, they cut a player's log line short.
            Assert.IsFalse(warning.Substring(0, fix).Any(char.IsControl), $"The cause is one line ({warning})");
            StringAssert.Contains("broke", warning);
        }

        [UnityTest]
        public IEnumerator Close_OverTcp_GivesTheAddressBack_AndEndsTheConnection()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, FlockRelayTransport.Tcp, opened);
            opened[0].Close();
            yield return FlockTestWait.Until(() => _relay.Releases == 1, "the release reached the relay");
            yield return FlockTestWait.Until(() => _relay.StreamConnections == 0, "the connection was closed");
            Assert.AreEqual(0, _relay.Reservations);
        }

        [UnityTest]
        public IEnumerator Send_OnAStreamTheRelayStoppedReading_NeverWaits_AndDropsWholePackets()
        {
            List<FlockRelayConnection> stalled = new List<FlockRelayConnection>();
            yield return HostOnUdpAndPlayerOn(FlockRelayTransport.Tcp, stalled);
            yield return Delivered(stalled[1], stalled[0].Address, stalled[0], "warm up");
            // The relay stops reading the stream, so what the player sends piles up behind it until the sending room is full.
            _relay.PauseStreamReading = true;
            byte[] payload = new byte[FlockRelayConnection.MostBytesInAPacket];
            Stopwatch longest = new Stopwatch();
            double slowest = 0;
            int dropped = 0;
            // Until a packet is dropped and 100 more after it: how much the system buffers before the room fills differs by platform
            // (a Linux runner holds megabytes on loopback); 40 MB in all at most.
            for (int i = 0; i < 30000 && dropped < 100; i++)
            {
                longest.Restart();
                if (!stalled[1].Send(stalled[0].Address, payload, 0, payload.Length))
                    dropped++;
                slowest = Math.Max(slowest, longest.Elapsed.TotalMilliseconds);
            }
            _relay.PauseStreamReading = false;

            // A send that waited would wait until the relay read again, which it does not while paused; 500 ms leaves room for a busy runner.
            Assert.Less(slowest, 500.0, "No send waited on the stalled stream");
            Assert.Greater(dropped, 0, "Once the room was full, whole packets were dropped");
            yield return RealSeconds(1f);
            Assert.AreEqual(0, _relay.UnreadableStreamBytes, "What did go went whole");
        }

        [UnityTest]
        public IEnumerator Packets_OverTcp_MakeNoGarbage_OnTheGamesThread()
        {
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return HostOnUdpAndPlayerOn(FlockRelayTransport.Tcp, pair);
            FlockRelayConnection host = pair[0];
            FlockRelayConnection player = pair[1];
            yield return Delivered(player, host.Address, host, "warm up");
            yield return Delivered(host, player.Address, player, "warm up back");
            player.KeepChannelTo(host.Address);
            host.KeepChannelTo(player.Address);
            yield return FlockTestWait.Until(() => player.ChannelNumberForTesting(host.Address) != 0 && host.ChannelNumberForTesting(player.Address) != 0, "channels bound");

            Recorder allocations = Recorder.Get("GC.Alloc");
            Assert.IsTrue(allocations.isValid, "Precondition: the allocation count can be read");
            allocations.FilterToCurrentThread();
            allocations.enabled = true;
            byte[] payload = new byte[201];
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
                for (int i = 0; i < AllocationsAControlFrameMakes; i++)
                    made.Add(new byte[100]);
                yield return null;
                allocating.Add(allocations.sampleBlockCount);
            }
            allocations.enabled = false;

            string counts = $"quiet frames {string.Join(",", quiet)}; sending frames {string.Join(",", working)}; allocating frames {string.Join(",", allocating)}";
            Assert.Greater(allocating.Min(), quiet.Max() + EngineSamplesAFrameVaryBy, $"Control: the count sees {made.Count} allocations ({counts})");
            Assert.LessOrEqual(working.OrderByDescending(count => count).ElementAt(1), quiet.Max() + EngineSamplesAFrameVaryBy, $"Sending over TCP and taking 100 packets a frame adds no allocation ({counts})");
            Assert.Greater(received, 100, "Control: packets were taken while measured");
        }

        private static bool Cancelled(Task task) => task.IsCanceled || (task.IsFaulted && task.Exception.InnerException is OperationCanceledException);

        [UnityTest]
        public IEnumerator Ways_TheSessionEndingAsAWayOpens_GivesThatWayBack()
        {
            CancellationTokenSource sessionStop = new CancellationTokenSource();
            FlockRelayWays.AfterAWayOpenedForTesting = () => sessionStop.Cancel();
            FlockRelayConnection.Timing timing = new FlockRelayConnection.Timing { NameLookUpForTesting = Loopback };
            Task<FlockRelayConnection> opening = FlockRelayWays.OpenFirstAsync(new[] { Login(A, FlockRelayTransport.Tcp) }, timing, _logger, sessionStop.Token);
            yield return Done(opening, "the race ended");

            Assert.IsTrue(Cancelled(opening), $"Ended as cancelled, not {opening.Status}");
            // Left open, nobody would hold it, and its reservation would be renewed for as long as the game ran.
            yield return FlockTestWait.Until(() => _relay.Releases >= 1 && _relay.Reservations == 0, "the way that opened was given back");
        }

        [UnityTest]
        public IEnumerator Ways_ASessionThatEndsWhileAWayOpens_StartsNoOtherWay()
        {
            _relay.IgnoresUdp = true;
            CancellationTokenSource sessionStop = new CancellationTokenSource();
            FlockRelayConnection.Timing timing = new FlockRelayConnection.Timing { NameLookUpForTesting = Loopback, SocketRoundWait = TimeSpan.FromSeconds(0.2) };
            Task<FlockRelayConnection> opening = FlockRelayWays.OpenFirstAsync(new[] { Login(A, FlockRelayTransport.Udp), Login(A, FlockRelayTransport.Tcp) }, timing, _logger, sessionStop.Token);
            yield return RealSeconds(0.3f);
            Assert.IsFalse(opening.IsCompleted, "Precondition: UDP is still trying");
            sessionStop.Cancel();
            yield return Done(opening, "the race ended");

            Assert.IsTrue(Cancelled(opening), $"Ended as cancelled, not {opening.Status}");
            yield return RealSeconds(1f);
            Assert.AreEqual(0, _relay.StreamsAccepted, "The UDP way ended by the session's end did not start the TCP way");
        }

        // ---- the session's ways in ----

        private FlockMultiplayerProvider SignedInAs(string player, params string[] relayUrls)
        {
            _server = new FakeSessionServer();
            _server.RelayUrls.Clear();
            _server.RelayUrls.AddRange(relayUrls);
            _h = FlockTestClient.Create(new FlockFakeTransport(), config => config.PartyRefreshInterval = TimeSpan.Zero);
            FlockHttpClient.Configure(_server);
            _h.LoginAs(player);
            _h.Client.Multiplayer.Sessions.RelayTiming.NameLookUpForTesting = Loopback;
            return _h.Client.Multiplayer;
        }

        private IEnumerator Hosted(FlockMultiplayerProvider multiplayer, List<FlockMultiplayerSession> held)
        {
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "the session was hosted");
            Assert.IsFalse(hosted.IsFaulted, "Precondition: hosted");
            held.Add(hosted.Result);
        }

        [UnityTest]
        public IEnumerator OpenRelay_UdpBlocked_OpensOverTcp_AfterTheNextWayWait_NotUdpsOwnWaits()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, _relay.Url, _relay.TcpUrl);
            _relay.IgnoresUdp = true;
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            Stopwatch clock = Stopwatch.StartNew();
            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            Task<double> openedAt = SecondsWhenDone(opening, clock);
            yield return Done(openedAt, "the relay opened", 15f);
            Assert.IsFalse(opening.IsFaulted, opening.Exception?.InnerException?.Message);

            Assert.AreEqual(FlockRelayTransport.Tcp, opening.Result.Transport);
            Assert.That(openedAt.Result, Is.InRange(1.8, 4.5), "Opened over TCP once UDP had had its 2 s, not after UDP's three socket rounds and 5 s answer wait");
            Assert.IsTrue(_h.Logger.Infos.Any(line => line.Contains("over TCP") && line.Contains("blocks UDP")), "Said why the relay is reached over TCP");
            yield return RealSeconds(1f);
            Assert.AreEqual(1, _relay.Reservations, "One reservation, the one kept");
        }

        [UnityTest]
        public IEnumerator OpenRelay_UdpAnswers_NoStreamIsEverOpened()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, _relay.TlsUrl, _relay.TcpUrl, _relay.Url);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "the relay opened");
            Assert.IsFalse(opening.IsFaulted, opening.Exception?.InnerException?.Message);
            Assert.AreEqual(FlockRelayTransport.Udp, opening.Result.Transport, "UDP first, wherever Flock lists it");
            yield return RealSeconds(2.5f);
            Assert.AreEqual(0, _relay.StreamConnections, "No TCP or TLS connection was made while UDP worked");
            Assert.AreEqual(1, _relay.Reservations);
        }

        [UnityTest]
        public IEnumerator OpenRelay_AWayStillOpeningWhenAnotherIsKept_GivesItsAddressBack()
        {
            // The UDP way's signed reservation is held unanswered, so TCP opens first; the held answer is let go afterwards.
            using (FakeRelayServer slow = new FakeRelayServer { HoldReservationAnswers = true })
            {
                FlockMultiplayerProvider multiplayer = SignedInAs(A, slow.Url, _relay.TcpUrl);
                List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
                yield return Hosted(multiplayer, held);

                Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
                yield return Done(opening, "the relay opened", 15f);
                Assert.IsFalse(opening.IsFaulted, opening.Exception?.InnerException?.Message);
                Assert.AreEqual(FlockRelayTransport.Tcp, opening.Result.Transport, "Precondition: TCP opened while the UDP way waited for its answer");
                Assert.Greater(slow.SeenRequests(FakeRelayServer.MethodAllocate).Count(seen => seen.Signed), 0, "Precondition: the UDP way had asked for a reservation");
                yield return RealSeconds(0.5f);
                // Left open, the UDP way would now open on this answer and keep its address, renewed, for the whole session.
                slow.ReleaseHeldAnswers();
                yield return RealSeconds(2f);
                Assert.AreEqual(0, slow.Reservations, "The UDP way's address was given back");
                Assert.GreaterOrEqual(slow.Releases, 1, "By its release");
                Assert.AreEqual(1, _relay.Reservations, "The kept one stays");
                Assert.IsTrue(opening.Result.IsOpen);
            }
        }

        [UnityTest]
        public IEnumerator OpenRelayOn_TheHostsServer_ByAnotherWayIn()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B, _relay.Url, _relay.TcpUrl);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            _relay.IgnoresUdp = true;

            // The host reserved over UDP and published that way; this player's network blocks UDP.
            Task<FlockRelayConnection> opening = held[0].OpenRelayOnAsync("127.0.0.1:" + _relay.Port);
            yield return Done(opening, "the relay opened on the host's server", 15f);
            Assert.IsFalse(opening.IsFaulted, opening.Exception?.InnerException?.Message);
            Assert.AreEqual(FlockRelayTransport.Tcp, opening.Result.Transport, "On the host's server, over TCP");

            Task<FlockRelayConnection> again = held[0].OpenRelayOnAsync("127.0.0.1:" + _relay.TlsPort);
            yield return Done(again, "asked again, naming another port of the same server");
            Assert.AreSame(opening.Result, again.Result, "The relay already on that server is handed back, whatever way it came in by");
            Assert.AreEqual(1, _relay.Reservations);
        }

        [UnityTest]
        public IEnumerator OpenRelay_NoWayOpens_TellsEveryWay_AndTheReasonOfAWayThatReachedTheServer()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, _relay.Url, _relay.TcpUrl);
            multiplayer.Sessions.RelayTiming.SocketRoundWait = TimeSpan.FromSeconds(0.2);
            multiplayer.Sessions.RelayTiming.AnswerWait = TimeSpan.FromSeconds(2);
            _relay.IgnoresUdp = true;
            _relay.Password = "not-what-flock-minted";
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            Task<FlockRelayConnection> opening = held[0].OpenRelayAsync();
            yield return Done(opening, "refused", 15f);
            FlockRelayException failure = FailureOf(opening);
            Assert.AreEqual(FlockRelayFailure.WrongLogin, failure.Reason, "The server answered over TCP: its refusal says more than UDP's silence");
            StringAssert.Contains("over UDP", failure.Message);
            StringAssert.Contains("over TCP", failure.Message);
            Assert.AreEqual(1, failure.Message.Split(new[] { "Fix:" }, StringSplitOptions.None).Length - 1, $"One fix, the told reason's, after every way's cause ({failure.Message})");
            Assert.AreEqual(1, failure.Message.Split(new[] { "Open the relay failed" }, StringSplitOptions.None).Length - 1, $"The operation named once ({failure.Message})");
            StringAssert.DoesNotContain("\n", failure.Message.Substring(0, failure.Message.IndexOf("Fix:", StringComparison.Ordinal) - 1), "Every way's cause on one line, before the fix");
            Assert.AreEqual(FlockRelayFailure.FixFor(FlockRelayFailure.WrongLogin), failure.Hint, "The fix for the reason told");
        }
    }
}
