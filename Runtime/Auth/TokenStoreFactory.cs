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
        /// <summary>The platform's store for the saved sign-in every game shares, as kept before 1.88.1. <c>FlockInitConfig</c> keeps one sign-in for each game instead.</summary>
        public static ITokenStore Create() => Create(null);

        /// <summary>The platform's store keeping this game's saved sign-in apart from every other game's; the one an earlier build kept for every game is taken over by the first game to read one.</summary>
        internal static ITokenStore CreateForGame(string gameKey)
        {
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
            // The login Keychain is the user's, not this app's: the items every Flock game shared before 1.88.1 may be another
            // studio's game's, so they are neither taken over nor cleared. A Mac player signs in once more after the update.
            return Create(gameKey);
#else
            return new FlockGameTokenStore(Create(gameKey), Create(null));
#endif
        }

        private static ITokenStore Create(string gameKey)
        {
#if UNITY_EDITOR
            // Editor fallback
            return new OtherTokenStore(gameKey);
#elif UNITY_ANDROID
            return new AndroidTokenStore(gameKey);
#elif UNITY_IOS || UNITY_TVOS
            return new IosTokenStore(gameKey);
#elif UNITY_STANDALONE_OSX
            return new MacTokenStore(gameKey);
#elif UNITY_STANDALONE_WIN
            return new WindowsTokenStore(gameKey);
#elif UNITY_WEBGL
            return new WebGlTokenStore(gameKey);
#else
            return new OtherTokenStore(gameKey);
#endif
        }
    }
}
