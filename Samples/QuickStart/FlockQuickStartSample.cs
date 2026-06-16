using System;
using UnityEngine;
using Flock.Interfaces;
#if !FLOCK_NO_PLAYER
using Flock.Models;
#endif

namespace Flock.Samples
{
    /// Minimal first-success sample. With a FlockBootstrap in the scene this logs the player
    /// in with their device id, shows who they are, fires a test analytics event, and reads
    /// their player data. UI is IMGUI so the sample is one script — drop it on any GameObject
    /// and press Play. Player-data writes go through your generated commands (see the docs).
    public class FlockQuickStartSample : MonoBehaviour
    {
        private string _status = "Ready.";
        private bool _busy;

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(16f, 16f, 380f, 300f), GUI.skin.box);
            GUILayout.Label("Flock - Quick Start");
            GUILayout.Space(6f);

            if (!FlockClient.IsInitialized)
            {
                GUILayout.Label(
                    "Flock is not initialized.\n\n" +
                    "Add a FlockBootstrap to the scene (Qwacks > Editor -> " +
                    "\"Add Flock Bootstrap to Scene\"), fill in your FlockConfig, then press Play.");
                GUILayout.EndArea();
                return;
            }

            IFlockClient client = FlockClient.Instance;
            GUI.enabled = !_busy;

            if (!client.IsAuthenticated)
            {
                GUILayout.Label("Step 1 - log in with this device.");
                if (GUILayout.Button("Log in (device)"))
                    LoginAsync();
            }
            else
            {
                GUILayout.Label("Player:  " + client.CurrentPlayerId);
                GUILayout.Label("Session: " + client.CurrentSessionId);
                GUILayout.Space(6f);
#if !FLOCK_NO_ANALYTICS
                if (GUILayout.Button("Fire test event"))
                    FireTestEventAsync();
#endif
#if !FLOCK_NO_PLAYER
                if (GUILayout.Button("Read my player data"))
                    ReadPlayerDataAsync();
#endif
            }

            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.Label(_status);
            GUILayout.EndArea();
        }

        // Auth methods throw on failure — catch and surface the message.
        private async void LoginAsync()
        {
            _busy = true;
            _status = "Logging in...";
            try
            {
                await FlockClient.Instance.Authentication.LoginWithDeviceAsync(SystemInfo.deviceUniqueIdentifier);
                _status = "Logged in.";
            }
            catch (Exception ex)
            {
                _status = "Login failed: " + ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

#if !FLOCK_NO_ANALYTICS
        private async void FireTestEventAsync()
        {
            _busy = true;
            _status = "Sending test event...";
            try
            {
                await FlockClient.Instance.Analytics.LogEventAsync("Hello from the Flock quick-start sample");
                _status = "Test event sent.";
            }
            catch (Exception ex)
            {
                _status = "Event failed: " + ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }
#endif

#if !FLOCK_NO_PLAYER
        private async void ReadPlayerDataAsync()
        {
            _busy = true;
            _status = "Reading player data...";
            try
            {
                PaginatedResponse<PlayerData> data = await FlockClient.Instance.Player.GetAllDataAsync();
                int count = data != null ? data.Total : 0;
                _status = "Player data rows: " + count;
            }
            catch (Exception ex)
            {
                _status = "Read failed: " + ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }
#endif
    }
}
