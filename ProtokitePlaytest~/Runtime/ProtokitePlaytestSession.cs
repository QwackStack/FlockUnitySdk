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

        // Proves which player is playing, so Protokite can link the session to their Community account; sent on the start alone.
        internal const string PlayerTokenHeader = "X-Flock-Player-Token";
        private static readonly TimeSpan PlayerTokenMargin = TimeSpan.FromSeconds(60);

        private static ProtokitePlaytestSessionState _sessionState;
        private static string _playtestSessionId;
        private static string _sessionApiUrl;
        private static Dictionary<string, string> _sessionHeaders;
        private static RetryPolicy _sessionRetryPolicy;
        private static Task<string> _sessionStart;
        private static Task<string> _sessionStartEndedAtQuit;
        private static bool _playtestNoLongerCollecting;
        private static bool _loggedWaitingForFlockSession;
        private static bool _loggedWaitingForUploadNetwork;
        private static Task<bool> _playerTokenRenewal;
        private static FlockClient _playerTokenRenewedFor;
        private static int _playerTokenRenewedSignIn;
        // The first Flock session the start was ready under, which it names, the client it belongs to and the sign-in it started under.
        private static string _flockSessionReadyUnder;
        private static FlockClient _flockSessionReadyClient;
        private static int _flockSessionReadySignIn;
        private static int _launch;
        private static string _steamId;
        private static string _playerName;
        // A start the game asked for, and whether the setting's one start a launch has been used.
        private static bool _sessionAskedFor;
        private static bool _automaticSessionTried;
        // The setting, read once a launch: asked every frame until the starts are used.
        private static bool? _startsAutomatically;
        // The game asked for the session to end: it ends once its recording has been sent, and then tells the game.
        private static bool _sessionEnding;
        private static Action<bool, string> _onSessionEnded;
        // The game's end once sent, until the game is told how it went: quitting waits for it.
        private static Task _gamesEnd;

        /// <summary>Raised on the main thread when a Protokite session opens (true, once Protokite has answered its start with an id) and when it closes (false, as its end is sent). Not raised for a start that failed, or while the game quits.</summary>
        public static event Action<string, bool> PlaytestSessionChanged;

        /// <summary>Where the device id is kept, when a test sets one; the game's persistent data folder otherwise.</summary>
        internal static string DeviceIdFilePathForTesting;

        /// <summary>How long quitting waits, when a test that is not about the bound needs longer than a slow disk takes to close a file.</summary>
        internal static TimeSpan? QuitWaitForTesting;

        /// <summary>Where this launch's session is.</summary>
        internal static ProtokitePlaytestSessionState SessionState => _sessionState;

        /// <summary>The open Protokite session's id; null until Protokite has started one, and once it has ended.</summary>
        public static string PlaytestSessionId => _sessionState == ProtokitePlaytestSessionState.Started ? _playtestSessionId : null;

        /// <summary>Sends this Steam id and name instead of the device id, when called while no session is starting or open (it goes with the next one); an id empty, over 64 characters or holding whitespace is refused, and null goes back to the device id.</summary>
        public static bool SetSteamId(string steamId, string playerName = null)
        {
            if (_sessionState == ProtokitePlaytestSessionState.Starting || _sessionState == ProtokitePlaytestSessionState.Started)
            {
                Debug.LogWarning(LogPrefix + "The Steam id was set after this Protokite session started, so it changes nothing until the next session (EndPlaytestSession, then StartPlaytestSession).");
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

        /// <summary>
        /// Starts a Protokite session, for a game that turned off Start Session And Recording Automatically or ended the last one. It
        /// starts once a Flock session has reached the server. False, saying why in the log, when one is starting or open, the playtest
        /// is not ready, or the player's answer collects nothing. Main thread only.
        /// </summary>
        public static bool StartPlaytestSession()
        {
            string whyNot = WhyASessionCannotStart(out bool playersChoice);
            if (whyNot != null)
            {
                string line = LogPrefix + "No Protokite session was started: " + whyNot;
                if (playersChoice)
                    Debug.Log(line);
                else
                    Debug.LogWarning(line);
                return false;
            }
            _sessionState = ProtokitePlaytestSessionState.NotStarted;
            _sessionAskedFor = true;
            // With the setting on, every session gets a recording of its own (owner, 2026-11-19); one already running goes to it.
            if (StartsAutomatically() && _videoRecording == null)
                _automaticRecordingTried = false;
            // The Flock session running now is the one this session names, not the one an earlier session did.
            _flockSessionReadyClient = null;
            _flockSessionReadyUnder = null;
            _loggedWaitingForFlockSession = false;
            _loggedWaitingForUploadNetwork = false;
            Refresh();
            return true;
        }

        /// <summary>
        /// Ends this launch's Protokite session: a recording still running stops, is uploaded to this session, and the end goes once the
        /// upload has an answer (at once when the player's network answer holds the upload back; it is kept and sent on Wi-Fi). ended, when
        /// given, hears whether Protokite took the end and why not. False, changing nothing, when no session is open, one is still
        /// starting, or one is already ending. <see cref="StartPlaytestSession"/> starts another afterwards. Main thread only.
        /// </summary>
        public static bool EndPlaytestSession(Action<bool, string> ended = null)
        {
            if (_sessionState != ProtokitePlaytestSessionState.Started || _sessionEnding)
            {
                Debug.LogWarning(LogPrefix + "No Protokite session was ended: " + (_sessionEnding
                    ? $"session {_playtestSessionId} is already ending."
                    : _sessionState == ProtokitePlaytestSessionState.Starting
                        ? "the session is still starting; end it once PlaytestSessionId is set."
                        : "none is open."));
                return false;
            }
            _sessionEnding = true;
            _onSessionEnded = ended;
            if (IsRecordingVideo)
                _videoRecording.StopCapturing(ProtokitePlaytestVideoStopReason.SessionEnded);
            Debug.Log(LogPrefix + $"Ending Protokite session {_playtestSessionId}: the end goes once its recording, if any, has been uploaded.");
            return true;
        }

        // Why StartPlaytestSession cannot start one now, or null; playersChoice when the player's answer is why.
        private static string WhyASessionCannotStart(out bool playersChoice)
        {
            playersChoice = false;
            if (_sessionState == ProtokitePlaytestSessionState.Starting)
                return "one is already starting.";
            if (_sessionEnding)
                return $"session {_playtestSessionId} is ending; start the next one once EndPlaytestSession has said how the end went.";
            if (_sessionState == ProtokitePlaytestSessionState.Started)
                return $"session {_playtestSessionId} is open; end it with EndPlaytestSession first.";
            ProtokitePlaytestStatus status = Status;
            if (status != ProtokitePlaytestStatus.Ready)
            {
                playersChoice = status == ProtokitePlaytestStatus.WaitingForPlayerConsent || status == ProtokitePlaytestStatus.PlayerRefusedPlaytest;
                return "the playtest is not ready. " + Describe(status);
            }
            if (_sessionState == ProtokitePlaytestSessionState.NotStarted && SessionWanted())
                return "one is already about to start: it starts once a Flock session has reached the server.";
            return null;
        }

        // Whether the setting starts this launch's session and recording by themselves; read once a launch.
        private static bool StartsAutomatically()
        {
            if (!_startsAutomatically.HasValue)
                _startsAutomatically = ProtokitePlaytestSettings.Load()?.StartSessionAndRecordingAutomatically ?? true;
            return _startsAutomatically.Value;
        }

        private static bool SessionWanted() => _sessionAskedFor || (!_automaticSessionTried && StartsAutomatically());

        // A session can start once one is wanted, the playtest is ready, the player's answer lets it collect something, and a Flock session has reached the server.
        private static bool SessionCanStart(FlockClient running)
            => _sessionState == ProtokitePlaytestSessionState.NotStarted && _configState == ProtokitePlaytestConfigState.Loaded
               && running != null && SessionWanted() && FlockSessionIdFor(running) != null && ProtokitePlaytestConsent.CollectsAnything(EffectiveConsent());

        /// <summary>Once a frame: a session the game asked to end ends once its recording has been sent, or can wait no longer for it.</summary>
        private static void EndTheSessionOnceItsRecordingIsSent()
        {
            if (!_sessionEnding || _sessionState != ProtokitePlaytestSessionState.Started || ThisSessionsRecordingIsOnItsWay())
                return;
            _sessionEnding = false;
            _sessionState = ProtokitePlaytestSessionState.Ended;
            Task end = SendTheEnd(_sessionApiUrl, _sessionHeaders, _sessionRetryPolicy, _playtestSessionId, CancellationToken.None);
            _gamesEnd = end;
            _ = TellTheGameHowItsEndWentAsync(end, _playtestSessionId, _launch);
            TellTheGameTheSessionChanged(_playtestSessionId, false);
            Refresh();
        }

        // A handler that throws is logged, and the others still hear.
        private static void TellTheGameTheSessionChanged(string sessionId, bool started)
        {
            Action<string, bool> handlers = PlaytestSessionChanged;
            if (handlers == null)
                return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string, bool>)handler)(sessionId, started);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        private static async Task TellTheGameHowItsEndWentAsync(Task end, string sessionId, int launch)
        {
            string whyNot = null;
            try
            {
                await end;
                Debug.Log(LogPrefix + $"Protokite session {sessionId} ended.");
            }
            catch (Exception ex)
            {
                whyNot = $"Protokite session {sessionId} could not be ended, so Protokite shows it as still in progress. {ex.Message}";
                Debug.LogWarning(LogPrefix + whyNot);
            }
            // Quitting may have told the game already, and a later launch's game has let go of what it handed over.
            if (launch == _launch && ReferenceEquals(_gamesEnd, end))
                TellTheGameTheSessionEnded(whyNot == null, whyNot);
        }

        // On the thread pool (see SendOffTheMainThread), so quitting can wait for it.
        private static Task SendTheEnd(string url, Dictionary<string, string> headers, RetryPolicy retryPolicy, string sessionId, CancellationToken cancellationToken)
        {
            ProtokiteClient client = new ProtokiteClient(retryPolicy);
            return SendOffTheMainThread(async () =>
            {
                await client.EndPlaytestSessionAsync(url, headers, sessionId, cancellationToken);
                return true;
            });
        }

        private static bool ThisSessionsRecordingIsOnItsWay()
            => _videoRecording != null || (string.Equals(_recordingSessionId, _playtestSessionId, StringComparison.Ordinal) && TheLastRecordingIsUploading())
               || TheRecordingsGivenThisSessionAreUploading();

        // Recordings made before this session started, sent by the earlier recordings' pass on a network the player allows.
        private static bool TheRecordingsGivenThisSessionAreUploading()
            => string.Equals(_recordingsGivenSessionId, _playtestSessionId, StringComparison.Ordinal) && RunningFlock() != null && !UploadsWaitForWiFi()
               && (_lookAgainForThisLaunchsRecordings || (EarlierUploads != null && !EarlierUploads.IsCompleted));

        // Finished, given a session, and uploading or about to (next frame) on a network the player allows.
        private static bool TheLastRecordingIsUploading()
        {
            if (ThisLaunchsUpload != null && !ThisLaunchsUpload.IsCompleted)
                return true;
            if (_recordingRun == null || FinishedVideo?.FilePath == null || _recordingSessionId == null
                || !ProtokitePlaytestConsent.AllowsVideoRecording(EffectiveConsent()) || UploadsWaitForWiFi())
                return false;
            return ThisLaunchsUpload == null || !ThisLaunchsUpload.IsCompleted;
        }

        // The first Flock session on the server the start was ready under: a renewal Flock refuses signs the player out and ends it, and the start still goes, unlinked.
        private static string FlockSessionIdFor(FlockClient running)
            => ReferenceEquals(_flockSessionReadyClient, running) ? _flockSessionReadyUnder : running.ServerSessionId;

        // Protokite links the session the start names to the player whose token it carries, so a sign-out, another player's sign-in or a new client sends none; a renewal keeps the sign-in.
        private static bool IsTheSignInTheFlockSessionStartedUnder(FlockClient running)
            => ReferenceEquals(_flockSessionReadyClient, running) && _flockSessionReadySignIn == running.SignInNumber;

        // Renewed this close to running out, so a slow start or a device clock behind the server's still sends a token Protokite takes.
        private static bool PlayerTokenRunsOutSoon(FlockClient running)
            => running.IsAuthenticated && running.TokenClaims?.ExpirationTime is DateTime expires && expires <= DateTime.UtcNow + PlayerTokenMargin;

        private static void StartPlaytestSessionWhenAllowed(FlockClient running, string protokiteApiUrl)
        {
            if (_sessionState != ProtokitePlaytestSessionState.NotStarted || !SessionWanted())
                return;
            // On a phone, once the player has said which networks recordings upload on, so the start carries it.
            if (UploadNetworkAnswerIsDue())
            {
                if (!_loggedWaitingForUploadNetwork)
                {
                    _loggedWaitingForUploadNetwork = true;
                    Debug.Log(LogPrefix + "The Protokite session starts once the player says which networks recordings may upload on, so the session carries the answer.");
                }
                return;
            }

            string flockSessionId = FlockSessionIdFor(running);
            if (flockSessionId == null)
            {
                if (!_loggedWaitingForFlockSession)
                {
                    _loggedWaitingForFlockSession = true;
                    Debug.Log(LogPrefix + "The Protokite session starts once a Flock session reaches the server. A Flock session starts when a player signs in, with Analytics Enabled and Analytics Auto Start Session on (or a StartSessionAsync call), and consent given when Analytics Require Explicit Consent is on: Flock > Settings.");
                }
                return;
            }

            // The sign-in that Flock session started under, not the one current now: another player may have signed in over it since.
            if (!ReferenceEquals(_flockSessionReadyClient, running))
            {
                _flockSessionReadyClient = running;
                _flockSessionReadyUnder = flockSessionId;
                _flockSessionReadySignIn = running.ServerSessionSignInNumber;
            }

            // A sign-in running out is renewed once per sign-in, here on the main thread, and the start waits for it; Protokite treats an expired token as none.
            // A token the start will not carry is neither renewed nor waited for.
            bool carriesTheToken = IsTheSignInTheFlockSessionStartedUnder(running);
            bool renewedThisSignIn = ReferenceEquals(_playerTokenRenewedFor, running) && _playerTokenRenewedSignIn == running.SignInNumber;
            if (carriesTheToken && PlayerTokenRunsOutSoon(running) && !renewedThisSignIn)
            {
                _playerTokenRenewedFor = running;
                _playerTokenRenewedSignIn = running.SignInNumber;
                _playerTokenRenewal = running.TryRefreshTokenAsync(CancellationToken.None);
            }
            if (carriesTheToken && ReferenceEquals(_playerTokenRenewedFor, running) && _playerTokenRenewal != null && !_playerTokenRenewal.IsCompleted)
                return;

            // From here the start is decided: whatever becomes of it, the setting's one start a launch, or the game's call, is used.
            _sessionAskedFor = false;
            _automaticSessionTried = true;
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
                    Debug.LogWarning(LogPrefix + "No Protokite session is started this launch, so nothing this launch collects is sent: " + whyNone);
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
            // The version the config was loaded for, not the settings read again: a Playtest ID changed in Play Mode since names another playtest.
            _sessionHeaders = HeadersForTheLoadedPlaytest(running);
            _sessionRetryPolicy = running.RetryPolicy;
            _sessionState = ProtokitePlaytestSessionState.Starting;

            ProtokiteClient client = new ProtokiteClient(_sessionRetryPolicy);
            string url = _sessionApiUrl;
            Dictionary<string, string> headers = _sessionHeaders;
            Dictionary<string, string> startHeaders = WithPlayerToken(headers, running);
            _sessionStart = SendOffTheMainThread(() => client.StartPlaytestSessionAsync(url, startHeaders, body, CancellationToken.None));
            _ = FinishPlaytestSessionStartAsync(_sessionStart, _launch, body["steam_id"] != null,
                body["flock_session_id"] != null ? flockSessionId : "(none: its id could not be sent)", url, headers, _sessionRetryPolicy);
        }

        // A copy carrying the player's token, raw, while one is signed in, not expired, and the sign-in the named Flock session started under; the game's headers otherwise.
        private static Dictionary<string, string> WithPlayerToken(Dictionary<string, string> headers, FlockClient running)
        {
            string token = running.PlayerAccessToken;
            if (token == null || running.IsTokenExpired || !IsTheSignInTheFlockSessionStartedUnder(running))
                return headers;
            return new Dictionary<string, string>(headers) { [PlayerTokenHeader] = token };
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
                SaveSessionBesideRecording();
                GiveTheHeldRecordingsThisSession();
                Debug.Log(LogPrefix + $"Protokite session {sessionId} started for this launch, with the player's {(sentSteamId ? "Steam id" : "device id")} and Flock session {flockSessionId}.");
                TellTheGameTheSessionChanged(sessionId, true);
                // A recording that finished before its session started goes now.
                UploadThisLaunchsRecordingWhenReady();
            }
            else
            {
                _sessionState = ProtokitePlaytestSessionState.StartFailed;
                if ((failure as FlockException)?.StatusCode == 400)
                    _playtestNoLongerCollecting = true;
                else
                    Debug.LogWarning(LogPrefix + "No Protokite session was started, and none is tried again unless the game calls StartPlaytestSession, because a start that reached Protokite may already have created one. " + failure?.Message);
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
            StopSelfTestForQuitting();
            // An upload still going stops here and its recording is kept: the next launch sends it.
            StopUploads();
            StopVideoForQuitting();
            // An end the game asked for goes now, without its recording, which a later launch sends; the game hears how it went.
            _sessionEnding = false;
#if UNITY_WEBGL && !UNITY_EDITOR
            // A page closing cannot be held up, and the transport needs the main thread this would block: the end is sent once.
            _onSessionEnded = null;
            _gamesEnd = null;
            if (_sessionState == ProtokitePlaytestSessionState.Started)
            {
                _sessionState = ProtokitePlaytestSessionState.Ended;
                EndInBackground(_sessionApiUrl, _sessionHeaders, _sessionRetryPolicy, _playtestSessionId);
            }
#else
            TimeSpan quitWait = QuitWaitForTesting ?? QuitWait;
            DateTime until = DateTime.UtcNow + quitWait;
            WaitForTheGamesEnd(until, quitWait);
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
                    SaveSessionBesideRecording();
                    GiveTheHeldRecordingsThisSession();
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
                    string whyNot = null;
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
                        whyNot = $"Protokite session {sessionId} could not be ended, so Protokite shows it as still in progress. {ex.GetBaseException().Message}";
                        ended = false;
                    }
                    // Once the wait is over, nothing more is sent: the end's retries stop here.
                    giveUp.Cancel();
                    if (ended)
                        Debug.Log(LogPrefix + $"Protokite session {sessionId} ended.");
                    else
                        Debug.LogWarning(LogPrefix + (whyNot ?? $"Protokite session {sessionId} could not be ended within {quitWait.TotalSeconds:0} s of quitting, so Protokite shows it as still in progress."));
                    TellTheGameTheSessionEnded(ended, ended ? null : whyNot ?? $"the game quit, and Protokite did not answer the end within {quitWait.TotalSeconds:0} s.");
                }
            }

            WaitForVideoAtQuit(Remaining(until));
#endif
            LetGoOfRecordingRunOnceWritten();
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
            try
            {
                await SendTheEnd(url, headers, retryPolicy, sessionId, CancellationToken.None);
                Debug.Log(LogPrefix + $"Protokite session {sessionId} ended.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(LogPrefix + $"Protokite session {sessionId} could not be ended, so Protokite shows it as still in progress. {ex.Message}");
            }
        }

        // An end the game asked for, already sent, gets the quit's bound too, and the game is told how it went.
        private static void WaitForTheGamesEnd(DateTime until, TimeSpan quitWait)
        {
            if (_gamesEnd == null)
                return;
            string whyNot = null;
            try
            {
                if (!_gamesEnd.Wait(Remaining(until)))
                    whyNot = $"the game quit, and Protokite did not answer the end within {quitWait.TotalSeconds:0} s.";
            }
            catch (AggregateException ex)
            {
                whyNot = "Protokite could not end the session: " + ex.GetBaseException().Message;
            }
            TellTheGameTheSessionEnded(whyNot == null, whyNot);
        }

        // Once, for the end the game asked for; a game's code that throws here is logged, never let stop the playtest.
        private static void TellTheGameTheSessionEnded(bool endedOk, string whyNot)
        {
            Action<bool, string> ended = _onSessionEnded;
            _onSessionEnded = null;
            _gamesEnd = null;
            try
            {
                ended?.Invoke(endedOk, whyNot);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
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
                // First, as it says whether the rest of the session was allowed to hold anything. Without the second, a build that
                // asks nobody and a player who allowed everything look the same.
                ["playtest_consent"] = ProtokitePlaytestConsent.ToWire(EffectiveConsent()),
                ["playtest_consent_asked"] = AsksThePlayer(ProtokitePlaytestSettings.Load()) ? "true" : "false",
                // So a dashboard can say why a phone's recording has not arrived: it may be waiting for Wi-Fi.
                ["playtest_upload_network"] = ProtokitePlaytestUploadNetwork.ToWire(SavedUploadNetwork()),
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
            _loggedWaitingForUploadNetwork = false;
            _playerTokenRenewal = null;
            _playerTokenRenewedFor = null;
            _flockSessionReadyUnder = null;
            _flockSessionReadyClient = null;
            _steamId = null;
            _playerName = null;
            _sessionAskedFor = false;
            _automaticSessionTried = false;
            _startsAutomatically = null;
            _sessionEnding = false;
            _onSessionEnded = null;
            _gamesEnd = null;
        }
    }
}
