using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    // Hermetic coverage of the offline write queue (catalog Feature 1 / CMD). "Offline" is the reachability
    // seam forced false; the fake transport still responds so pre-offline row resolution can succeed.
    // Overlay tests use string-typed fields so they don't couple to the numeric DataFieldValueConverter —
    // the append/replace/preserve-type mechanic is type-agnostic. CMD-04 (achievement offline) and the
    // trigger-wiring rows (CMD-19..22) are deferred to their own slices.
    public class FlockCommandProviderTests
    {
        private static DataField Field(string name, string value, string type = "string")
            => new DataField { FieldName = name, Value = value, Type = type };

        private static string FieldJson(DataField f)
            => "{\"field_name\":\"" + f.FieldName + "\",\"type\":\"" + f.Type + "\",\"value\":\"" + f.Value + "\"}";

        private static string RowJson(string playerId, string templateId, string playerDataId, List<DataField> data)
        {
            List<string> fields = new List<string>();
            foreach (DataField f in data)
                fields.Add(FieldJson(f));
            return "{\"id\":\"" + playerDataId + "\",\"player_template_id\":\"" + templateId
                + "\",\"player_id\":\"" + playerId + "\",\"data\":[" + string.Join(",", fields) + "]}";
        }

        // Prime the player-data row cache the way a real fetch would (GetMyDataByTemplateAsync paginates
        // player_data and caches by template) so offline overlays can find the row via TryGetCachedRow.
        private static PlayerData PrimeCachedRow(FlockTestClient h, string playerId, string templateId, string playerDataId, List<DataField> data)
        {
            string page = "{\"items\":[" + RowJson(playerId, templateId, playerDataId, data) + "],\"total\":1,\"page\":1,\"limit\":100}";
            h.Transport.On("player_data?page=", FlockFakeTransport.Ok(page));
            h.SetReachable(true);
            PlayerData primed = h.Run(() => h.Client.Player.GetMyDataByTemplateAsync(templateId));
            Assert.IsNotNull(primed, "priming fetch should return the seeded row");
            return primed;
        }

        // Enqueue two offline writes to different rows (no cache needed — proves enqueue without a cached row).
        private static void EnqueueTwoOfflineUpdates(FlockTestClient h)
        {
            h.SetReachable(false);
            h.Run(() => h.Client.Commands.UpdatePlayerDataAsync("pd-1", new List<DataField> { Field("a", "1") }));
            h.Run(() => h.Client.Commands.UpdatePlayerDataAsync("pd-2", new List<DataField> { Field("b", "2") }));
        }

        // ---- CMD-01: offline update enqueues + overlays the cached row in place ----
        [Test]
        public void UpdatePlayerData_Offline_Enqueues_And_OverlaysCachedRow()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                PrimeCachedRow(h, "player-a", "tpl-1", "pd-1", new List<DataField> { Field("score", "10") });
                h.SetReachable(false);

                PlayerData result = h.Run(() => h.Client.Commands.UpdatePlayerDataAsync(
                    "pd-1", new List<DataField> { Field("score", "42") }));

                Assert.IsNotNull(result, "Cached row should be returned optimistically.");
                DataField score = result.Data.Find(f => f.FieldName == "score");
                Assert.AreEqual("42", score.Value, "Optimistic overlay should win in the cached row.");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "No POST while offline.");
            }
        }

        // ---- CMD-02: offline update with no cached row still enqueues, returns null ----
        [Test]
        public void UpdatePlayerData_Offline_NoCachedRow_ReturnsNull_ButStillEnqueues()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(false);

                PlayerData result = h.Run(() => h.Client.Commands.UpdatePlayerDataAsync(
                    "pd-unseen", new List<DataField> { Field("score", "42") }));

                Assert.IsNull(result, "Nothing cached to overlay -> null optimistic value (documented).");

                // Reconnect + flush: the queued write must replay, proving it was enqueued despite the null return.
                transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-unseen\"}"));
                h.SetReachable(true);
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Queued write replays on flush.");
            }
        }

        // ---- CMD-03: field update overlay keeps the existing type when the change omits it ----
        [Test]
        public void UpdatePlayerDataField_Offline_Overlay_PreservesExistingType()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                PrimeCachedRow(h, "player-a", "tpl-1", "pd-1", new List<DataField> { Field("coins", "5", "string") });
                h.SetReachable(false);

                PlayerData result = h.Run(() => h.Client.Commands.UpdatePlayerDataFieldAsync("pd-1", "coins", "9"));

                DataField coins = result.Data.Find(f => f.FieldName == "coins");
                Assert.AreEqual("string", coins.Type, "Overlay must keep the existing type when the change omits it.");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.CommandUpdatePlayerDataKey), "No POST while offline.");
            }
        }

        // ---- CMD-05: AddGameFunds offline never grants (money is not queued) ----
        [Test]
        public void AddGameFunds_Offline_Throws_And_SendsNoFundsPost()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(false);

                // Whether the wallet resolves to null (validation) or the money gate trips (network), the invariant
                // is the same: no add_game_funds POST is ever sent offline. Assert.Catch accepts any FlockException
                // subclass (Assert.Throws is exact-type).
                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Commands.AddGameFundsAsync("gold", 100, "curr-tpl-1")));
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.CommandAddGameFunds), "Money must never be granted offline.");
            }
        }

        // ---- CMD-23: empty ids fail validation before any network ----
        [Test]
        public void Update_EmptyIds_ThrowValidation_WithoutNetwork()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Commands.UpdatePlayerDataAsync("", new List<DataField>())));
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Commands.UpdatePlayerDataFieldAsync("pd-1", "", "1")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation should short-circuit before any request.");
            }
        }

        // ---- CMD wire-shape guard: the update payload's `data` must serialize as a JSON object (dict),
        // not the DataField descriptor array. Regression guard for the HTTP 422 the backend returns on an array. ----
        [Test]
        public void UpdatePlayerData_Online_SerializesDataAsObject_NotArray()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData,
                FlockFakeTransport.Ok(RowJson("player-a", "tpl-1", "pd-1", new List<DataField> { Field("score", "42") })));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                List<DataField> data = new List<DataField>
                {
                    Field("score", "42"),
                    new DataField
                    {
                        FieldName = "profile", Type = "object",
                        Value = new List<DataField> { Field("name", "neo") }
                    }
                };
                h.Run(() => h.Client.Commands.UpdatePlayerDataAsync("pd-1", data));

                FlockHttpRequest sent = h.Transport.LastTo(FlockEndpoints.CommandUpdatePlayerData);
                Assert.IsNotNull(sent, "Update should have hit the command endpoint.");

                JObject body = JObject.Parse(sent.JsonBody);
                Assert.AreEqual(JTokenType.Object, body["data"].Type,
                    "`data` must serialize as a JSON object, not the DataField array (backend 422s on an array).");
                Assert.AreEqual("42", (string)body["data"]["score"], "Scalar field should flatten to key -> value.");
                Assert.AreEqual(JTokenType.Object, body["data"]["profile"].Type, "Nested object field should flatten to a dict.");
                Assert.AreEqual("neo", (string)body["data"]["profile"]["name"], "Nested field should flatten by name.");
            }
        }

        // ---- CMD-06 + CMD-07: reconnect flush replays FIFO and drains (2nd flush is a no-op) ----
        [Test]
        public void Flush_Reconnect_ReplaysAllQueuedWrites_ThenDrains()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                EnqueueTwoOfflineUpdates(h);

                h.SetReachable(true);
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Both queued writes replay.");

                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Drained queue -> no extra POSTs.");
            }
        }

        // ---- CMD-08: flush no-ops while the seam reports unreachable ----
        [Test]
        public void Flush_WhileUnreachable_SendsNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                EnqueueTwoOfflineUpdates(h); // leaves reachable=false

                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());

                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Unreachable -> flush is a no-op.");
            }
        }

        // ---- CMD-09: a transient failure halts the flush and keeps the queue ----
        [Test]
        public void Flush_TransientFailure_HaltsAndKeepsQueue()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            // First POST hits a transport error (transient); the sequence then serves OK (its sticky).
            transport.OnSequence(FlockEndpoints.CommandUpdatePlayerData,
                FlockFakeTransport.Offline(),
                FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                EnqueueTwoOfflineUpdates(h);

                h.SetReachable(true);
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Halt at the failed write; don't advance.");

                // Kept in the queue -> the next flush (now OK) drains both.
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(3, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Retry replays the kept writes.");
            }
        }

        // ---- CMD-10: a 401 (auth) is recoverable -> keep queued, don't drop ----
        [Test]
        public void Flush_AuthFailure_KeepsQueued()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Coded(401, "player.invalid_refresh_token"));
            // Refresh succeeds so the player stays authed; the write still 401s, isolating "auth failure keeps queued".
            string refreshOk = "{\"player_id\":\"player-a\",\"access_token\":\"" + FlockTestClient.MakeJwt("player-a", 3600, "refreshed") + "\",\"refresh_token\":\"refresh-2\"}";
            transport.On(FlockEndpoints.PlayerTokenRefresh, FlockFakeTransport.Ok(refreshOk));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                EnqueueTwoOfflineUpdates(h);

                h.SetReachable(true);
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());

                int keptBase = transport.CountTo(FlockEndpoints.CommandUpdatePlayerData);
                // Recover: neither write was dropped -> both replay once the endpoint is healthy again.
                transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());

                Assert.AreEqual(keptBase + 2, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Auth failure kept both writes queued for retry.");
            }
        }

        // ---- CMD-11: a permanent 4xx drops the offending write; the flush continues past it ----
        [Test]
        public void Flush_PermanentFailure_DropsWrite_And_Continues()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            // First write permanently rejected (400), second succeeds -> queue must end empty.
            transport.OnSequence(FlockEndpoints.CommandUpdatePlayerData,
                FlockFakeTransport.Coded(400, "player.invalid_update"),
                FlockFakeTransport.Ok("{\"id\":\"pd-2\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                EnqueueTwoOfflineUpdates(h);

                h.SetReachable(true);
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Dropped write + continued to the next.");

                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Queue drained after drop+success.");
            }
        }

        // ---- CMD-11/12: a permanent drop evicts the optimistic row when no other queued write targets it ----
        [Test]
        public void Flush_PermanentFailure_EvictsOptimisticRow_WhenSoleWriteToThatRow()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Coded(400, "player.invalid_update"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                PrimeCachedRow(h, "player-a", "tpl-1", "pd-1", new List<DataField> { Field("score", "1") });
                h.SetReachable(false);
                h.Run(() => h.Client.Commands.UpdatePlayerDataAsync("pd-1", new List<DataField> { Field("score", "2") }));

                h.SetReachable(true);
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());

                Assert.IsNull(h.Client.Player.TryGetCachedRow("pd-1"), "Rejected sole write must evict its optimistic row.");
            }
        }

        // ---- CMD-12: eviction is skipped when another still-queued write targets the same row ----
        [Test]
        public void Flush_PermanentFailure_KeepsOverlay_WhenAnotherQueuedWriteTargetsSameRow()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            // Two writes to the SAME row pd-1: first rejected (400), second succeeds.
            transport.OnSequence(FlockEndpoints.CommandUpdatePlayerData,
                FlockFakeTransport.Coded(400, "player.invalid_update"),
                FlockFakeTransport.Ok("{\"id\":\"pd-1\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                PrimeCachedRow(h, "player-a", "tpl-1", "pd-1", new List<DataField> { Field("score", "1") });
                h.SetReachable(false);
                h.Run(() => h.Client.Commands.UpdatePlayerDataAsync("pd-1", new List<DataField> { Field("score", "2") }));
                h.Run(() => h.Client.Commands.UpdatePlayerDataAsync("pd-1", new List<DataField> { Field("score", "3") }));

                h.SetReachable(true);
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());

                Assert.IsNotNull(h.Client.Player.TryGetCachedRow("pd-1"), "Overlay row must survive while another write targets it.");
            }
        }

        // ---- CMD-13: a re-entrant flush is a no-op (single-flight guard) ----
        [Test]
        public void Flush_IsSingleFlight_NoDoublePost()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                EnqueueTwoOfflineUpdates(h);
                h.SetReachable(true);

                Task first = h.Client.Commands.FlushPendingWritesAsync();
                Task second = h.Client.Commands.FlushPendingWritesAsync();
                h.Run(() => Task.WhenAll(first, second));

                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Single-flight -> each write POSTed exactly once.");
            }
        }

        // ---- CMD-14: an offline write survives a client relaunch (persisted to the snapshot store) ----
        [Test]
        public void OfflineWrite_SurvivesRelaunch_ThenFlushes()
        {
            string sharedDir = Path.Combine(Path.GetTempPath(), "flock_relaunch_" + Guid.NewGuid().ToString("N"));
            try
            {
                // First "launch": enqueue offline, then Shutdown (process death leaves disk intact; do NOT Dispose,
                // which would delete sharedDir).
                FlockFakeTransport transport1 = new FlockFakeTransport();
                FlockTestClient h1 = FlockTestClient.Create(transport1, config => config.OfflineCacheDirectory = sharedDir);
                h1.LoginAs("player-a");
                h1.SetReachable(false);
                h1.Run(() => h1.Client.Commands.UpdatePlayerDataAsync("pd-1", new List<DataField> { Field("a", "1") }));
                FlockClient.Shutdown();

                // Second "launch": a fresh client on the same dir rehydrates + replays.
                FlockFakeTransport transport2 = new FlockFakeTransport();
                transport2.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-1\"}"));
                using (FlockTestClient h2 = FlockTestClient.Create(transport2, config => config.OfflineCacheDirectory = sharedDir))
                {
                    h2.LoginAs("player-a");
                    h2.SetReachable(true);
                    h2.Run(() => h2.Client.Commands.FlushPendingWritesAsync());
                    Assert.AreEqual(1, transport2.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Persisted write replays after relaunch.");
                }
            }
            finally
            {
                if (Directory.Exists(sharedDir))
                    Directory.Delete(sharedDir, true);
            }
        }

        // ---- CMD-15 + CMD-17: player A's offline queue never replays under player B ----
        [Test]
        public void Queue_IsPlayerScoped_DoesNotReplayUnderAnotherPlayer()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-1\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                // Player A queues one offline write.
                h.LoginAs("player-a");
                h.SetReachable(false);
                h.Run(() => h.Client.Commands.UpdatePlayerDataAsync("pd-1", new List<DataField> { Field("a", "1") }));

                // Switch to B and flush online: B's queue is empty, so nothing replays under B.
                h.Client.Authentication.Logout();
                h.LoginAs("player-b");
                h.SetReachable(true);
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Player A's writes must not replay under player B.");

                // Back to A: the persisted queue is still under A's key and now replays.
                h.Client.Authentication.Logout();
                h.LoginAs("player-a");
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Player A's own queue replays when A returns.");
            }
        }

        // ---- CMD-18: overlay appends new fields, replaces by name, and keeps type on omission ----
        [Test]
        public void Overlay_AppendsNew_ReplacesByName_KeepsTypeOnOmission()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                PrimeCachedRow(h, "player-a", "tpl-1", "pd-1", new List<DataField>
                {
                    Field("name", "duck", "string"),
                    Field("coins", "5", "string")
                });
                h.SetReachable(false);

                PlayerData result = h.Run(() => h.Client.Commands.UpdatePlayerDataAsync("pd-1", new List<DataField>
                {
                    new DataField { FieldName = "coins", Value = "9" }, // no Type -> should inherit "string"
                    Field("level", "3", "string")                       // new -> appended
                }));

                DataField coins = result.Data.Find(f => f.FieldName == "coins");
                DataField name = result.Data.Find(f => f.FieldName == "name");
                DataField level = result.Data.Find(f => f.FieldName == "level");

                Assert.AreEqual("9", coins.Value, "Replace-by-name: coins updated.");
                Assert.AreEqual("string", coins.Type, "Type preserved when the change omits it.");
                Assert.IsNotNull(name, "Untouched field survives.");
                Assert.AreEqual("string", name.Type, "Untouched field keeps its type.");
                Assert.IsNotNull(level, "New field appended.");
                Assert.AreEqual("3", level.Value);
            }
        }

        // ---- CMD-16 (Known-bug, decisions.md §13): a player switch mid-flush must not replay A's writes under B.
        // Real reproduction via the gated transport, as a non-blocking [UnityTest] (yields so the gated flush
        // continuation pumps instead of dead-locking the main thread). Asserts the intended-correct behaviour
        // (A's token), which currently fails -> stays [Ignore] until the bug is fixed.
        [UnityTest]
        [Ignore("exposes CMD-16: mid-flush player switch replays under the wrong player's auth (decisions.md §13)")]
        public IEnumerator Flush_PlayerSwitchMidFlight_DoesNotReplayUnderWrongPlayer()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-x\"}"));
            transport.GateNext(FlockEndpoints.CommandUpdatePlayerData); // hold the first replay POST open
            FlockTestClient h = FlockTestClient.Create(transport);
            try
            {
                h.LoginAs("player-a");
                string bearerA = h.Client.GetBaseHeaders()["Authorization"];
                EnqueueTwoOfflineUpdates(h); // player-a queues pd-1, pd-2 (leaves reachable=false)
                h.SetReachable(true);

                Task flush = h.Client.Commands.FlushPendingWritesAsync();
                int guard = 0;
                while (transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) < 1 && guard++ < 1000)
                    yield return null;

                // Switch player while the first write is in flight, then let the flush resume.
                h.Client.Authentication.Logout();
                h.LoginAs("player-b");
                transport.ReleaseGate();

                guard = 0;
                while (!flush.IsCompleted && guard++ < 2000)
                    yield return null;

                Assert.IsTrue(flush.IsCompleted, "Flush completed.");
                List<FlockHttpRequest> posts = transport.AllTo(FlockEndpoints.CommandUpdatePlayerData);
                Assert.AreEqual(2, posts.Count, "Both queued writes were sent.");
                Assert.AreEqual(bearerA, posts[1].Headers["Authorization"], "Player A's queued write must not replay under player B's auth.");
            }
            finally
            {
                h.Dispose();
            }
        }
    }
}
