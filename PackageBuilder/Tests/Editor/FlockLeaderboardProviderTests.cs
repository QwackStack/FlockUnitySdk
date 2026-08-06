using System;
using System.IO;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // LeaderboardProvider: read-only standings / my-rank / around-me, all addressed by board NAME. Every read
    // resolves the name to an ID through /leaderboard/by-name first (memoized per session), then hits the ID
    // route. All four routes are enveloped (GenericResponse) despite standings taking page+limit — it is not a
    // PaginationResponse. My-rank and around-me are bearer-only and guard before anything, including the resolve;
    // standings and the name lookup are open to signed-out players.
    public class FlockLeaderboardProviderTests
    {
        private const string BoardName = "weekly_high_scores";

        private const string BoardBody =
            "{\"result\":{\"id\":\"lb-1\",\"name\":\"weekly_high_scores\",\"value_type\":\"integer\"," +
            "\"direction\":\"higher\",\"aggregation\":\"best\",\"window_type\":\"weekly\",\"scope\":\"global\"}}";

        private const string StandingsBody =
            "{\"result\":{\"window\":\"weekly\",\"total\":2,\"items\":[" +
            "{\"rank\":1,\"player_id\":\"p1\",\"player_name\":\"Ada\",\"score\":100.5,\"country\":\"US\",\"achieved_at\":\"2026-08-01T00:00:00Z\"}," +
            "{\"rank\":2,\"player_id\":\"p2\",\"player_name\":null,\"score\":90.0,\"country\":null,\"achieved_at\":\"2026-08-01T00:00:00Z\"}]}}";

        private static string ByName => FlockEndpoints.LeaderboardByName(BoardName);

        // Every read needs the name lookup answered first.
        private static FlockFakeTransport TransportWithBoard()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(ByName, FlockFakeTransport.Ok(BoardBody));
            return transport;
        }

        // ---- LB-01 ----
        [Test]
        public void GetStandings_Success_UnwrapsEnvelope()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Standings r = h.Run(() => h.Client.Leaderboard.GetStandingsAsync(BoardName));

                Assert.IsNotNull(r);
                Assert.AreEqual("weekly", r.Window);
                Assert.AreEqual(2, r.Total);
                Assert.AreEqual(2, r.Items.Count);
                Assert.AreEqual(1, r.Items[0].Rank);
                Assert.AreEqual(100.5d, r.Items[0].Score);
                Assert.IsNull(r.Items[1].PlayerName, "A nullable player_name must survive as null, not empty string.");
            }
        }

        // ---- LB-02: the envelope is real — a bare body must not be accepted ----
        [Test]
        public void GetStandings_BareBodyWithoutEnvelope_Throws()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"),
                FlockFakeTransport.Ok("{\"window\":\"weekly\",\"total\":2,\"items\":[]}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Leaderboard.GetStandingsAsync(BoardName)),
                    "Standings is enveloped; a root-level payload means the SDK type is wrong.");
            }
        }

        // ---- LB-03 (validation) ----
        [Test]
        public void GetStandings_EmptyName_ThrowsValidation()
        {
            FlockFakeTransport transport = TransportWithBoard();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Leaderboard.GetStandingsAsync("")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request, including the name lookup.");
            }
        }

        // ---- LB-04: outgoing query shape ----
        [Test]
        public void GetStandings_SendsFiltersAndPaging_InQuery()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                h.Run(() => h.Client.Leaderboard.GetStandingsAsync(
                    BoardName, FlockLeaderboardWindow.Period("2026-W31"), "SA", 3, 25));

                FlockHttpRequest sent = transport.LastTo(FlockEndpoints.LeaderboardById("lb-1"));
                Assert.IsTrue(sent.Url.Contains("window=2026-W31"), sent.Url);
                Assert.IsTrue(sent.Url.Contains("country=SA"), sent.Url);
                Assert.IsTrue(sent.Url.Contains("page=3"), sent.Url);
                Assert.IsTrue(sent.Url.Contains("limit=25"), sent.Url);
            }
        }

        // ---- LB-05: optional filters are omitted, never sent empty ----
        [Test]
        public void GetStandings_NullFilters_AreOmittedFromQuery()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                h.Run(() => h.Client.Leaderboard.GetStandingsAsync(BoardName));

                FlockHttpRequest sent = transport.LastTo(FlockEndpoints.LeaderboardById("lb-1"));
                Assert.IsFalse(sent.Url.Contains("window="), "Null window must be omitted, not sent empty.");
                Assert.IsFalse(sent.Url.Contains("country="), "Null country must be omitted, not sent empty.");
                Assert.IsTrue(sent.Url.Contains("page=1") && sent.Url.Contains("limit=50"), sent.Url);
            }
        }

        // ---- LB-05b: window is a KEY, not the board's window type ----
        // The dashboard (QwacksUI StandingsView.jsx) sends blank for the live window and `season:{id}` for a
        // past season; `never`/`weekly`/`seasonal` are board config and are never sent here.
        [Test]
        public void Window_FactoriesProduceWireKeys()
        {
            Assert.IsNull(FlockLeaderboardWindow.Current.ToWireValue(), "Current must omit the param.");
            Assert.IsNull(default(FlockLeaderboardWindow).ToWireValue(), "The struct default must behave as Current.");
            Assert.AreEqual("season:s-1", FlockLeaderboardWindow.Season("s-1").ToWireValue());
            Assert.AreEqual("2026-W31", FlockLeaderboardWindow.Period("2026-W31").ToWireValue());
        }

        // ---- LB-05c: a past season reaches the wire, url-encoded ----
        [Test]
        public void GetStandings_SeasonWindow_SendsEncodedSeasonKey()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                h.Run(() => h.Client.Leaderboard.GetStandingsAsync(BoardName, FlockLeaderboardWindow.Season("s-1")));

                string url = transport.LastTo(FlockEndpoints.LeaderboardById("lb-1")).Url;
                Assert.IsTrue(url.Contains("window=season%3As-1"), url);
            }
        }

        // ---- LB-06: bearer-only route fails fast, before the guaranteed 401 and before the name lookup ----
        [Test]
        public void GetMyRank_SignedOut_ThrowsAuth_AndSendsNothing()
        {
            FlockFakeTransport transport = TransportWithBoard();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.Throws<FlockAuthException>(() => h.Run(() => h.Client.Leaderboard.GetMyRankAsync(BoardName)));
                Assert.AreEqual(0, transport.Requests.Count, "The auth guard short-circuits before any request — the name lookup must not be spent either.");
            }
        }

        // ---- LB-07: an unranked player is a valid result, not an error ----
        [Test]
        public void GetMyRank_NoEntryYet_ReturnsNullRankAndScore()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardMe("lb-1"),
                FlockFakeTransport.Ok("{\"result\":{\"player_id\":\"player-a\",\"window\":\"weekly\",\"rank\":null,\"score\":null}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                PlayerRank r = h.Run(() => h.Client.Leaderboard.GetMyRankAsync(BoardName));

                Assert.IsNotNull(r);
                Assert.IsFalse(r.Rank.HasValue, "An unranked player returns a null rank rather than throwing.");
                Assert.IsFalse(r.Score.HasValue);
            }
        }

        // ---- LB-08: around-me sends the neighbour count as `n` ----
        [Test]
        public void GetAroundMe_SendsNeighbourCountAsN()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardAroundMe("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Leaderboard.GetAroundMeAsync(BoardName, 9));

                FlockHttpRequest sent = transport.LastTo(FlockEndpoints.LeaderboardAroundMe("lb-1"));
                Assert.IsTrue(sent.Url.Contains("n=9"), sent.Url);
            }
        }

        // ---- LB-09: standings fall back to the snapshot when the network is gone ----
        [Test]
        public void GetStandings_Offline_ServedFromSnapshot()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.Run(() => h.Client.Leaderboard.GetStandingsAsync(BoardName));
                int callsWhileOnline = transport.CountTo(FlockEndpoints.LeaderboardById("lb-1"));

                h.SetReachable(false);
                Standings cached = h.Run(() => h.Client.Leaderboard.GetStandingsAsync(BoardName));

                Assert.IsNotNull(cached);
                Assert.AreEqual(2, cached.Total);
                Assert.AreEqual(callsWhileOnline, transport.CountTo(FlockEndpoints.LeaderboardById("lb-1")),
                    "With a cached copy and no connectivity the network must be skipped entirely.");
            }
        }

        // ---- LB-10: one player's cached placement must never be served to the next player ----
        [Test]
        public void GetMyRank_CacheIsPlayerScoped_AcrossPlayerSwitch()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardMe("lb-1"),
                FlockFakeTransport.Ok("{\"result\":{\"player_id\":\"player-a\",\"window\":\"weekly\",\"rank\":1,\"score\":999.0}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                PlayerRank first = h.Run(() => h.Client.Leaderboard.GetMyRankAsync(BoardName));
                Assert.AreEqual(1, first.Rank);

                // Second player on the same device, with the network gone: there is no cache under their key.
                h.LoginAs("player-b");
                h.SetReachable(false);
                transport.GoOffline();

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Leaderboard.GetMyRankAsync(BoardName)),
                    "player-b must not be served player-a's cached rank.");
            }
        }

        // ---- LB-11: the board lookup unwraps its envelope and types every enum field ----
        [Test]
        public void GetByName_UnwrapsEnvelope_AndTypesEnums()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardByName("best_lap"), FlockFakeTransport.Ok(
                "{\"result\":{\"id\":\"lb-2\",\"name\":\"best_lap\",\"value_type\":\"duration\"," +
                "\"direction\":\"lower\",\"aggregation\":\"latest\",\"window_type\":\"seasonal\",\"scope\":\"country\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Leaderboard board = h.Run(() => h.Client.Leaderboard.GetByNameAsync("best_lap"));

                Assert.AreEqual("lb-2", board.Id);
                Assert.AreEqual("best_lap", board.Name);
                Assert.AreEqual(FlockLeaderboardValueType.DurationSeconds, board.ValueType, "`duration` maps to the seconds-named member.");
                Assert.AreEqual(FlockLeaderboardDirection.Lower, board.Direction);
                Assert.AreEqual(FlockLeaderboardAggregation.Latest, board.Aggregation);
                Assert.AreEqual(FlockLeaderboardWindowType.Seasonal, board.WindowType);
                Assert.AreEqual(FlockLeaderboardScope.Country, board.Scope);
                Assert.IsFalse(board.IsHigherBetter, "A `lower` board is a best-time board.");
            }
        }

        // ---- LB-12: the name→id resolve is memoized — repeated reads must not re-lookup ----
        [Test]
        public void Reads_ResolveNameOnce_PerSession()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                h.Run(() => h.Client.Leaderboard.GetStandingsAsync(BoardName));
                h.Run(() => h.Client.Leaderboard.GetStandingsAsync(BoardName, page: 2));
                string id = h.Run(() => h.Client.Leaderboard.ResolveIdAsync(BoardName));

                Assert.AreEqual("lb-1", id);
                Assert.AreEqual(1, transport.CountTo(ByName), "The name lookup is memoized; only the first read pays for it.");
            }
        }

        // ---- LB-13: the board name is url-encoded into the path ----
        [Test]
        public void GetByName_EncodesNameInPath()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardByName("weekly high"), FlockFakeTransport.Ok(BoardBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                h.Run(() => h.Client.Leaderboard.GetByNameAsync("weekly high"));

                string url = transport.LastTo("leaderboard/by-name/").Url;
                Assert.IsTrue(url.Contains("leaderboard/by-name/weekly%20high"), url);
            }
        }

        // ---- LB-14: a name this game doesn't have fails at the lookup, without touching the data route ----
        [Test]
        public void GetStandings_UnknownName_Throws_AndSkipsDataCall()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardByName("nope"), FlockFakeTransport.Status(404, "{\"detail\":\"not found\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Leaderboard.GetStandingsAsync("nope")));
                Assert.AreEqual(1, transport.Requests.Count, "The failed lookup is the only request — no data call on an unresolvable name.");
            }
        }

        // ---- LB-15: on a later offline launch the resolve itself is served from the snapshot ----
        [Test]
        public void Standings_OfflineRelaunch_ResolveAndDataBothFromSnapshot()
        {
            string sharedDir = Path.Combine(Path.GetTempPath(), "flock_lb_" + Guid.NewGuid().ToString("N"));

            FlockFakeTransport t1 = TransportWithBoard();
            t1.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            FlockTestClient h1 = FlockTestClient.Create(t1, config => config.OfflineCacheDirectory = sharedDir);
            h1.SetReachable(true);
            h1.Run(() => h1.Client.Leaderboard.GetStandingsAsync(BoardName));
            FlockClient.Shutdown(); // relaunch: leave the snapshots on disk (Dispose would delete the dir)

            try
            {
                FlockFakeTransport t2 = new FlockFakeTransport();
                using (FlockTestClient h2 = FlockTestClient.Create(t2, config => config.OfflineCacheDirectory = sharedDir))
                {
                    h2.SetReachable(false);

                    Standings offline = h2.Run(() => h2.Client.Leaderboard.GetStandingsAsync(BoardName));

                    Assert.AreEqual(2, offline.Total, "A previously-seen board still reads offline on a cold in-memory cache.");
                    Assert.AreEqual(0, t2.Requests.Count, "Neither the resolve nor the data call may hit the network offline.");
                }
            }
            finally
            {
                if (Directory.Exists(sharedDir))
                    Directory.Delete(sharedDir, true);
            }
        }

        // ---- LB-16: ClearCache drops the memo, so the next read resolves again ----
        [Test]
        public void ClearCache_ForcesNameLookupAgain()
        {
            FlockFakeTransport transport = TransportWithBoard();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.Run(() => h.Client.Leaderboard.GetStandingsAsync(BoardName));

                h.Client.Leaderboard.ClearCache();
                h.Run(() => h.Client.Leaderboard.GetStandingsAsync(BoardName));

                Assert.AreEqual(2, transport.CountTo(ByName), "ClearCache drops the name memo along with the snapshots.");
            }
        }

        // ---- LB-17: FormatScore renders per the board's value type; duration values are seconds ----
        [Test]
        public void FormatScore_RendersPerValueType()
        {
            Leaderboard integer = new Leaderboard { ValueType = FlockLeaderboardValueType.Integer };
            Leaderboard floating = new Leaderboard { ValueType = FlockLeaderboardValueType.Float };
            Leaderboard duration = new Leaderboard { ValueType = FlockLeaderboardValueType.DurationSeconds };

            Assert.AreEqual("12345", integer.FormatScore(12345d));
            Assert.AreEqual("90.5", floating.FormatScore(90.5d));
            Assert.AreEqual("100.57", floating.FormatScore(100.567d), "Float trims to two decimals.");
            Assert.AreEqual("1:05.250", duration.FormatScore(65.25d), "Seconds render as m:ss.fff.");
            Assert.AreEqual("1:01:01.500", duration.FormatScore(3661.5d), "Past an hour the clock gains an hours field.");
            Assert.AreEqual(string.Empty, duration.FormatScore(null), "An unranked player's null score formats as empty, not '0'.");
        }

        // ---- LB-18: an unranked player's PlayerRank formats cleanly through the board ----
        [Test]
        public void FormatScore_HandlesUnrankedPlayerRank()
        {
            Leaderboard board = new Leaderboard { ValueType = FlockLeaderboardValueType.Integer, Direction = FlockLeaderboardDirection.Higher };
            PlayerRank unranked = new PlayerRank { PlayerId = "p1", Window = "weekly", Rank = null, Score = null };

            Assert.IsTrue(board.IsHigherBetter);
            Assert.AreEqual(string.Empty, board.FormatScore(unranked.Score));
        }
    }
}
