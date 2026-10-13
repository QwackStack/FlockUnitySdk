using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;

namespace Flock.Providers
{
    /// <summary>The party routes on the wire, each sent as the sign-in it was asked for or not at all.</summary>
    internal sealed class FlockPartyRequests : FlockProviderBase
    {
        internal FlockPartyRequests(FlockClient client) : base(client)
        {
        }

        internal void RequireGiven(string value, string name) => RequireNotEmpty(value, name);

        // Kept out of a retry: a lost answer may mean the party was made, and a second try would say "already in a party".
        internal Task<PartyRecord> CreateAsync(int signInNumber, int? maxSize, IReadOnlyDictionary<string, object> settings, CancellationToken cancellationToken)
        {
            PartySizeAndSettingsBody body = new PartySizeAndSettingsBody { MaxSize = maxSize, Settings = settings };
            return ExecuteAsync(async () => RequireParty(await FlockHttpClient.PostAsync<GenericResponse<PartyRecord>>(
                Url(FlockEndpoints.Party), body, Client.GetBaseHeaders(), cancellationToken)),
                "Create party", cancellationToken, idempotent: false, actsForSignIn: signInNumber);
        }

        // Joining the party the player is already in answers 200, so a retry is safe.
        internal Task<PartyRecord> JoinAsync(int signInNumber, string inviteCode, CancellationToken cancellationToken)
        {
            JoinPartyBody body = new JoinPartyBody { InviteCode = inviteCode };
            return ExecuteAsync(async () => RequireParty(await FlockHttpClient.PostAsync<GenericResponse<PartyRecord>>(
                Url(FlockEndpoints.PartyJoin), body, Client.GetBaseHeaders(), cancellationToken)),
                "Join party", cancellationToken, actsForSignIn: signInNumber);
        }

        internal Task<PartyDetailRecord> ReadAsync(int signInNumber, string partyId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireParty(await FlockHttpClient.GetAsync<GenericResponse<PartyDetailRecord>>(
                Url(FlockEndpoints.PartyById(partyId)), Client.GetBaseHeaders(), cancellationToken)),
                "Read party", cancellationToken, actsForSignIn: signInNumber);
        }

        /// <summary>The player's party with its members, or null when the player is in none.</summary>
        internal Task<PartyDetailRecord> ReadMineAsync(int signInNumber, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () =>
            {
                GenericResponse<PartyDetailRecord> response = await FlockHttpClient.GetAsync<GenericResponse<PartyDetailRecord>>(
                    Url(FlockEndpoints.PartyMine), Client.GetBaseHeaders(), cancellationToken);
                // A null result is the server saying "in no party"; no envelope at all is still a misread.
                if (response == null)
                    throw new FlockNetworkException("Invalid response from server");
                return response.Result == null ? null : RequireParty(response);
            }, "Read my party", cancellationToken, actsForSignIn: signInNumber);
        }

        // Retrying is safe: the caller reads "not a member" after a lost answer as the leave having gone through.
        internal Task LeaveAsync(int signInNumber, string partyId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireParty(await FlockHttpClient.PostAsync<GenericResponse<PartyRecord>>(
                Url(FlockEndpoints.PartyLeave(partyId)), new object(), Client.GetBaseHeaders(), cancellationToken)),
                "Leave party", cancellationToken, actsForSignIn: signInNumber);
        }

        // Kept out of a retry: a second try after a lost answer would be refused for a player already gone.
        internal Task KickAsync(int signInNumber, string partyId, string playerId, CancellationToken cancellationToken)
        {
            PlayerIdBody body = new PlayerIdBody { PlayerId = playerId };
            return ExecuteAsync(async () => RequireParty(await FlockHttpClient.PostAsync<GenericResponse<PartyRecord>>(
                Url(FlockEndpoints.PartyKick(partyId)), body, Client.GetBaseHeaders(), cancellationToken)),
                "Remove party member", cancellationToken, idempotent: false, actsForSignIn: signInNumber);
        }

        // Kept out of a retry: after a lost answer the player is no longer the leader, and a second try would be refused.
        internal Task TransferAsync(int signInNumber, string partyId, string playerId, CancellationToken cancellationToken)
        {
            PlayerIdBody body = new PlayerIdBody { PlayerId = playerId };
            return ExecuteAsync(async () => RequireParty(await FlockHttpClient.PostAsync<GenericResponse<PartyRecord>>(
                Url(FlockEndpoints.PartyTransfer(partyId)), body, Client.GetBaseHeaders(), cancellationToken)),
                "Make party leader", cancellationToken, idempotent: false, actsForSignIn: signInNumber);
        }

        internal Task UpdateAsync(int signInNumber, string partyId, int? maxSize, IReadOnlyDictionary<string, object> settings, CancellationToken cancellationToken)
        {
            PartySizeAndSettingsBody body = new PartySizeAndSettingsBody { MaxSize = maxSize, Settings = settings };
            return ExecuteAsync(async () => RequireParty(await FlockHttpClient.PatchAsync<GenericResponse<PartyRecord>>(
                Url(FlockEndpoints.PartyById(partyId)), body, Client.GetBaseHeaders(), cancellationToken)),
                "Update party", cancellationToken, actsForSignIn: signInNumber);
        }

        // Kept out of a retry: after a lost answer the party is gone, and a second try would say it was not found.
        internal Task DisbandAsync(int signInNumber, string partyId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireParty(await FlockHttpClient.DeleteAsync<GenericResponse<PartyRecord>>(
                Url(FlockEndpoints.PartyById(partyId)), Client.GetBaseHeaders(), cancellationToken)),
                "Disband party", cancellationToken, idempotent: false, actsForSignIn: signInNumber);
        }

        private string Url(string path) => $"{Client.GetVersionedApiUrl()}/{path}";

        // A party answer that names no party is not one, whatever its status.
        private static T RequireParty<T>(GenericResponse<T> response) where T : PartyRecord
        {
            if (response?.Result == null || string.IsNullOrEmpty(response.Result.Id))
                throw new FlockNetworkException("Invalid response from server");
            return response.Result;
        }
    }
}
