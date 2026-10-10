using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Flock.Providers;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // The relay's messages: a signed request matching one Python's own hmac, md5 and crc wrote (Libraries/multiplayer-relay/
    // make_wire_vector.py, the form coturn_p2p accepted in MM-13), answers checked before they are believed, addresses, channel
    // frames, and the relay servers read from Flock's answer.
    public class FlockRelayWireTests
    {
        private const string Username = "1791570268:game:player";
        private const string Realm = "qwacks";
        private const string Password = "secret-password";
        private const string Nonce = "abcdef0123456789";
        private const string PythonKey = "890f3679a1a303612a7bd7ace7816e4b";
        private const string PythonMessage =
            "000300642112a4420102030405060708090a0b0c001900041100000000060016313739313537303236383a67616d653a706c6179657200000014" +
            "0006717761636b730000001500106162636465663031323334353637383900080014424fe9fdfa1f96a2af4d5447bedfb282db32f10480280004a19d7a0b";

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(value => value.ToString("x2")));

        private static byte[] Bytes(string hex) => Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();

        private static byte[] Transaction() => Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();

        private static KeyValuePair<ushort, byte[]> Attribute(ushort kind, byte[] value) => new KeyValuePair<ushort, byte[]>(kind, value);

        private static byte[] SignedAllocate(byte[] key)
        {
            return FlockRelayWire.Write((ushort)(FlockRelayWire.AllocateMethod | FlockRelayWire.RequestClass), Transaction(), new List<KeyValuePair<ushort, byte[]>>
            {
                Attribute(FlockRelayWire.RequestedTransportAttribute, FlockRelayWire.UdpTransportValue()),
                Attribute(FlockRelayWire.UsernameAttribute, Encoding.UTF8.GetBytes(Username)),
                Attribute(FlockRelayWire.RealmAttribute, Encoding.UTF8.GetBytes(Realm)),
                Attribute(FlockRelayWire.NonceAttribute, Encoding.UTF8.GetBytes(Nonce)),
            }, key);
        }

        [Test]
        public void LoginKey_MatchesAnotherImplementation()
        {
            Assert.AreEqual(PythonKey, Hex(FlockRelayWire.LoginKey(Username, Realm, Password)));
        }

        [Test]
        public void Write_SignedRequest_MatchesAnotherImplementation_ByteForByte()
        {
            Assert.AreEqual(PythonMessage, Hex(SignedAllocate(FlockRelayWire.LoginKey(Username, Realm, Password))));
        }

        [Test]
        public void Read_Signature_HoldsOnlyForTheBytesAndKeyItWasMadeWith()
        {
            byte[] key = Bytes(PythonKey);
            byte[] packet = Bytes(PythonMessage);
            FlockRelayWire.Message message = FlockRelayWire.Read(packet, packet.Length);
            Assert.IsNotNull(message);
            Assert.IsTrue(FlockRelayWire.SignatureHolds(message, key), "Signed with the login's key");
            Assert.AreEqual(Username, message.Text(FlockRelayWire.UsernameAttribute));

            Assert.IsFalse(FlockRelayWire.SignatureHolds(message, FlockRelayWire.LoginKey(Username, Realm, "another-password")), "Another key");
            byte[] changed = (byte[])packet.Clone();
            changed[40] ^= 0x01;
            FlockRelayWire.Message tampered = FlockRelayWire.Read(changed, changed.Length);
            Assert.IsFalse(FlockRelayWire.SignatureHolds(tampered, key), "A byte changed under the signature");
        }

        [Test]
        public void Read_UnsignedMessage_HasNoSignatureToHold()
        {
            byte[] packet = SignedAllocate(null);
            FlockRelayWire.Message message = FlockRelayWire.Read(packet, packet.Length);
            Assert.AreEqual(-1, message.IntegrityAt);
            Assert.Greater(message.FingerprintAt, 0, "Control: the message was read to its fingerprint");
            Assert.IsFalse(FlockRelayWire.SignatureHolds(message, Bytes(PythonKey)));
        }

        [Test]
        public void Read_AttributesAfterTheSignature_AreNotTaken()
        {
            byte[] packet = Bytes(PythonMessage);
            FlockRelayWire.Message message = FlockRelayWire.Read(packet, packet.Length);
            Assert.IsFalse(message.Attributes.ContainsKey(FlockRelayWire.FingerprintAttribute), "Only the fingerprint follows a signature, and it is read apart");
            Assert.Greater(message.FingerprintAt, message.IntegrityAt);
        }

        [Test]
        public void Read_NotAStunMessage_IsNothing()
        {
            byte[] packet = Bytes(PythonMessage);
            Assert.IsNull(FlockRelayWire.Read(packet, 19), "Shorter than a header");
            byte[] noCookie = (byte[])packet.Clone();
            noCookie[4] = 0;
            Assert.IsNull(FlockRelayWire.Read(noCookie, noCookie.Length), "No magic cookie");
            Assert.IsNull(FlockRelayWire.Read(packet, packet.Length - 4), "A length longer than the packet");
        }

        [Test]
        public void Address_IsWrittenAndReadBack_ForIpv4Only()
        {
            Assert.IsTrue(FlockRelayPeer.TryParse("203.0.113.50:50000", out FlockRelayPeer peer));
            byte[] value = FlockRelayWire.AddressValue(peer);
            Assert.IsTrue(FlockRelayWire.TryReadAddress(value, 0, value.Length, out FlockRelayPeer read));
            Assert.AreEqual(peer, read);
            Assert.AreNotEqual(0xCB, value[4], "The address is masked with the magic cookie, not written plain");

            value[1] = 0x02;
            Assert.IsFalse(FlockRelayWire.TryReadAddress(value, 0, value.Length, out _), "An IPv6 address");
            Assert.IsFalse(FlockRelayWire.TryReadAddress(new byte[20], 0, 20, out _), "An IPv6-sized value");
        }

        [Test]
        public void DataIndication_IsReadInPlace()
        {
            FlockRelayPeer.TryParse("203.0.113.50:50001", out FlockRelayPeer from);
            byte[] data = { 9, 8, 7, 6, 5 };
            byte[] packet = FlockRelayWire.Write((ushort)(FlockRelayWire.DataMethod | FlockRelayWire.IndicationClass), Transaction(), new List<KeyValuePair<ushort, byte[]>>
            {
                Attribute(FlockRelayWire.XorPeerAddressAttribute, FlockRelayWire.AddressValue(from)),
                Attribute(FlockRelayWire.DataAttribute, data),
            }, null);

            Assert.IsTrue(FlockRelayWire.TryReadDataIndication(packet, packet.Length, out FlockRelayPeer peer, out int offset, out int count));
            Assert.AreEqual(from, peer);
            CollectionAssert.AreEqual(data, packet.Skip(offset).Take(count).ToArray());

            byte[] noPeer = FlockRelayWire.Write((ushort)(FlockRelayWire.DataMethod | FlockRelayWire.IndicationClass), Transaction(), new List<KeyValuePair<ushort, byte[]>>
            {
                Attribute(FlockRelayWire.DataAttribute, data),
            }, null);
            Assert.IsFalse(FlockRelayWire.TryReadDataIndication(noPeer, noPeer.Length, out _, out _, out _), "No peer address");
            byte[] request = Bytes(PythonMessage);
            Assert.IsFalse(FlockRelayWire.TryReadDataIndication(request, request.Length, out _, out _, out _), "Another kind of message");
        }

        [Test]
        public void SendIndication_IsWhatTheRelayReads_PaddedToFourBytes()
        {
            FlockRelayPeer.TryParse("203.0.113.50:50002", out FlockRelayPeer to);
            byte[] data = { 1, 2, 3, 4, 5 };
            byte[] buffer = new byte[64];
            int length = FlockRelayWire.WriteSendIndication(buffer, Transaction(), to, data, 0, data.Length);

            Assert.AreEqual(0, length % 4);
            FlockRelayWire.Message message = FlockRelayWire.Read(buffer, length);
            Assert.IsNotNull(message);
            Assert.AreEqual((ushort)(FlockRelayWire.SendMethod | FlockRelayWire.IndicationClass), message.Type);
            Assert.IsTrue(message.TryGetAddress(FlockRelayWire.XorPeerAddressAttribute, out FlockRelayPeer peer));
            Assert.AreEqual(to, peer);
            CollectionAssert.AreEqual(data, message.Attributes[FlockRelayWire.DataAttribute]);
        }

        [Test]
        public void ChannelFrame_IsWrittenAndReadBack_AndAShortOneRefused()
        {
            byte[] data = { 1, 2, 3 };
            byte[] buffer = new byte[16];
            int length = FlockRelayWire.WriteChannelFrame(buffer, 0x4001, data, 0, data.Length);

            Assert.AreEqual(7, length, "Four bytes before the data, nothing after over UDP");
            Assert.IsTrue(FlockRelayWire.TryReadChannelFrame(buffer, length, out ushort channel, out int count));
            Assert.AreEqual(0x4001, channel);
            Assert.AreEqual(3, count);
            Assert.IsFalse(FlockRelayWire.TryReadChannelFrame(buffer, length - 1, out _, out _), "A frame claiming more than it holds");
            byte[] request = Bytes(PythonMessage);
            Assert.IsFalse(FlockRelayWire.TryReadChannelFrame(request, request.Length, out _, out _), "A STUN message is no channel frame");
        }

        [Test]
        public void ErrorCode_ReadsClassAndNumber()
        {
            byte[] unauthorized = new byte[] { 0, 0, 4, 1 }.Concat(Encoding.UTF8.GetBytes("Unauthorized")).ToArray();
            Assert.AreEqual(401, FlockRelayWire.ErrorCodeOf(unauthorized, out string words));
            Assert.AreEqual("Unauthorized", words);
            Assert.AreEqual(438, FlockRelayWire.ErrorCodeOf(new byte[] { 0, 0, 4, 38 }, out _));
            Assert.AreEqual(508, FlockRelayWire.ErrorCodeOf(new byte[] { 0, 0, 5, 8 }, out _));
            Assert.AreEqual(0, FlockRelayWire.ErrorCodeOf(new byte[] { 0, 4 }, out _), "Too short");
        }

        [Test]
        public void Class_IsReadFromTheType()
        {
            Assert.AreEqual(FlockRelayWire.ErrorClass, FlockRelayWire.ClassOf(0x0113));
            Assert.AreEqual(FlockRelayWire.SuccessClass, FlockRelayWire.ClassOf(0x0109));
            Assert.AreEqual(FlockRelayWire.IndicationClass, FlockRelayWire.ClassOf(0x0017));
            Assert.AreEqual(FlockRelayWire.RequestClass, FlockRelayWire.ClassOf(0x0003));
        }

        [TestCase("turn:relay.test:3479?transport=udp", "relay.test", 3479)]
        [TestCase("turn:relay.test:3479", "relay.test", 3479)]
        [TestCase("turn:relay.test", "relay.test", 3478)]
        [TestCase("TURN:Relay.Test:5000?Transport=UDP", "Relay.Test", 5000)]
        [TestCase("turn:203.0.113.9:3479?transport=udp", "203.0.113.9", 3479)]
        public void RelayServer_UdpAddresses_AreRead(string url, string host, int port)
        {
            Assert.IsTrue(FlockRelayLogin.TryReadUdpServer(url, out string readHost, out int readPort));
            Assert.AreEqual(host, readHost);
            Assert.AreEqual(port, readPort);
        }

        [TestCase("turn:relay.test:3479?transport=tcp")]
        [TestCase("turns:relay.test:5349?transport=tcp")]
        [TestCase("turn:[::1]:3478")]
        [TestCase("turn::3478")]
        [TestCase("turn:relay.test:70000")]
        [TestCase("turn:relay.test:port")]
        [TestCase("stun:stun.l.google.com:19302")]
        [TestCase("")]
        [TestCase(null)]
        public void RelayServer_AnythingElse_IsLeftOut(string url)
        {
            Assert.IsFalse(FlockRelayLogin.TryReadUdpServer(url, out _, out _));
        }

        [Test]
        public void Logins_AreFlocksUdpRelays_InFlocksOrder()
        {
            RelayCredentialsRecord answer = new RelayCredentialsRecord
            {
                IceServers = new List<IceServerRecord>
                {
                    new IceServerRecord { Urls = new List<string> { "turn:main.test:3479?transport=udp", "turn:main.test:3479?transport=tcp" }, Username = "u1", Credential = "p1" },
                    new IceServerRecord { Urls = new List<string> { "turn:no-login.test:3479" } },
                    new IceServerRecord { Urls = new List<string> { "turn:next.test:3480" }, Username = "u2", Credential = "p2" },
                    new IceServerRecord { Urls = new List<string> { "stun:stun.test:19302" } },
                    null,
                },
            };
            List<FlockRelayLogin> logins = FlockRelayLogin.InAnswer(answer);
            CollectionAssert.AreEqual(new[] { "main.test:3479", "next.test:3480" }, logins.Select(login => login.ToString()).ToArray());
            Assert.AreEqual("u1", logins[0].Username);
            Assert.AreEqual("p2", logins[1].Password);
            Assert.IsEmpty(FlockRelayLogin.InAnswer(new RelayCredentialsRecord()), "No servers listed");
        }

        [Test]
        public void Peer_IsReadFromText_AndWrittenBack()
        {
            Assert.IsTrue(FlockRelayPeer.TryParse("203.0.113.50:50000", out FlockRelayPeer peer));
            Assert.AreEqual("203.0.113.50:50000", peer.ToString());
            Assert.AreEqual(0xCB007132u, peer.Address);
            Assert.AreEqual(50000, peer.Port);
        }

        [TestCase("203.0.113.50")]
        [TestCase("203.0.113:5")]
        [TestCase("256.0.113.50:5")]
        [TestCase("203.0.113.50:0")]
        [TestCase("203.0.113.50:65536")]
        [TestCase("+203.0.113.50:5")]
        [TestCase("+1.2.3.4:5")]
        [TestCase("1.2.3. 4:5")]
        [TestCase("203.0.113.50:+5")]
        [TestCase(" 203.0.113.50:5")]
        [TestCase("203.0.113.50: 5")]
        [TestCase("203.0. 113.50:5")]
        [TestCase("[::1]:5")]
        [TestCase("")]
        [TestCase(null)]
        public void Peer_AnythingButDigits_IsRefused(string text)
        {
            Assert.IsFalse(FlockRelayPeer.TryParse(text, out _));
        }
    }
}
