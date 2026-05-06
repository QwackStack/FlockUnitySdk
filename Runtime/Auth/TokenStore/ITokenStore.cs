using System.IO;

namespace Flock.Auth
{
    /// <summary>
    /// Persistence layer for auth tokens between app launches. The SDK selects an
    /// implementation automatically via <see cref="TokenStoreFactory.Create"/> based
    /// on the runtime platform — Keychain on iOS/macOS, Keystore on Android, DPAPI on
    /// Windows, PlayerPrefs on Editor/WebGL/other. Implement this interface to plug
    /// in custom or platform-specific storage and assign to
    /// <c>FlockInitConfig.TokenStore</c>.
    /// </summary>
    public abstract class ITokenStore
    {
        public abstract void Save(string accessToken, string refreshToken);
        public abstract StoredTokens Load();
        public abstract void Clear();
    }

    public class StoredTokens
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
    }
}
