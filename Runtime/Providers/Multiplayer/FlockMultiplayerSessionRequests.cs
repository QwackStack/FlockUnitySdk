using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;

namespace Flock.Providers
{
    /// <summary>The multiplayer session routes on the wire, each sent as the sign-in it was asked for or not at all.</summary>
    internal sealed class FlockMultiplayerSessionRequests : FlockProviderBase
    {
        internal FlockMultiplayerSessionRequests(FlockClient client) : base(client)
        {
        }

        internal void RequireGiven(string value, string name) => RequireNotEmpty(value, name);

        // Kept out of a retry: a lost answer may mean the session was made, and a second try would make another.
        internal Task<SessionRecord> HostAsync(int signInNumber, int? maxPlayers, IReadOnlyDictionary<string, object> data, bool sameVersionOnly, CancellationToken cancellationToken)
        {
            HostSessionBody body = new HostSessionBody { MaxPlayers = maxPlayers, Data = data, SameVersionOnly = sameVersionOnly ? (bool?)null : false };
            return ExecuteAsync(async () => RequireSession(await FlockHttpClient.PostAsync<GenericResponse<SessionRecord>>(
                Url(FlockEndpoints.Sessions), body, Client.GetBaseHeaders(), cancellationToken)),
                "Host session", cancellationToken, idempotent: false, actsForSignIn: signInNumber);
        }

        // Joining a session the player is already seated in answers 200, so a retry is safe.
        internal Task<SessionRecord> JoinAsync(int signInNumber, string joinCode, CancellationToken cancellationToken)
        {
            JoinSessionBody body = new JoinSessionBody { JoinCode = joinCode };
            return ExecuteAsync(async () => RequireSession(await FlockHttpClient.PostAsync<GenericResponse<SessionRecord>>(
                Url(FlockEndpoints.SessionJoin), body, Client.GetBaseHeaders(), cancellationToken)),
                "Join session", cancellationToken, actsForSignIn: signInNumber);
        }

        internal Task<SessionRecord> ReadAsync(int signInNumber, string sessionId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireSession(await FlockHttpClient.GetAsync<GenericResponse<SessionRecord>>(
                Url(FlockEndpoints.SessionById(sessionId)), Client.GetBaseHeaders(), cancellationToken)),
                "Read session", cancellationToken, actsForSignIn: signInNumber);
        }

        // The live session the player is seated in, found without its id; refused with session_not_found when seated nowhere.
        internal Task<SessionRecord> ReadCurrentAsync(int signInNumber, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireSession(await FlockHttpClient.GetAsync<GenericResponse<SessionRecord>>(
                Url(FlockEndpoints.SessionCurrent), Client.GetBaseHeaders(), cancellationToken)),
                "Read my session", cancellationToken, actsForSignIn: signInNumber);
        }

        // Keeps the seat and answers the session as it is now; a second beat after a lost answer only keeps it again.
        internal Task<SessionRecord> HeartbeatAsync(int signInNumber, string sessionId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireSession(await FlockHttpClient.PostAsync<GenericResponse<SessionRecord>>(
                Url(FlockEndpoints.SessionHeartbeat(sessionId)), new object(), Client.GetBaseHeaders(), cancellationToken)),
                "Session heartbeat", cancellationToken, actsForSignIn: signInNumber);
        }

        // Retrying is safe: the caller reads "not a participant" after a lost answer as the leave having gone through.
        internal Task<SessionRecord> LeaveAsync(int signInNumber, string sessionId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireSession(await FlockHttpClient.PostAsync<GenericResponse<SessionRecord>>(
                Url(FlockEndpoints.SessionLeave(sessionId)), new object(), Client.GetBaseHeaders(), cancellationToken)),
                "Leave session", cancellationToken, actsForSignIn: signInNumber);
        }

        // Kept out of a retry: after a lost answer the session is over, and a second try would say it was not found.
        internal Task<SessionRecord> EndAsync(int signInNumber, string sessionId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireSession(await FlockHttpClient.PostAsync<GenericResponse<SessionRecord>>(
                Url(FlockEndpoints.SessionEnd(sessionId)), new object(), Client.GetBaseHeaders(), cancellationToken)),
                "End session", cancellationToken, idempotent: false, actsForSignIn: signInNumber);
        }

        // Kept out of a retry: after a lost answer the player is no longer the host, and a second try would be refused.
        internal Task<SessionRecord> MakeHostAsync(int signInNumber, string sessionId, string playerId, CancellationToken cancellationToken)
        {
            PlayerIdBody body = new PlayerIdBody { PlayerId = playerId };
            return ExecuteAsync(async () => RequireSession(await FlockHttpClient.PostAsync<GenericResponse<SessionRecord>>(
                Url(FlockEndpoints.SessionHost(sessionId)), body, Client.GetBaseHeaders(), cancellationToken)),
                "Make session host", cancellationToken, idempotent: false, actsForSignIn: signInNumber);
        }

        // Publishing the same connection again only moves its epoch, so a retry is safe.
        internal Task<SessionRecord> PublishConnectionAsync(int signInNumber, string sessionId, Dictionary<string, object> connection, CancellationToken cancellationToken)
        {
            PublishConnectionBody body = new PublishConnectionBody { ConnectionInfo = connection };
            return ExecuteAsync(async () => RequireSession(await FlockHttpClient.PostAsync<GenericResponse<SessionRecord>>(
                Url(FlockEndpoints.SessionConnection(sessionId)), body, Client.GetBaseHeaders(), cancellationToken)),
                "Publish session connection", cancellationToken, actsForSignIn: signInNumber);
        }

        internal Task<JoinTokenRecord> RequestJoinTokenAsync(int signInNumber, string sessionId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () =>
            {
                GenericResponse<JoinTokenRecord> response = await FlockHttpClient.PostAsync<GenericResponse<JoinTokenRecord>>(
                    Url(FlockEndpoints.SessionJoinToken(sessionId)), new object(), Client.GetBaseHeaders(), cancellationToken);
                if (string.IsNullOrEmpty(response?.Result?.Token))
                    throw new FlockNetworkException("Invalid response from server");
                return response.Result;
            }, "Request join token", cancellationToken, actsForSignIn: signInNumber);
        }

        internal Task<VerifiedJoinTokenRecord> VerifyJoinTokenAsync(int signInNumber, string sessionId, string token, CancellationToken cancellationToken)
        {
            JoinTokenBody body = new JoinTokenBody { Token = token };
            return ExecuteAsync(async () =>
            {
                GenericResponse<VerifiedJoinTokenRecord> response = await FlockHttpClient.PostAsync<GenericResponse<VerifiedJoinTokenRecord>>(
                    Url(FlockEndpoints.SessionVerifyJoinToken(sessionId)), body, Client.GetBaseHeaders(), cancellationToken);
                if (string.IsNullOrEmpty(response?.Result?.PlayerId))
                    throw new FlockNetworkException("Invalid response from server");
                return response.Result;
            }, "Verify join token", cancellationToken, actsForSignIn: signInNumber);
        }

        internal Task<RelayCredentialsRecord> RelayCredentialsAsync(int signInNumber, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () =>
            {
                GenericResponse<RelayCredentialsRecord> response = await FlockHttpClient.PostAsync<GenericResponse<RelayCredentialsRecord>>(
                    Url(FlockEndpoints.RelayCredentials), new RelayCredentialsBody(), Client.GetBaseHeaders(), cancellationToken);
                if (response?.Result?.IceServers == null)
                    throw new FlockNetworkException("Invalid response from server");
                return response.Result;
            }, "Read the servers to connect through", cancellationToken, actsForSignIn: signInNumber);
        }

        private string Url(string path) => $"{Client.GetVersionedApiUrl()}/{path}";

        // A session answer that names no session is not one, whatever its status.
        private static SessionRecord RequireSession(GenericResponse<SessionRecord> response)
        {
            if (response?.Result == null || string.IsNullOrEmpty(response.Result.Id))
                throw new FlockNetworkException("Invalid response from server");
            return response.Result;
        }
    }
}
