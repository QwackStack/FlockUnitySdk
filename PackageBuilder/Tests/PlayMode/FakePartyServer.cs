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
    // The party routes in memory, with the backend's rules as measured on 2026-10-07: the bearer names the player, only the two
    // reads carry members (in join order), update replaces the whole settings, the leader leaving promotes the oldest member, the
    // last one out disbands, and each refusal has the backend's own status and code. Other players act through its methods.
    internal sealed class FakePartyServer : IFlockHttpAdapter
    {
        internal const string Create = "create";
        internal const string Join = "join";
        internal const string Mine = "mine";
        internal const string Read = "read";
        internal const string Update = "update";
        internal const string Leave = "leave";
        internal const string Kick = "kick";
        internal const string Transfer = "transfer";
        internal const string Disband = "disband";
        internal const string Other = "other";

        internal sealed class Party
        {
            public string Id;
            public string LeaderId;
            public string Status = "active";
            public string InviteCode;
            public int MaxSize;
            public JObject Settings = new JObject();
            public readonly List<string> Members = new List<string>();
        }

        // One request held: its answer is worked out when it arrives, as the server would, and handed back on release.
        internal sealed class Held
        {
            private readonly TaskCompletionSource<FlockHttpResponse> _answer = new TaskCompletionSource<FlockHttpResponse>();
            internal FlockHttpResponse Computed;
            internal bool Arrived => Computed != null;
            internal Task<FlockHttpResponse> Answer => _answer.Task;
            internal void Release() => _answer.TrySetResult(Computed);
            internal void ReleaseWith(FlockHttpResponse answer) => _answer.TrySetResult(answer);
        }

        private readonly object _lock = new object();
        private readonly List<Party> _parties = new List<Party>();
        private readonly Dictionary<string, Queue<Held>> _holds = new Dictionary<string, Queue<Held>>();
        private readonly Dictionary<string, Queue<FlockHttpResponse>> _cannedAnswers = new Dictionary<string, Queue<FlockHttpResponse>>();
        private readonly List<KeyValuePair<string, FlockHttpRequest>> _requests = new List<KeyValuePair<string, FlockHttpRequest>>();
        private int _nextId;

        // Told the party's id whenever its players change, as the backend then stops the party's matchmaking search.
        internal Action<string> RosterChanged;

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

        // Party requests only: anything else the client sends is answered and not counted.
        internal int TotalRequests
        {
            get { lock (_lock) return _requests.Count(pair => pair.Key != Other); }
        }

        // Holds the next request to this route only; later ones are answered at once.
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

        // The next request to this route gets this answer and changes nothing.
        internal void AnswerNext(string route, FlockHttpResponse answer)
        {
            lock (_lock)
            {
                if (!_cannedAnswers.TryGetValue(route, out Queue<FlockHttpResponse> queue))
                    _cannedAnswers[route] = queue = new Queue<FlockHttpResponse>();
                queue.Enqueue(answer);
            }
        }

        internal Party PartyOf(string playerId)
        {
            lock (_lock)
                return _parties.FirstOrDefault(party => party.Status == "active" && party.Members.Contains(playerId));
        }

        internal Party ActiveById(string partyId)
        {
            lock (_lock)
                return _parties.FirstOrDefault(party => party.Status == "active" && party.Id == partyId);
        }

        // ---- what other players (or this player on another device) do ----

        internal Party MadeBy(string playerId, int maxSize = 4, JObject settings = null)
        {
            lock (_lock)
            {
                Party party = new Party { Id = NewId(), LeaderId = playerId, InviteCode = NewCode(), MaxSize = maxSize, Settings = settings ?? new JObject() };
                party.Members.Add(playerId);
                _parties.Add(party);
                return party;
            }
        }

        internal void Joins(Party party, string playerId)
        {
            lock (_lock)
                party.Members.Add(playerId);
            RosterChanged?.Invoke(party.Id);
        }

        internal void Removes(Party party, string playerId)
        {
            lock (_lock)
            {
                party.Members.Remove(playerId);
                if (party.Members.Count == 0)
                    party.Status = "disbanded";
                else if (party.LeaderId == playerId)
                    party.LeaderId = party.Members[0];
            }
            RosterChanged?.Invoke(party.Id);
        }

        internal void Leads(Party party, string playerId)
        {
            lock (_lock)
                party.LeaderId = playerId;
        }

        internal void Disbands(Party party)
        {
            lock (_lock)
            {
                party.Members.Clear();
                party.Status = "disbanded";
            }
            RosterChanged?.Invoke(party.Id);
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
                    answer = Handle(route, path, PlayerOf(request), request.JsonBody == null ? null : JObject.Parse(request.JsonBody));
            }
            if (held == null)
                return Task.FromResult(answer);
            held.Computed = answer;
            return held.Answer;
        }

        private static string RouteOf(string method, string path)
        {
            if (path != "party" && !path.StartsWith("party/", StringComparison.Ordinal))
                return Other;
            if (path == "party")
                return Create;
            if (path == "party/join")
                return Join;
            if (path == "party/me")
                return Mine;
            if (path.EndsWith("/leave", StringComparison.Ordinal))
                return Leave;
            if (path.EndsWith("/kick", StringComparison.Ordinal))
                return Kick;
            if (path.EndsWith("/transfer", StringComparison.Ordinal))
                return Transfer;
            if (method == "PATCH")
                return Update;
            if (method == "DELETE")
                return Disband;
            return Read;
        }

        private FlockHttpResponse Handle(string route, string path, string player, JObject body)
        {
            if (route == Other)
                return Refused(404, "request.not_found");
            if (player == null)
                return Refused(401, "player.missing_token");
            string partyId = path.StartsWith("party/", StringComparison.Ordinal) ? path.Substring(6).Split('/')[0] : null;
            Party party = _parties.FirstOrDefault(each => each.Id == partyId && each.Status == "active");

            switch (route)
            {
                case Create:
                {
                    // The backend's size is an int with a default: a null is refused, not taken as "the default".
                    if (body?["max_size"]?.Type == JTokenType.Null)
                        return Refused(422, "request.validation_failed");
                    int size = body?["max_size"]?.Value<int>() ?? 4;
                    if (size < 2 || size > 64)
                        return Refused(422, "request.validation_failed");
                    if (PartyOf(player) != null)
                        return Refused(409, "party.already_in_party");
                    Party made = MadeBy(player, size, body?["settings"] as JObject);
                    return Answer(Describe(made, false));
                }
                case Join:
                {
                    string code = ((string)body?["invite_code"] ?? "").Trim().ToUpperInvariant();
                    Party joining = _parties.FirstOrDefault(each => each.Status == "active" && each.InviteCode == code);
                    if (joining == null)
                        return Refused(404, "party.invalid_invite_code");
                    Party current = PartyOf(player);
                    if (current == joining)
                        return Answer(Describe(joining, false));
                    if (current != null)
                        return Refused(409, "party.already_in_party");
                    if (joining.Members.Count >= joining.MaxSize)
                        return Refused(409, "party.full");
                    joining.Members.Add(player);
                    RosterChanged?.Invoke(joining.Id);
                    return Answer(Describe(joining, false));
                }
                case Mine:
                {
                    Party mine = PartyOf(player);
                    return Answer(mine == null ? (JToken)JValue.CreateNull() : Describe(mine, true));
                }
                case Read:
                    if (party == null || !party.Members.Contains(player))
                        return Refused(404, "party.not_found");
                    return Answer(Describe(party, true));
                case Leave:
                    if (party == null)
                        return Refused(404, "party.not_found");
                    if (!party.Members.Contains(player))
                        return Refused(404, "party.not_a_member");
                    Removes(party, player);
                    return Answer(Describe(party, false));
            }

            if (party == null)
                return Refused(404, "party.not_found");
            if (party.LeaderId != player)
                return Refused(403, "party.not_party_leader");
            string target = (string)body?["player_id"];
            switch (route)
            {
                case Kick:
                    if (target == party.LeaderId)
                        return Refused(400, "party.cannot_kick_leader");
                    if (!party.Members.Contains(target))
                        return Refused(404, "party.target_not_a_member");
                    party.Members.Remove(target);
                    RosterChanged?.Invoke(party.Id);
                    break;
                case Transfer:
                    if (target != player && !party.Members.Contains(target))
                        return Refused(404, "party.target_not_a_member");
                    party.LeaderId = target;
                    break;
                case Update:
                    int? size = body?["max_size"]?.Type == JTokenType.Integer ? body["max_size"].Value<int>() : (int?)null;
                    if (size.HasValue && size.Value < party.Members.Count)
                        return Refused(400, "party.full");
                    if (size.HasValue)
                        party.MaxSize = size.Value;
                    if (body?["settings"] is JObject settings)
                        party.Settings = settings;
                    break;
                case Disband:
                    Disbands(party);
                    break;
            }
            return Answer(Describe(party, false));
        }

        private static JObject Describe(Party party, bool withMembers)
        {
            JObject described = new JObject
            {
                ["id"] = party.Id,
                ["game_id"] = "01KVCSANMD9V0CRW85ADE1WJA1",
                ["leader_player_id"] = party.LeaderId,
                ["status"] = party.Status,
                ["invite_code"] = party.InviteCode,
                ["max_size"] = party.MaxSize,
                ["settings"] = party.Settings.DeepClone(),
                ["created_at"] = "2026-10-07T17:35:22.872482",
                ["updated_at"] = "2026-10-07T17:35:22.872487",
            };
            if (withMembers)
                described["members"] = new JArray(party.Members.Select(member => new JObject { ["player_id"] = member, ["joined_at"] = "2026-10-07T17:35:22.872872" }));
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

        // A 2xx in the right envelope that names no party.
        internal static FlockHttpResponse NamingNoParty() => Answer(JValue.CreateNull());

        internal static FlockHttpResponse Refused(int status, string code)
        {
            JObject body = new JObject { ["detail"] = new JObject { ["code"] = code, ["message"] = "refused by the test server" } };
            return new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = status, Body = body.ToString() };
        }

        // The bearer is the test client's unsigned token, whose middle part names the player.
        private static string PlayerOf(FlockHttpRequest request)
        {
            if (request.Headers == null || !request.Headers.TryGetValue("Authorization", out string bearer))
                return null;
            string payload = bearer.Substring("Bearer ".Length).Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return (string)JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))["sub"];
        }

        private string NewId() => "01PARTY" + (++_nextId).ToString("D19");

        private string NewCode() => "C" + (_nextId + 10000).ToString("D5");
    }
}
