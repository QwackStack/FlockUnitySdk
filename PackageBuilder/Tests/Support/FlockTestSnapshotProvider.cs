using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Http;
using Newtonsoft.Json;

namespace Flock.Tests.Support
{
    // Minimal serializable payload for snapshot tests.
    public sealed class SnapshotProbe
    {
        [JsonProperty("value")] public string Value { get; set; }
    }

    // Exposes FlockProviderBase's protected snapshot plumbing so the server-first / cache-fallback
    // logic can be tested provider-agnostically, without coupling to a specific feature's wire shape.
    public sealed class FlockTestSnapshotProvider : FlockProviderBase
    {
        public FlockTestSnapshotProvider(FlockClient client) : base(client) { }

        // Runs the real snapshot-backed fetch (server-first, cache fallback, no TTL) against `path`.
        public Task<SnapshotProbe> FetchAsync(string category, string key, string path, CancellationToken cancellationToken = default)
            => FetchWithSnapshotAsync(category, key,
                () => FlockHttpClient.GetAsync<SnapshotProbe>($"{Client.GetVersionedApiUrl()}/{path}", Client.GetBaseHeaders(), cancellationToken),
                "test snapshot fetch", cancellationToken);

        public bool TryReadCached(string category, string key, out SnapshotProbe value) => TryReadSnapshot(category, key, out value);
        public void Seed(string category, string key, SnapshotProbe value) => WriteSnapshot(category, key, value);
    }
}
