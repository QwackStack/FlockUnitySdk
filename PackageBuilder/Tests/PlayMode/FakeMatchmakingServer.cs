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
    // The matchmaking ticket routes in memory, with the party and session fakes behind it, and the backend's rules as measured on
    // 2026-10-08: a party is queued by its leader as one ticket per member and never split, a player searches once at a time, a
    // ticket is read and cancelled by its own player only, any member's cancel stops the party's search, a roster change stops
    // it as party_changed, a match makes its session (the longest waiting hosts) unless the game's settings make none, and each
    // refusal has the backend's own status and code. Matches form, and tickets expire, when a test says so.
    internal sealed class FakeMatchmakingServer : IFlockHttpAdapter
    {
        internal const string Queues = "queues";
        internal const string Create = "create";
        internal const string Current = "current";
        internal const string ReadTicket = "read-ticket";
        internal const string Cancel = "cancel";
        internal const string Other = "other";

        internal const string Duel = "duel";
        internal const string Trio = "trio";
        internal const string DuelId = "01QUEUEDUEL000000000000000";
        internal const string TrioId = "01QUEUETRIO000000000000000";

        internal sealed class Ticket
        {
            public string Id;
            public string QueueId;
            public string PlayerId;
            public string Status = "queued";
            public string PartyId;
            public int PartySize = 1;
            public string CancelReason;
            public Match Match;
            public string GameVersion;
            public int Order;
        }

        internal sealed class Match
        {
            public string Id;
            public string QueueId;
            public List<string> PlayerIds;
            public string SessionId;
        }

        internal sealed class Held
        {
            private readonly TaskCompletionSource<FlockHttpResponse> _answer = new TaskCompletionSource<FlockHttpResponse>();
            internal FlockHttpResponse Computed;
            internal bool Arrived => Computed != null;
            internal Task<FlockHttpResponse> Answer => _answer.Task;
            internal void Release() => _answer.TrySetResult(Computed);
            // As a real transport does, the request gives up when its caller cancels; otherwise it waits for Release.
            internal bool EndsWhenCancelled;
        }

        internal readonly FakeSessionServer Sessions = new FakeSessionServer();
        internal readonly FakePartyServer Parties = new FakePartyServer();

        // The game's multiplayer setting "make a session on a match"; on by default, as on the backend.
        internal bool MakesSessions = true;

        private readonly object _lock = new object();
        private readonly Dictionary<string, int> _queueSizes = new Dictionary<string, int> { { DuelId, 2 }, { TrioId, 3 } };
        private readonly List<Ticket> _tickets = new List<Ticket>();
        private readonly Dictionary<string, Queue<Held>> _holds = new Dictionary<string, Queue<Held>>();
        private readonly Dictionary<string, Queue<FlockHttpResponse>> _cannedAnswers = new Dictionary<string, Queue<FlockHttpResponse>>();
        private readonly List<KeyValuePair<string, FlockHttpRequest>> _requests = new List<KeyValuePair<string, FlockHttpRequest>>();
        private int _nextId;
        private int _nextOrder;

        internal FakeMatchmakingServer()
        {
            Parties.RosterChanged = partyId => StopPartySearch(partyId, "party_changed");
        }

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

        internal List<Ticket> TicketsOf(string playerId)
        {
            lock (_lock)
                return _tickets.Where(ticket => ticket.PlayerId == playerId).ToList();
        }

        internal Ticket QueuedTicketOf(string playerId)
        {
            lock (_lock)
                return _tickets.FirstOrDefault(ticket => ticket.PlayerId == playerId && ticket.Status == "queued");
        }

        // ---- what other players, or the server's engine, do ----

        // A player alone, or a party by its leader, joins a queue without the checks a request goes through.
        internal List<Ticket> QueuedBy(string queueId, string playerId, FakePartyServer.Party party = null, string gameVersion = FakeSessionServer.GameBuild)
        {
            lock (_lock)
                return MakeTickets(queueId, party == null ? new List<string> { playerId } : party.Members.ToList(), party?.Id, gameVersion);
        }

        // One tick of the engine: in each queue, the longest waiting fill a match, a party never split and skipped while it does not fit.
        internal List<Match> FormMatches()
        {
            List<Match> formed = new List<Match>();
            List<KeyValuePair<List<string>, string>> sessionsToMake = new List<KeyValuePair<List<string>, string>>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, int> queue in _queueSizes)
                {
                    List<List<Ticket>> units = _tickets
                        .Where(ticket => ticket.QueueId == queue.Key && ticket.Status == "queued")
                        .OrderBy(ticket => ticket.Order)
                        .GroupBy(ticket => ticket.PartyId ?? ticket.Id)
                        .Select(group => group.ToList())
                        .ToList();
                    List<Ticket> chosen = new List<Ticket>();
                    foreach (List<Ticket> unit in units)
                    {
                        if (chosen.Count + unit.Count > queue.Value)
                            continue;
                        chosen.AddRange(unit);
                        if (chosen.Count < queue.Value)
                            continue;
                        Match match = new Match { Id = "01MATCH" + (++_nextId).ToString("D19"), QueueId = queue.Key, PlayerIds = chosen.Select(ticket => ticket.PlayerId).ToList() };
                        foreach (Ticket ticket in chosen)
                        {
                            ticket.Status = "matched";
                            ticket.Match = match;
                        }
                        formed.Add(match);
                        sessionsToMake.Add(new KeyValuePair<List<string>, string>(match.PlayerIds, chosen[0].GameVersion));
                        chosen = new List<Ticket>();
                    }
                }
            }
            if (MakesSessions)
            {
                for (int i = 0; i < formed.Count; i++)
                    formed[i].SessionId = Sessions.SeatedByMatch(sessionsToMake[i].Key, sessionsToMake[i].Value).Id;
            }
            return formed;
        }

        // The engine's sweep after 5 minutes, for every ticket still queued.
        internal void ExpiresAll()
        {
            lock (_lock)
            {
                foreach (Ticket ticket in _tickets.Where(each => each.Status == "queued"))
                    ticket.Status = "expired";
            }
        }

        // The player's other game, or another member of the party, cancels the search.
        internal void CancelledElsewhere(Ticket ticket)
        {
            lock (_lock)
                StopSearch(ticket, "player");
        }

        private void StopPartySearch(string partyId, string reason)
        {
            lock (_lock)
            {
                foreach (Ticket ticket in _tickets.Where(each => each.PartyId == partyId && each.Status == "queued"))
                {
                    ticket.Status = "cancelled";
                    ticket.CancelReason = reason;
                }
            }
        }

        private void StopSearch(Ticket ticket, string reason)
        {
            IEnumerable<Ticket> stopped = ticket.PartyId == null
                ? new[] { ticket }
                : _tickets.Where(each => each.PartyId == ticket.PartyId && each.Status == "queued");
            foreach (Ticket each in stopped.Where(each => each.Status == "queued").ToList())
            {
                each.Status = "cancelled";
                each.CancelReason = reason;
            }
        }

        private List<Ticket> MakeTickets(string queueId, List<string> playerIds, string partyId, string gameVersion)
        {
            int order = ++_nextOrder;
            List<Ticket> made = playerIds.Select(playerId => new Ticket
            {
                Id = "01TICKET" + (++_nextId).ToString("D18"),
                QueueId = queueId,
                PlayerId = playerId,
                PartyId = partyId,
                PartySize = playerIds.Count,
                GameVersion = gameVersion,
                Order = order,
            }).ToList();
            _tickets.AddRange(made);
            return made;
        }

        // ---- the wire ----

        public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
        {
            string path = request.Url.Substring(request.Url.IndexOf("/v1/", StringComparison.Ordinal) + 4);
            if (path == "party" || path.StartsWith("party/", StringComparison.Ordinal))
                return Parties.SendAsync(request, cancellationToken);
            if (path.StartsWith("multiplayer/", StringComparison.Ordinal))
                return Sessions.SendAsync(request, cancellationToken);

            string route = RouteOf(request.Method, path);
            string player = PlayerOf(request);
            // A party's leader and members are read before this fake's own lock, as the party fake keeps its own.
            JObject body = request.JsonBody == null ? null : JObject.Parse(request.JsonBody);
            FakePartyServer.Party party = route == Create && body?["party_id"] != null ? Parties.ActiveById((string)body["party_id"]) : null;
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
                    answer = Handle(route, path, player, VersionOf(request), body, party);
            }
            if (held == null)
                return Task.FromResult(answer);
            held.Computed = answer;
            return held.EndsWhenCancelled ? AnswerOrCancelled(held.Answer, cancellationToken) : held.Answer;
        }

        private static async Task<FlockHttpResponse> AnswerOrCancelled(Task<FlockHttpResponse> answer, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> cancelled = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(answer, cancelled.Task) != answer)
                    throw new OperationCanceledException(cancellationToken);
                return await answer;
            }
        }

        private static string RouteOf(string method, string path)
        {
            int query = path.IndexOf('?');
            if (query >= 0)
                path = path.Substring(0, query);
            if (path == "matchmaking/queues")
                return Queues;
            if (path == "matchmaking/ticket")
                return method == "POST" ? Create : Other;
            if (path == "matchmaking/ticket/current")
                return Current;
            if (path.StartsWith("matchmaking/ticket/", StringComparison.Ordinal))
                return method == "DELETE" ? Cancel : ReadTicket;
            return Other;
        }

        private FlockHttpResponse Handle(string route, string path, string player, string version, JObject body, FakePartyServer.Party party)
        {
            if (route == Other)
                return Refused(404, "request.not_found");
            if (route == Queues)
                return Answer(new JArray(
                    new JObject { ["id"] = DuelId, ["name"] = Duel },
                    new JObject { ["id"] = TrioId, ["name"] = Trio }));
            if (player == null)
                return Refused(401, "player.missing_token");

            switch (route)
            {
                case Create:
                {
                    string queueId = (string)body?["queue_id"];
                    if (queueId == null || !_queueSizes.TryGetValue(queueId, out int size))
                        return Refused(404, "matchmaking.queue_not_found");
                    List<string> playerIds;
                    string partyId = null;
                    if (body["party_id"] != null)
                    {
                        if (party == null)
                            return Refused(404, "party.not_found");
                        if (party.LeaderId != player)
                            return Refused(403, "matchmaking.not_party_leader");
                        playerIds = party.Members.ToList();
                        partyId = party.Id;
                    }
                    else
                    {
                        playerIds = new List<string> { player };
                    }
                    if (playerIds.Count > size)
                        return Refused(400, "matchmaking.party_too_large");
                    if (_tickets.Any(ticket => ticket.Status == "queued" && playerIds.Contains(ticket.PlayerId)))
                        return Refused(409, "matchmaking.already_queued");
                    List<Ticket> made = MakeTickets(queueId, playerIds, partyId, version);
                    return Answer(new JArray(made.Select(ticket => Describe(ticket, false))));
                }
                case Current:
                {
                    Ticket mine = _tickets.FirstOrDefault(ticket => ticket.PlayerId == player && ticket.Status == "queued")
                        ?? _tickets.LastOrDefault(ticket => ticket.PlayerId == player);
                    return mine == null ? Refused(404, "matchmaking.ticket_not_found") : Answer(Describe(mine, true));
                }
            }

            string ticketId = path.Substring("matchmaking/ticket/".Length);
            Ticket own = _tickets.FirstOrDefault(ticket => ticket.Id == ticketId && ticket.PlayerId == player);
            if (own == null)
                return Refused(404, "matchmaking.ticket_not_found");
            if (route == ReadTicket)
                return Answer(Describe(own, true));
            if (own.Status != "queued")
                return Refused(409, "matchmaking.ticket_not_cancelable");
            StopSearch(own, "player");
            return Answer(Describe(own, false));
        }

        // A ticket as the routes answer it; the reads embed the match once there is one, which names its session.
        private static JObject Describe(Ticket ticket, bool withMatch)
        {
            JObject described = new JObject
            {
                ["id"] = ticket.Id,
                ["queue_id"] = ticket.QueueId,
                ["game_id"] = "01KVCSANMD9V0CRW85ADE1WJA1",
                ["player_id"] = ticket.PlayerId,
                ["status"] = ticket.Status,
                ["attributes"] = new JObject(),
                ["party_id"] = ticket.PartyId,
                ["party_size"] = ticket.PartySize,
                ["match_id"] = ticket.Match?.Id,
                ["matched_at"] = ticket.Match == null ? null : "2026-10-08T09:18:31.123456",
                ["cancel_reason"] = ticket.CancelReason,
                ["game_version_id"] = ticket.GameVersion,
                ["created_at"] = "2026-10-08T09:18:29.654321",
            };
            if (withMatch)
            {
                described["match"] = ticket.Match == null ? JValue.CreateNull() : (JToken)new JObject
                {
                    ["id"] = ticket.Match.Id,
                    ["queue_id"] = ticket.Match.QueueId,
                    ["game_id"] = "01KVCSANMD9V0CRW85ADE1WJA1",
                    ["status"] = "ready",
                    ["player_ids"] = new JArray(ticket.Match.PlayerIds),
                    ["connection_info"] = ticket.Match.SessionId == null
                        ? new JObject()
                        : new JObject { ["mode"] = "p2p", ["session_id"] = ticket.Match.SessionId },
                    ["created_at"] = "2026-10-08T09:18:31.123456",
                };
            }
            return described;
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
