#if UNITY_IOS || UNITY_TVOS
using System;
using System.Runtime.InteropServices;

namespace Flock.Auth
{
    /// <summary>
    /// Persists tokens in the iOS Keychain via a small Objective-C shim
    /// (<c>Plugins/iOS/FlockKeychain.mm</c>). Items are stored as
    /// <c>kSecClassGenericPassword</c> with
    /// <c>kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly</c> — OS-encrypted,
    /// available after first unlock following reboot, and never synced to iCloud.
    /// </summary>
    public class IosTokenStore : ITokenStore
    {
        private const string Service = "com.flock.tokens";
        private const string AccessAccount = "access";
        private const string RefreshAccount = "refresh";

        public override void Save(string accessToken, string refreshToken)
        {
            FlockKeychainSet(Service, AccessAccount, accessToken);
            FlockKeychainSet(Service, RefreshAccount, refreshToken);
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
            FlockKeychainDelete(Service, AccessAccount);
            FlockKeychainDelete(Service, RefreshAccount);
        }

        private static string Read(string account)
        {
            IntPtr ptr = FlockKeychainGet(Service, account);
            if (ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringAnsi(ptr); }
            finally { FlockKeychainFreeString(ptr); }
        }

        [DllImport("__Internal")]
        private static extern int FlockKeychainSet(string service, string account, string value);

        [DllImport("__Internal")]
        private static extern IntPtr FlockKeychainGet(string service, string account);

        [DllImport("__Internal")]
        private static extern int FlockKeychainDelete(string service, string account);

        [DllImport("__Internal")]
        private static extern void FlockKeychainFreeString(IntPtr ptr);
    }
}
#endif
