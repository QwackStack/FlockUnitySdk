#if UNITY_STANDALONE_OSX
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Flock.Auth
{
    /// <summary>
    /// Persists tokens in the user's macOS login Keychain via Security.framework
    /// generic-password APIs. The OS handles encryption and per-app access
    /// control; items live in <c>~/Library/Keychains/login.keychain-db</c> and
    /// persist across game launches .
    /// </summary>
    public class MacTokenStore : ITokenStore
    {
        private const string Service = "com.flock.tokens";
        private const string AccessAccount = "access";
        private const string RefreshAccount = "refresh";
        private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        public override void Save(string accessToken, string refreshToken)
        {
            Write(AccessAccount, accessToken);
            Write(RefreshAccount, refreshToken);
        }

        public override StoredTokens Load()
        {
            string a = Read(AccessAccount);
            string r = Read(RefreshAccount);
            if (a == null || r == null) return null;
            return new StoredTokens { AccessToken = a, RefreshToken = r };
        }

        public override void Clear()
        {
            Delete(AccessAccount);
            Delete(RefreshAccount);
        }

        private static void Write(string account, string value)
        {
            // Delete-then-add avoids errSecDuplicateItem and keeps the ACL clean
            // across token rotations.
            Delete(account);

            byte[] svc = Encoding.UTF8.GetBytes(Service);
            byte[] acc = Encoding.UTF8.GetBytes(account);
            byte[] val = Encoding.UTF8.GetBytes(value);
            SecKeychainAddGenericPassword(IntPtr.Zero,
                (uint)svc.Length, svc,
                (uint)acc.Length, acc,
                (uint)val.Length, val,
                IntPtr.Zero);
        }

        private static string Read(string account)
        {
            byte[] svc = Encoding.UTF8.GetBytes(Service);
            byte[] acc = Encoding.UTF8.GetBytes(account);

            uint len;
            IntPtr data;
            IntPtr itemRef;
            int status = SecKeychainFindGenericPassword(IntPtr.Zero,
                (uint)svc.Length, svc,
                (uint)acc.Length, acc,
                out len, out data, out itemRef);

            if (status != 0 || data == IntPtr.Zero) return null;

            try
            {
                byte[] buf = new byte[len];
                Marshal.Copy(data, buf, 0, (int)len);
                return Encoding.UTF8.GetString(buf);
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (itemRef != IntPtr.Zero) CFRelease(itemRef);
            }
        }

        private static void Delete(string account)
        {
            byte[] svc = Encoding.UTF8.GetBytes(Service);
            byte[] acc = Encoding.UTF8.GetBytes(account);

            uint len;
            IntPtr data;
            IntPtr itemRef;
            int status = SecKeychainFindGenericPassword(IntPtr.Zero,
                (uint)svc.Length, svc,
                (uint)acc.Length, acc,
                out len, out data, out itemRef);

            if (status != 0) return;

            try
            {
                if (itemRef != IntPtr.Zero) SecKeychainItemDelete(itemRef);
            }
            finally
            {
                if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (itemRef != IntPtr.Zero) CFRelease(itemRef);
            }
        }

        [DllImport(SecurityLib)]
        private static extern int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceLen, byte[] service,
            uint accountLen, byte[] account,
            uint passwordLen, byte[] password,
            IntPtr itemRef);

        [DllImport(SecurityLib)]
        private static extern int SecKeychainFindGenericPassword(
            IntPtr keychain,
            uint serviceLen, byte[] service,
            uint accountLen, byte[] account,
            out uint passwordLen, out IntPtr passwordData,
            out IntPtr itemRef);

        [DllImport(SecurityLib)]
        private static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(SecurityLib)]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundationLib)]
        private static extern void CFRelease(IntPtr cf);
    }
}
#endif
