using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
using Flock.Http;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Protokite.Playtest
{
    /// <summary>Where this launch's Protokite session is. A launch starts at most one.</summary>
    internal enum ProtokitePlaytestSessionState
    {
        NotStarted,
        Starting,
        Started,
        Ended,
        NoPlayerIdentity,
        StartFailed
    }

    public static partial class ProtokitePlaytest
    {
        // How long quitting waits for the session end: Protokite takes no end after the fact, so this is its one chance.
        private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(3);

        private static ProtokitePlaytestSessionState _sessionState;
        private static string _playtestSessionId;
        private static string _sessionApiUrl;
        private static Dictionary<string, string> _sessionHeaders;
        private static RetryPolicy _sessionRetryPolicy;
        private static Task<string> _sessionStart;
        private static Task<string> _sessionStartEndedAtQuit;
        private static bool _playtestNoLongerCollecting;
        private static bool _loggedWaitingForFlockSession;
        private static int _launch;
        private static string _steamId;
        private static string _playerName;

        /// <summary>Where the device id is kept, when a test sets one; the game's persistent data folder otherwise.</summary>
        internal static string DeviceIdFilePathForTesting;

        /// <summary>Where this launch's session is.</summary>
        internal static ProtokitePlaytestSessionState SessionState => _sessionState;

        /// <summary>This launch's Protokite session id, or null until Protokite has started one.</summary>
        public static string PlaytestSessionId => _sessionState == ProtokitePlaytestSessionState.Started ? _playtestSessionId : null;

        /// <summary>
        /// Sends this Steam id (and the player's Steam name) instead of the device id; call it before the session starts. An id
        /// that is empty, over 64 characters or holds whitespace is refused, not trimmed. Null goes back to the device id.
        /// </summary>
        public static bool SetSteamId(string steamId, string playerName = null)
        {
            if (_sessionState != ProtokitePlaytestSessionState.NotStarted)
            {
                bool sent = _sessionState == ProtokitePlaytestSessionState.Starting || _sessionState == ProtokitePlaytestSessionState.Started
                            || _sessionState == ProtokitePlaytestSessionState.Ended;
                Debug.LogWarning(LogPrefix + (sent
                    ? "The Steam id was set after this launch's Protokite session started, so it changes nothing this launch."
                    : "The Steam id was set after this launch's one Protokite session start was given up, so it changes nothing this launch."));
                return false;
            }
            if (steamId == null)
            {
                _steamId = null;
                _playerName = null;
                return true;
            }
            if (!ProtokitePlaytestIds.IsUsable(steamId, ProtokitePlaytestIdentityLimits.SteamIdLength))
            {
                _steamId = null;
                _playerName = null;
                Debug.LogWarning(LogPrefix + $"Steam id '{steamId}' was refused: it is empty, longer than {ProtokitePlaytestIdentityLimits.SteamIdLength} characters, or holds whitespace. This install's device id is sent instead.");
                return false;
            }
            _steamId = steamId;
            _playerName = playerName != null && playerName.Length <= ProtokitePlaytestIdentityLimits.PlayerNameLength ? playerName : null;
            return true;
        }

        // A session can start once the playtest is ready and a Flock session has reached the server.
        private static bool SessionCanStart(FlockClient running)
            => _sessionState == ProtokitePlaytestSessionState.NotStarted && _configState == ProtokitePlaytestConfigState.Loaded
               && running != null && running.ServerSessionId != null;

        private static void StartPlaytestSessionWhenAllowed(FlockClient running, string protokiteApiUrl)
        {
            if (_sessionState != ProtokitePlaytestSessionState.NotStarted)
                return;

            string flockSessionId = running.ServerSessionId;
            if (flockSessionId == null)
            {
                if (!_loggedWaitingForFlockSession)
                {
                    _loggedWaitingForFlockSession = true;
                    Debug.Log(LogPrefix + "The Protokite session starts once a Flock session reaches the server. A Flock session starts when a player signs in, with Analytics Enabled and Analytics Auto Start Session on (or a StartSessionAsync call), and consent given when Analytics Require Explicit Consent is on: Flock > Settings.");
                }
                return;
            }

            JObject body = new JObject();
            if (_steamId != null)
            {
                body["steam_id"] = _steamId;
                if (!string.IsNullOrEmpty(_playerName))
                    body["player_name"] = _playerName;
            }
            else
            {
                string deviceId = ReadDeviceId(out string whyNone);
                if (deviceId == null)
                {
                    _sessionState = ProtokitePlaytestSessionState.NoPlayerIdentity;
                    Debug.LogWarning(LogPrefix + "No Protokite session is started this launch, and nothing is sent: " + whyNone);
                    return;
                }
                body["device_id"] = deviceId;
            }

            if (ProtokitePlaytestIds.IsUsable(flockSessionId, ProtokitePlaytestIdentityLimits.FlockSessionIdLength))
                body["flock_session_id"] = flockSessionId;
            else
                Debug.LogWarning(LogPrefix + $"Flock session id '{flockSessionId}' cannot be sent to Protokite (longer than {ProtokitePlaytestIdentityLimits.FlockSessionIdLength} characters, or holding whitespace), so the session starts without naming it.");
            body["extra_debug"] = SessionDebugInfo();

            // Kept for the end, which goes to the same place with the same headers even after the Flock SDK has shut down.
            _sessionApiUrl = protokiteApiUrl;
            _sessionHeaders = running.GetGameHeaders();
            _sessionRetryPolicy = running.RetryPolicy;
            _sessionState = ProtokitePlaytestSessionState.Starting;

            ProtokiteClient client = new ProtokiteClient(_sessionRetryPolicy);
            string url = _sessionApiUrl;
            Dictionary<string, string> headers = _sessionHeaders;
            _sessionStart = SendOffTheMainThread(() => client.StartPlaytestSessionAsync(url, headers, body, CancellationToken.None));
            _ = FinishPlaytestSessionStartAsync(_sessionStart, _launch, body["steam_id"] != null,
                body["flock_session_id"] != null ? flockSessionId : "(none: its id could not be sent)", url, headers, _sessionRetryPolicy);
        }

        private static async Task FinishPlaytestSessionStartAsync(Task<string> start, int launch, bool sentSteamId, string flockSessionId,
            string url, Dictionary<string, string> headers, RetryPolicy retryPolicy)
        {
            string sessionId = null;
            Exception failure = null;
            try
            {
                sessionId = await start;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // The launch ended while the start was on its way: a session that was created is ended at once, not left open,
            // unless quitting already waited for this start and ended it.
            if (launch != _launch)
            {
                if (sessionId != null && !ReferenceEquals(start, _sessionStartEndedAtQuit))
                    EndInBackground(url, headers, retryPolicy, sessionId);
                return;
            }

            _sessionStart = null;
            if (sessionId != null)
            {
                _sessionState = ProtokitePlaytestSessionState.Started;
                _playtestSessionId = sessionId;
                Debug.Log(LogPrefix + $"Protokite session {sessionId} started for this launch, with the player's {(sentSteamId ? "Steam id" : "device id")} and Flock session {flockSessionId}.");
            }
            else
            {
                _sessionState = ProtokitePlaytestSessionState.StartFailed;
                if ((failure as FlockException)?.StatusCode == 400)
                    _playtestNoLongerCollecting = true;
                else
                    Debug.LogWarning(LogPrefix + "No Protokite session was started, and none is tried again this launch, because a start that reached Protokite may already have created one. " + failure?.Message);
            }
            Refresh();
        }

        // On the thread pool, so quitting can wait for it without the main thread. Not on WebGL: its transport runs on the
        // main thread and there is no thread pool, so a send there starts where it is and quitting cannot wait for it.
        private static Task<T> SendOffTheMainThread<T>(Func<Task<T>> send)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return send();
#else
            return Task.Run(send);
#endif
        }

        /// <summary>
        /// Ends this launch's session before the game closes, waiting a bounded time for Protokite to take it. The video's file is
        /// finished on its own threads meanwhile, and waited for in the same time.
        /// </summary>
        internal static void HandleGameQuitting()
        {
            StopVideoForQuitting();
#if UNITY_WEBGL && !UNITY_EDITOR
            // A page closing cannot be held up, and the transport needs the main thread this would block: the end is sent once.
            if (_sessionState == ProtokitePlaytestSessionState.Started)
            {
                _sessionState = ProtokitePlaytestSessionState.Ended;
                EndInBackground(_sessionApiUrl, _sessionHeaders, _sessionRetryPolicy, _playtestSessionId);
            }
#else
            DateTime until = DateTime.UtcNow + QuitWait;
            if (_sessionState == ProtokitePlaytestSessionState.Starting && _sessionStart != null)
            {
                try
                {
                    _sessionStart.Wait(Remaining(until));
                }
                catch (Exception)
                {
                    // A failed start has nothing to end.
                }
                if (_sessionStart.Status == TaskStatus.RanToCompletion)
                {
                    _playtestSessionId = _sessionStart.Result;
                    _sessionState = ProtokitePlaytestSessionState.Started;
                    _sessionStartEndedAtQuit = _sessionStart;
                }
            }

            if (_sessionState == ProtokitePlaytestSessionState.Started)
            {
                _sessionState = ProtokitePlaytestSessionState.Ended;
                using (CancellationTokenSource giveUp = new CancellationTokenSource())
                {
                    ProtokiteClient client = new ProtokiteClient(_sessionRetryPolicy);
                    string url = _sessionApiUrl;
                    Dictionary<string, string> headers = _sessionHeaders;
                    string sessionId = _playtestSessionId;
                    Task end = SendOffTheMainThread(async () =>
                    {
                        await client.EndPlaytestSessionAsync(url, headers, sessionId, giveUp.Token);
                        return true;
                    });
                    bool ended;
                    try
                    {
                        ended = end.Wait(Remaining(until));
                    }
                    catch (AggregateException ex) when (ex.GetBaseException() is OperationCanceledException)
                    {
                        // The bound ran out while a retry was waiting.
                        ended = false;
                    }
                    catch (AggregateException ex)
                    {
                        Debug.LogWarning(LogPrefix + $"Protokite session {sessionId} could not be ended, so Protokite shows it as still in progress. {ex.GetBaseException().Message}");
                        ended = true;
                        sessionId = null;
                    }
                    // Once the wait is over, nothing more is sent: the end's retries stop here.
                    giveUp.Cancel();
                    if (sessionId != null)
                    {
                        if (ended)
                            Debug.Log(LogPrefix + $"Protokite session {sessionId} ended.");
                        else
                            Debug.LogWarning(LogPrefix + $"Protokite session {sessionId} could not be ended within {QuitWait.TotalSeconds:0} s of quitting, so Protokite shows it as still in progress.");
                    }
                }
            }

            WaitForVideoAtQuit(Remaining(until));
#endif
            // From here on, a start still on its way belongs to a launch that has ended.
            _launch++;
        }

        private static TimeSpan Remaining(DateTime until)
        {
            TimeSpan left = until - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        private static void EndInBackground(string url, Dictionary<string, string> headers, RetryPolicy retryPolicy, string sessionId)
            => _ = EndAndReportAsync(url, headers, retryPolicy, sessionId);

        private static async Task EndAndReportAsync(string url, Dictionary<string, string> headers, RetryPolicy retryPolicy, string sessionId)
        {
            ProtokiteClient client = new ProtokiteClient(retryPolicy);
            try
            {
                await SendOffTheMainThread(async () =>
                {
                    await client.EndPlaytestSessionAsync(url, headers, sessionId, CancellationToken.None);
                    return true;
                });
                Debug.Log(LogPrefix + $"Protokite session {sessionId} ended.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(LogPrefix + $"Protokite session {sessionId} could not be ended, so Protokite shows it as still in progress. {ex.Message}");
            }
        }

        private static string ReadDeviceId(out string whyNone)
        {
            whyNone = "";
            ProtokitePlaytestDeviceIdFile file = new ProtokitePlaytestDeviceIdFile(DeviceIdFilePathForTesting ?? ProtokitePlaytestDeviceIdFile.DefaultPath);
            switch (file.ReadOrCreate(out string deviceId))
            {
                case ProtokitePlaytestDeviceIdFileResult.Read:
                case ProtokitePlaytestDeviceIdFileResult.Created:
                    return deviceId;
                case ProtokitePlaytestDeviceIdFileResult.Replaced:
                    Debug.LogWarning(LogPrefix + $"The device id file {file.Path} did not hold a device id, so a new one replaced it. Protokite sees this install as a new player from now on.");
                    return deviceId;
                case ProtokitePlaytestDeviceIdFileResult.Unreadable:
                    whyNone = $"no Steam id was set, and the device id file {file.Path} exists but could not be read, so it was left alone.";
                    return null;
                default:
                    whyNone = $"no Steam id was set, and no device id could be saved to {file.Path}. An id that changed on every launch would show one player as many.";
                    return null;
            }
        }

        // Facts about the build and machine, sent as extra_debug. The GPU and scene are left out when unknown.
        private static JObject SessionDebugInfo()
        {
            JObject facts = new JObject
            {
                ["engine_version"] = Application.unityVersion,
                ["build_configuration"] = Application.isEditor ? "Editor" : Debug.isDebugBuild ? "Development" : "Release",
                ["sdk_version"] = ProtokitePlaytestVersion.Current
            };
            // "Null Device" is what Unity names the GPU of a build running without graphics: no GPU is known then.
            string gpu = SystemInfo.graphicsDeviceName;
            if (!string.IsNullOrEmpty(gpu) && gpu != "Null Device")
                facts["gpu"] = gpu;
            string scene = SceneManager.GetActiveScene().name;
            if (!string.IsNullOrEmpty(scene))
                facts["map"] = scene;
            return facts;
        }

        private static void ResetSessionForNewLaunch()
        {
            _launch++;
            _sessionState = ProtokitePlaytestSessionState.NotStarted;
            _playtestSessionId = null;
            _sessionStart = null;
            _playtestNoLongerCollecting = false;
            _loggedWaitingForFlockSession = false;
            _steamId = null;
            _playerName = null;
        }
    }
}
