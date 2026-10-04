using System.Collections.Generic;
using System.Text.RegularExpressions;
using Flock.Auth;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    /// <summary>Bytes cross to and from Java as Java's own signed type, bit for bit, so tokens saved before still decrypt.</summary>
    public class FlockJavaBytesTests
    {
        // A Java call that reads a byte array back, the form Unity warns on.
        private static readonly Regex JavaCallReadingBytes = new Regex(@"\.(Call|CallStatic|Get|GetStatic)<byte\[\]>");

        [Test]
        public void EveryByteValueCrossesToJavaAndBackUnchanged()
        {
            byte[] bytes = new byte[256];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)i;

            sbyte[] forJava = FlockJavaBytes.ForJava(bytes);
            Assert.AreEqual(256, forJava.Length);
            Assert.AreEqual(127, forJava[127]);
            Assert.AreEqual(-128, forJava[128], "Bits copied, not values checked: an IV or ciphertext byte above 127 is a negative Java byte");
            Assert.AreEqual(-1, forJava[255]);
            CollectionAssert.AreEqual(bytes, FlockJavaBytes.FromJava(forJava));
        }

        [Test]
        public void NothingStaysNothingAndEmptyStaysEmpty()
        {
            Assert.IsNull(FlockJavaBytes.ForJava(null));
            Assert.IsNull(FlockJavaBytes.FromJava(null));
            Assert.AreEqual(0, FlockJavaBytes.ForJava(new byte[0]).Length);
            Assert.AreEqual(0, FlockJavaBytes.FromJava(new sbyte[0]).Length);
        }

        [Test]
        public void NoJavaCallInTheSdkReadsAByteArray()
        {
            // Controls: the pattern knows the form it is for, and the scan reads the token store, whose code compiles only for Android.
            Assert.IsTrue(JavaCallReadingBytes.IsMatch("byte[] iv = cipher.Call<byte[]>(\"getIV\");"), "Precondition: the pattern matches the form Unity warns on");
            Assert.GreaterOrEqual(FlockRuntimeSource.LinesMatchingIn("AndroidTokenStore.cs", new Regex(@"Call<sbyte\[\]>")).Count, 2,
                "Precondition: the scan reads the Android token store");

            List<string> found = FlockRuntimeSource.LinesMatching(JavaCallReadingBytes, null);
            Assert.IsEmpty(found, "Read Java's bytes as sbyte[] through FlockJavaBytes: " + string.Join("; ", found));
        }
    }
}
