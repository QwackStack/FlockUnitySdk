using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // LeaderboardProvider: read-only standings / my-rank / around-me. All three routes are enveloped
    // (GenericResponse) despite standings taking page+limit — it is not a PaginationResponse. My-rank and
    // around-me are bearer-only and guard before the request; standings is open to signed-out players.
    public class FlockLeaderboardProviderTests
    {
        private const string StandingsBody =
            "{\"result\":{\"window\":\"weekly\",\"total\":2,\"items\":[" +
            "{\"rank\":1,\"player_id\":\"p1\",\"player_name\":\"Ada\",\"score\":100.5,\"country\":\"US\",\"achieved_at\":\"2026-08-01T00:00:00Z\"}," +
            "{\"rank\":2,\"player_id\":\"p2\",\"player_name\":null,\"score\":90.0,\"country\":null,\"achieved_at\":\"2026-08-01T00:00:00Z\"}]}}";

        // ---- LB-01 ----
        [Test]
        public void GetStandings_Success_UnwrapsEnvelope()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Standings r = h.Run(() => h.Client.Leaderboard.GetStandingsAsync("lb-1"));

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
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"),
                FlockFakeTransport.Ok("{\"window\":\"weekly\",\"total\":2,\"items\":[]}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Leaderboard.GetStandingsAsync("lb-1")),
                    "Standings is enveloped; a root-level payload means the SDK type is wrong.");
            }
        }

        // ---- LB-03 (validation) ----
        [Test]
        public void GetStandings_EmptyId_ThrowsValidation()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Leaderboard.GetStandingsAsync("")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request.");
            }
        }

        // ---- LB-04: outgoing query shape ----
        [Test]
        public void GetStandings_SendsFiltersAndPaging_InQuery()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                h.Run(() => h.Client.Leaderboard.GetStandingsAsync(
                    "lb-1", FlockLeaderboardWindow.Period("2026-W31"), "SA", 3, 25));

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
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                h.Run(() => h.Client.Leaderboard.GetStandingsAsync("lb-1"));

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
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                h.Run(() => h.Client.Leaderboard.GetStandingsAsync("lb-1", FlockLeaderboardWindow.Season("s-1")));

                string url = transport.LastTo(FlockEndpoints.LeaderboardById("lb-1")).Url;
                Assert.IsTrue(url.Contains("window=season%3As-1"), url);
            }
        }

        // ---- LB-06: bearer-only route fails fast, before the guaranteed 401 ----
        [Test]
        public void GetMyRank_SignedOut_ThrowsAuth_AndSendsNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.Throws<FlockAuthException>(() => h.Run(() => h.Client.Leaderboard.GetMyRankAsync("lb-1")));
                Assert.AreEqual(0, transport.Requests.Count, "Auth guard short-circuits before any request.");
            }
        }

        // ---- LB-07: an unranked player is a valid result, not an error ----
        [Test]
        public void GetMyRank_NoEntryYet_ReturnsNullRankAndScore()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardMe("lb-1"),
                FlockFakeTransport.Ok("{\"result\":{\"player_id\":\"player-a\",\"window\":\"weekly\",\"rank\":null,\"score\":null}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                PlayerRank r = h.Run(() => h.Client.Leaderboard.GetMyRankAsync("lb-1"));

                Assert.IsNotNull(r);
                Assert.IsFalse(r.Rank.HasValue, "An unranked player returns a null rank rather than throwing.");
                Assert.IsFalse(r.Score.HasValue);
            }
        }

        // ---- LB-08: around-me sends the neighbour count as `n` ----
        [Test]
        public void GetAroundMe_SendsNeighbourCountAsN()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardAroundMe("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Leaderboard.GetAroundMeAsync("lb-1", 9));

                FlockHttpRequest sent = transport.LastTo(FlockEndpoints.LeaderboardAroundMe("lb-1"));
                Assert.IsTrue(sent.Url.Contains("n=9"), sent.Url);
            }
        }

        // ---- LB-09: standings fall back to the snapshot when the network is gone ----
        [Test]
        public void GetStandings_Offline_ServedFromSnapshot()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardById("lb-1"), FlockFakeTransport.Ok(StandingsBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.Run(() => h.Client.Leaderboard.GetStandingsAsync("lb-1"));
                int callsWhileOnline = transport.CountTo(FlockEndpoints.LeaderboardById("lb-1"));

                h.SetReachable(false);
                Standings cached = h.Run(() => h.Client.Leaderboard.GetStandingsAsync("lb-1"));

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
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.LeaderboardMe("lb-1"),
                FlockFakeTransport.Ok("{\"result\":{\"player_id\":\"player-a\",\"window\":\"weekly\",\"rank\":1,\"score\":999.0}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                PlayerRank first = h.Run(() => h.Client.Leaderboard.GetMyRankAsync("lb-1"));
                Assert.AreEqual(1, first.Rank);

                // Second player on the same device, with the network gone: there is no cache under their key.
                h.LoginAs("player-b");
                h.SetReachable(false);
                transport.GoOffline();

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Leaderboard.GetMyRankAsync("lb-1")),
                    "player-b must not be served player-a's cached rank.");
            }
        }
    }
}
