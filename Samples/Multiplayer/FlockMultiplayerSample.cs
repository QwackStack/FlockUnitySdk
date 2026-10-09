using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Providers;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Flock.Samples
{
    /// What both multiplayer samples share: signing in, starting Netcode for GameObjects for a session through Flock, the
    /// players, a Wave sent over the netcode, and leaving. Each sample adds how its players find each other.
    public abstract class FlockMultiplayerSample : MonoBehaviour
    {
        private const string WaveMessage = "flock-sample-wave";
        private const int LinesKept = 4;

        [Tooltip("The NetworkManager to start. Left empty, the scene's is used, or the sample makes one.")]
        [SerializeField] private NetworkManager _networkManager;

        [Tooltip("The port the host listens on when this sample makes the NetworkManager.")]
        [SerializeField] private ushort _port = 7777;

        private readonly List<string> _heard = new List<string>();
        private string _deviceId;
        private NetworkManager _network;

        /// <summary>The session this player is in, or null.</summary>
        public FlockMultiplayerSession Session { get; private set; }

        /// <summary>What the sample last said, as shown on screen.</summary>
        public string Status { get; protected set; } = "Ready.";

        /// <summary>True while a call is on its way; the buttons wait.</summary>
        public bool Busy { get; protected set; }

        /// <summary>True once this player is hosting, or connected to the host, over the netcode.</summary>
        public bool Playing => _network != null && (_network.IsHost || _network.IsConnectedClient) && Session != null && !Session.HasEnded;

        /// <summary>The waves heard, newest last.</summary>
        public IReadOnlyList<string> Heard => _heard;

        /// <summary>The NetworkManager the sample starts; set it before playing, or the sample finds or makes one.</summary>
        public NetworkManager NetworkManagerInUse
        {
            get => _network != null ? _network : _networkManager;
            set => _networkManager = value;
        }

        protected abstract string Title { get; }

        // The sample's own way for players to find each other, drawn once signed in and in no session.
        protected abstract void DrawFindingPlayers();

        protected virtual void Awake()
        {
            _deviceId = SystemInfo.deviceUniqueIdentifier;
        }

        // A scene change gives the seat up too, so nobody waits on a player who can no longer play.
        protected virtual void OnDestroy()
        {
            _ = LeaveQuietlyAsync();
        }

        private void OnGUI()
        {
            // As tall as the window allows, so the status and the note below stay on screen in a small window.
            GUILayout.BeginArea(new Rect(16f, 16f, Mathf.Min(440f, Screen.width - 32f), Mathf.Min(460f, Screen.height - 32f)), GUI.skin.box);
            GUILayout.Label("Flock - " + Title);
            GUILayout.Space(6f);
            if (!FlockClient.IsInitialized)
            {
                GUILayout.Label("Flock is not initialized.\n\nAdd a FlockBootstrap to the scene (Flock > Settings -> \"Add Flock Bootstrap to Scene\"), fill in your FlockConfig, then press Play.");
                GUILayout.EndArea();
                return;
            }
            GUI.enabled = !Busy;
            if (!FlockClient.Instance.IsAuthenticated)
                DrawSignIn();
            else if (Session == null || Session.HasEnded)
                DrawFindingPlayers();
            else
                DrawSession();
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.Label(Status);
            GUILayout.Label("Connects on a LAN, or where the host's port is forwarded to it.");
            GUILayout.EndArea();
        }

        private void DrawSignIn()
        {
            // Two copies of a game on one computer share a device id, and so would be one player: change it in one of them.
            GUILayout.Label("Device id (change it in a second copy on this computer):");
            _deviceId = GUILayout.TextField(_deviceId ?? "", 64);
            if (GUILayout.Button("Sign In With This Device"))
                _ = SignInAsync(_deviceId);
        }

        private void DrawSession()
        {
            GUILayout.Label($"Session {Session.Id}, code {Session.JoinCode}{(Session.IsHost ? " (you host)" : "")}");
            if (_network != null && _network.IsHost)
            {
                GUILayout.Label("Connected: " + _network.ConnectedClientsIds.Count);
                foreach (ulong clientId in _network.ConnectedClientsIds)
                    GUILayout.Label($"  {clientId}  {Session.PlayerForConnection(clientId)}{(clientId == NetworkManager.ServerClientId ? " (you)" : "")}");
            }
            else
            {
                GUILayout.Label("Players in the session: " + Session.Players.Count);
                foreach (FlockMultiplayerSessionPlayer player in Session.Players)
                    GUILayout.Label($"  {player.PlayerId}{(player.PlayerId == Session.HostPlayerId ? " (host)" : "")}{(player.PlayerId == FlockClient.Instance.CurrentPlayerId ? " (you)" : "")}");
            }
            GUILayout.BeginHorizontal();
            GUI.enabled = !Busy && Playing;
            if (GUILayout.Button("Wave"))
                Wave();
            GUI.enabled = !Busy;
            if (GUILayout.Button(Session.IsHost ? "End Session" : "Leave"))
                _ = LeaveAsync();
            GUILayout.EndHorizontal();
            foreach (string line in _heard)
                GUILayout.Label(line);
        }

        /// <summary>Signs in with a device id, registering it as a new player the first time Flock sees it.</summary>
        public async Task SignInAsync(string deviceId)
        {
            Busy = true;
            Status = "Signing in...";
            try
            {
                try
                {
                    await FlockClient.Instance.Authentication.LoginWithDeviceAsync(deviceId);
                }
                // A device Flock has never seen is not a player yet, so it is registered, with no name: Flock keeps names unique.
                catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.PlayerInvalidLoginCredentials)
                {
                    await FlockClient.Instance.Authentication.RegisterWithDeviceAsync(deviceId);
                }
                Status = "Signed in as " + FlockClient.Instance.CurrentPlayerId + ".";
            }
            catch (Exception ex)
            {
                Status = "Sign-in failed: " + ex.Message;
            }
            finally
            {
                Busy = false;
            }
        }

        // Starts the netcode for a session found or joined: the host listens and says where it is, a player connects to it.
        protected async Task PlayAsync(FlockMultiplayerSession session)
        {
            Session = session;
            session.Ended += OnSessionEnded;
            Status = session.IsHost ? "Starting to host..." : "Waiting for the host...";
            FlockNetcodeStartResult started;
            try
            {
                started = await session.StartNetcodeAsync(NetworkManagerToUse());
            }
            catch
            {
                // A start that failed gives the seat up, so the player is in no session they cannot play.
                await LeaveQuietlyAsync();
                throw;
            }
            switch (started.Outcome)
            {
                case FlockNetcodeStartOutcome.StartedAsHost:
                    Status = "Hosting. Friends join with code " + session.JoinCode + ".";
                    break;
                case FlockNetcodeStartOutcome.Connected:
                    Status = "Connected to the host.";
                    break;
                case FlockNetcodeStartOutcome.Refused:
                    Status = "The host refused this player: " + started.RefusedReason + ".";
                    break;
                case FlockNetcodeStartOutcome.CouldNotConnect:
                    Status = "Could not reach the host. Outside a LAN, its port must be forwarded to it.";
                    break;
                case FlockNetcodeStartOutcome.HostNeverPublished:
                    Status = "The host never said where it is.";
                    break;
                default:
                    Status = "Could not start: " + started.Outcome + ".";
                    break;
            }
            if (started.Outcome != FlockNetcodeStartOutcome.StartedAsHost && started.Outcome != FlockNetcodeStartOutcome.Connected)
            {
                await LeaveQuietlyAsync();
                return;
            }
            _network.CustomMessagingManager.RegisterNamedMessageHandler(WaveMessage, OnWave);
            _network.OnClientDisconnectCallback += OnDisconnected;
        }

        /// <summary>Sends a wave over the netcode; the host passes each player's wave on to the others.</summary>
        public void Wave()
        {
            if (!Playing)
                return;
            Remember("You waved.");
            if (!_network.IsHost)
            {
                // The host names the player from the connection, so a player sends no name of its own.
                Send(NetworkManager.ServerClientId, string.Empty);
                return;
            }
            string me = FlockClient.Instance.CurrentPlayerId;
            foreach (ulong clientId in _network.ConnectedClientsIds)
            {
                if (clientId != NetworkManager.ServerClientId)
                    Send(clientId, me);
            }
        }

        /// <summary>Leaves the session, or ends it for everyone when this player hosts, and stops the netcode.</summary>
        public async Task LeaveAsync()
        {
            Busy = true;
            try
            {
                FlockMultiplayerSession session = Session;
                StopNetcode();
                if (session != null && !session.HasEnded)
                {
                    if (session.IsHost)
                        await session.EndAsync();
                    else
                        await session.LeaveAsync();
                }
                Status = "Left the session.";
            }
            catch (Exception ex)
            {
                Status = "Leaving failed: " + ex.Message;
            }
            finally
            {
                Busy = false;
            }
        }

        private async Task LeaveQuietlyAsync()
        {
            FlockMultiplayerSession session = Session;
            StopNetcode();
            try
            {
                if (session != null && !session.HasEnded)
                    await session.LeaveAsync();
            }
            catch (Exception)
            {
                // The seat is given up by the server's sweep if this does not reach it (Flock shut down, no network).
            }
        }

        private void OnSessionEnded(string reason)
        {
            StopNetcode();
            Status = "The session ended (" + reason + ").";
        }

        private void OnDisconnected(ulong clientId)
        {
            if (_network != null && !_network.IsHost && (clientId == _network.LocalClientId || !_network.IsConnectedClient))
                Status = "Lost the host's connection.";
        }

        private void OnWave(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out string named);
            // The host takes the player Flock verified for the connection, never a name a player sends; players take the host's.
            string playerId = _network.IsHost ? Session.PlayerForConnection(sender) : named;
            if (string.IsNullOrEmpty(playerId))
                return;
            Remember(playerId + " waved.");
            if (!_network.IsHost)
                return;
            // The host passes a player's wave on to everyone else.
            foreach (ulong clientId in _network.ConnectedClientsIds)
            {
                if (clientId != NetworkManager.ServerClientId && clientId != sender)
                    Send(clientId, playerId);
            }
        }

        private void Send(ulong clientId, string playerId)
        {
            using (FastBufferWriter writer = new FastBufferWriter(128, Allocator.Temp))
            {
                writer.WriteValueSafe(playerId);
                _network.CustomMessagingManager.SendNamedMessage(WaveMessage, clientId, writer, NetworkDelivery.Reliable);
            }
        }

        private void Remember(string line)
        {
            _heard.Add(line);
            if (_heard.Count > LinesKept)
                _heard.RemoveAt(0);
        }

        // The NetworkManager given, else the scene's, else one this sample makes on Unity Transport's port.
        private NetworkManager NetworkManagerToUse()
        {
            if (_network != null)
                return _network;
            _network = _networkManager != null ? _networkManager : NetworkManager.Singleton;
            if (_network != null)
                return _network;
            GameObject holder = new GameObject("NetworkManager (Flock sample)");
            DontDestroyOnLoad(holder);
            _network = holder.AddComponent<NetworkManager>();
            UnityTransport transport = holder.AddComponent<UnityTransport>();
            transport.SetConnectionData("127.0.0.1", _port);
            _network.NetworkConfig = new NetworkConfig { NetworkTransport = transport };
            return _network;
        }

        private void StopNetcode()
        {
            if (Session != null)
                Session.Ended -= OnSessionEnded;
            if (_network == null)
                return;
            _network.OnClientDisconnectCallback -= OnDisconnected;
            if (_network.CustomMessagingManager != null)
                _network.CustomMessagingManager.UnregisterNamedMessageHandler(WaveMessage);
            if (_network.IsListening)
                _network.Shutdown();
        }
    }
}
