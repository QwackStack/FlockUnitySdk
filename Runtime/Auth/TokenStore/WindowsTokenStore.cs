#if UNITY_STANDALONE_WIN
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Flock.Auth
{
    public class WindowsTokenStore : ITokenStore
    {
        private readonly FlockTokenFile _file = new FlockTokenFile(FlockUtil.FlockFilePath,
            plain => Dpapi(plain, encrypt: true), sealedBytes => Dpapi(sealedBytes, encrypt: false),
            message => Debug.LogWarning("[Flock SDK] " + message));

        public override void Save(string accessToken, string refreshToken) => _file.Save(accessToken, refreshToken);

        public override StoredTokens Load() => _file.Load();

        public override void Clear() => _file.Clear();

        private static byte[] Dpapi(byte[] input, bool encrypt)
        {
            DataBlob inBlob = new DataBlob();
            DataBlob outBlob = new DataBlob();
            inBlob.pb = Marshal.AllocHGlobal(input.Length);
            try
            {
                Marshal.Copy(input, 0, inBlob.pb, input.Length);
                inBlob.cb = input.Length;
                bool ok;
                if (encrypt)
                {
                    ok = CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob);
                    
                }
                else
                {
                    ok = CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0,
                        ref outBlob);
                }
                  
                if (!ok)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                byte[] result = new byte[outBlob.cb];
                Marshal.Copy(outBlob.pb, result, 0, outBlob.cb);
                return result;
            }
            finally
            {
                if (inBlob.pb != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pb);
                if (outBlob.pb != IntPtr.Zero) LocalFree(outBlob.pb);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int cb; public IntPtr pb; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob pIn, string desc,
            IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob pOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob pIn, IntPtr desc,
            IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob pOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);
    }
}
#endif
