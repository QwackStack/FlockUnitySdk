using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Flock.Providers
{
    /// <summary>A server's IPv4 address from its name, for the STUN and relay servers Flock lists.</summary>
    internal static class FlockNameLookup
    {
        /// <summary>The first IPv4 address of <paramref name="host"/> (an address as written is taken as it is), or null when none comes within <paramref name="wait"/>: a lookup takes no token and can hang for as long as the network's resolver does.</summary>
        internal static async Task<IPAddress> FindIpv4Async(string host, TimeSpan wait, Func<string, Task<IPAddress[]>> lookUpForTesting, CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(host, out IPAddress literal))
                return literal.AddressFamily == AddressFamily.InterNetwork ? literal : null;
            Task<IPAddress[]> looking = lookUpForTesting != null ? lookUpForTesting(host) : Dns.GetHostAddressesAsync(host);
            FlockMultiplayerSessions.LetRun(looking);
            using (CancellationTokenSource stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task first = await Task.WhenAny(looking, FlockWaiting.DelayAsync(wait, stopWaiting.Token));
                stopWaiting.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                if (first != looking || looking.IsFaulted || looking.IsCanceled)
                    return null;
            }
            foreach (IPAddress address in looking.Result)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    return address;
            }
            return null;
        }
    }
}
