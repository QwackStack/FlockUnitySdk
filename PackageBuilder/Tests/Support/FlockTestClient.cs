using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Config;
using Flock.Http;
using UnityEngine;

namespace Flock.Tests.Support
{
    // Creates a real FlockClient wired to a FlockFakeTransport, with a quiet analytics config,
    // retries off, offline cache on (temp dir), and a Dispose() that tears everything down.
    // Use inside `using (FlockTestClient h = FlockTestClient.Create(transport)) { ... }`.
    public sealed class FlockTestClient : IDisposable
    {
        private const string PrefAccessToken = "Flock.AccessToken";
        private const string PrefRefreshToken = "Flock.RefreshToken";
        private const string PrefAuthMethod = "flock_auth_method";

        public FlockClient Client { get; }
        public FlockFakeTransport Transport { get; }
        public string SnapshotDirectory { get; }

        private FlockTestClient(FlockClient client, FlockFakeTransport transport, string snapshotDir)
        {
            Client = client;
            Transport = transport;
            SnapshotDirectory = snapshotDir;
        }

        // `tweak` lets a test override the config (e.g. pin a fixed snapshot dir for a relaunch test).
        public static FlockTestClient Create(FlockFakeTransport transport, Action<FlockInitConfig> tweak = null)
        {
            ClearAuthPrefs();
            string dir = Path.Combine(Path.GetTempPath(), "flock_test_" + Guid.NewGuid().ToString("N"));

            FlockAnalyticsConfig analytics = new FlockAnalyticsConfig
            {
                PersistSessionOnDisk = false,
                AutoStartSession = false,
                HeartbeatIntervalSeconds = 0f,
                EventBufferFlushIntervalSeconds = 0f
            };

            FlockInitConfig config = new FlockInitConfig(
                "https://test.invalid", "test-key", "test-game", "1.0.0",
                analyticsConfig: analytics,
                retryPolicy: new RetryPolicy { MaxRetries = 0, InitialDelay = TimeSpan.Zero })
            {
                GameVersionId = "test-gvid",
                EnableOfflineCache = true,
                OfflineCacheDirectory = dir
            };
            tweak?.Invoke(config);

            FlockClient client = FlockClient.Create(config);
            // The constructor rebuilds the real transport from HttpTimeout — re-apply the fake after Create.
            FlockHttpClient.Configure(transport);
            return new FlockTestClient(client, transport, config.OfflineCacheDirectory);
        }

        // Force the reachability seam. false = the SDK treats the network as down.
        public void SetReachable(bool reachable) => Client.ReachabilityProbe = () => reachable;

        // Establish an authenticated player without the login round-trip (SetTokens is internal to Runtime).
        public void LoginAs(string playerId) => Client.SetTokens(MakeJwt(playerId, 3600), "refresh-" + playerId);

        // Safe to block inline: the fake always returns completed tasks, so every await resolves synchronously.
        public T Run<T>(Func<Task<T>> action) => action().GetAwaiter().GetResult();
        public void Run(Func<Task> action) => action().GetAwaiter().GetResult();

        // Unsigned JWT with just the claims the SDK reads; nonce keeps two tokens for the same player distinct.
        public static string MakeJwt(string playerId, int expiresInSeconds, string nonce = "0")
        {
            long exp = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToUnixTimeSeconds();
            string payload = $"{{\"sub\":\"{playerId}\",\"exp\":{exp},\"nonce\":\"{nonce}\"}}";
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return $"header.{encoded}.signature";
        }

        public void Dispose()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ClearAuthPrefs();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30)); // restore real transport
            try
            {
                if (Directory.Exists(SnapshotDirectory))
                    Directory.Delete(SnapshotDirectory, true);
            }
            catch (Exception)
            {
                // temp-dir cleanup is best-effort
            }
        }

        private static void ClearAuthPrefs()
        {
            PlayerPrefs.DeleteKey(PrefAccessToken);
            PlayerPrefs.DeleteKey(PrefRefreshToken);
            PlayerPrefs.DeleteKey(PrefAuthMethod);
        }
    }
}
