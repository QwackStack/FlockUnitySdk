using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;
using Newtonsoft.Json.Linq;

namespace Flock.Tests.PlayMode
{
    // The multiplayer session routes in memory, with the backend's rules as measured on 2026-10-07 and 2026-10-08: the bearer
    // names the player, every route answers the whole session (players in seniority order) with the heartbeat interval, a player
    // holds one seat a game, the host leaving hands hosting to the longest seated or ends the session (host_left), a change of
    // host clears the address and reopens the session, a session keeps to its host's game version unless told otherwise, and
    // each refusal has the backend's own status and code. Other players act through its methods.
    internal sealed class FakeSessionServer : IFlockHttpAdapter
    {
        internal const string Host = "host";
        internal const string Join = "join";
        internal const string Current = "current";
        internal const string Heartbeat = "heartbeat";
        internal const string Read = "read";
        internal const string Leave = "leave";
        internal const string End = "end";
        internal const string MakeHost = "make-host";
        internal const string Publish = "publish";
        internal const string JoinToken = "join-token";
        internal const string Verify = "verify";
        internal const string RelayCredentials = "relay-credentials";
        internal const string Other = "other";

        internal sealed class Seat
        {
            public string PlayerId;
            public string Status = "joined";
        }

        internal sealed class Session
        {
            public string Id;
            public string Code;
            public string Status = "open";
            public string EndedReason;
            public string HostId;
            public int HostEpoch = 1;
            public int MaxPlayers = 8;
            public JObject Connection = new JObject();
            public int ConnectionEpoch;
            public JObject Data = new JObject();
            public string GameVersion = GameBuild;
            public bool SameVersionOnly = true;
            public readonly List<Seat> Seats = new List<Seat>();
            public bool Live => Status != "ended";
            public IEnumerable<string> Seated => Seats.Where(seat => seat.Status == "joined").Select(seat => seat.PlayerId);
        }

        internal sealed class Held
        {
            private readonly TaskCompletionSource<FlockHttpResponse> _answer = new TaskCompletionSource<FlockHttpResponse>();
            internal FlockHttpResponse Computed;
            internal bool Arrived => Computed != null;
            internal Task<FlockHttpResponse> Answer => _answer.Task;
            internal void Release() => _answer.TrySetResult(Computed);
        }

        // The Game Version the test client sends: other players are on the game's build unless a test says otherwise.
        internal const string GameBuild = "test-gvid";

        // What every session answer says about heartbeats; null leaves the interval out, as a server from before it did.
        internal int? HeartbeatSeconds = 20;

        // The STUN servers the relay route lists, after a relay entry with credentials, as the backend answers.
        internal readonly List<string> StunUrls = new List<string>();

        private readonly object _lock = new object();
        private readonly List<Session> _sessions = new List<Session>();
        private readonly Dictionary<string, Queue<Held>> _holds = new Dictionary<string, Queue<Held>>();
        private readonly Dictionary<string, Queue<FlockHttpResponse>> _cannedAnswers = new Dictionary<string, Queue<FlockHttpResponse>>();
        private readonly List<KeyValuePair<string, FlockHttpRequest>> _requests = new List<KeyValuePair<string, FlockHttpRequest>>();
        private int _nextId;

        internal int Count(string route)
        {
            lock (_lock)
                return _requests.Count(pair => pair.Key == route);
        }

        internal List<FlockHttpRequest> RequestsTo(string route)
        {
            lock (_lock)
                return _requests.Where(pair => pair.Key == route).Select(pair => pair.Value).ToList();
        }

        internal int TotalRequests
        {
            get { lock (_lock) return _requests.Count(pair => pair.Key != Other); }
        }

        internal Held HoldNext(string route)
        {
            Held held = new Held();
            lock (_lock)
            {
                if (!_holds.TryGetValue(route, out Queue<Held> queue))
                    _holds[route] = queue = new Queue<Held>();
                queue.Enqueue(held);
            }
            return held;
        }

        internal void AnswerNext(string route, FlockHttpResponse answer)
        {
            lock (_lock)
            {
                if (!_cannedAnswers.TryGetValue(route, out Queue<FlockHttpResponse> queue))
                    _cannedAnswers[route] = queue = new Queue<FlockHttpResponse>();
                queue.Enqueue(answer);
            }
        }

        internal Session ById(string id)
        {
            lock (_lock)
                return _sessions.FirstOrDefault(session => session.Id == id);
        }

        // ---- what other players (or this player on another device, or the server's own sweep) do ----

        internal Session HostedBy(string playerId, int maxPlayers = 8)
        {
            lock (_lock)
            {
                ReleaseSeatsOf(playerId);
                Session session = new Session { Id = "01SESSION" + (++_nextId).ToString("D17"), Code = "S" + (_nextId + 10000).ToString("D5"), HostId = playerId, MaxPlayers = maxPlayers };
                session.Seats.Add(new Seat { PlayerId = playerId });
                _sessions.Add(session);
                return session;
            }
        }

        // A match's session, as the matchmaking engine makes it: every player seated, the longest waiting hosts, other seats given up.
        internal Session SeatedByMatch(IReadOnlyList<string> playerIds, string gameVersion)
        {
            lock (_lock)
            {
                foreach (string playerId in playerIds)
                    ReleaseSeatsOf(playerId);
                Session session = new Session
                {
                    Id = "01SESSION" + (++_nextId).ToString("D17"),
                    Code = "S" + (_nextId + 10000).ToString("D5"),
                    HostId = playerIds[0],
                    MaxPlayers = Math.Max(8, playerIds.Count),
                    GameVersion = gameVersion,
                };
                foreach (string playerId in playerIds)
                    session.Seats.Add(new Seat { PlayerId = playerId });
                _sessions.Add(session);
                return session;
            }
        }

        // A player seated again keeps one row, now the least senior, as the backend resets the old row.
        internal void Joins(Session session, string playerId)
        {
            lock (_lock)
            {
                ReleaseSeatsOf(playerId);
                session.Seats.RemoveAll(seat => seat.PlayerId == playerId);
                session.Seats.Add(new Seat { PlayerId = playerId });
            }
        }

        internal void Leaves(Session session, string playerId) => GivesUp(session, playerId, "left");

        // The server's sweep after it stopped hearing from the player.
        internal void Drops(Session session, string playerId) => GivesUp(session, playerId, "dropped");

        internal void Ends(Session session, string reason = "ended_by_host")
        {
            lock (_lock)
                Close(session, reason);
        }

        internal void Publishes(Session session, JObject connection)
        {
            lock (_lock)
            {
                session.Connection = connection;
                session.ConnectionEpoch++;
                session.Status = "active";
            }
        }

        internal void MakesHost(Session session, string playerId)
        {
            lock (_lock)
                HandOver(session, playerId);
        }

        // Every change of host: the old host's address is cleared and the session is open again until the new host publishes.
        private static void HandOver(Session session, string playerId)
        {
            session.HostId = playerId;
            session.HostEpoch++;
            session.Connection = new JObject();
            session.Status = "open";
        }

        // ---- the wire ----

        public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
        {
            string path = request.Url.Substring(request.Url.IndexOf("/v1/", StringComparison.Ordinal) + 4);
            string route = RouteOf(request.Method, path);
            Held held = null;
            FlockHttpResponse answer;
            lock (_lock)
            {
                _requests.Add(new KeyValuePair<string, FlockHttpRequest>(route, request));
                if (_holds.TryGetValue(route, out Queue<Held> holds) && holds.Count > 0)
                    held = holds.Dequeue();
                if (_cannedAnswers.TryGetValue(route, out Queue<FlockHttpResponse> canned) && canned.Count > 0)
                    answer = canned.Dequeue();
                else
                    answer = Handle(route, path, PlayerOf(request), VersionOf(request), request.JsonBody == null ? null : JObject.Parse(request.JsonBody));
            }
            if (held == null)
                return Task.FromResult(answer);
            held.Computed = answer;
            return held.Answer;
        }

        private static string RouteOf(string method, string path)
        {
            const string prefix = "multiplayer/sessions";
            if (path == "multiplayer/relay-credentials" && method == "POST")
                return RelayCredentials;
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                return Other;
            if (path == prefix)
                return Host;
            if (path == prefix + "/join")
                return Join;
            if (path == prefix + "/current")
                return Current;
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
                return Heartbeat;
            if (path.EndsWith("/leave", StringComparison.Ordinal))
                return Leave;
            if (path.EndsWith("/end", StringComparison.Ordinal))
                return End;
            if (path.EndsWith("/host", StringComparison.Ordinal))
                return MakeHost;
            if (path.EndsWith("/connection-info", StringComparison.Ordinal))
                return Publish;
            if (path.EndsWith("/join-token", StringComparison.Ordinal))
                return JoinToken;
            if (path.EndsWith("/verify-join-token", StringComparison.Ordinal))
                return Verify;
            return Read;
        }

        private FlockHttpResponse Handle(string route, string path, string player, string version, JObject body)
        {
            if (route == Other)
                return Refused(404, "request.not_found");
            if (player == null)
                return Refused(401, "player.missing_token");
            if (route == RelayCredentials)
            {
                JArray servers = new JArray
                {
                    new JObject
                    {
                        ["urls"] = new JArray("turn:relay.test:3479?transport=udp", "turn:relay.test:3479?transport=tcp"),
                        ["username"] = "1760000000:game:" + player,
                        ["credential"] = "minted-for-the-test",
                    },
                };
                if (StunUrls.Count > 0)
                    servers.Add(new JObject { ["urls"] = new JArray(StunUrls.ToArray()), ["username"] = null, ["credential"] = null });
                return Answer(new JObject { ["ttl"] = 600, ["ice_servers"] = servers, ["relay_paused"] = false });
            }
            string sessionId = path.Length > "multiplayer/sessions/".Length ? path.Substring("multiplayer/sessions/".Length).Split('/')[0] : null;
            Session session = _sessions.FirstOrDefault(each => each.Id == sessionId);
            bool live = session != null && session.Live;
            bool seated = session != null && session.Seats.Any(seat => seat.PlayerId == player && seat.Status == "joined");

            switch (route)
            {
                case Host:
                {
                    JToken size = body?["max_players"];
                    if (size != null && (size.Type == JTokenType.Null || size.Value<int>() < 2 || size.Value<int>() > 16))
                        return Refused(422, "request.validation_failed");
                    Session made = HostedBy(player, size == null ? 8 : size.Value<int>());
                    if (body?["data"] is JObject data)
                        made.Data = data;
                    made.GameVersion = version;
                    made.SameVersionOnly = body?["same_version_only"]?.Value<bool>() ?? true;
                    return Answer(Describe(made, player, true));
                }
                case Current:
                {
                    Session mine = _sessions.FirstOrDefault(each => each.Live && each.Seated.Contains(player));
                    return mine == null ? Refused(404, "multiplayer.session_not_found") : Answer(Describe(mine, player, false));
                }
                case Heartbeat:
                    if (!live || !seated)
                        return Refused(404, "multiplayer.session_not_found");
                    return Answer(Describe(session, player, true));
                case Join:
                {
                    string code = ((string)body?["join_code"] ?? "").Trim().ToUpperInvariant();
                    Session joining = _sessions.FirstOrDefault(each => each.Live && each.Code == code);
                    if (joining == null)
                        return Refused(404, "multiplayer.invalid_join_code");
                    if (joining.Seated.Contains(player))
                        return Answer(Describe(joining, player, true));
                    if (joining.SameVersionOnly && joining.GameVersion != version)
                        return Refused(409, "multiplayer.version_mismatch");
                    if (joining.Seated.Count() >= joining.MaxPlayers)
                        return Refused(409, "multiplayer.session_full");
                    ReleaseSeatsOf(player);
                    Seat old = joining.Seats.FirstOrDefault(seat => seat.PlayerId == player);
                    if (old != null)
                        joining.Seats.Remove(old);
                    joining.Seats.Add(new Seat { PlayerId = player });
                    return Answer(Describe(joining, player, true));
                }
                case Read:
                    if (session == null || session.Seats.All(seat => seat.PlayerId != player))
                        return Refused(404, "multiplayer.session_not_found");
                    return Answer(Describe(session, player, false));
                case Leave:
                    if (!live)
                        return Refused(404, "multiplayer.session_not_found");
                    if (!seated)
                        return Refused(404, "multiplayer.not_a_participant");
                    GivesUp(session, player, "left");
                    return Answer(Describe(session, player, true));
            }

            if (!live)
                return Refused(404, "multiplayer.session_not_found");
            if (route == JoinToken)
            {
                if (!seated)
                    return Refused(404, "multiplayer.session_not_found");
                return Answer(new JObject { ["token"] = "token-" + session.Id + "-" + player, ["expires_in"] = 60 });
            }
            if (session.HostId != player)
                return Refused(403, "multiplayer.not_host");
            switch (route)
            {
                case End:
                    Close(session, "ended_by_host");
                    return Answer(Describe(session, player, true));
                case MakeHost:
                {
                    string target = (string)body?["player_id"];
                    if (target == player)
                        return Answer(Describe(session, player, true));
                    if (!session.Seated.Contains(target))
                        return Refused(404, "multiplayer.target_not_a_participant");
                    HandOver(session, target);
                    return Answer(Describe(session, player, true));
                }
                case Publish:
                {
                    JObject connection = body?["connection_info"] as JObject;
                    string mode = (string)connection?["mode"];
                    if (string.IsNullOrWhiteSpace(mode) || mode.Length > 32)
                        return Refused(422, "request.validation_failed");
                    session.Connection = connection;
                    session.ConnectionEpoch++;
                    session.Status = "active";
                    return Answer(Describe(session, player, true));
                }
                case Verify:
                {
                    string token = (string)body?["token"];
                    if (string.IsNullOrEmpty(token))
                        return Refused(422, "request.validation_failed");
                    string expected = "token-" + session.Id + "-";
                    string named = token.StartsWith(expected, StringComparison.Ordinal) ? token.Substring(expected.Length) : null;
                    if (named == null || !session.Seated.Contains(named))
                        return Refused(400, "multiplayer.invalid_join_token");
                    return Answer(new JObject { ["session_id"] = session.Id, ["player_id"] = named });
                }
            }
            return Refused(404, "request.not_found");
        }

        // A seat given up: the host's is handed to the longest seated of the others, and a session nobody is left in closes.
        private void GivesUp(Session session, string playerId, string how)
        {
            lock (_lock)
            {
                Seat seat = session.Seats.FirstOrDefault(each => each.PlayerId == playerId && each.Status == "joined");
                if (seat == null)
                    return;
                seat.Status = how;
                string next = session.Seated.FirstOrDefault();
                if (session.HostId == playerId)
                {
                    if (next == null)
                    {
                        Close(session, "host_left");
                        return;
                    }
                    HandOver(session, next);
                }
                else if (next == null)
                {
                    Close(session, "empty");
                }
            }
        }

        private void ReleaseSeatsOf(string playerId)
        {
            foreach (Session session in _sessions.Where(each => each.Live && each.Seated.Contains(playerId)).ToList())
                GivesUp(session, playerId, "left");
        }

        private static void Close(Session session, string reason)
        {
            session.Status = "ended";
            session.EndedReason = reason;
            foreach (Seat seat in session.Seats.Where(each => each.Status == "joined"))
                seat.Status = "left";
        }

        // Every route answers the whole session; a read blanks the address for a player without a seat, as the backend does.
        private JObject Describe(Session session, string reader, bool asWritten)
        {
            bool seated = session.Seats.Any(seat => seat.PlayerId == reader && seat.Status == "joined");
            return new JObject
            {
                ["id"] = session.Id,
                ["game_id"] = "01KVCSANMD9V0CRW85ADE1WJA1",
                ["match_id"] = null,
                ["status"] = session.Status,
                ["ended_reason"] = session.EndedReason,
                ["host_player_id"] = session.HostId,
                ["host_epoch"] = session.HostEpoch,
                ["join_code"] = session.Code,
                ["max_players"] = session.MaxPlayers,
                ["connection_info"] = asWritten || seated ? session.Connection.DeepClone() : new JObject(),
                ["connection_epoch"] = session.ConnectionEpoch,
                ["data"] = session.Data.DeepClone(),
                ["game_version_id"] = session.GameVersion,
                ["same_version_only"] = session.SameVersionOnly,
                ["participant_timeout_seconds"] = 90,
                ["host_timeout_seconds"] = 60,
                ["heartbeat_interval_seconds"] = HeartbeatSeconds,
                ["last_host_seen_at"] = "2026-10-07T17:35:34.921920",
                ["expires_at"] = "2026-10-07T21:35:34.921920",
                ["ended_at"] = null,
                ["created_at"] = "2026-10-07T17:35:34.926450",
                ["updated_at"] = "2026-10-07T17:35:34.926454",
                ["participants"] = new JArray(session.Seats.Select(seat => new JObject
                {
                    ["player_id"] = seat.PlayerId,
                    ["status"] = seat.Status,
                    ["joined_at"] = "2026-10-07T17:35:34.921920",
                    ["left_at"] = null,
                    ["last_seen_at"] = "2026-10-07T17:35:34.921920",
                    ["relay_credential_mints"] = 0,
                })),
            };
        }

        private static FlockHttpResponse Answer(JToken result)
        {
            JObject envelope = new JObject
            {
                ["error"] = new JObject { ["code"] = null },
                ["response"] = new JObject { ["message"] = null, ["code"] = null },
                ["result"] = result,
            };
            return new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 200, Body = envelope.ToString() };
        }

        internal static FlockHttpResponse Refused(int status, string code)
        {
            JObject body = new JObject { ["detail"] = new JObject { ["code"] = code, ["message"] = "refused by the test server" } };
            return new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = status, Body = body.ToString() };
        }

        private static string VersionOf(FlockHttpRequest request)
        {
            if (request.Headers == null || !request.Headers.TryGetValue("X-Game-Version-ID", out string version))
                return null;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }

        private static string PlayerOf(FlockHttpRequest request)
        {
            if (request.Headers == null || !request.Headers.TryGetValue("Authorization", out string bearer))
                return null;
            string payload = bearer.Substring("Bearer ".Length).Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return (string)JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))["sub"];
        }
    }
}
