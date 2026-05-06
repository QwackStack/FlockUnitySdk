#if UNITY_STANDALONE_WIN
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Flock.Auth
{
    public class WindowsTokenStore : ITokenStore
    {
        public override void Save(string accessToken, string refreshToken)
        {
            Directory.CreateDirectory(FlockUtil.FlockFilePath);
            File.WriteAllBytes(FlockUtil.AccessTokenPath, Protect(accessToken));
            File.WriteAllBytes(FlockUtil.RefreshTokenPath, Protect(refreshToken));
        }

        public override StoredTokens Load()
        {
            if (!File.Exists(FlockUtil.AccessTokenPath) || !File.Exists(FlockUtil.RefreshTokenPath))
                return null;
            return new StoredTokens
            {
                AccessToken = Unprotect(File.ReadAllBytes(FlockUtil.AccessTokenPath)),
                RefreshToken = Unprotect(File.ReadAllBytes(FlockUtil.RefreshTokenPath))
            };
        }

        public override void Clear()
        {
            if (File.Exists(FlockUtil.AccessTokenPath))
            {
                File.Delete(FlockUtil.AccessTokenPath);
            }
            if (File.Exists(FlockUtil.RefreshTokenPath))
            {
                File.Delete(FlockUtil.RefreshTokenPath);
            }
        }

        private byte[] Protect(string token) => Dpapi(Encoding.UTF8.GetBytes(token), encrypt: true);

        private string Unprotect(byte[] data) => Encoding.UTF8.GetString(Dpapi(data, encrypt: false));

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
