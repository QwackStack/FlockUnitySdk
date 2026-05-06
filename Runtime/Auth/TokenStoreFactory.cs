namespace Flock.Auth
{
    /// <summary>
    /// Selects the most secure <see cref="ITokenStore"/> for the current build target.
    /// Selection happens at compile time via platform <c>#if</c> guards so each build
    /// only ships the native bindings it needs:
    /// <list type="bullet">
    ///   <item><description>Editor → <see cref="OtherTokenStore"/> (fast iteration)</description></item>
    ///   <item><description>Android → <see cref="AndroidTokenStore"/> (Android Keystore + Cipher)</description></item>
    ///   <item><description>iOS → <see cref="IosTokenStore"/> (Keychain Services)</description></item>
    ///   <item><description>macOS Standalone → <see cref="MacTokenStore"/> (Keychain Services)</description></item>
    ///   <item><description>Windows Standalone → <see cref="WindowsTokenStore"/> (DPAPI / CryptProtectData)</description></item>
    ///   <item><description>WebGL → <see cref="WebGlTokenStore"/> (PlayerPrefs / IndexedDB)</description></item>
    ///   <item><description>Switch / Linux / other → <see cref="OtherTokenStore"/> fallback</description></item>
    /// </list>
    /// </summary>
    public static class TokenStoreFactory
    {
        public static ITokenStore Create()
        {
#if UNITY_EDITOR
            // Editor fallback
            return new OtherTokenStore();
#elif UNITY_ANDROID
            return new AndroidTokenStore();
#elif UNITY_IOS || UNITY_TVOS
            return new IosTokenStore();
#elif UNITY_STANDALONE_OSX
            return new MacTokenStore();
#elif UNITY_STANDALONE_WIN
            return new WindowsTokenStore();
#elif UNITY_WEBGL
            return new WebGlTokenStore();
#else
            return new OtherTokenStore();
#endif
        }
    }
}
