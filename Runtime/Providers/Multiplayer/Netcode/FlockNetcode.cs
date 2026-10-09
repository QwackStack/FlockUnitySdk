using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Flock.Providers
{
    /// <summary>How <see cref="FlockNetcode.StartNetcodeAsync"/> ended. An open set: compare with these and keep a default.</summary>
    public static class FlockNetcodeStartOutcome
    {
        /// <summary>This device hosts: Netcode for GameObjects started as host and the direct connection is published.</summary>
        public const string StartedAsHost = "started_as_host";
        /// <summary>This device connected to the host as a client.</summary>
        public const string Connected = "connected";
        /// <summary>The host did not publish how to reach it within <see cref="FlockNetcodeOptions.WaitForHost"/>.</summary>
        public const string HostNeverPublished = "host_never_published";
        /// <summary>The session ended while waiting for the host.</summary>
        public const string SessionEnded = "session_ended";
        /// <summary>The client started but could not connect to the host, or the host's address could not be used.</summary>
        public const string CouldNotConnect = "could_not_connect";
        /// <summary>Netcode for GameObjects refused to start, such as when the port is already in use.</summary>
        public const string CouldNotStart = "could_not_start";
        /// <summary>The host published a connection of the game's own (such as Steam); <see cref="FlockNetcodeStartResult.Connection"/> holds it.</summary>
        public const string NotDirect = "not_direct";
    }

    /// <summary>Settings for <see cref="FlockNetcode.StartNetcodeAsync"/>.</summary>
    public sealed class FlockNetcodeOptions
    {
        /// <summary>How long a player waits for the host to publish how to reach it (60 s).</summary>
        public TimeSpan WaitForHost { get; set; } = TimeSpan.FromSeconds(60);
    }

    /// <summary>How starting Netcode for GameObjects for a session ended.</summary>
    public sealed class FlockNetcodeStartResult
    {
        internal FlockNetcodeStartResult(string outcome, bool isHost, string address, int port, FlockMultiplayerSessionConnection connection)
        {
            Outcome = outcome;
            IsHost = isHost;
            Address = address;
            Port = port;
            Connection = connection;
        }

        /// <summary>A <see cref="FlockNetcodeStartOutcome"/> value.</summary>
        public string Outcome { get; }

        public bool IsHost { get; }

        /// <summary>The address published (host) or connected to (player); null when there was none.</summary>
        public string Address { get; }

        public int Port { get; }

        /// <summary>The host's connection as published; null when the host never published.</summary>
        public FlockMultiplayerSessionConnection Connection { get; }
    }

    /// <summary>Starts Netcode for GameObjects for a Flock session over Unity Transport, connecting players directly.</summary>
    public static class FlockNetcode
    {
        // How often a client still connecting is checked for Netcode for GameObjects having stopped without saying why.
        private static readonly TimeSpan StoppedCheckInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>The host starts Netcode for GameObjects as host on Unity Transport's port, then publishes its direct connection; a player waits for that, connects to the host's address, and returns once connected. Not in a web player.</summary>
        public static Task<FlockNetcodeStartResult> StartNetcodeAsync(this FlockMultiplayerSession session, NetworkManager networkManager, FlockNetcodeOptions options = null, CancellationToken cancellationToken = default)
        {
            if (session == null)
                throw new FlockValidationException("StartNetcodeAsync needs the session: host or join one, or find a match, first.");
            if (session.HasEnded)
                throw new FlockValidationException($"This session has ended ({session.EndReason}). Host or join another.");
            if (networkManager == null)
                throw new FlockValidationException("StartNetcodeAsync needs the scene's NetworkManager.");
            if (networkManager.IsListening || networkManager.ShutdownInProgress)
                throw new FlockValidationException("Netcode for GameObjects is already running on this NetworkManager. Shut it down before starting it for a session.");
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                throw new FlockValidationException("A web player cannot connect directly. Use a connection of the game's own with PublishConnectionAsync.");
            UnityTransport transport = TransportOf(networkManager);
            if (transport == null)
                throw new FlockValidationException("StartNetcodeAsync connects through Unity Transport: set the NetworkManager's transport to UnityTransport.");
            return session.IsHost
                ? StartAsHostAsync(session, networkManager, transport, cancellationToken)
                : ConnectAsync(session, networkManager, transport, options ?? new FlockNetcodeOptions(), cancellationToken);
        }

        private static UnityTransport TransportOf(NetworkManager networkManager)
        {
            UnityTransport configured = networkManager.NetworkConfig?.NetworkTransport as UnityTransport;
            return configured != null ? configured : networkManager.GetComponent<UnityTransport>();
        }

        private static async Task<FlockNetcodeStartResult> StartAsHostAsync(FlockMultiplayerSession session, NetworkManager networkManager, UnityTransport transport, CancellationToken cancellationToken)
        {
            ushort port = transport.ConnectionData.Port;
            // Listens on every address, so players on the LAN and from outside it both reach the host.
            transport.SetConnectionData(transport.ConnectionData.Address, port, "0.0.0.0");
            if (!networkManager.StartHost())
                return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.CouldNotStart, true, null, port, session.Connection);
            try
            {
                await session.PublishDirectConnectionAsync(port, cancellationToken);
            }
            catch
            {
                // Nobody can reach a host that did not say where it is.
                networkManager.Shutdown();
                throw;
            }
            FlockMultiplayerSessionConnection published = session.Connection;
            string address = null;
            published?.TryGetValue(DirectConnectionValues.Address, out address);
            return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.StartedAsHost, true, address, port, published);
        }

        private static async Task<FlockNetcodeStartResult> ConnectAsync(FlockMultiplayerSession session, NetworkManager networkManager, UnityTransport transport, FlockNetcodeOptions options, CancellationToken cancellationToken)
        {
            FlockMultiplayerSessionConnection connection = await session.WaitForConnectionAsync(options.WaitForHost, cancellationToken);
            if (connection == null)
            {
                string waited = session.HasEnded ? FlockNetcodeStartOutcome.SessionEnded : FlockNetcodeStartOutcome.HostNeverPublished;
                return new FlockNetcodeStartResult(waited, false, null, 0, null);
            }
            if (!string.Equals(connection.Mode, FlockMultiplayerConnectionMode.Direct, StringComparison.Ordinal))
                return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.NotDirect, false, null, 0, connection);
            FlockDirectAddress address = await connection.FindDirectAddressAsync(cancellationToken);
            if (address == null)
            {
                Warn("The host published a direct connection with no usable IPv4 address and port, so this player cannot connect to it.");
                return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.CouldNotConnect, false, null, 0, connection);
            }

            transport.SetConnectionData(address.Address, (ushort)address.Port);
            TaskCompletionSource<string> ended = new TaskCompletionSource<string>();
            Action<ulong> connected = clientId =>
            {
                if (clientId == networkManager.LocalClientId)
                    ended.TrySetResult(FlockNetcodeStartOutcome.Connected);
            };
            Action<ulong> disconnected = clientId =>
            {
                if (clientId == networkManager.LocalClientId || !networkManager.IsConnectedClient)
                    ended.TrySetResult(FlockNetcodeStartOutcome.CouldNotConnect);
            };
            Action transportFailed = () => ended.TrySetResult(FlockNetcodeStartOutcome.CouldNotConnect);
            networkManager.OnClientConnectedCallback += connected;
            networkManager.OnClientDisconnectCallback += disconnected;
            networkManager.OnTransportFailure += transportFailed;
            string outcome;
            try
            {
                if (!networkManager.StartClient())
                    return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.CouldNotStart, false, address.Address, address.Port, connection);
                outcome = await ConnectedOrStoppedAsync(networkManager, ended.Task, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (networkManager.IsListening)
                    networkManager.Shutdown();
                throw;
            }
            finally
            {
                networkManager.OnClientConnectedCallback -= connected;
                networkManager.OnClientDisconnectCallback -= disconnected;
                networkManager.OnTransportFailure -= transportFailed;
            }
            // A client that failed to connect has shut itself down already (Netcode for GameObjects 1.x and 2.x both do).
            if (outcome != FlockNetcodeStartOutcome.Connected)
            {
                string reason = string.IsNullOrEmpty(networkManager.DisconnectReason) ? "no reason given" : networkManager.DisconnectReason;
                Warn($"Could not connect to the host at {address.Address}:{address.Port} ({reason}). Outside a LAN the host's port must be forwarded to it.");
            }
            return new FlockNetcodeStartResult(outcome, false, address.Address, address.Port, connection);
        }

        // Ends with the client's connect or disconnect, or when Netcode for GameObjects stops listening without either.
        private static async Task<string> ConnectedOrStoppedAsync(NetworkManager networkManager, Task<string> ended, CancellationToken cancellationToken)
        {
            while (!ended.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!networkManager.IsListening && !networkManager.ShutdownInProgress)
                    return FlockNetcodeStartOutcome.CouldNotConnect;
                await Task.WhenAny(ended, FlockWaiting.DelayAsync(StoppedCheckInterval, cancellationToken));
            }
            return ended.Result;
        }

        // Flock may have shut down while the netcode was connecting; the outcome is still handed back.
        private static void Warn(string message)
        {
            if (FlockClient.IsInitialized)
                FlockClient.Instance.Logger.LogWarning(message);
        }
    }
}
