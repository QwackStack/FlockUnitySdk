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
        /// <summary>The host refused this player; <see cref="FlockNetcodeStartResult.RefusedReason"/> says why.</summary>
        public const string Refused = "refused";
    }

    /// <summary>Why a host refused a joining player (<see cref="FlockNetcodeStartResult.RefusedReason"/>). An open set: the game's own check gives reasons of its own.</summary>
    public static class FlockNetcodeRefusedReason
    {
        /// <summary>The player connected without a Flock join token, so not through StartNetcodeAsync.</summary>
        public const string NoJoinToken = "no_join_token";
        /// <summary>Flock refused the join token: made up, meant for another session, expired (they last 60 seconds), or its player no longer holds a seat.</summary>
        public const string JoinTokenInvalid = "join_token_invalid";
        /// <summary>The same player is already connected to this host.</summary>
        public const string AlreadyConnected = "already_connected";
        /// <summary>The host could not get Flock's answer in time, so it let nobody unchecked in; the player may try again.</summary>
        public const string FlockUnreachable = "flock_unreachable";
        /// <summary>The session has ended.</summary>
        public const string SessionEnded = "session_ended";
        /// <summary>The device the player reached is no longer the session's host.</summary>
        public const string NotHost = "not_host";
        /// <summary>The game's own check refused the player without giving a reason.</summary>
        public const string RefusedByTheGame = "refused_by_the_game";
        /// <summary>The game's own check threw, so the player was refused.</summary>
        public const string GameCheckFailed = "game_check_failed";
    }

    /// <summary>A player Flock has let in, handed to the game's own check (<see cref="FlockNetcodeOptions.ApproveJoiningPlayer"/>).</summary>
    public sealed class FlockJoiningPlayer
    {
        internal FlockJoiningPlayer(string playerId, ulong clientId, byte[] connectionData)
        {
            PlayerId = playerId;
            ClientId = clientId;
            ConnectionData = connectionData ?? Array.Empty<byte>();
        }

        /// <summary>The Flock player, as Flock verified it.</summary>
        public string PlayerId { get; }

        /// <summary>The player's Netcode for GameObjects client id.</summary>
        public ulong ClientId { get; }

        /// <summary>The bytes the player's game put in NetworkConfig.ConnectionData, without Flock's join token.</summary>
        public byte[] ConnectionData { get; }
    }

    /// <summary>Settings for <see cref="FlockNetcode.StartNetcodeAsync"/>.</summary>
    public sealed class FlockNetcodeOptions
    {
        /// <summary>How long a player waits for the host to publish how to reach it (60 s).</summary>
        public TimeSpan WaitForHost { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>The game's own check on the host, run once Flock has verified a joining player's join token (and for the host's own connection, which Netcode for GameObjects never refuses). The response arrives approved, with the player object the NetworkConfig names; refuse with Approved = false and a Reason, before returning.</summary>
        public Action<FlockJoiningPlayer, NetworkManager.ConnectionApprovalResponse> ApproveJoiningPlayer { get; set; }
    }

    /// <summary>How starting Netcode for GameObjects for a session ended.</summary>
    public sealed class FlockNetcodeStartResult
    {
        internal FlockNetcodeStartResult(string outcome, bool isHost, string address, int port, FlockMultiplayerSessionConnection connection, string refusedReason = null)
        {
            Outcome = outcome;
            IsHost = isHost;
            Address = address;
            Port = port;
            Connection = connection;
            RefusedReason = refusedReason;
        }

        /// <summary>Why the host refused this player when <see cref="Outcome"/> is refused: a <see cref="FlockNetcodeRefusedReason"/> value, the game's own reason, or Netcode for GameObjects' own words when the host's netcode turned the player away itself (such as builds whose netcode settings differ); null otherwise.</summary>
        public string RefusedReason { get; }

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

        /// <summary>The host starts Netcode for GameObjects as host on Unity Transport's port, letting in only players whose Flock join token checks out (one connection each), then publishes its direct connection; a player waits for that, connects to the host's address with a join token, and returns once connected or refused. Not in a web player.</summary>
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
            if (session.IsHost && networkManager.ConnectionApprovalCallback != null)
                throw new FlockValidationException("This NetworkManager already has a ConnectionApprovalCallback. StartNetcodeAsync installs Flock's check of every joining player: move the game's own check to FlockNetcodeOptions.ApproveJoiningPlayer.");
            options = options ?? new FlockNetcodeOptions();
            return session.IsHost
                ? StartAsHostAsync(session, networkManager, transport, options, cancellationToken)
                : ConnectAsync(session, networkManager, transport, options, cancellationToken);
        }

        /// <summary>The Flock player behind a Netcode for GameObjects connection on this host, the host's own included; null for a connection that is not here or a device that is not hosting through StartNetcodeAsync.</summary>
        public static string PlayerForConnection(this FlockMultiplayerSession session, ulong clientId)
        {
            if (session == null)
                throw new FlockValidationException("PlayerForConnection needs the session the host started Netcode for GameObjects for.");
            return FlockJoinChecks.PlayerFor(session, clientId);
        }

        private static UnityTransport TransportOf(NetworkManager networkManager)
        {
            UnityTransport configured = networkManager.NetworkConfig?.NetworkTransport as UnityTransport;
            return configured != null ? configured : networkManager.GetComponent<UnityTransport>();
        }

        private static async Task<FlockNetcodeStartResult> StartAsHostAsync(FlockMultiplayerSession session, NetworkManager networkManager, UnityTransport transport, FlockNetcodeOptions options, CancellationToken cancellationToken)
        {
            ushort port = transport.ConnectionData.Port;
            // Listens on every address, so players on the LAN and from outside it both reach the host.
            transport.SetConnectionData(transport.ConnectionData.Address, port, "0.0.0.0");
            FlockJoinChecks checks = FlockJoinChecks.Install(session, networkManager, options.ApproveJoiningPlayer);
            bool listening = false;
            try
            {
                listening = networkManager.StartHost();
            }
            finally
            {
                // A host that did not start, or threw starting, leaves the NetworkManager as the game had it.
                if (!listening)
                    checks.Remove();
            }
            if (!listening)
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

            // A token lasts 60 s, so it is asked for once the host is known and sent in front of the game's own connection bytes.
            FlockMultiplayerSessionJoinToken joinToken = await session.RequestJoinTokenAsync(cancellationToken);
            byte[] gamesConnectionData = networkManager.NetworkConfig.ConnectionData;
            bool gamesApproval = networkManager.NetworkConfig.ConnectionApproval;
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
                networkManager.NetworkConfig.ConnectionData = FlockJoinTokenPayload.Wrap(joinToken.Token, gamesConnectionData);
                // The host checks every player, and the switch is part of the config both ends must agree on to connect.
                networkManager.NetworkConfig.ConnectionApproval = true;
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
                // Read when the connection request is sent, after StartClient; put back only once the connect has ended.
                networkManager.NetworkConfig.ConnectionData = gamesConnectionData;
                networkManager.NetworkConfig.ConnectionApproval = gamesApproval;
                networkManager.OnClientConnectedCallback -= connected;
                networkManager.OnClientDisconnectCallback -= disconnected;
                networkManager.OnTransportFailure -= transportFailed;
            }
            if (outcome == FlockNetcodeStartOutcome.Connected)
                return new FlockNetcodeStartResult(outcome, false, address.Address, address.Port, connection);
            // A client that failed to connect has shut itself down already (Netcode for GameObjects 1.x and 2.x both do).
            string refusedReason = ReasonTheHostGave(networkManager.DisconnectReason);
            if (refusedReason != null)
            {
                Warn($"The host at {address.Address}:{address.Port} refused this player ({refusedReason}).");
                return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.Refused, false, address.Address, address.Port, connection, refusedReason);
            }
            string reason = string.IsNullOrEmpty(networkManager.DisconnectReason) ? "no reason given" : networkManager.DisconnectReason;
            Warn($"Could not connect to the host at {address.Address}:{address.Port} ({reason}). Outside a LAN the host's port must be forwarded to it.");
            return new FlockNetcodeStartResult(outcome, false, address.Address, address.Port, connection);
        }

        // Netcode for GameObjects 2.x writes a "[Disconnect Event]..." text of its own when the host gave no reason; 1.x leaves it empty.
        private static string ReasonTheHostGave(string disconnectReason)
            => string.IsNullOrEmpty(disconnectReason) || disconnectReason.StartsWith("[Disconnect Event]", StringComparison.Ordinal) ? null : disconnectReason;

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
        internal static void Warn(string message)
        {
            if (FlockClient.IsInitialized)
                FlockClient.Instance.Logger.LogWarning(message);
        }
    }
}
