using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Logging;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
// Unity Transport 2.x names its endpoint NetworkEndpoint, 1.x NetworkEndPoint (each netcode takes its own).
#if FLOCK_UNITY_TRANSPORT_2
using TransportEndpoint = Unity.Networking.Transport.NetworkEndpoint;
#else
using TransportEndpoint = Unity.Networking.Transport.NetworkEndPoint;
#endif

namespace Flock.Providers
{
    // A joining player's token rides in front of the game's own connection bytes: a marker, the token's length, the token.
    internal static class FlockJoinTokenPayload
    {
        private static readonly byte[] Marker = { (byte)'F', (byte)'L', (byte)'K', (byte)'J', 1 };
        private const int LengthBytes = 2;

        internal static byte[] Wrap(string token, byte[] gameData)
        {
            byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
            int header = Marker.Length + LengthBytes;
            int gameLength = gameData?.Length ?? 0;
            byte[] payload = new byte[header + tokenBytes.Length + gameLength];
            Buffer.BlockCopy(Marker, 0, payload, 0, Marker.Length);
            payload[Marker.Length] = (byte)(tokenBytes.Length >> 8);
            payload[Marker.Length + 1] = (byte)(tokenBytes.Length & 0xFF);
            Buffer.BlockCopy(tokenBytes, 0, payload, header, tokenBytes.Length);
            if (gameLength > 0)
                Buffer.BlockCopy(gameData, 0, payload, header + tokenBytes.Length, gameLength);
            return payload;
        }

        // False for a payload with no join token in front: a player that did not connect through StartNetcodeAsync.
        internal static bool TryRead(byte[] payload, out string token, out byte[] gameData)
        {
            token = null;
            gameData = null;
            int header = Marker.Length + LengthBytes;
            if (payload == null || payload.Length < header)
                return false;
            for (int index = 0; index < Marker.Length; index++)
            {
                if (payload[index] != Marker[index])
                    return false;
            }
            int length = (payload[Marker.Length] << 8) | payload[Marker.Length + 1];
            if (length == 0 || header + length > payload.Length)
                return false;
            token = Encoding.UTF8.GetString(payload, header, length);
            gameData = new byte[payload.Length - header - length];
            Buffer.BlockCopy(payload, header + length, gameData, 0, gameData.Length);
            return true;
        }
    }

    // The host's check of every joining player, installed by StartNetcodeAsync: a player is let in only once Flock has verified their
    // join token, with one connection a player, and each connection's player is remembered. Removed when the netcode stops.
    internal sealed class FlockJoinChecks
    {
        private static readonly ConditionalWeakTable<FlockMultiplayerSession, FlockJoinChecks> Running = new ConditionalWeakTable<FlockMultiplayerSession, FlockJoinChecks>();

        // Netcode for GameObjects drops a waiting player without a reason after its connection buffer timeout; the check ends first.
        private static readonly TimeSpan EndsBeforeNetcodeDrops = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ShortestCheck = TimeSpan.FromSeconds(1);
        // A player gives up by its own timeout, 10 s unless its game changed it, so the refusal reaches it before then.
        private static readonly TimeSpan LongestCheck = TimeSpan.FromSeconds(8);

        private readonly FlockMultiplayerSession _session;
        private readonly NetworkManager _networkManager;
        private readonly Action<FlockJoiningPlayer, NetworkManager.ConnectionApprovalResponse> _gamesCheck;
        private readonly bool _approvalWasOn;
        private readonly Action<NetworkManager.ConnectionApprovalRequest, NetworkManager.ConnectionApprovalResponse> _approve;
        private readonly Action<bool> _netcodeStopped;
        private readonly Dictionary<ulong, string> _players = new Dictionary<ulong, string>();
        private readonly CancellationTokenSource _removedToken = new CancellationTokenSource();
        private bool _removed;
        // Relayed players reach the host from a loopback port of the bridge's: one let in is told to the bridge, and forgotten when it leaves.
        private FlockRelayBridge _bridge;
        private UnityTransport _transport;
        private readonly Dictionary<ulong, FlockRelayPeer> _relayedPlayers = new Dictionary<ulong, FlockRelayPeer>();
        private readonly Action<ulong> _clientLeft;

        private FlockJoinChecks(FlockMultiplayerSession session, NetworkManager networkManager, Action<FlockJoiningPlayer, NetworkManager.ConnectionApprovalResponse> gamesCheck)
        {
            _session = session;
            _networkManager = networkManager;
            _gamesCheck = gamesCheck;
            _approvalWasOn = networkManager.NetworkConfig.ConnectionApproval;
            _approve = Approve;
            _netcodeStopped = _ => Remove();
            _clientLeft = ForgetRelayedPlayer;
        }

        internal static FlockJoinChecks Install(FlockMultiplayerSession session, NetworkManager networkManager, Action<FlockJoiningPlayer, NetworkManager.ConnectionApprovalResponse> gamesCheck)
        {
            FlockJoinChecks checks = new FlockJoinChecks(session, networkManager, gamesCheck);
            networkManager.NetworkConfig.ConnectionApproval = true;
            networkManager.ConnectionApprovalCallback = checks._approve;
            networkManager.OnServerStopped += checks._netcodeStopped;
            Running.Remove(session);
            Running.Add(session, checks);
            return checks;
        }

        internal static string PlayerFor(FlockMultiplayerSession session, ulong clientId)
            => Running.TryGetValue(session, out FlockJoinChecks checks) ? checks.PlayerOf(clientId) : null;

        internal static FlockRelayBridge RelayBridgeForTesting(FlockMultiplayerSession session)
            => Running.TryGetValue(session, out FlockJoinChecks checks) ? checks._bridge : null;

        /// <summary>Players the host takes through the relay reach it through <paramref name="bridge"/>; each one let in gets a relay channel.</summary>
        internal void UseRelayBridge(FlockRelayBridge bridge, UnityTransport transport)
        {
            if (_removed)
                return;
            _bridge = bridge;
            _transport = transport;
            _networkManager.OnClientDisconnectCallback += _clientLeft;
        }

        // Puts the NetworkManager back as the game had it, unless the game has set a check of its own since.
        internal void Remove()
        {
            _removed = true;
            _removedToken.Cancel();
            // Another start for the same session may have registered since; that one stays.
            if (Running.TryGetValue(_session, out FlockJoinChecks current) && current == this)
                Running.Remove(_session);
            _networkManager.OnServerStopped -= _netcodeStopped;
            _networkManager.OnClientDisconnectCallback -= _clientLeft;
            if (_networkManager.ConnectionApprovalCallback == _approve)
            {
                _networkManager.ConnectionApprovalCallback = null;
                _networkManager.NetworkConfig.ConnectionApproval = _approvalWasOn;
            }
        }

        private string PlayerOf(ulong clientId)
        {
            if (!IsStillHere(clientId))
                return null;
            return _players.TryGetValue(clientId, out string playerId) ? playerId : null;
        }

        private bool IsStillHere(ulong clientId)
            => _networkManager.PendingClients.ContainsKey(clientId) || _networkManager.ConnectedClientsIds.Contains(clientId);

        private void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            ulong clientId = request.ClientNetworkId;
            if (clientId == NetworkManager.ServerClientId)
            {
                // The host's own connection, which Netcode for GameObjects approves at once whatever the answer.
                _players[clientId] = _session.PlayerId;
                Admit(response);
                // A copy: the game's own NetworkConfig bytes stay as they are whatever its check does.
                AskTheGame(clientId, _session.PlayerId, (byte[])request.Payload?.Clone(), response);
                return;
            }
            if (!FlockJoinTokenPayload.TryRead(request.Payload, out string token, out byte[] gameData))
            {
                Refuse(response, FlockNetcodeRefusedReason.NoJoinToken);
                return;
            }
            response.Pending = true;
            _ = CheckAsync(clientId, token, gameData, response);
        }

        // Asks Flock without holding the frame; the answer is taken on the main thread, where Netcode for GameObjects reads it.
        private async Task CheckAsync(ulong clientId, string token, byte[] gameData, NetworkManager.ConnectionApprovalResponse response)
        {
            string refused = null;
            string playerId = null;
            // Why Flock gave no verdict, said once the player is really refused for it.
            string unreachableBecause = null;
            using (CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(_removedToken.Token))
            {
                Task<string> verifying = null;
                try
                {
                    verifying = _session.VerifyJoinTokenAsync(token, giveUp.Token);
                    Task first = await Task.WhenAny(verifying, FlockWaiting.DelayAsync(CheckTime(), giveUp.Token));
                    if (first == verifying)
                    {
                        playerId = await verifying;
                    }
                    else
                    {
                        giveUp.Cancel();
                        refused = _session.HasEnded ? FlockNetcodeRefusedReason.SessionEnded : FlockNetcodeRefusedReason.FlockUnreachable;
                        unreachableBecause = $"Flock did not answer the check of their join token within {CheckTime().TotalSeconds:0.#} s";
                    }
                }
                catch (OperationCanceledException)
                {
                    refused = _session.HasEnded ? FlockNetcodeRefusedReason.SessionEnded : FlockNetcodeRefusedReason.FlockUnreachable;
                }
                catch (Exception failure)
                {
                    refused = ReasonFor(failure);
                    unreachableBecause = $"Flock could not check their join token ({FlockExceptionText.MessageOf(failure)})";
                }
                if (verifying != null)
                    ObserveLateFailure(verifying);
            }
            // The netcode stopped meanwhile and dropped the waiting player itself.
            if (_removed)
                return;
            if (!_networkManager.PendingClients.ContainsKey(clientId))
            {
                // The player left while being checked: nobody is waiting for an answer, and nothing is remembered.
                response.Approved = false;
                response.Pending = false;
                return;
            }
            if (refused == null && IsConnectedElsewhere(playerId, clientId))
                refused = FlockNetcodeRefusedReason.AlreadyConnected;
            if (refused != null)
            {
                if (refused == FlockNetcodeRefusedReason.FlockUnreachable && unreachableBecause != null)
                    FlockNetcode.Warn($"Refused a joining player: {unreachableBecause}.");
                Refuse(response, refused);
                return;
            }
            Admit(response);
            AskTheGame(clientId, playerId, gameData, response);
            // Remembered once let in, so a refused connection never holds the player's place.
            if (response.Approved)
            {
                _players[clientId] = playerId;
                LetInThroughTheRelay(clientId);
            }
            response.Pending = false;
        }

        // A player let in from one of the bridge's loopback ports is a relayed player: it keeps its place there and gets a relay channel.
        private void LetInThroughTheRelay(ulong clientId)
        {
            if (_bridge == null)
                return;
            TransportEndpoint from = _transport.GetEndpoint(clientId);
            if (!from.IsLoopback || !_bridge.TryGetPeer(from.Port, out FlockRelayPeer peer))
                return;
            _relayedPlayers[clientId] = peer;
            _bridge.LetIn(peer);
        }

        private void ForgetRelayedPlayer(ulong clientId)
        {
            if (!_relayedPlayers.TryGetValue(clientId, out FlockRelayPeer peer))
                return;
            _relayedPlayers.Remove(clientId);
            _bridge.Forget(peer);
        }

        // Flock's verdict, or the host's state; any other failure lets nobody in unchecked.
        private string ReasonFor(Exception failure)
        {
            FlockErrorCode? code = (failure as FlockException)?.ErrorCode;
            if (_session.HasEnded || code == FlockErrorCode.MultiplayerSessionNotFound)
                return FlockNetcodeRefusedReason.SessionEnded;
            if (code == FlockErrorCode.MultiplayerInvalidJoinToken)
                return FlockNetcodeRefusedReason.JoinTokenInvalid;
            if (code == FlockErrorCode.MultiplayerNotHost)
                return FlockNetcodeRefusedReason.NotHost;
            return FlockNetcodeRefusedReason.FlockUnreachable;
        }

        // One connection a player: another connection of the same player still here, waiting or connected, keeps its place.
        private bool IsConnectedElsewhere(string playerId, ulong clientId)
        {
            foreach (ulong other in _players.Keys.ToList())
            {
                if (!IsStillHere(other))
                    _players.Remove(other);
                else if (other != clientId && string.Equals(_players[other], playerId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private TimeSpan CheckTime()
        {
            TimeSpan check = TimeSpan.FromSeconds(_networkManager.NetworkConfig.ClientConnectionBufferTimeout) - EndsBeforeNetcodeDrops;
            if (check > LongestCheck)
                check = LongestCheck;
            return check < ShortestCheck ? ShortestCheck : check;
        }

        // Approved with what Netcode for GameObjects does with approval off: the player object when the config names one.
        private void Admit(NetworkManager.ConnectionApprovalResponse response)
        {
            response.Approved = true;
            response.Reason = null;
            response.CreatePlayerObject = _networkManager.NetworkConfig.PlayerPrefab != null;
        }

        private static void Refuse(NetworkManager.ConnectionApprovalResponse response, string reason)
        {
            response.Approved = false;
            response.CreatePlayerObject = false;
            response.Reason = reason;
            response.Pending = false;
        }

        private void AskTheGame(ulong clientId, string playerId, byte[] gameData, NetworkManager.ConnectionApprovalResponse response)
        {
            if (_gamesCheck == null)
                return;
            try
            {
                _gamesCheck(new FlockJoiningPlayer(playerId, clientId, gameData), response);
                if (!response.Approved && string.IsNullOrEmpty(response.Reason))
                    response.Reason = FlockNetcodeRefusedReason.RefusedByTheGame;
            }
            catch (Exception failure)
            {
                FlockNetcode.Warn($"Refused a joining player: the game's own check threw ({FlockExceptionText.Describe(failure)}).");
                Refuse(response, FlockNetcodeRefusedReason.GameCheckFailed);
            }
        }

        // A check given up on may still fail later; its failure is read here so it never reaches the unobserved-task handler.
        private static void ObserveLateFailure(Task verifying)
        {
            if (!verifying.IsCompleted)
                verifying.ContinueWith(done => _ = done.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            else if (verifying.IsFaulted)
                _ = verifying.Exception;
        }
    }
}
