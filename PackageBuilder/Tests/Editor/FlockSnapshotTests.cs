using System;
using System.IO;
using Flock.Exceptions;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // Direct unit coverage of the on-disk snapshot store (envelope versioning, corruption handling,
    // atomic overwrite, scope delete, version prune, collision-safe keys). No FlockClient needed.
    public class FlockSnapshotStoreTests
    {
        private string _dir;
        private FlockSnapshotStore _store;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "flock_snap_" + Guid.NewGuid().ToString("N"));
            _store = new FlockSnapshotStore(_dir, null);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        private static SnapshotProbe Probe(string v) => new SnapshotProbe { Value = v };
        private string SingleJsonFile() => Directory.GetFiles(_dir, "*.json", SearchOption.AllDirectories)[0];

        [Test]
        public void RoundTrip_WriteThenRead_ReturnsValue()
        {
            _store.Write("scope", "key", Probe("x"));

            Assert.IsTrue(_store.TryRead("scope", "key", out SnapshotProbe value));
            Assert.AreEqual("x", value.Value);
        }

        // ---- SNAP-09 ----
        [Test]
        public void TryRead_EnvelopeVersionMismatch_ReturnsFalse_AndDeletes()
        {
            _store.Write("scope", "key", Probe("x"));
            string file = SingleJsonFile();
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"v\":1", "\"v\":2"));

            bool ok = _store.TryRead("scope", "key", out SnapshotProbe value);

            Assert.IsFalse(ok);
            Assert.IsNull(value);
            Assert.IsFalse(File.Exists(file), "A version-mismatched snapshot is deleted on read.");
        }

        // ---- SNAP-10 ----
        [Test]
        public void TryRead_CorruptJson_ReturnsFalse_AndDeletes()
        {
            _store.Write("scope", "key", Probe("x"));
            string file = SingleJsonFile();
            File.WriteAllText(file, "{ this is not json ");

            bool ok = _store.TryRead("scope", "key", out SnapshotProbe value);

            Assert.IsFalse(ok);
            Assert.IsFalse(File.Exists(file), "A corrupt snapshot is deleted on read.");
        }

        // ---- SNAP-11 ----
        [Test]
        public void Write_Overwrite_ReplacesCleanly_NoTmpLeftover()
        {
            _store.Write("scope", "key", Probe("v1"));
            _store.Write("scope", "key", Probe("v2"));

            Assert.IsTrue(_store.TryRead("scope", "key", out SnapshotProbe value));
            Assert.AreEqual("v2", value.Value, "Second write overwrites the first.");
            Assert.AreEqual(0, Directory.GetFiles(_dir, "*.tmp", SearchOption.AllDirectories).Length, "No leftover .tmp after an atomic swap.");
        }

        // ---- SNAP-12 ----
        [Test]
        public void DeleteScope_RemovesEntries()
        {
            _store.Write("myscope", "key", Probe("x"));
            Assert.IsTrue(_store.TryRead<SnapshotProbe>("myscope", "key", out _));

            _store.DeleteScope("myscope");

            Assert.IsFalse(_store.TryRead<SnapshotProbe>("myscope", "key", out _));
        }

        // ---- SNAP-13 ----
        [Test]
        public void PruneOtherVersions_KeepsCurrentAndBootstrap_DeletesOthers()
        {
            _store.Write("gv-keep/cat", "k", Probe("keep"));
            _store.Write("gv-old/cat", "k", Probe("old"));
            _store.Write("bootstrap/cat", "k", Probe("boot"));

            _store.PruneOtherVersions("gv-keep");

            Assert.IsTrue(_store.TryRead<SnapshotProbe>("gv-keep/cat", "k", out _), "Current version kept.");
            Assert.IsTrue(_store.TryRead<SnapshotProbe>("bootstrap/cat", "k", out _), "Bootstrap scope kept.");
            Assert.IsFalse(_store.TryRead<SnapshotProbe>("gv-old/cat", "k", out _), "Other versions pruned.");
        }

        // ---- SNAP-13b: state must outlive a version bump (X11) ----
        [Test]
        public void PruneOtherVersions_NeverTouchesTheStateScope()
        {
            // The defect: the offline write queue used to live at {version}/command/{player}, so shipping a
            // build with a new GameVersionId deleted the player's unsent writes — no error, no log.
            string state = $"{FlockSnapshotStore.StateScope}/command/p-1";
            _store.Write(state, "pending_writes", Probe("queued"));
            _store.Write("gv-old/cat", "k", Probe("cached"));

            _store.PruneOtherVersions("gv-new");

            Assert.IsTrue(_store.TryRead<SnapshotProbe>(state, "pending_writes", out _),
                "Queued writes must survive a game-version change.");
            Assert.IsFalse(_store.TryRead<SnapshotProbe>("gv-old/cat", "k", out _),
                "Cached answers from another version must still be pruned.");

            _store.PruneOtherVersions("gv-newer-still");
            Assert.IsTrue(_store.TryRead<SnapshotProbe>(state, "pending_writes", out _),
                "And must survive every subsequent one too.");
        }

        // ---- SNAP-13c: the pre-fix queue is rescued, not pruned (X11) ----
        [Test]
        public void MigrateLegacyState_MovesAQueueOutOfTheVersionTree_BeforeItIsPruned()
        {
            _store.Write("gv-old/command/p-1", "pending_writes", Probe("queued"));
            _store.Write("gv-old/cat", "k", Probe("cached"));

            Assert.AreEqual(1, _store.MigrateLegacyState("command"), "One queue file should have moved.");
            _store.PruneOtherVersions("gv-new");

            Assert.IsTrue(_store.TryRead<SnapshotProbe>(
                    $"{FlockSnapshotStore.StateScope}/command/p-1", "pending_writes", out SnapshotProbe kept),
                "The queued write should have survived the upgrade.");
            Assert.AreEqual("queued", kept.Value);
            Assert.IsFalse(_store.TryRead<SnapshotProbe>("gv-old/command/p-1", "pending_writes", out _),
                "And should no longer be in the version tree.");
            Assert.IsFalse(_store.TryRead<SnapshotProbe>("gv-old/cat", "k", out _),
                "Cache beside it is left for the pruner — the migration moves state and nothing else.");
        }

        // ---- SNAP-13d ----
        [Test]
        public void MigrateLegacyState_IsIdempotent_AndPrefersTheNewerLayout()
        {
            // It runs on every Create(), so it has to be idempotent rather than merely correct once.
            _store.Write("gv-old/command/p-1", "pending_writes", Probe("legacy"));
            Assert.AreEqual(1, _store.MigrateLegacyState("command"));
            Assert.AreEqual(0, _store.MigrateLegacyState("command"), "Second run has nothing left to move.");

            // Two versions can each hold a queue for one player — one from before an upgrade, one from a
            // build that was rolled back. The newer layout wins; the point is that neither is left in the
            // version tree for the pruner to eat.
            _store.Write("gv-older/command/p-1", "pending_writes", Probe("even-older"));
            _store.MigrateLegacyState("command");

            Assert.IsTrue(_store.TryRead<SnapshotProbe>(
                $"{FlockSnapshotStore.StateScope}/command/p-1", "pending_writes", out SnapshotProbe kept));
            Assert.AreEqual("legacy", kept.Value, "The already-migrated copy should win.");
            Assert.IsFalse(_store.TryRead<SnapshotProbe>("gv-older/command/p-1", "pending_writes", out _),
                "And the legacy copy must not be left behind for the pruner.");
        }

        // ---- SNAP-13e ----
        [Test]
        public void StateScope_CannotCollideWithARealGameVersion()
        {
            // A version id is a ULID ([0-9A-Z]) so it can never sanitize to the reserved name, and an unset
            // version sanitizes to "_" rather than to it. Without both, the pruner would either eat state or
            // spare a stale version — and neither would be visible.
            _store.Write($"{FlockSnapshotStore.StateScope}/command/p-1", "pending_writes", Probe("queued"));
            _store.PruneOtherVersions("01KZ3ZY8RQHTXRK9VS8H89P296");

            Assert.IsTrue(_store.TryRead<SnapshotProbe>(
                    $"{FlockSnapshotStore.StateScope}/command/p-1", "pending_writes", out _),
                "A realistic ULID version must not collide with the reserved state scope.");
            Assert.IsTrue(FlockSnapshotStore.StateScope.StartsWith("_"),
                "The reserved name must not look like a ULID.");
        }

        // ---- SNAP-14 ----
        [Test]
        public void Keys_WithUnsafeChars_DoNotCollide_ViaHashSuffix()
        {
            // Both keys sanitize to "a_b_c" on disk, but the hash suffix of the raw key keeps them distinct.
            _store.Write("scope", "a/b:c", Probe("x1"));
            _store.Write("scope", "a_b_c", Probe("x2"));

            Assert.IsTrue(_store.TryRead("scope", "a/b:c", out SnapshotProbe p1));
            Assert.IsTrue(_store.TryRead("scope", "a_b_c", out SnapshotProbe p2));
            Assert.AreEqual("x1", p1.Value);
            Assert.AreEqual("x2", p2.Value, "Sanitized-identical keys stay distinct via the hash suffix.");
        }
    }

    // Coverage of the server-first / cache-fallback read logic in FlockProviderBase (FetchWithSnapshotAsync
    // -> FetchAtScopeAsync), driven through FlockTestSnapshotProvider so it isn't tied to a feature's wire shape.
    public class FlockSnapshotFallbackTests
    {
        private const string Cat = "probecat";
        private const string Key = "k1";
        private const string Path = "probe/thing";

        private static FlockTestSnapshotProvider Provider(FlockTestClient h) => new FlockTestSnapshotProvider(h.Client);
        private static SnapshotProbe Probe(string v) => new SnapshotProbe { Value = v };

        // ---- SNAP-01 ----
        [Test]
        public void ColdCache_ServerSuccess_ReturnsAndWritesSnapshot()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Ok("{\"value\":\"srv\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                FlockTestSnapshotProvider p = Provider(h);

                SnapshotProbe r = h.Run(() => p.FetchAsync(Cat, Key, Path));

                Assert.AreEqual("srv", r.Value);
                Assert.IsTrue(p.TryReadCached(Cat, Key, out SnapshotProbe cached), "Server result is snapshotted.");
                Assert.AreEqual("srv", cached.Value);
            }
        }

        // ---- SNAP-02 ----
        [Test]
        public void WarmCache_ServerSuccess_OverwritesSnapshot()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Ok("{\"value\":\"v2\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                FlockTestSnapshotProvider p = Provider(h);
                p.Seed(Cat, Key, Probe("v1"));

                SnapshotProbe r = h.Run(() => p.FetchAsync(Cat, Key, Path));

                Assert.AreEqual("v2", r.Value, "Server-first: fresh value wins over cache.");
                Assert.IsTrue(p.TryReadCached(Cat, Key, out SnapshotProbe cached));
                Assert.AreEqual("v2", cached.Value, "Snapshot overwritten (no TTL).");
            }
        }

        // ---- SNAP-03 (+ one-attempt-then-fallback) ----
        [Test]
        public void WarmCache_ConnectionError_ServesCachedSnapshot()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Offline());
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true); // reachable, but the request itself fails at the transport
                FlockTestSnapshotProvider p = Provider(h);
                p.Seed(Cat, Key, Probe("cached"));

                SnapshotProbe r = h.Run(() => p.FetchAsync(Cat, Key, Path));

                Assert.AreEqual("cached", r.Value, "A failed request with a cached copy serves the snapshot.");
                Assert.AreEqual(1, transport.CountTo(Path), "One attempt, then fall back to cache.");
            }
        }

        // ---- SNAP-04 ----
        [Test]
        public void WarmCache_Unreachable_ShortCircuitsToCache_NoNetwork()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Ok("{\"value\":\"srv\"}")); // would win if the network were hit
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockTestSnapshotProvider p = Provider(h);
                p.Seed(Cat, Key, Probe("cached"));
                h.SetReachable(false);

                SnapshotProbe r = h.Run(() => p.FetchAsync(Cat, Key, Path));

                Assert.AreEqual("cached", r.Value, "Unreachable + cached -> skip the network entirely.");
                Assert.AreEqual(0, transport.CountTo(Path), "Network not touched when unreachable with a cache.");
            }
        }

        // ---- SNAP-05 ----
        [Test]
        public void ColdCache_ConnectionError_Throws()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Offline());
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                FlockTestSnapshotProvider p = Provider(h);

                Assert.Catch<FlockNetworkException>(() => h.Run(() => p.FetchAsync(Cat, Key, Path)));
            }
        }

        // ---- SNAP-06 ----
        [Test]
        public void WarmCache_PermanentError_Throws_DoesNotServeCache()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Coded(404, "not_found"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                FlockTestSnapshotProvider p = Provider(h);
                p.Seed(Cat, Key, Probe("cached"));

                Assert.Catch<FlockException>(() => h.Run(() => p.FetchAsync(Cat, Key, Path)));
                Assert.IsTrue(p.TryReadCached(Cat, Key, out SnapshotProbe cached));
                Assert.AreEqual("cached", cached.Value, "Permanent error must not overwrite the cache.");
            }
        }

        // ---- SNAP-08 ----
        [Test]
        public void MalformedSuccess_Throws_NoCacheFallback()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Ok("this is not json"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                FlockTestSnapshotProvider p = Provider(h);
                p.Seed(Cat, Key, Probe("cached"));

                // A live-but-malformed 2xx is a serialization failure (non-retryable) and must NOT serve the cache.
                Assert.Catch<FlockException>(() => h.Run(() => p.FetchAsync(Cat, Key, Path)));
                Assert.IsTrue(p.TryReadCached(Cat, Key, out SnapshotProbe cached));
                Assert.AreEqual("cached", cached.Value, "Malformed success must not overwrite the cache.");
            }
        }

        // ---- SNAP-16 ----
        [Test]
        public void OfflineCacheDisabled_BypassesCache_OfflineThrows()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Offline());
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.EnableOfflineCache = false))
            {
                Assert.IsNull(h.Client.SnapshotStore, "EnableOfflineCache=false -> no snapshot store.");
                h.SetReachable(true);
                FlockTestSnapshotProvider p = Provider(h);

                Assert.Catch<FlockNetworkException>(() => h.Run(() => p.FetchAsync(Cat, Key, Path)));
            }
        }
    }
}
