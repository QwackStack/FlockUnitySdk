#if FLOCK_NETCODE_FOR_GAMEOBJECTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // A relayed player whose game starts its netcode again on the same relay address before the host has noticed the old connection is
    // gone: the old connection leaving must not take the path the next one comes through.
    public partial class FlockNetcodeTests
    {
        // The host lets a silent connection go after this, so a test need not wait Unity Transport's own 30 s.
        private const int HostLetsASilentConnectionGoAfterMs = 2500;

        private sealed class Reconnect
        {
            public readonly List<FlockRelayConnection> Relays = new List<FlockRelayConnection>();
            public readonly List<NetworkManager> Players = new List<NetworkManager>();
            public ulong Old;
            public FlockRelayBridge HostsBridge;
            public int Port;
            public FakeSessionServer.Held Checking;
            public NetworkManager Next;
        }

        // A relayed player let in; then its game closes its bridge (so the old netcode's goodbye never leaves) and connects again through a
        // new bridge on the same relay address. The next connection's token check is held until the host has let the old connection go.
        private IEnumerator ReconnectingOnTheSameRelayAddress(Hosting hosting, Reconnect reconnect, FlockNetcodeOptions options)
        {
            yield return HostedWithNetcode(hosting, options, manager =>
            {
                RelayOn();
                TransportOf(manager).DisconnectTimeoutMS = HostLetsASilentConnectionGoAfterMs;
            });
            yield return RelayedPlayer(hosting, B, WithToken(SeatedWithToken(hosting, B)), reconnect.Relays, reconnect.Players);
            yield return LetIn(hosting.Manager, reconnect.Players[0], 2);
            reconnect.Old = hosting.Manager.ConnectedClientsIds.Single(id => id != NetworkManager.ServerClientId);
            reconnect.HostsBridge = FlockJoinChecks.RelayBridgeForTesting(hosting.Session);
            reconnect.Port = reconnect.HostsBridge.PeerPortsForTesting.Single();

            _otherBridges[0].Close();
            reconnect.Players[0].Shutdown();
            reconnect.Checking = _server.HoldNext(FakeSessionServer.Verify);
            FlockRelayBridge next = OtherBridge(FlockRelayBridge.ToTheHost(reconnect.Relays[0], RelayAddressIn(Published()), _h.Logger));
            reconnect.Next = PlayerConnecting(next.LocalPort, WithToken(SeatedWithToken(hosting, B)));
            yield return FlockTestWait.Until(() => reconnect.Checking.Arrived, "the next connection's token is being checked");
            Assert.IsTrue(hosting.Manager.ConnectedClientsIds.Contains(reconnect.Old), "Precondition: the host has not noticed the old connection is gone");
            yield return FlockTestWait.Until(() => !hosting.Manager.ConnectedClientsIds.Contains(reconnect.Old), "the host let the old connection go", 10f);
        }

        [UnityTest]
        public IEnumerator AnOldConnectionLeaving_WhileTheNextOnTheSameRelayAddressIsChecked_LeavesTheNextItsPath()
        {
            Hosting hosting = new Hosting();
            Reconnect reconnect = new Reconnect();
            yield return ReconnectingOnTheSameRelayAddress(hosting, reconnect, new FlockNetcodeOptions { Relay = FlockRelayUse.Always });

            reconnect.Checking.Release();
            yield return LetIn(hosting.Manager, reconnect.Next, 2);
            Assert.AreEqual(B, hosting.Session.PlayerForConnection(Newest(hosting.Manager)));
            Assert.IsTrue(hosting.Session.Relay.KeepsChannelToForTesting(reconnect.Relays[0].Address), "Let in again: its channel is kept");
            // Longer than the host keeps a silent connection: both ends still hear each other.
            yield return new WaitForSecondsRealtime(HostLetsASilentConnectionGoAfterMs / 1000f + 1f);
            Assert.IsTrue(reconnect.Next.IsConnectedClient && hosting.Manager.ConnectedClientsIds.Count == 2, "Still connected");
            CollectionAssert.AreEqual(new[] { reconnect.Port }, reconnect.HostsBridge.PeerPortsForTesting, "Through the socket it first came through");
        }

        [UnityTest]
        public IEnumerator ANextConnectionRefused_AfterTheOldOneLeft_GivesTheRelayAddressBack_LikeAnyoneNeverLetIn()
        {
            FlockRelayBridge.NotLetInWithinForTesting = TimeSpan.FromSeconds(1);
            bool refusing = false;
            // WhenNeeded, so the old connection's address also had a direct link to let go of.
            FlockNetcodeOptions options = new FlockNetcodeOptions { ApproveJoiningPlayer = (joining, response) => response.Approved = !refusing };
            Hosting hosting = new Hosting();
            Reconnect reconnect = new Reconnect();
            yield return ReconnectingOnTheSameRelayAddress(hosting, reconnect, options);
            FlockRelayPeer address = reconnect.Relays[0].Address;
            Assert.IsFalse(hosting.Session.Relay.KeepsChannelToForTesting(address), "No longer let in: its channel is let go");
            Assert.IsNull(reconnect.HostsBridge.DirectLinkForTesting(address), "and its direct link");

            refusing = true;
            reconnect.Checking.Release();
            yield return FlockTestWait.Until(() => !reconnect.Next.IsListening, "the next connection was refused");
            yield return new WaitForSecondsRealtime(1.5f);
            // A stranger heard by the host is what gives back places nobody was let into.
            List<NetworkManager> strangers = new List<NetworkManager>();
            yield return RelayedPlayer(hosting, C, WithToken("made-up"), reconnect.Relays, strangers);
            yield return FlockTestWait.Until(() => reconnect.HostsBridge.PeersForTesting >= 1 && !reconnect.HostsBridge.PeerPortsForTesting.Contains(reconnect.Port),
                "the refused player's place was given back", 5f);
        }

        [UnityTest]
        public IEnumerator AStrangerHeard_WhileTheNextConnectionIsChecked_LeavesItItsPlace()
        {
            // Shorter than the old connection lived, longer than it takes a stranger to reach the host after it left.
            FlockRelayBridge.NotLetInWithinForTesting = TimeSpan.FromSeconds(2);
            Hosting hosting = new Hosting();
            Reconnect reconnect = new Reconnect();
            yield return ReconnectingOnTheSameRelayAddress(hosting, reconnect, new FlockNetcodeOptions { Relay = FlockRelayUse.Always });

            // A stranger's first packet is what gives back places nobody was let into in their time.
            List<NetworkManager> strangers = new List<NetworkManager>();
            yield return RelayedPlayer(hosting, C, WithToken("made-up"), reconnect.Relays, strangers);
            yield return FlockTestWait.Until(() => reconnect.HostsBridge.PeersForTesting >= 2, "the host heard the stranger");
            reconnect.Checking.Release();

            yield return LetIn(hosting.Manager, reconnect.Next, 2);
            CollectionAssert.Contains(reconnect.HostsBridge.PeerPortsForTesting, reconnect.Port, "Its time to be let in counted from when the old connection left");
        }
    }
}
#endif
