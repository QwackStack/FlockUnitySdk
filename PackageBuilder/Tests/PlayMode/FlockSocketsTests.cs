using System.Net;
using System.Net.Sockets;
using Flock.Providers;
using NUnit.Framework;

namespace Flock.Tests.PlayMode
{
    // The SDK's UDP sockets to servers and players: dual-stack where the platform allows (a phone on mobile data heard Cloudflare's STUN
    // server only that way, measured), plain IPv4 where it does not, and addresses kept IPv4 on both sides of the socket.
    public class FlockSocketsTests
    {
        [TearDown]
        public void TearDown() => FlockSockets.ResetForTesting();

        /// <summary>The family a socket made for both IPv6 and IPv4 gets on this machine: IPv6 where it has IPv6, IPv4 where it has none (a CI container may not).</summary>
        internal static AddressFamily FamilyThisMachineGives()
        {
            try
            {
                using (Socket probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.DualMode = true;
                    probe.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                    return AddressFamily.InterNetworkV6;
                }
            }
            catch (System.Exception)
            {
                return AddressFamily.InterNetwork;
            }
        }

        [Test]
        public void ASocket_IsDualStack_WhereThePlatformAllows()
        {
            using (Socket socket = FlockSockets.Open())
            {
                Assert.AreEqual(FamilyThisMachineGives(), socket.AddressFamily);
                if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                    Assert.IsTrue(socket.DualMode, "Carries IPv4 too");
                Assert.AreNotEqual(0, ((IPEndPoint)socket.LocalEndPoint).Port, "Bound to a port of the system's choosing");
            }
        }

        [Test]
        public void AClient_IsDualStack_WhereThePlatformAllows()
        {
            using (UdpClient client = FlockSockets.OpenClient())
            {
                Assert.AreEqual(FamilyThisMachineGives(), client.Client.AddressFamily);
                if (client.Client.AddressFamily == AddressFamily.InterNetworkV6)
                    Assert.IsTrue(client.Client.DualMode);
            }
        }

        [Test]
        public void WithoutIpv6_ASocketAndAClient_ArePlainIpv4()
        {
            FlockSockets.DualStackRefusedForTesting = true;
            using (Socket socket = FlockSockets.Open())
            using (UdpClient client = FlockSockets.OpenClient())
            {
                Assert.AreEqual(AddressFamily.InterNetwork, socket.AddressFamily);
                Assert.AreNotEqual(0, ((IPEndPoint)socket.LocalEndPoint).Port);
                Assert.AreEqual(AddressFamily.InterNetwork, client.Client.AddressFamily);
            }
        }

        [Test]
        public void ARefusedDualStack_IsRemembered_SoLaterSocketsGoStraightToIpv4()
        {
            if (FamilyThisMachineGives() != AddressFamily.InterNetworkV6)
                Assert.Ignore("This machine has no IPv6 to refuse");
            FlockSockets.DualStackRefusedForTesting = true;
            using (Socket refused = FlockSockets.Open())
                Assert.AreEqual(AddressFamily.InterNetwork, refused.AddressFamily);
            Assert.IsTrue(FlockSockets.RemembersARefusalForTesting, "The refusal is remembered for the launch");

            // The platform would refuse again: the next socket does not ask.
            FlockSockets.DualStackRefusedForTesting = false;
            using (Socket later = FlockSockets.Open())
            using (UdpClient laterClient = FlockSockets.OpenClient())
            {
                Assert.AreEqual(AddressFamily.InterNetwork, later.AddressFamily);
                Assert.AreEqual(AddressFamily.InterNetwork, laterClient.Client.AddressFamily);
            }
        }

        [Test]
        public void AnIpv4Address_IsSentToInItsIpv6Form_OnlyOnADualStackSocket()
        {
            IPEndPoint server = new IPEndPoint(IPAddress.Parse("162.159.207.0"), 3478);
            if (FamilyThisMachineGives() != AddressFamily.InterNetworkV6)
                Assert.Ignore("This machine has no IPv6 to map to");
            using (Socket dualStack = FlockSockets.Open())
            {
                IPEndPoint toward = FlockSockets.Toward(dualStack, server);
                Assert.AreEqual(AddressFamily.InterNetworkV6, toward.AddressFamily);
                Assert.IsTrue(toward.Address.IsIPv4MappedToIPv6);
                Assert.AreEqual("162.159.207.0", toward.Address.MapToIPv4().ToString());
                Assert.AreEqual(3478, toward.Port);
            }
            FlockSockets.DualStackRefusedForTesting = true;
            using (Socket ipv4 = FlockSockets.Open())
                Assert.AreSame(server, FlockSockets.Toward(ipv4, server), "A plain IPv4 socket sends to the address as it is");
        }

        [Test]
        public void WhatADualStackSocketHearsFromAnIpv4Sender_IsReadAsIpv4()
        {
            IPEndPoint heard = FlockSockets.AsHeard(new IPEndPoint(IPAddress.Parse("203.0.113.7").MapToIPv6(), 51000));
            Assert.AreEqual(AddressFamily.InterNetwork, heard.AddressFamily);
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51000), heard);

            IPEndPoint ipv4 = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51000);
            Assert.AreEqual(ipv4, FlockSockets.AsHeard(ipv4), "An IPv4 sender heard on an IPv4 socket stays as it is");
            IPEndPoint ipv6 = new IPEndPoint(IPAddress.Parse("2606:4700:49::"), 3478);
            Assert.AreEqual(ipv6, FlockSockets.AsHeard(ipv6), "A real IPv6 sender stays IPv6, so it matches no IPv4 address the SDK knows");
        }

        [Test]
        public void ADualStackSocketThatCannotConnect_IsReplacedByAPlainIpv4One_ConnectedToTheSameServer()
        {
            FlockSockets.DualStackConnectRefusedForTesting = true;
            using (Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                IPEndPoint server = (IPEndPoint)listener.LocalEndPoint;
                using (Socket socket = FlockSockets.OpenConnectedTo(server))
                {
                    Assert.AreEqual(AddressFamily.InterNetwork, socket.AddressFamily, "A plain IPv4 socket stands in");
                    Assert.AreEqual(server, (IPEndPoint)socket.RemoteEndPoint, "Connected to the server all the same");
                    socket.Send(new byte[] { 9 });
                    Assert.IsTrue(listener.Poll(2000000, SelectMode.SelectRead), "And it reaches it");
                }
            }
        }

        [Test]
        public void TwoDualStackSockets_ReachEachOther_OverIpv4Loopback()
        {
            using (Socket a = FlockSockets.Open())
            using (Socket b = FlockSockets.Open())
            {
                IPEndPoint bAddress = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)b.LocalEndPoint).Port);
                a.SendTo(new byte[] { 7 }, FlockSockets.Toward(a, bAddress));
                Assert.IsTrue(b.Poll(2000000, SelectMode.SelectRead), "Arrived");
                byte[] buffer = new byte[16];
                EndPoint from = FlockSockets.AnySender(b);
                int count = b.ReceiveFrom(buffer, ref from);
                Assert.AreEqual(1, count);
                IPEndPoint sender = FlockSockets.AsHeard(from);
                Assert.AreEqual(IPAddress.Loopback, sender.Address, "Heard from 127.0.0.1, not its IPv6 form");
                Assert.AreEqual(((IPEndPoint)a.LocalEndPoint).Port, sender.Port);
            }
        }
    }
}
