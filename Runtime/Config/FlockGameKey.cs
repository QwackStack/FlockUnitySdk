using System.Security.Cryptography;
using System.Text;

namespace Flock.Config
{
    /// <summary>
    /// Names one game as the server knows it: the backend's address and the game's API key, the only two things every request
    /// carries that tell games apart. What a game keeps on the device between launches (its saved sign-in, its analytics files) is
    /// kept under this name, so one game never restores or sends another's. The first 16 hex characters of a SHA-256, so the API
    /// key itself is never written to disk.
    /// </summary>
    internal static class FlockGameKey
    {
        /// <summary>The key for this backend and API key: the address without surrounding spaces or trailing slashes, in any letter case, and the API key without surrounding spaces.</summary>
        internal static string Of(string apiUrl, string apiKey)
        {
            string url = (FlockInitConfig.TrimApiUrl(apiUrl) ?? string.Empty).ToLowerInvariant();
            string key = apiKey?.Trim() ?? string.Empty;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url + "\n" + key));
                StringBuilder text = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>A name kept for one game: <paramref name="name"/> followed by the game's key; the name alone for what every game shares (null).</summary>
        internal static string Named(string name, string gameKey) => gameKey == null ? name : name + "-" + gameKey;
    }
}
