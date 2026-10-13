using System;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Providers;
using UnityEngine;

namespace Flock.Samples
{
    /// Play with friends over Netcode for GameObjects: one player hosts and reads out the session's code, a friend types it in and
    /// joins. Drop it on an empty GameObject with a FlockBootstrap in the scene and press Play; it makes a NetworkManager if the
    /// scene has none.
    public class FlockPlayWithFriendsSample : FlockMultiplayerSample
    {
        private string _code = "";

        protected override string Title => "Play With Friends";

        protected override void DrawFindingPlayers()
        {
            GUILayout.Label("Host, and read your code out to a friend:");
            if (GUILayout.Button("Host"))
                _ = HostAsync();
            GUILayout.Space(8f);
            GUILayout.Label("Or type a friend's code:");
            GUILayout.BeginHorizontal();
            _code = GUILayout.TextField(_code, 12, GUILayout.Width(160f));
            if (GUILayout.Button("Join"))
                _ = JoinAsync(_code);
            GUILayout.EndHorizontal();
        }

        /// <summary>Hosts a session and starts the netcode as its host; friends join with the session's code.</summary>
        public async Task HostAsync()
        {
            Busy = true;
            Status = "Hosting...";
            try
            {
                FlockMultiplayerSession session = await FlockClient.Instance.Multiplayer.HostSessionAsync();
                await PlayAsync(session);
            }
            catch (Exception ex)
            {
                Status = "Hosting failed: " + ex.Message;
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>Joins a friend's session by its code and connects to them over the netcode.</summary>
        public async Task JoinAsync(string code)
        {
            Busy = true;
            Status = "Joining...";
            try
            {
                FlockMultiplayerSession session = await FlockClient.Instance.Multiplayer.JoinSessionAsync(code);
                await PlayAsync(session);
            }
            catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.MultiplayerInvalidJoinCode)
            {
                Status = "No session has the code " + code + ". Check it with your friend.";
            }
            catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.MultiplayerSessionFull)
            {
                Status = "That session is full.";
            }
            catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.MultiplayerVersionMismatch)
            {
                Status = "That session runs another version of the game.";
            }
            catch (Exception ex)
            {
                Status = "Joining failed: " + ex.Message;
            }
            finally
            {
                Busy = false;
            }
        }
    }
}
