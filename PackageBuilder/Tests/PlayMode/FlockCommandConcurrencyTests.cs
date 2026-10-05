using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // CMD single-flight under real concurrency. PlayMode because the gated flush suspends on Unity's
    // SynchronizationContext and only the player loop pumps the continuation (EditMode never does, so the
    // EditMode CMD-13 completes the first flush synchronously and never actually contends the guard). A gate
    // holds the first flush's POST open while a second flush is invoked in the same window; the single-flight
    // guard must make that second flush a no-op so the queue is never double-POSTed.
    public class FlockCommandConcurrencyTests
    {
        private FlockTestClient _h;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            // A test that failed while a request was held lets it go, so the run ends with that test's own failure.
            _h?.Transport.ReleaseGate();
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        private static DataField Field(string name, string value)
            => new DataField { FieldName = name, Value = value, Type = "string" };

        // ---- CMD-13 (real contention): a second flush invoked while the first is mid-POST is a no-op ----
        [UnityTest]
        public IEnumerator ConcurrentFlushes_SingleFlight_NoDoublePost()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));

            _h = FlockTestClient.Create(transport);
            _h.LoginAs("player-a");

            // Queue two offline writes (these do not suspend, so Run is safe in play mode).
            _h.SetReachable(false);
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-1", new List<DataField> { Field("a", "1") }));
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-2", new List<DataField> { Field("b", "2") }));

            // Hold the first replay POST open, then fire two flushes with no yield between them, so the first is
            // guaranteed to be the one that engages the gate and holds the single-flight lock.
            transport.GateNext(FlockEndpoints.CommandUpdatePlayerData);
            _h.SetReachable(true);
            Task first = _h.Client.Commands.FlushPendingWritesAsync();
            Task second = _h.Client.Commands.FlushPendingWritesAsync();

            int guard = 0;
            while (transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) < 1 && guard++ < 600)
                yield return null;

            // While the first flush is parked at the gate, only its POST exists — the re-entrant flush must not
            // POST again (a broken guard would double-POST the same still-queued write here).
            Assert.AreEqual(1, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData),
                "Re-entrant flush must be a no-op while the first flush holds the single-flight lock.");

            transport.ReleaseGate();

            Task all = Task.WhenAll(first, second);
            guard = 0;
            while ((!all.IsCompleted || transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) < 2) && guard++ < 600)
                yield return null;

            Assert.IsTrue(all.IsCompleted, "Both flush calls completed.");
            Assert.AreEqual(2, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData),
                "Single-flight: each queued write is POSTed exactly once despite two concurrent flushes.");
        }

        private static List<DataField> Level(string value) => new List<DataField> { Field("level", value) };

        private static List<string> RowsSent(FlockFakeTransport transport)
            => transport.AllTo(FlockEndpoints.CommandUpdatePlayerData).ConvertAll(r => (string)JObject.Parse(r.JsonBody)["player_data_id"]);

        // ---- CMD-16 (in flight): a write made while a flush is sending goes out after it, not beside it ----
        [UnityTest]
        public IEnumerator AWriteMadeWhileAFlushIsSendingGoesOutAfterIt()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));
            _h = FlockTestClient.Create(transport);
            _h.LoginAs("player-a");
            _h.SetReachable(false);
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-old", Level("5")));

            transport.GateNext(FlockEndpoints.CommandUpdatePlayerData);
            _h.SetReachable(true);
            Task flush = _h.Client.Commands.FlushPendingWritesAsync();
            yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) >= 1, "The older write is on its way");

            Task<PlayerData> newer = _h.Client.Commands.UpdatePlayerDataAsync("pd-new", Level("6"));
            Assert.IsTrue(newer.IsCompleted, "Queued, so the call does not wait for the flush");
            Assert.AreEqual(1, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Not sent beside the older write");

            transport.ReleaseGate();
            yield return FlockTestWait.Until(() => flush.IsCompleted && transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) >= 2, "Both sent");
            CollectionAssert.AreEqual(new[] { "pd-old", "pd-new" }, RowsSent(transport));
        }

        // ---- CMD-17: a player change mid-flush takes nothing off the next player's queue, and that queue is then sent ----
        [UnityTest]
        public IEnumerator APlayerChangeMidFlushTakesNothingOffTheNextPlayersQueue()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));
            _h = FlockTestClient.Create(transport);
            _h.SetReachable(false);
            _h.LoginAs("player-b");
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-b1", Level("1")));
            _h.Client.Authentication.Logout();
            _h.LoginAs("player-a");
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-a1", Level("1")));
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-a2", Level("2")));

            transport.GateNext(FlockEndpoints.CommandUpdatePlayerData);
            _h.SetReachable(true);
            Task flush = _h.Client.Commands.FlushPendingWritesAsync();
            yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) >= 1, "A's first write is on its way");

            _h.Client.Authentication.Logout();
            _h.LoginAs("player-b");
            Task<PlayerData> b2 = _h.Client.Commands.UpdatePlayerDataAsync("pd-b2", Level("2"));
            Assert.IsTrue(b2.IsCompleted, "Queued behind B's older write, so the call does not wait");
            transport.ReleaseGate();

            yield return FlockTestWait.Until(() => flush.IsCompleted && transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) >= 3, "B's queue was sent after A's flush stopped");
            CollectionAssert.AreEqual(new[] { "pd-a1", "pd-b1", "pd-b2" }, RowsSent(transport), "Nothing of B's was taken off unsent");

            _h.Client.Authentication.Logout();
            _h.LoginAs("player-a");
            _h.Run(() => _h.Client.Commands.FlushPendingWritesAsync());
            CollectionAssert.AreEqual(new[] { "pd-a1", "pd-b1", "pd-b2", "pd-a1", "pd-a2" }, RowsSent(transport),
                "A's writes stayed queued for A, the one in flight included, in order");
        }

        // ---- CMD-17b: after a player change mid-flush, none of the first player's writes goes out under the next one's sign-in ----
        [UnityTest]
        public IEnumerator APlayerChangeMidFlushSendsNothingMoreUnderTheNextPlayersSignIn()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));
            _h = FlockTestClient.Create(transport);
            _h.LoginAs("player-a");
            string bearerA = _h.Client.GetBaseHeaders()["Authorization"];
            _h.SetReachable(false);
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-a1", Level("1")));
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-a2", Level("2")));

            transport.GateNext(FlockEndpoints.CommandUpdatePlayerData);
            _h.SetReachable(true);
            Task flush = _h.Client.Commands.FlushPendingWritesAsync();
            yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) >= 1, "A's first write is on its way");

            // B signs in and writes nothing, so A's queue is still the one loaded.
            _h.Client.Authentication.Logout();
            _h.LoginAs("player-b");
            transport.ReleaseGate();
            yield return FlockTestWait.Until(() => flush.IsCompleted, "The flush ended");
            DateTime settle = DateTime.UtcNow.AddSeconds(0.5);
            while (DateTime.UtcNow < settle)
                yield return null;

            List<FlockHttpRequest> posts = transport.AllTo(FlockEndpoints.CommandUpdatePlayerData);
            Assert.AreEqual(1, posts.Count, "A's second write waits for A");
            Assert.AreEqual(bearerA, posts[0].Headers["Authorization"]);
        }

        // ---- CMD-18: the same player's queue reloaded mid-flush (signed out and back in) loses and reorders nothing ----
        [UnityTest]
        public IEnumerator AQueueReloadedMidFlushLosesAndReordersNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));
            _h = FlockTestClient.Create(transport);
            _h.LoginAs("player-a");
            _h.SetReachable(false);
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-a1", Level("1")));
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-a2", Level("2")));

            transport.GateNext(FlockEndpoints.CommandUpdatePlayerData);
            _h.SetReachable(true);
            Task flush = _h.Client.Commands.FlushPendingWritesAsync();
            yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) >= 1, "The first write is on its way");

            // Signed out, a write loads the signed-out queue; signing back in, the next write reloads A's from disk.
            _h.Client.Authentication.Logout();
            _h.SetReachable(false);
            _h.Run(() => _h.Client.Commands.UpdatePlayerDataAsync("pd-nobody", Level("0")));
            _h.SetReachable(true);
            _h.LoginAs("player-a");
            Task<PlayerData> a3 = _h.Client.Commands.UpdatePlayerDataAsync("pd-a3", Level("3"));
            Assert.IsTrue(a3.IsCompleted, "Queued behind the reloaded writes, so the call does not wait");
            transport.ReleaseGate();

            yield return FlockTestWait.Until(() => flush.IsCompleted && transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) >= 4, "The reloaded queue was sent");
            CollectionAssert.AreEqual(new[] { "pd-a1", "pd-a1", "pd-a2", "pd-a3" }, RowsSent(transport));
        }
    }
}
