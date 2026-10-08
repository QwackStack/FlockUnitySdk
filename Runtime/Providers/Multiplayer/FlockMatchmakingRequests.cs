using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;

namespace Flock.Providers
{
    /// <summary>The matchmaking ticket routes on the wire, each sent as the sign-in it was asked for or not at all.</summary>
    internal sealed class FlockMatchmakingRequests : FlockProviderBase
    {
        internal FlockMatchmakingRequests(FlockClient client) : base(client)
        {
        }

        // A retry after a lost answer is refused as already queued, which the caller answers as a search left behind.
        internal Task<List<TicketRecord>> CreateAsync(int signInNumber, string queueId, string partyId, IReadOnlyDictionary<string, object> attributes, CancellationToken cancellationToken)
        {
            CreateTicketBody body = new CreateTicketBody { QueueId = queueId, PartyId = partyId, Attributes = attributes };
            return ExecuteAsync(async () =>
            {
                GenericResponse<List<TicketRecord>> response = await FlockHttpClient.PostAsync<GenericResponse<List<TicketRecord>>>(
                    Url(FlockEndpoints.MatchmakingTicket), body, Client.GetBaseHeaders(), cancellationToken);
                if (response?.Result == null || response.Result.Count == 0 || response.Result.Exists(ticket => string.IsNullOrEmpty(ticket?.Id)))
                    throw new FlockNetworkException("Invalid response from server");
                return response.Result;
            }, "Find match", cancellationToken, actsForSignIn: signInNumber);
        }

        internal Task<TicketRecord> ReadAsync(int signInNumber, string ticketId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireTicket(await FlockHttpClient.GetAsync<GenericResponse<TicketRecord>>(
                Url(FlockEndpoints.MatchmakingTicketById(ticketId)), Client.GetBaseHeaders(), cancellationToken)),
                "Read matchmaking search", cancellationToken, actsForSignIn: signInNumber);
        }

        // The player's search without its id: the one queued, else the latest; refused with ticket_not_found for a player who never searched.
        internal Task<TicketRecord> ReadCurrentAsync(int signInNumber, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireTicket(await FlockHttpClient.GetAsync<GenericResponse<TicketRecord>>(
                Url(FlockEndpoints.MatchmakingTicketCurrent), Client.GetBaseHeaders(), cancellationToken)),
                "Read my matchmaking search", cancellationToken, actsForSignIn: signInNumber);
        }

        // A retry after a lost answer is refused as not cancelable, which the caller answers by reading how the search ended.
        internal Task<TicketRecord> CancelAsync(int signInNumber, string ticketId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () => RequireTicket(await FlockHttpClient.DeleteAsync<GenericResponse<TicketRecord>>(
                Url(FlockEndpoints.MatchmakingTicketById(ticketId)), Client.GetBaseHeaders(), cancellationToken)),
                "Cancel matchmaking search", cancellationToken, actsForSignIn: signInNumber);
        }

        private string Url(string path) => $"{Client.GetVersionedApiUrl()}/{path}";

        // A ticket answer that names no ticket or no status is not one, whatever its status code.
        private static TicketRecord RequireTicket(GenericResponse<TicketRecord> response)
        {
            if (response?.Result == null || string.IsNullOrEmpty(response.Result.Id) || string.IsNullOrEmpty(response.Result.Status))
                throw new FlockNetworkException("Invalid response from server");
            return response.Result;
        }
    }
}
