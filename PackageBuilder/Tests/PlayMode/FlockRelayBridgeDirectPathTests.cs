using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // Two bridges that go direct when they can, on real loopback sockets with a relay in memory (which, like coturn and Cloudflare's, answers
    // a STUN request with the address it saw it from): the netcode's packets move off the relay once a probe is answered, and back when the
    // direct path goes quiet; a host offers its addresses only to players its netcode let in, and only probes carrying both keys count.
    public partial class FlockRelayBridgeTests
    {
        private static FlockDirectLink.Timing Quick() => new FlockDirectLink.Timing
        {
            Tick = TimeSpan.FromMilliseconds(20),
            AskAddressEvery = TimeSpan.FromMilliseconds(100),
            AddressAsks = 2,
            OfferEvery = TimeSpan.FromMilliseconds(200),
            OfferFor = TimeSpan.FromSeconds(3),
            ProbeEvery = TimeSpan.FromMilliseconds(50),
            ProbeFor = TimeSpan.FromSeconds(1.5),
            KeepAliveEvery = TimeSpan.FromMilliseconds(200),
            QuietFor = TimeSpan.FromMilliseconds(800),
        };

        private int RelayedFrom(FlockRelayConnection relay) => _relay.PassedOnFrom(relay.LocalPortForTesting);

        private sealed class Pair
        {
            public readonly List<FlockRelayConnection> Relays = new List<FlockRelayConnection>();
            public FlockRelayBridge Host;
            public FlockRelayBridge Player;
            public StandInNetcode HostNetcode;
            public StandInNetcode PlayerNetcode;
            // The loopback port the host's netcode hears the player from.
            public int PlayersPortAtTheHost;

            public FlockRelayPeer PlayersAddress => Relays[1].Address;
            public FlockRelayPeer HostsAddress => Relays[0].Address;
            public FlockDirectLink HostsLink => Host.DirectLinkForTesting(PlayersAddress);
            public FlockDirectLink PlayersLink => Player.DirectLinkForTesting(HostsAddress);
        }

        // A host's bridge and a player's on one relay, the player's netcode heard by the host's; the host lets it in unless told not to.
        private IEnumerator Paired(Pair pair, bool hostGoesDirect = true, bool playerGoesDirect = true, bool letIn = true, FlockDirectLink.Timing timing = null)
        {
            FlockDirectLink.TimingForTesting = timing ?? Quick();
            yield return Opened(Host, pair.Relays);
            yield return Opened(Player, pair.Relays, () => pair.Relays[0].Address);
            pair.HostNetcode = Netcode();
            pair.PlayerNetcode = Netcode();
            pair.Host = Kept(FlockRelayBridge.ForTheHost(pair.Relays[0], pair.HostNetcode.Port, _logger, hostGoesDirect));
            pair.Player = Kept(FlockRelayBridge.ToTheHost(pair.Relays[1], pair.HostsAddress, _logger, playerGoesDirect));
            pair.PlayerNetcode.SendTo(pair.Player.LocalPort, "hello");
            List<KeyValuePair<string, int>> heard = new List<KeyValuePair<string, int>>();
            yield return Heard(pair.HostNetcode, "hello", heard);
            pair.PlayersPortAtTheHost = heard.First(packet => packet.Key == "hello").Value;
            if (letIn)
                pair.Host.LetIn(pair.PlayersAddress);
        }

        private static IEnumerator BothDirect(Pair pair, float seconds = 5f)
        {
            yield return FlockTestWait.Until(() => pair.HostsLink != null && pair.HostsLink.IsDirect && pair.PlayersLink != null && pair.PlayersLink.IsDirect,
                "both ends went direct", seconds);
        }

        // One packet each way between the two netcodes, judged by what they hear and from where.
        private static IEnumerator OneEachWay(Pair pair, string word)
        {
            List<KeyValuePair<string, int>> atTheHost = new List<KeyValuePair<string, int>>();
            pair.PlayerNetcode.SendTo(pair.Player.LocalPort, "to-host-" + word);
            yield return Heard(pair.HostNetcode, "to-host-" + word, atTheHost);
            Assert.AreEqual(pair.PlayersPortAtTheHost, atTheHost.First(packet => packet.Key == "to-host-" + word).Value, "The host's netcode hears the player from the port it always has");
            List<KeyValuePair<string, int>> atThePlayer = new List<KeyValuePair<string, int>>();
            pair.HostNetcode.SendTo(pair.PlayersPortAtTheHost, "to-player-" + word);
            yield return Heard(pair.PlayerNetcode, "to-player-" + word, atThePlayer);
            Assert.AreEqual(pair.Player.LocalPort, atThePlayer.First(packet => packet.Key == "to-player-" + word).Value, "The player's netcode hears the host from the port it connected to");
        }

        private static bool InfoLogged(RecordingFlockLogger logger, string fragment) => logger.Logged(logger.Infos, fragment);

        [UnityTest]
        public IEnumerator TwoBridgesThatReachEachOther_MoveTheNetcodesPacketsToADirectPath()
        {
            Pair pair = new Pair();
            yield return Paired(pair);
            yield return BothDirect(pair);
            int hostRelayed = RelayedFrom(pair.Relays[0]);
            int playerRelayed = RelayedFrom(pair.Relays[1]);

            yield return OneEachWay(pair, "direct");
            yield return OneEachWay(pair, "direct-again");
            Assert.AreEqual(playerRelayed, RelayedFrom(pair.Relays[1]), "The player's packets no longer go through the relay");
            Assert.AreEqual(hostRelayed, RelayedFrom(pair.Relays[0]), "The host's packets no longer go through the relay");
            Assert.IsTrue(InfoLogged(_logger, "now go directly"), "Saying so in the log");
            Assert.AreEqual(RelayedFrom(pair.Relays[1]), pair.Relays[1].PacketsSent, "The relay client counts what it sent on as the relay saw it");
            Assert.AreEqual(RelayedFrom(pair.Relays[0]), pair.Relays[0].PacketsSent);
        }

        [UnityTest]
        public IEnumerator AHost_OffersNothing_ToAPlayerItsNetcodeHasNotLetIn()
        {
            Pair pair = new Pair();
            yield return Paired(pair, letIn: false);
            yield return FlockTestWait.Until(() => pair.PlayersLink.HasStopped, "the player gave up waiting for the host's offer", 6f);

            Assert.IsNull(pair.HostsLink, "No link to a player the netcode never let in");
            Assert.IsFalse(pair.PlayersLink.IsDirect);
            Assert.IsTrue(InfoLogged(_logger, "never offered its addresses"), "The player says why it stays on the relay");
            yield return OneEachWay(pair, "relayed");
        }

        [UnityTest]
        public IEnumerator APlayerWhoOfferedBeforeItWasLetIn_GoesDirectOnceLetIn()
        {
            Pair pair = new Pair();
            yield return Paired(pair, letIn: false);
            // The player offers while the host's netcode checks it, and none of it is taken.
            yield return RealSeconds(0.6f);
            Assert.IsNull(pair.HostsLink, "Precondition: nothing yet for a player not let in");

            pair.Host.LetIn(pair.PlayersAddress);
            yield return BothDirect(pair);
            yield return OneEachWay(pair, "after-let-in");
        }

        [UnityTest]
        public IEnumerator APlayerKeptOnTheRelay_OffersNothing_AndItsHostStaysOnTheRelay()
        {
            Pair pair = new Pair();
            yield return Paired(pair, playerGoesDirect: false);
            yield return FlockTestWait.Until(() => pair.HostsLink != null && pair.HostsLink.HasStopped, "the host gave up waiting for the player's offer", 6f);

            Assert.IsNull(pair.PlayersLink, "A player on the relay only (FlockRelayUse.Always) makes no link");
            Assert.IsFalse(pair.HostsLink.IsDirect);
            yield return OneEachWay(pair, "relayed");
        }

        [UnityTest]
        public IEnumerator TheBridgesOwnMessages_NeverReachTheNetcode()
        {
            Pair pair = new Pair();
            yield return Paired(pair, letIn: false);
            byte[] offer = FlockDirectLinkWire.WriteOffer(new byte[FlockDirectLinkWire.KeyLength], null, new List<IPEndPoint> { new IPEndPoint(IPAddress.Loopback, 9) });
            Assert.IsTrue(pair.Relays[0].Send(pair.PlayersAddress, offer, 0, offer.Length), "Precondition: an offer sent to the player through the relay");
            byte[] probe = FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.Probe, new byte[FlockDirectLinkWire.KeyLength], new byte[FlockDirectLinkWire.KeyLength]);
            Assert.IsTrue(pair.Relays[1].Send(pair.HostsAddress, probe, 0, probe.Length), "Precondition: a probe sent to the host through the relay");
            SendThrough(pair.Relays[0], pair.PlayersAddress, "control");
            SendThrough(pair.Relays[1], pair.HostsAddress, "control");

            List<KeyValuePair<string, int>> atThePlayer = new List<KeyValuePair<string, int>>();
            yield return Heard(pair.PlayerNetcode, "control", atThePlayer);
            List<KeyValuePair<string, int>> atTheHost = new List<KeyValuePair<string, int>>();
            yield return Heard(pair.HostNetcode, "control", atTheHost);
            Assert.AreEqual(1, atThePlayer.Count, "The player's netcode heard only the plain packet");
            Assert.AreEqual(1, atTheHost.Count, "The host's netcode heard only the plain packet");
        }

        [UnityTest]
        public IEnumerator AProbeOrAPacketFromAnyoneElse_OnTheDirectSocket_IsDropped()
        {
            Pair pair = new Pair();
            yield return Paired(pair, letIn: false);
            int linkPort = pair.PlayersLink.LocalPortForTesting;
            using (UdpClient stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                byte[] probe = FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.Probe, new byte[FlockDirectLinkWire.KeyLength], new byte[FlockDirectLinkWire.KeyLength]);
                byte[] answer = FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.Answer, new byte[FlockDirectLinkWire.KeyLength], new byte[FlockDirectLinkWire.KeyLength]);
                byte[] packet = Encoding.UTF8.GetBytes("from-a-stranger");
                for (int i = 0; i < 5; i++)
                {
                    stranger.Send(probe, probe.Length, new IPEndPoint(IPAddress.Loopback, linkPort));
                    stranger.Send(answer, answer.Length, new IPEndPoint(IPAddress.Loopback, linkPort));
                    stranger.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, linkPort));
                }
                yield return RealSeconds(0.5f);
                Assert.IsFalse(pair.PlayersLink.IsDirect, "Probes and answers without the keys open nothing");
                Assert.IsFalse(pair.PlayerNetcode.Received().Any(each => each.Key == "from-a-stranger"), "A stranger's packet never reaches the netcode");
                Assert.AreEqual(0, stranger.Available, "Nothing was answered to the stranger");
            }
        }

        [UnityTest]
        public IEnumerator WhileTheEndsProbe_AStrangersProbeNamingOtherKeys_OpensNothing()
        {
            Pair pair = new Pair();
            FlockDirectLink.Timing probingLonger = Quick();
            probingLonger.ProbeFor = TimeSpan.FromSeconds(5);
            yield return Paired(pair, letIn: false, timing: probingLonger);
            pair.PlayersLink.DropsAnswersForTesting = true;
            pair.Host.LetIn(pair.PlayersAddress);
            pair.HostsLink.DropsAnswersForTesting = true;
            yield return FlockTestWait.Until(() => pair.PlayersLink.IsProbingForTesting, "the player knows the host's key and probes", 3f);
            int linkPort = pair.PlayersLink.LocalPortForTesting;
            using (UdpClient stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                byte[] otherKey = { 9, 9, 9, 9, 9, 9, 9, 9 };
                byte[] probe = FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.Probe, otherKey, otherKey);
                byte[] packet = Encoding.UTF8.GetBytes("from-a-stranger");
                for (int i = 0; i < 5; i++)
                {
                    stranger.Send(probe, probe.Length, new IPEndPoint(IPAddress.Loopback, linkPort));
                    stranger.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, linkPort));
                }
                yield return RealSeconds(0.3f);
                Assert.IsTrue(pair.PlayersLink.IsProbingForTesting, "Precondition: still probing");
                Assert.AreEqual(0, stranger.Available, "Nothing was answered to the stranger");
                Assert.IsFalse(pair.PlayerNetcode.Received().Any(each => each.Key == "from-a-stranger"), "A stranger's packet never reaches the netcode");
            }
        }

        [UnityTest]
        public IEnumerator ADirectPathTheNetcodeLeavesQuiet_StaysDirect_OnTheLinksKeepAlives()
        {
            Pair pair = new Pair();
            yield return Paired(pair);
            yield return BothDirect(pair);
            // Twice the quiet time and more, with nothing from either netcode.
            yield return RealSeconds(2f);

            Assert.IsTrue(pair.HostsLink.IsDirect && pair.PlayersLink.IsDirect);
        }

        [UnityTest]
        public IEnumerator WordOfGoingBackNamingOtherKeys_LeavesTheDirectPathAsItIs()
        {
            Pair pair = new Pair();
            yield return Paired(pair);
            yield return BothDirect(pair);
            // As an earlier link to the same player, made before a restart, would have sent it.
            byte[] otherKey = { 9, 9, 9, 9, 9, 9, 9, 9 };
            byte[] stale = FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.BackToTheRelay, otherKey, otherKey);
            Assert.IsTrue(pair.Relays[1].Send(pair.HostsAddress, stale, 0, stale.Length), "Precondition: sent to the host through the relay");
            yield return RealSeconds(0.3f);

            Assert.IsTrue(pair.HostsLink.IsDirect, "Still direct");
            int hostRelayed = RelayedFrom(pair.Relays[0]);
            yield return OneEachWay(pair, "still-direct");
            Assert.AreEqual(hostRelayed, RelayedFrom(pair.Relays[0]));
        }

        [UnityTest]
        public IEnumerator ADirectPathThatGoesQuiet_GoesBackToTheRelay_AndTheOtherEndFollows()
        {
            Pair pair = new Pair();
            yield return Paired(pair);
            yield return BothDirect(pair);
            pair.HostsLink.HearsNothingForTesting = true;

            yield return FlockTestWait.Until(() => pair.HostsLink.HasStopped && pair.PlayersLink.HasStopped, "both went back to the relay", 4f);
            Assert.IsTrue(InfoLogged(_logger, "went quiet"), "The end that heard nothing says so");
            Assert.IsTrue(InfoLogged(_logger, "went back to Flock's relay"), "The other end follows when told");
            int playerRelayed = RelayedFrom(pair.Relays[1]);
            yield return OneEachWay(pair, "back-on-the-relay");
            Assert.Greater(RelayedFrom(pair.Relays[1]), playerRelayed, "The player's packets go through the relay again");
        }

        [UnityTest]
        public IEnumerator AnswersThatNeverComeBack_LeaveThePairOnTheRelay()
        {
            Pair pair = new Pair();
            yield return Paired(pair, letIn: false);
            pair.PlayersLink.DropsAnswersForTesting = true;
            pair.Host.LetIn(pair.PlayersAddress);
            pair.HostsLink.DropsAnswersForTesting = true;

            yield return FlockTestWait.Until(() => pair.HostsLink.HasStopped && pair.PlayersLink.HasStopped, "both gave up probing", 6f);
            Assert.IsFalse(pair.HostsLink.IsDirect || pair.PlayersLink.IsDirect, "A probe heard one way only is not a direct path");
            Assert.IsTrue(InfoLogged(_logger, "no probe was answered"));
            // The player's hello and a few offers each: none once each end holds the other's key, though they probed for a while.
            Assert.Less(RelayedFrom(pair.Relays[1]), 8, "The player's offers stopped");
            Assert.Less(RelayedFrom(pair.Relays[0]), 8, "The host's offers stopped");
            yield return OneEachWay(pair, "relayed");
        }

        [UnityTest]
        public IEnumerator EndsBehindRoutersThatDropTheUnasked_StillOpenTheDirectPath_BecauseBothProbe()
        {
            Pair pair = new Pair();
            yield return Paired(pair, letIn: false);
            pair.PlayersLink.FiltersLikeARouterForTesting = true;
            pair.Host.LetIn(pair.PlayersAddress);
            pair.HostsLink.FiltersLikeARouterForTesting = true;

            yield return BothDirect(pair);
            yield return OneEachWay(pair, "through-routers");
        }

        [UnityTest]
        public IEnumerator AnEndReachedOnlyAtTheAddressItsProbesCameFrom_StillGoesDirect()
        {
            // The player's router gives each destination a port of its own, so the address it offers reaches nothing.
            FlockDirectLink.NextLinkOffersForTesting = new List<IPEndPoint> { new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9) };
            Pair pair = new Pair();
            yield return Paired(pair);

            yield return BothDirect(pair);
            yield return OneEachWay(pair, "where-the-probes-came-from");
        }

        [UnityTest]
        public IEnumerator APlayerLetInAgain_ThroughANewBridge_GetsANewLink_AndGoesDirect()
        {
            Pair pair = new Pair();
            yield return Paired(pair);
            yield return BothDirect(pair);
            FlockDirectLink first = pair.HostsLink;

            // The player's netcode starts again on the same relay address: a new bridge, so a new link with a new key.
            pair.Player.Close();
            pair.Player = Kept(FlockRelayBridge.ToTheHost(pair.Relays[1], pair.HostsAddress, _logger, true));
            pair.PlayerNetcode.SendTo(pair.Player.LocalPort, "hello-again");
            yield return Heard(pair.HostNetcode, "hello-again", new List<KeyValuePair<string, int>>());
            pair.Host.LetIn(pair.PlayersAddress);

            Assert.IsTrue(first.HasStopped, "The link to the player's earlier bridge is let go");
            yield return BothDirect(pair);
            yield return OneEachWay(pair, "after-the-restart");
        }

        [UnityTest]
        public IEnumerator Closing_ClosesEveryLinksSocket()
        {
            Pair pair = new Pair();
            yield return Paired(pair);
            yield return BothDirect(pair);
            FlockDirectLink hosts = pair.HostsLink;
            FlockDirectLink players = pair.PlayersLink;
            int hostsPort = hosts.LocalPortForTesting;

            pair.Host.Close();
            pair.Player.Close();
            Assert.IsTrue(hosts.HasStopped && players.HasStopped);
            yield return RealSeconds(0.2f);
            using (Socket again = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                Assert.DoesNotThrow(() => again.Bind(new IPEndPoint(IPAddress.Any, hostsPort)), "The host's link let its port go");
        }

        [UnityTest]
        public IEnumerator AHostThatForgetsAPlayer_ClosesItsLink()
        {
            Pair pair = new Pair();
            yield return Paired(pair);
            yield return BothDirect(pair);
            FlockDirectLink link = pair.HostsLink;

            pair.Host.Forget(pair.PlayersAddress);
            Assert.IsTrue(link.HasStopped);
            Assert.IsNull(pair.HostsLink, "The forgotten player has no link any more");
        }

        // ---- the bridges' own messages ----

        [Test]
        public void AnOffer_ReadsBackAsWritten()
        {
            byte[] key = { 1, 2, 3, 4, 5, 6, 7, 8 };
            byte[] receivers = { 9, 10, 11, 12, 13, 14, 15, 16 };
            List<IPEndPoint> addresses = new List<IPEndPoint> { new IPEndPoint(IPAddress.Parse("203.0.113.9"), 61000), new IPEndPoint(IPAddress.Parse("192.168.1.4"), 5000) };
            byte[] offer = FlockDirectLinkWire.WriteOffer(key, receivers, addresses);

            Assert.IsTrue(FlockDirectLinkWire.TryReadOffer(offer, 0, offer.Length, out byte[] readKey, out byte[] readReceivers, out List<IPEndPoint> readAddresses));
            CollectionAssert.AreEqual(key, readKey);
            CollectionAssert.AreEqual(receivers, readReceivers);
            CollectionAssert.AreEqual(addresses, readAddresses);

            byte[] first = FlockDirectLinkWire.WriteOffer(key, null, addresses);
            Assert.IsTrue(FlockDirectLinkWire.TryReadOffer(first, 0, first.Length, out _, out byte[] noneHeld, out _));
            Assert.IsNull(noneHeld, "Before the sender has heard the receiver's key");
        }

        [Test]
        public void AShortOrMalformedOffer_IsNotRead()
        {
            byte[] key = new byte[FlockDirectLinkWire.KeyLength];
            byte[] offer = FlockDirectLinkWire.WriteOffer(key, null, new List<IPEndPoint> { new IPEndPoint(IPAddress.Loopback, 7777) });
            Assert.IsTrue(FlockDirectLinkWire.TryReadOffer(offer, 0, offer.Length, out _, out _, out _), "Precondition: whole, it reads");
            Assert.IsFalse(FlockDirectLinkWire.TryReadOffer(offer, 0, offer.Length - 1, out _, out _, out _), "Cut short");
            byte[] noPort = (byte[])offer.Clone();
            noPort[noPort.Length - 1] = 0;
            noPort[noPort.Length - 2] = 0;
            Assert.IsFalse(FlockDirectLinkWire.TryReadOffer(noPort, 0, noPort.Length, out _, out _, out _), "Port 0");
            byte[] tooMany = (byte[])offer.Clone();
            tooMany[6 + 2 * FlockDirectLinkWire.KeyLength] = FlockDirectLinkWire.MostAddressesOffered + 1;
            Assert.IsFalse(FlockDirectLinkWire.TryReadOffer(tooMany, 0, tooMany.Length, out _, out _, out _), "More addresses than allowed");
            byte[] probe = FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.Probe, key, key);
            Assert.IsFalse(FlockDirectLinkWire.TryReadOffer(probe, 0, probe.Length, out _, out _, out _), "Another kind");
        }

        [Test]
        public void NoNetcodePacket_ReadsAsTheBridgesOwnMessage()
        {
            // Unity Transport's first byte: 0 to 6 (1.x), 1 to 5 or "U" of "UTP" (2.x), and a TLS record type in secure mode.
            foreach (byte first in new byte[] { 0, 1, 2, 3, 4, 5, 6, (byte)'U', 20, 21, 22, 23 })
            {
                byte[] packet = { first, (byte)'L', (byte)'N', (byte)'K', FlockDirectLinkWire.Version, FlockDirectLinkWire.Offer, 0, 0 };
                Assert.IsFalse(FlockDirectLinkWire.IsMessage(packet, 0, packet.Length), $"A packet starting {first}");
            }
            byte[] markerOnly = { FlockDirectLinkWire.Marker, 1, 2, 3, 4, 5 };
            Assert.IsFalse(FlockDirectLinkWire.IsMessage(markerOnly, 0, markerOnly.Length), "The marker without the letters");
        }

        [Test]
        public void EveryMessage_LeavesUnityTransport1sPipelineFlagClear()
        {
            // An earlier SDK's bridge hands these to the netcode; Unity Transport 1.x reads the second byte as flags and logs an error for a packet with 0x02 set.
            byte[] key = new byte[FlockDirectLinkWire.KeyLength];
            List<byte[]> messages = new List<byte[]> { FlockDirectLinkWire.WriteOffer(key, key, new List<IPEndPoint> { new IPEndPoint(IPAddress.Loopback, 7777) }) };
            foreach (byte kind in new[] { FlockDirectLinkWire.Probe, FlockDirectLinkWire.Answer, FlockDirectLinkWire.KeepAlive, FlockDirectLinkWire.BackToTheRelay })
                messages.Add(FlockDirectLinkWire.WriteBetween(kind, key, key));
            foreach (byte[] message in messages)
            {
                Assert.AreEqual(0, message[1] & 0x02, $"Kind {message[5]}");
                Assert.GreaterOrEqual(message.Length, 10, "Long enough for Unity Transport 1.x to read a header and drop it");
            }
        }

        [Test]
        public void AMessageNamingOtherKeys_IsNotBetweenThisPair()
        {
            byte[] ours = { 1, 1, 1, 1, 1, 1, 1, 1 };
            byte[] theirs = { 2, 2, 2, 2, 2, 2, 2, 2 };
            byte[] probe = FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.Probe, ours, theirs);
            Assert.IsTrue(FlockDirectLinkWire.IsBetween(probe, 0, probe.Length, ours, theirs));
            Assert.IsFalse(FlockDirectLinkWire.IsBetween(probe, 0, probe.Length, theirs, ours), "The keys the other way round");
            Assert.IsFalse(FlockDirectLinkWire.IsBetween(probe, 0, probe.Length, ours, null), "Before the other end's key is known");
        }
    }
}
