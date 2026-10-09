using System;
using Flock.Providers;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // Reading a STUN server's answer: the address it saw, from the newer masked attribute or the older plain one, and nothing for
    // an answer to another request, an error, or bytes cut short.
    public class FlockDirectAddressesTests
    {
        private static readonly byte[] Transaction = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

        // A binding answer of the given type holding the given attributes, each already padded to four bytes.
        private static byte[] Answer(ushort type, byte[] transaction, params byte[][] attributes)
        {
            int length = 0;
            foreach (byte[] attribute in attributes)
                length += attribute.Length;
            byte[] answer = new byte[20 + length];
            answer[0] = (byte)(type >> 8);
            answer[1] = (byte)type;
            answer[2] = (byte)(length >> 8);
            answer[3] = (byte)length;
            answer[4] = 0x21;
            answer[5] = 0x12;
            answer[6] = 0xA4;
            answer[7] = 0x42;
            Array.Copy(transaction, 0, answer, 8, 12);
            int at = 20;
            foreach (byte[] attribute in attributes)
            {
                Array.Copy(attribute, 0, answer, at, attribute.Length);
                at += attribute.Length;
            }
            return answer;
        }

        // 203.0.113.7:50000, masked with the magic cookie as XOR-MAPPED-ADDRESS carries it.
        private static byte[] XorMapped(byte a = 203, byte b = 0, byte c = 113, byte d = 7)
        {
            int port = 50000 ^ 0x2112;
            return new byte[] { 0x00, 0x20, 0x00, 0x08, 0x00, 0x01, (byte)(port >> 8), (byte)port, (byte)(a ^ 0x21), (byte)(b ^ 0x12), (byte)(c ^ 0xA4), (byte)(d ^ 0x42) };
        }

        private static byte[] PlainMapped(byte a, byte b, byte c, byte d) => new byte[] { 0x00, 0x01, 0x00, 0x08, 0x00, 0x01, 0xC3, 0x50, a, b, c, d };

        // An attribute the reader does not use, five bytes long so its padding is read too.
        private static byte[] Software() => new byte[] { 0x80, 0x22, 0x00, 0x05, (byte)'t', (byte)'e', (byte)'s', (byte)'t', (byte)'!', 0, 0, 0 };

        [Test]
        public void TheMaskedAddress_IsRead()
        {
            Assert.AreEqual("203.0.113.7", FlockDirectAddresses.PublicAddressInStunAnswer(Answer(0x0101, Transaction, XorMapped()), Transaction));
        }

        [Test]
        public void TheMaskedAddress_IsPreferred_OverThePlainOne_WhereverItSits()
        {
            byte[] answer = Answer(0x0101, Transaction, PlainMapped(198, 51, 100, 4), Software(), XorMapped());
            Assert.AreEqual("203.0.113.7", FlockDirectAddresses.PublicAddressInStunAnswer(answer, Transaction));
        }

        [Test]
        public void ThePlainAddress_IsRead_WhenItIsTheOnlyOne()
        {
            byte[] answer = Answer(0x0101, Transaction, Software(), PlainMapped(198, 51, 100, 4));
            Assert.AreEqual("198.51.100.4", FlockDirectAddresses.PublicAddressInStunAnswer(answer, Transaction));
        }

        [Test]
        public void AnAnswerToAnotherRequest_ReadsNothing()
        {
            byte[] other = (byte[])Transaction.Clone();
            other[11] = 99;
            Assert.IsNull(FlockDirectAddresses.PublicAddressInStunAnswer(Answer(0x0101, other, XorMapped()), Transaction));
        }

        [Test]
        public void AnErrorAnswer_ReadsNothing()
        {
            Assert.IsNull(FlockDirectAddresses.PublicAddressInStunAnswer(Answer(0x0111, Transaction, XorMapped()), Transaction));
        }

        [Test]
        public void BytesCutShort_ReadNothing_AndDoNotThrow()
        {
            byte[] whole = Answer(0x0101, Transaction, XorMapped());
            for (int length = 0; length < whole.Length; length++)
            {
                byte[] cut = new byte[length];
                Array.Copy(whole, cut, length);
                Assert.IsNull(FlockDirectAddresses.PublicAddressInStunAnswer(cut, Transaction), $"{length} bytes");
            }
            Assert.IsNull(FlockDirectAddresses.PublicAddressInStunAnswer(null, Transaction));
        }

        [Test]
        public void AnIpv6Address_IsLeftOut()
        {
            byte[] ipv6 = new byte[24];
            ipv6[1] = 0x20;
            ipv6[3] = 20;
            ipv6[5] = 0x02;
            Assert.IsNull(FlockDirectAddresses.PublicAddressInStunAnswer(Answer(0x0101, Transaction, ipv6), Transaction));
        }
    }
}
