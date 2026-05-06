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

        public override void Save(string accessToken, string refreshToken)
        {
            PlayerPrefs.SetString(AccessKey, accessToken ?? string.Empty);
            PlayerPrefs.SetString(RefreshKey, refreshToken ?? string.Empty);
            PlayerPrefs.Save();
        }

        public override StoredTokens Load()
        {
            if (!PlayerPrefs.HasKey(AccessKey) || !PlayerPrefs.HasKey(RefreshKey))
                return null;
            return new StoredTokens
            {
                AccessToken = PlayerPrefs.GetString(AccessKey),
                RefreshToken = PlayerPrefs.GetString(RefreshKey)
            };
        }

        public override void Clear()
        {
            PlayerPrefs.DeleteKey(AccessKey);
            PlayerPrefs.DeleteKey(RefreshKey);
            PlayerPrefs.Save();
        }
    }
}
