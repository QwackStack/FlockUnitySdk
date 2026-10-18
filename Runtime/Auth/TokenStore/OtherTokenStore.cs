using Flock.Config;
using UnityEngine;

namespace Flock.Auth
{
    /// <summary>
    /// PlayerPrefs-backed fallback used in the Unity Editor and on platforms
    /// without a dedicated secure store (Linux, Switch, anything not handled
    /// by <see cref="TokenStoreFactory"/>). PlayerPrefs is unencrypted but
    /// works on every Unity target, so this trades security for portability —
    /// safe enough for editor iteration and platforms that don't expose a
    /// platform keystore.
    /// </summary>
    public class OtherTokenStore : ITokenStore
    {
        private const string AccessKey = "Flock.AccessToken";
        private const string RefreshKey = "Flock.RefreshToken";

        private readonly string _accessKey;
        private readonly string _refreshKey;

        /// <summary>The saved sign-in every game shares, as kept before 1.88.1.</summary>
        public OtherTokenStore() : this(null) { }

        /// <summary>One game's saved sign-in, under keys named for the game; null for the one every game shares.</summary>
        internal OtherTokenStore(string gameKey)
        {
            _accessKey = AccessKeyOf(gameKey);
            _refreshKey = RefreshKeyOf(gameKey);
        }

        /// <summary>The PlayerPrefs key of a game's access token; null names the one every game shares.</summary>
        internal static string AccessKeyOf(string gameKey) => FlockGameKey.Named(AccessKey, gameKey);

        /// <summary>The PlayerPrefs key of a game's refresh token; null names the one every game shares.</summary>
        internal static string RefreshKeyOf(string gameKey) => FlockGameKey.Named(RefreshKey, gameKey);

        public override void Save(string accessToken, string refreshToken)
        {
            PlayerPrefs.SetString(_accessKey, accessToken ?? string.Empty);
            PlayerPrefs.SetString(_refreshKey, refreshToken ?? string.Empty);
            PlayerPrefs.Save();
        }

        public override StoredTokens Load()
        {
            if (!PlayerPrefs.HasKey(_accessKey) || !PlayerPrefs.HasKey(_refreshKey))
                return null;
            return new StoredTokens
            {
                AccessToken = PlayerPrefs.GetString(_accessKey),
                RefreshToken = PlayerPrefs.GetString(_refreshKey)
            };
        }

        public override void Clear()
        {
            PlayerPrefs.DeleteKey(_accessKey);
            PlayerPrefs.DeleteKey(_refreshKey);
            PlayerPrefs.Save();
        }
    }
}
