using System;

namespace Flock.Auth
{
    /// <summary>Bytes handed to and from Java as Java's own signed type: Unity marks byte arrays at its Java boundary obsolete and warns on each call.</summary>
    internal static class FlockJavaBytes
    {
        /// <summary>The same bits as Java's byte array; null stays null.</summary>
        public static sbyte[] ForJava(byte[] bytes)
        {
            if (bytes == null)
                return null;
            sbyte[] signed = new sbyte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, signed, 0, bytes.Length);
            return signed;
        }

        /// <summary>The same bits back from Java's byte array; null stays null.</summary>
        public static byte[] FromJava(sbyte[] signed)
        {
            if (signed == null)
                return null;
            byte[] bytes = new byte[signed.Length];
            Buffer.BlockCopy(signed, 0, bytes, 0, signed.Length);
            return bytes;
        }
    }
}
