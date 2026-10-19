#if FLOCK_NETCODE_FOR_GAMEOBJECTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // StartNetcodeAsync with Flock's relay in memory and the other player's game on this machine: once a WhenNeeded host lets a relayed
    // player in, or a WhenNeeded player connects through the relay, the two move the netcode's packets to a direct path; Always never does.
    public partial class FlockNetcodeTests
    {
        // The address asked on the SDK's own 0.5 s three times (it ends at the first answer); probes given 3 s and a direct path 3 s of
        // quiet, so a busy machine's pause is not taken for an unanswered probe or a path gone quiet; keep-alives every 0.2 s.
        private static FlockDirectLink.Timing QuickDirectLinks() => new FlockDirectLink.Timing
        {
            Tick = TimeSpan.FromMilliseconds(20),
            OfferEvery = TimeSpan.FromMilliseconds(200),
            OfferFor = TimeSpan.FromSeconds(3),
            ProbeEvery = TimeSpan.FromMilliseconds(50),
            ProbeFor = TimeSpan.FromSeconds(3),
            KeepAliveEvery = TimeSpan.FromMilliseconds(200),
            QuietFor = TimeSpan.FromSeconds(3),
        };

        private int RelayedFrom(FlockRelayConnection relay) => _relay.PassedOnFrom(relay.LocalPortForTesting);

        private static bool IsDirect(FlockRelayBridge bridge, FlockRelayPeer peer)
        {
            FlockDirectLink link = bridge?.DirectLinkForTesting(peer);
            return link != null && link.IsDirect;
        }

        [UnityTest]
        public IEnumerator AWhenNeededHost_MovesARelayedPlayerItLetIn_OntoADirectPath()
        {
            FlockDirectLink.TimingForTesting = QuickDirectLinks();
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, null, manager => RelayOn());
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            List<NetworkManager> players = new List<NetworkManager>();
            yield return RelayedPlayer(hosting, B, WithToken(SeatedWithToken(hosting, B)), relays, players, goesDirect: true);
            yield return LetIn(hosting.Manager, players[0], 2);
            FlockRelayBridge bridge = FlockJoinChecks.RelayBridgeForTesting(hosting.Session);

            yield return FlockTestWait.Until(() => IsDirect(bridge, relays[0].Address) && IsDirect(_otherBridges[0], hosting.Session.Relay.Address),
                "the host and the player went direct");
            // A packet relayed just before the switch may still be on its way to the relay.
            yield return new WaitForSecondsRealtime(0.3f);
            int relayed = RelayedFrom(relays[0]);
            yield return new WaitForSecondsRealtime(1.5f);
            Assert.IsTrue(players[0].IsConnectedClient, "Still connected, now directly");
            Assert.AreEqual(relayed, RelayedFrom(relays[0]), "The player's netcode packets no longer go through the relay");
        }

        [UnityTest]
        public IEnumerator AnAlwaysHost_KeepsARelayedPlayerOnTheRelay_AndOffersNothing()
        {
            FlockDirectLink.TimingForTesting = QuickDirectLinks();
            Hosting hosting = new Hosting();
            yield return HostedWithNetcode(hosting, new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, manager => RelayOn());
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            List<NetworkManager> players = new List<NetworkManager>();
            yield return RelayedPlayer(hosting, B, WithToken(SeatedWithToken(hosting, B)), relays, players, goesDirect: true);
            yield return LetIn(hosting.Manager, players[0], 2);
            FlockRelayBridge bridge = FlockJoinChecks.RelayBridgeForTesting(hosting.Session);
            yield return new WaitForSecondsRealtime(1f);

            Assert.IsNull(bridge.DirectLinkForTesting(relays[0].Address), "No link: Always keeps every address behind the relay");
            int relayed = RelayedFrom(relays[0]);
            yield return new WaitForSecondsRealtime(1.5f);
            Assert.IsTrue(players[0].IsConnectedClient);
            Assert.Greater(RelayedFrom(relays[0]), relayed, "The player's netcode packets still go through the relay");
            Assert.IsFalse(IsDirect(_otherBridges[0], hosting.Session.Relay.Address));
        }

        [UnityTest]
        public IEnumerator AWhenNeededPlayer_ThroughTheRelay_MovesOntoADirectPath()
        {
            FlockDirectLink.TimingForTesting = QuickDirectLinks();
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(null, (host, port) => OnTheHostsNetworkAt(host, port, UnansweringLanAddress), started, held, relays, hostGoesDirect: true);
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome, started[0].Result.RefusedReason);
            Assert.IsTrue(started[0].Result.ThroughRelay, "Precondition: connected through the relay");

            // The other game lets the player in, as Flock's check does.
            _otherBridges[0].LetIn(held[0].Relay.Address);
            yield return FlockTestWait.Until(() => IsDirect(_otherBridges[0], held[0].Relay.Address), "the host's end went direct");
            // This player's own end, whose link says so in the log once its probe is answered.
            yield return FlockTestWait.Until(() => _h.Logger.Logged(_h.Logger.Infos, "Packets to the host now go directly"), "this player's end went direct");
            // A packet relayed just before the switch may still be on its way to the relay.
            yield return new WaitForSecondsRealtime(0.3f);
            int relayed = RelayedFrom(held[0].Relay);
            yield return new WaitForSecondsRealtime(1.5f);
            Assert.IsTrue(_managers.Last().IsConnectedClient, "Still connected, now directly");
            Assert.AreEqual(relayed, RelayedFrom(held[0].Relay), "This player's netcode packets no longer go through the relay");
        }

        [UnityTest]
        public IEnumerator AnAlwaysPlayer_StaysOnTheRelay()
        {
            FlockDirectLink.TimingForTesting = QuickDirectLinks();
            List<Task<FlockNetcodeStartResult>> started = new List<Task<FlockNetcodeStartResult>>();
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return PlayerAgainstARelayedHost(new FlockNetcodeOptions { Relay = FlockRelayUse.Always }, (host, port) => RelayedOnThisMachine(host, port), started, held, relays,
                hostGoesDirect: true);
            Assert.AreEqual(FlockNetcodeStartOutcome.Connected, started[0].Result.Outcome, started[0].Result.RefusedReason);

            _otherBridges[0].LetIn(held[0].Relay.Address);
            yield return new WaitForSecondsRealtime(1f);
            int relayed = RelayedFrom(held[0].Relay);
            yield return new WaitForSecondsRealtime(1.5f);
            Assert.IsFalse(IsDirect(_otherBridges[0], held[0].Relay.Address), "Nothing offered to the host, so nothing direct");
            Assert.IsTrue(_managers.Last().IsConnectedClient);
            Assert.Greater(RelayedFrom(held[0].Relay), relayed, "This player's netcode packets still go through the relay");
        }
    }
}
#endif
