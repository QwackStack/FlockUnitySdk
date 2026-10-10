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
        /// <summary>This device hosts: Netcode for GameObjects started as host and how to reach it is published.</summary>
        public const string StartedAsHost = "started_as_host";
        /// <summary>This device connected to the host as a client.</summary>
        public const string Connected = "connected";
        /// <summary>The host did not publish how to reach it within <see cref="FlockNetcodeOptions.WaitForHost"/>.</summary>
        public const string HostNeverPublished = "host_never_published";
        /// <summary>The session ended while waiting for the host, or while trying the host's address on this player's network.</summary>
        public const string SessionEnded = "session_ended";
        /// <summary>The client started but could not connect to the host, or the host's address could not be used.</summary>
        public const string CouldNotConnect = "could_not_connect";
        /// <summary>Netcode for GameObjects refused to start, such as when the port is already in use.</summary>
        public const string CouldNotStart = "could_not_start";
        /// <summary>The host published a connection of the game's own (such as Steam); <see cref="FlockNetcodeStartResult.Connection"/> holds it.</summary>
        public const string NotDirect = "not_direct";
        /// <summary>The host refused this player; <see cref="FlockNetcodeStartResult.RefusedReason"/> says why.</summary>
        public const string Refused = "refused";
        /// <summary>Flock's relay was needed and could not be used; <see cref="FlockNetcodeStartResult.RelayFailureReason"/> says why.</summary>
        public const string RelayFailed = "relay_failed";
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

    /// <summary>Which players go through Flock's relay (<see cref="FlockNetcodeOptions.Relay"/>). Set it the same on every player of a game.</summary>
    public enum FlockRelayUse
    {
        /// <summary>A player sharing the host's public address tries the host's address on its network for a few seconds (at least two of Unity Transport's connect attempts), then goes through Flock's relay if that is not answered; everyone else goes through the relay, so no port has to be forwarded. Against a host that offers no relay, players connect to the address it published.</summary>
        WhenNeeded,
        /// <summary>Every player goes through Flock's relay, on the host's network too: the host publishes no address of its own and listens on its loopback only.</summary>
        Always,
        /// <summary>Never through the relay: players reach the host on its network, or on a port it forwarded.</summary>
        Never,
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

        /// <summary>Which players go through Flock's relay: by default players on the host's network connect directly (through the relay if the host does not answer there) and everyone else through the relay.</summary>
        public FlockRelayUse Relay { get; set; } = FlockRelayUse.WhenNeeded;
    }

    /// <summary>How starting Netcode for GameObjects for a session ended.</summary>
    public sealed class FlockNetcodeStartResult
    {
        internal FlockNetcodeStartResult(string outcome, bool isHost, string address, int port, FlockMultiplayerSessionConnection connection, string refusedReason = null,
            bool throughRelay = false, string relayFailureReason = null)
        {
            Outcome = outcome;
            IsHost = isHost;
            Address = address;
            Port = port;
            Connection = connection;
            RefusedReason = refusedReason;
            ThroughRelay = throughRelay;
            RelayFailureReason = relayFailureReason;
        }

        /// <summary>Why the host refused this player when <see cref="Outcome"/> is refused: a <see cref="FlockNetcodeRefusedReason"/> value, the game's own reason, or Netcode for GameObjects' own words when the host's netcode turned the player away itself (such as builds whose netcode settings differ); null otherwise.</summary>
        public string RefusedReason { get; }

        /// <summary>A <see cref="FlockNetcodeStartOutcome"/> value.</summary>
        public string Outcome { get; }

        public bool IsHost { get; }

        /// <summary>For a host, the direct address it published (null when it published none, with Relay Always); for a player, the host's address it connected to, its relay IP when it went through the relay.</summary>
        public string Address { get; }

        public int Port { get; }

        /// <summary>The host's connection as published; null when the host never published.</summary>
        public FlockMultiplayerSessionConnection Connection { get; }

        /// <summary>True for a player connected through Flock's relay, and for a host that takes players through it.</summary>
        public bool ThroughRelay { get; }

        /// <summary>Why Flock's relay could not be used, a <see cref="FlockRelayFailure"/> value: set with the relay_failed outcome, and for a host that takes direct players only because its relay could not be opened; null otherwise.</summary>
        public string RelayFailureReason { get; }
    }

    /// <summary>Starts Netcode for GameObjects for a Flock session over Unity Transport, connecting players directly or through Flock's relay.</summary>
    public static class FlockNetcode
    {
        // How often a client still connecting is checked for Netcode for GameObjects having stopped without saying why.
        private static readonly TimeSpan StoppedCheckInterval = TimeSpan.FromMilliseconds(250);
        // The shortest try at the host's address on this network before a WhenNeeded player goes through the relay.
        private static readonly TimeSpan ShortestTryOnTheHostsNetwork = TimeSpan.FromSeconds(3);
        // How long a direct try that is given up waits for Netcode for GameObjects to finish stopping before the relay's start.
        private static readonly TimeSpan LongestWaitToStop = TimeSpan.FromSeconds(5);
        private const string Loopback = "127.0.0.1";
        // Ends a connect whose host never answered in time; never handed to the game.
        private const string HostNeverAnswered = "host_never_answered";

        // Called at the moment a try at the host's network has run out, before it is given up, so a test can stop the netcode in that frame.
        internal static Action<NetworkManager> TryOnTheHostsNetworkRanOutForTesting;

        /// <summary>The host starts Netcode for GameObjects as host on Unity Transport's port, letting in only players whose Flock join token checks out (one connection each), opens its address on Flock's relay unless told not to, then publishes how to reach it; a player waits for that, connects to the host on its network or through the relay (<see cref="FlockNetcodeOptions.Relay"/>) with a join token, and returns once connected or refused. Not in a web player.</summary>
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
                throw new FlockValidationException("A web player cannot connect directly or through Flock's relay. Use a connection of the game's own with PublishConnectionAsync.");
            UnityTransport transport = TransportOf(networkManager);
            if (transport == null)
                throw new FlockValidationException("StartNetcodeAsync connects through Unity Transport: set the NetworkManager's transport to UnityTransport.");
            if (session.IsHost && networkManager.ConnectionApprovalCallback != null)
                throw new FlockValidationException("This NetworkManager already has a ConnectionApprovalCallback. StartNetcodeAsync installs Flock's check of every joining player: move the game's own check to FlockNetcodeOptions.ApproveJoiningPlayer.");
            options = options ?? new FlockNetcodeOptions();
            if (!Enum.IsDefined(typeof(FlockRelayUse), options.Relay))
                throw new FlockValidationException($"FlockNetcodeOptions.Relay must be WhenNeeded, Always or Never, not {(int)options.Relay}.");
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
            // Every address, so players on the LAN and from outside it both reach the host; only this device's own when every player comes through the relay.
            transport.SetConnectionData(transport.ConnectionData.Address, port, options.Relay == FlockRelayUse.Always ? Loopback : "0.0.0.0");
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

            FlockRelayBridge bridge = null;
            string relayFailure = null;
            try
            {
                if (options.Relay != FlockRelayUse.Never)
                {
                    try
                    {
                        bridge = await OpenRelayForPlayersAsync(session, networkManager, transport, port, checks, cancellationToken);
                    }
                    catch (FlockRelayException failure)
                    {
                        relayFailure = failure.Reason;
                        if (options.Relay == FlockRelayUse.Always)
                        {
                            Warn($"Stopped hosting: every player comes through Flock's relay, and it could not be opened ({failure.Message})");
                            networkManager.Shutdown();
                            return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.RelayFailed, true, null, port, session.Connection, relayFailureReason: relayFailure);
                        }
                        // A relay switched off for the game is the studio's choice; any other reason is worth saying.
                        if (relayFailure != FlockRelayFailure.NotOffered)
                            Warn($"Players outside this device's network cannot join through Flock's relay ({failure.Message}); those on its network still can.");
                    }
                }
                // The game may have stopped the netcode while the relay opened; a host that is gone is not published.
                if (!networkManager.IsListening)
                    return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.CouldNotStart, true, null, port, session.Connection, relayFailureReason: relayFailure);
                if (bridge != null)
                    await session.PublishRelayConnectionAsync(bridge.Relay, port, options.Relay == FlockRelayUse.WhenNeeded, cancellationToken);
                else
                    await session.PublishDirectConnectionAsync(port, cancellationToken);
            }
            catch
            {
                // Nobody can reach a host that did not say where it is; stopping the netcode also closes the bridge.
                networkManager.Shutdown();
                throw;
            }
            FlockMultiplayerSessionConnection published = session.Connection;
            string address = null;
            published?.TryGetValue(DirectConnectionValues.Address, out address);
            return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.StartedAsHost, true, address, port, published, throughRelay: bridge != null, relayFailureReason: relayFailure);
        }

        // The host's address on the relay, opened to the relay's own IP: every player reserves on this relay server, so their addresses share it.
        private static async Task<FlockRelayBridge> OpenRelayForPlayersAsync(FlockMultiplayerSession session, NetworkManager networkManager, UnityTransport transport, ushort port,
            FlockJoinChecks checks, CancellationToken cancellationToken)
        {
            FlockRelayConnection relay = await session.OpenRelayAsync(cancellationToken);
            try
            {
                await relay.OpenToHostAsync(relay.Address, cancellationToken);
            }
            catch
            {
                // A relay address no player can reach is given back rather than renewed for the whole session; the next start opens another.
                relay.Close();
                throw;
            }
            FlockRelayBridge bridge = FlockRelayBridge.ForTheHost(relay, port, FlockClient.Instance.Logger);
            checks.UseRelayBridge(bridge, transport);
            CloseWhenStopped(bridge, networkManager, session, true);
            return bridge;
        }

        private static async Task<FlockNetcodeStartResult> ConnectAsync(FlockMultiplayerSession session, NetworkManager networkManager, UnityTransport transport, FlockNetcodeOptions options, CancellationToken cancellationToken)
        {
            FlockMultiplayerSessionConnection connection = await session.WaitForConnectionAsync(options.WaitForHost, cancellationToken);
            if (connection == null)
            {
                string waited = session.HasEnded ? FlockNetcodeStartOutcome.SessionEnded : FlockNetcodeStartOutcome.HostNeverPublished;
                return new FlockNetcodeStartResult(waited, false, null, 0, null);
            }
            bool hostIsDirect = string.Equals(connection.Mode, FlockMultiplayerConnectionMode.Direct, StringComparison.Ordinal);
            bool hostIsRelayed = string.Equals(connection.Mode, FlockMultiplayerConnectionMode.Relay, StringComparison.Ordinal);
            if (!hostIsDirect && !hostIsRelayed)
                return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.NotDirect, false, null, 0, connection);
            FlockRelayPeer hostOnTheRelay = default;
            string relayServer = null;
            if (hostIsRelayed && !connection.TryGetRelay(out hostOnTheRelay, out relayServer))
            {
                Warn("The host published a relay connection with no usable relay address, so this player cannot connect to it.");
                return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.CouldNotConnect, false, null, 0, connection);
            }

            if (hostIsRelayed && options.Relay != FlockRelayUse.Never)
            {
                if (options.Relay == FlockRelayUse.WhenNeeded)
                {
                    // Sharing the host's public address suggests its network, but a network may keep its devices apart (or many share one address), so the relay follows a try that is not answered.
                    FlockDirectAddress onItsNetwork = await connection.FindDirectAddressAsync(cancellationToken);
                    if (onItsNetwork != null && onItsNetwork.SharesTheHostsPublicAddress)
                    {
                        FlockNetcodeStartResult direct = await ConnectToAsync(session, networkManager, transport, connection, onItsNetwork.Address, onItsNetwork.Port, null,
                            TryOnTheHostsNetwork(transport), cancellationToken);
                        if (direct != null)
                            return direct;
                        // The try lasts seconds, and the relay opens only for a session still running.
                        if (session.HasEnded)
                            return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.SessionEnded, false, null, 0, connection);
                        Inform($"The host's address on this network ({onItsNetwork.Address}:{onItsNetwork.Port}) did not answer, so this player connects through Flock's relay.");
                    }
                }
                return await ConnectThroughTheRelayAsync(session, networkManager, transport, connection, hostOnTheRelay, relayServer, cancellationToken);
            }
            if (options.Relay == FlockRelayUse.Always)
            {
                Warn("The host takes no players through Flock's relay, and this player's FlockNetcodeOptions.Relay is Always, so it cannot connect.");
                return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.RelayFailed, false, null, 0, connection, relayFailureReason: FlockRelayFailure.NotOfferedByTheHost);
            }
            FlockDirectAddress address = await connection.FindDirectAddressAsync(cancellationToken);
            if (address == null)
            {
                Warn(hostIsRelayed
                    ? "The host takes players through Flock's relay only, and this player's FlockNetcodeOptions.Relay is Never, so it cannot connect."
                    : "The host published a direct connection with no usable IPv4 address and port, so this player cannot connect to it.");
                return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.CouldNotConnect, false, null, 0, connection);
            }
            return await ConnectToAsync(session, networkManager, transport, connection, address.Address, address.Port, null, null, cancellationToken);
        }

        /// <summary>How long a WhenNeeded player tries the host's address on its network: at least two of Unity Transport's connect attempts, and 3 s.</summary>
        internal static TimeSpan TryOnTheHostsNetwork(UnityTransport transport)
        {
            TimeSpan twoAttempts = TimeSpan.FromMilliseconds(2.0 * transport.ConnectTimeoutMS);
            return twoAttempts > ShortestTryOnTheHostsNetwork ? twoAttempts : ShortestTryOnTheHostsNetwork;
        }

        // Reserves on the relay server the host uses, opens to the host's relay address and connects the netcode to a bridge standing in for the host.
        private static async Task<FlockNetcodeStartResult> ConnectThroughTheRelayAsync(FlockMultiplayerSession session, NetworkManager networkManager, UnityTransport transport,
            FlockMultiplayerSessionConnection connection, FlockRelayPeer host, string relayServer, CancellationToken cancellationToken)
        {
            FlockRelayBridge bridge;
            try
            {
                FlockRelayConnection relay = await session.OpenRelayOnAsync(relayServer, cancellationToken);
                await relay.OpenToHostAsync(host, cancellationToken);
                // The host is the one peer this player sends to, so it gets a channel at once.
                relay.KeepChannelTo(host);
                bridge = FlockRelayBridge.ToTheHost(relay, host, FlockClient.Instance.Logger);
            }
            catch (FlockRelayException failure)
            {
                Warn($"Could not reach the host through Flock's relay ({failure.Message})");
                return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.RelayFailed, false, null, 0, connection, relayFailureReason: failure.Reason);
            }
            FlockNetcodeStartResult result;
            try
            {
                result = await ConnectToAsync(session, networkManager, transport, connection, Loopback, bridge.LocalPort, host, null, cancellationToken);
            }
            catch
            {
                bridge.Close();
                throw;
            }
            if (result.Outcome == FlockNetcodeStartOutcome.Connected)
                CloseWhenStopped(bridge, networkManager, session, false);
            else
                bridge.Close();
            return result;
        }

        // Connects to the host at address:port with a join token; through the relay, that is the bridge and relayedHost is who it stands in for.
        // With giveUpUnansweredAfter, null when the host's transport never answered (in that time, or failed first): the netcode is stopped for another try.
        private static async Task<FlockNetcodeStartResult> ConnectToAsync(FlockMultiplayerSession session, NetworkManager networkManager, UnityTransport transport,
            FlockMultiplayerSessionConnection connection, string address, int port, FlockRelayPeer? relayedHost, TimeSpan? giveUpUnansweredAfter, CancellationToken cancellationToken)
        {
            bool throughRelay = relayedHost.HasValue;
            string reportedAddress = throughRelay ? relayedHost.Value.Ip : address;
            int reportedPort = throughRelay ? relayedHost.Value.Port : port;
            string where = throughRelay ? $"through Flock's relay at {relayedHost.Value}" : $"at {address}:{port}";

            // A token lasts 60 s, so it is asked for once the host is known and sent in front of the game's own connection bytes.
            FlockMultiplayerSessionJoinToken joinToken = await session.RequestJoinTokenAsync(cancellationToken);
            byte[] gamesConnectionData = networkManager.NetworkConfig.ConnectionData;
            bool gamesApproval = networkManager.NetworkConfig.ConnectionApproval;
            transport.SetConnectionData(address, (ushort)port);
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
            // The host's transport answering comes before its check of the player, so a slow check is never taken for an address that does not answer.
            bool hostAnswered = false;
            bool transportGaveUp = false;
            NetworkTransport.TransportEventDelegate heard = (eventType, clientId, payload, receiveTime) =>
            {
                if (eventType == NetworkEvent.Connect)
                    hostAnswered = true;
                // Unity Transport 1.x and 2.x raise a disconnect of their own (a shutdown, a refusal) only once the host answered, so one before is the transport giving up.
                else if ((eventType == NetworkEvent.Disconnect || eventType == NetworkEvent.TransportFailure) && !hostAnswered)
                    transportGaveUp = true;
            };
            System.Diagnostics.Stopwatch trying = System.Diagnostics.Stopwatch.StartNew();
            Func<bool> neverAnswered = null;
            if (giveUpUnansweredAfter.HasValue)
            {
                neverAnswered = () => !hostAnswered && trying.Elapsed >= giveUpUnansweredAfter.Value;
                // Added before StartClient adds the netcode's own, so a failure is heard before the netcode starts shutting down.
                transport.OnTransportEvent += heard;
            }
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
                    return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.CouldNotStart, false, reportedAddress, reportedPort, connection, throughRelay: throughRelay);
                trying.Restart();
                outcome = await ConnectedOrStoppedAsync(networkManager, ended.Task, neverAnswered, cancellationToken);
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
                if (giveUpUnansweredAfter.HasValue)
                    transport.OnTransportEvent -= heard;
            }
            if (outcome == HostNeverAnswered || (outcome == FlockNetcodeStartOutcome.CouldNotConnect && transportGaveUp))
            {
                // Checked just before with no shutdown under way, so this one is ours; one the transport began is waited for.
                if (outcome == HostNeverAnswered)
                    networkManager.Shutdown();
                await StoppedAsync(networkManager, cancellationToken);
                return null;
            }
            if (outcome == FlockNetcodeStartOutcome.Connected)
                return new FlockNetcodeStartResult(outcome, false, reportedAddress, reportedPort, connection, throughRelay: throughRelay);
            // A client that failed to connect has shut itself down already (Netcode for GameObjects 1.x and 2.x both do).
            string refusedReason = ReasonTheHostGave(networkManager.DisconnectReason);
            if (refusedReason != null)
            {
                Warn($"The host {where} refused this player ({refusedReason}).");
                return new FlockNetcodeStartResult(FlockNetcodeStartOutcome.Refused, false, reportedAddress, reportedPort, connection, refusedReason, throughRelay);
            }
            string reason = string.IsNullOrEmpty(networkManager.DisconnectReason) ? "no reason given" : networkManager.DisconnectReason;
            // Forwarding a port is no help to a player tried on the host's network or through the relay.
            Warn(throughRelay || giveUpUnansweredAfter.HasValue
                ? $"Could not connect to the host {where} ({reason})."
                : $"Could not connect to the host {where} ({reason}). Outside a LAN the host's port must be forwarded to it.");
            return new FlockNetcodeStartResult(outcome, false, reportedAddress, reportedPort, connection, throughRelay: throughRelay);
        }

        // A bridge lasts as long as the netcode it serves and the session whose relay it uses, whichever stops first.
        private static void CloseWhenStopped(FlockRelayBridge bridge, NetworkManager networkManager, FlockMultiplayerSession session, bool asHost)
        {
            Action<bool> netcodeStopped = null;
            Action<string> sessionEnded = null;
            Action close = () =>
            {
                if (asHost)
                    networkManager.OnServerStopped -= netcodeStopped;
                else
                    networkManager.OnClientStopped -= netcodeStopped;
                session.Ended -= sessionEnded;
                bridge.Close();
            };
            netcodeStopped = _ => close();
            sessionEnded = _ => close();
            if (asHost)
                networkManager.OnServerStopped += netcodeStopped;
            else
                networkManager.OnClientStopped += netcodeStopped;
            session.Ended += sessionEnded;
            // Stopped before the handlers were in place: nothing would raise them now.
            if (session.HasEnded || !networkManager.IsListening)
                close();
        }

        // Netcode for GameObjects 2.x writes a "[Disconnect Event]..." text of its own when the host gave no reason; 1.x leaves it empty.
        private static string ReasonTheHostGave(string disconnectReason)
            => string.IsNullOrEmpty(disconnectReason) || disconnectReason.StartsWith("[Disconnect Event]", StringComparison.Ordinal) ? null : disconnectReason;

        // Ends with the client's connect or disconnect, when Netcode for GameObjects stops listening without either, or when neverAnswered says the host never answered.
        private static async Task<string> ConnectedOrStoppedAsync(NetworkManager networkManager, Task<string> ended, Func<bool> neverAnswered, CancellationToken cancellationToken)
        {
            while (!ended.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!networkManager.IsListening && !networkManager.ShutdownInProgress)
                    return FlockNetcodeStartOutcome.CouldNotConnect;
                if (neverAnswered != null && neverAnswered())
                {
                    TryOnTheHostsNetworkRanOutForTesting?.Invoke(networkManager);
                    // The game may stop the netcode in the frame the try runs out: a shutdown under way ends the connect on its own.
                    if (!networkManager.ShutdownInProgress)
                        return HostNeverAnswered;
                }
                await Task.WhenAny(ended, FlockWaiting.DelayAsync(StoppedCheckInterval, cancellationToken));
            }
            return ended.Result;
        }

        // Waits for Netcode for GameObjects to finish stopping, so the next start is not refused; a netcode that never stops makes that start fail instead.
        private static async Task StoppedAsync(NetworkManager networkManager, CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
            while ((networkManager.IsListening || networkManager.ShutdownInProgress) && waited.Elapsed < LongestWaitToStop)
                await FlockWaiting.DelayAsync(StoppedCheckInterval, cancellationToken);
        }

        // Flock may have shut down while the netcode was connecting; the outcome is still handed back.
        internal static void Warn(string message)
        {
            if (FlockClient.IsInitialized)
                FlockClient.Instance.Logger.LogWarning(message);
        }

        private static void Inform(string message)
        {
            if (FlockClient.IsInitialized)
                FlockClient.Instance.Logger.LogInfo(message);
        }
    }
}
