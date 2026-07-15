using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
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
    }
}
