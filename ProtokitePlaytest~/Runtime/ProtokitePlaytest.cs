using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>Whether the playtest can run, and if not, what stops it.</summary>
    public enum ProtokitePlaytestStatus
    {
        /// <summary>Playtesting is switched off, or the project has no playtest settings.</summary>
        TurnedOff,
        /// <summary>Playtesting is on but no Protokite API URL is set.</summary>
        ProtokiteApiUrlMissing,
        /// <summary>The Protokite API URL is not an http or https address.</summary>
        ProtokiteApiUrlUnusable,
        /// <summary>Everything is set, and the playtest is waiting for the Flock SDK to start.</summary>
        WaitingForFlock,
        /// <summary>The playtest is fetching this build's playtest config from Protokite.</summary>
        FetchingPlaytestConfig,
        /// <summary>No Protokite playtest is linked to this build's Game Version ID.</summary>
        PlaytestNotLinked,
        /// <summary>Protokite refused the Flock API key.</summary>
        ProtokiteRefusedApiKey,
        /// <summary>Protokite could not be reached or did not answer usefully; it is asked again when the next Flock session starts.</summary>
        PlaytestConfigUnavailable,
        /// <summary>Protokite answered with the playtest of another Game Version ID than this build sent.</summary>
        PlaytestConfigForAnotherVersion,
        /// <summary>The playtest config is loaded and the playtest can run.</summary>
        Ready,
        /// <summary>The playtest has closed and takes no more sessions, so playtesting is off until the game is launched again.</summary>
        PlaytestNoLongerCollecting,
        /// <summary>The playtest is loaded, and nothing is collected until the player says what it may collect.</summary>
        WaitingForPlayerConsent,
        /// <summary>The player asked the playtest to collect nothing: it behaves as with playtesting off, except that a feedback form the player sends still goes.</summary>
        PlayerRefusedPlaytest,
        /// <summary>A Playtest ID is set, but the editor has not resolved it to a playtest's version, so no playtest is asked for.</summary>
        PlaytestIdNotResolved
    }

    /// <summary>Where fetching the playtest config has got to, for the Flock client it was fetched under.</summary>
    internal enum ProtokitePlaytestConfigState
    {
        NotFetched,
        Fetching,
        Loaded,
        PlaytestNotLinked,
        ApiKeyRefused,
        Unavailable,
        ForAnotherVersion
    }

    /// <summary>The playtest's entry point.</summary>
    public static partial class ProtokitePlaytest
    {
        private const string LogPrefix = "[Protokite Playtest] ";
        private const string GameVersionHeader = "X-Game-Version-ID";

        // The Flock client everything below belongs to. A config is only ever this client's: another one may carry
        // another key or version.
        private static FlockClient _flock;
        private static ProtokitePlaytestConfigState _configState;
        private static ProtokitePlaytestConfig _config;
        private static string _configProblem = "";
        private static string _configGameVersionId;
        // Whether the loaded playtest is the game's newest, asked for with no version as no Playtest ID chose one.
        private static bool _configIsTheNewest;
        private static int _timesConfigForgotten;
        private static CancellationTokenSource _configFetchCancel;
        private static ProtokitePlaytestStatus? _statusLastReported;
        private static (ProtokitePlaytestConfigState, ProtokitePlaytestSessionState, bool, ProtokitePlaytestConsentChoice)? _stateAtLastRefresh;

        /// <summary>Whether the playtest can run right now, and if not, what stops it. Read it on the main thread.</summary>
        public static ProtokitePlaytestStatus Status
            => StatusFor(ProtokitePlaytestSettings.Load(), RunningFlock() != null, EffectiveConsent(), ConfigStateForRunningFlock(), _playtestNoLongerCollecting);

        /// <summary>This build's playtest config, or null until <see cref="Status"/> is <see cref="ProtokitePlaytestStatus.Ready"/>. Main thread only.</summary>
        public static ProtokitePlaytestConfig Config => Status == ProtokitePlaytestStatus.Ready ? _config : null;

        /// <summary>True only when the playtest is ready, the player's answer allows the feature (one this build does not know needs the answer allowing everything) and the config turns it on. Main thread only.</summary>
        public static bool IsFeatureEnabled(string featureName)
        {
            ProtokitePlaytestConfig config = Config;
            return config != null && ProtokitePlaytestConsent.AllowsFeature(EffectiveConsent(), featureName) && config.IsFeatureEnabled(featureName);
        }

        /// <summary>The status for these settings (null means the project has none), whether Flock is running, the answer in force, and the config state.</summary>
        internal static ProtokitePlaytestStatus StatusFor(ProtokitePlaytestSettings settings, bool flockIsRunning, ProtokitePlaytestConsentChoice consent,
            ProtokitePlaytestConfigState configState = ProtokitePlaytestConfigState.NotFetched, bool playtestNoLongerCollecting = false)
        {
            if (settings == null || !settings.PlaytestingEnabled)
                return ProtokitePlaytestStatus.TurnedOff;

            string url = settings.ProtokiteApiUrl?.Trim();
            if (string.IsNullOrEmpty(url))
                return ProtokitePlaytestStatus.ProtokiteApiUrlMissing;
            if (!IsUsableApiUrl(url))
                return ProtokitePlaytestStatus.ProtokiteApiUrlUnusable;
            // Never the Flock SDK's version instead, and never none: Protokite answers a request naming no version with the game's newest playtest.
            if (settings.ChoosesAPlaytest && settings.PlaytestVersionId == null)
                return ProtokitePlaytestStatus.PlaytestIdNotResolved;
            if (playtestNoLongerCollecting)
                return ProtokitePlaytestStatus.PlaytestNoLongerCollecting;
            if (!flockIsRunning)
                return ProtokitePlaytestStatus.WaitingForFlock;

            switch (configState)
            {
                // Decided last, so a build that is set up wrong, or that no playtest is linked to, never puts a question to a player.
                case ProtokitePlaytestConfigState.Loaded:
                    if (!ProtokitePlaytestConsent.IsAnswered(consent))
                        return ProtokitePlaytestStatus.WaitingForPlayerConsent;
                    return ProtokitePlaytestConsent.CollectsAnything(consent) ? ProtokitePlaytestStatus.Ready : ProtokitePlaytestStatus.PlayerRefusedPlaytest;
                case ProtokitePlaytestConfigState.PlaytestNotLinked: return ProtokitePlaytestStatus.PlaytestNotLinked;
                case ProtokitePlaytestConfigState.ApiKeyRefused: return ProtokitePlaytestStatus.ProtokiteRefusedApiKey;
                case ProtokitePlaytestConfigState.Unavailable: return ProtokitePlaytestStatus.PlaytestConfigUnavailable;
                case ProtokitePlaytestConfigState.ForAnotherVersion: return ProtokitePlaytestStatus.PlaytestConfigForAnotherVersion;
                default: return ProtokitePlaytestStatus.FetchingPlaytestConfig;
            }
        }

        /// <summary>
        /// What a finished fetch means. Decided from the HTTP status alone, never the exception's type: the Flock SDK counts a
        /// 403 as an authentication failure, but Protokite never answers this route with one, so it came from a proxy or
        /// firewall on the way and says nothing about the key.
        /// </summary>
        internal static ProtokitePlaytestConfigState ConfigStateFor(ProtokitePlaytestConfig config, Exception failure, string sentGameVersionId)
        {
            if (failure == null)
            {
                // Compared letter for letter, as the server matches the id it is sent. Nothing to compare when none was sent (the
                // game's newest playtest was asked for) or the playtest has none.
                string answered = config.FlockGameVersionId;
                return !string.IsNullOrEmpty(answered) && !string.IsNullOrEmpty(sentGameVersionId)
                       && !string.Equals(answered, sentGameVersionId, StringComparison.Ordinal)
                    ? ProtokitePlaytestConfigState.ForAnotherVersion
                    : ProtokitePlaytestConfigState.Loaded;
            }

            switch ((failure as FlockException)?.StatusCode)
            {
                case 404: return ProtokitePlaytestConfigState.PlaytestNotLinked;
                case 401:
                case 422: return ProtokitePlaytestConfigState.ApiKeyRefused;
                default: return ProtokitePlaytestConfigState.Unavailable;
            }
        }

        /// <summary>
        /// Follows the Flock SDK, once a frame, and fetches the config when one is needed. Flock clears every event
        /// subscription when it shuts down, so a new Flock client is noticed here rather than by event.
        /// </summary>
        internal static void Refresh()
        {
            FlockClient running = RunningFlock();
            bool flockChanged = !ReferenceEquals(running, _flock);
            // Before the early return below: these wait on the finishing pass and the device's network, which change no state Refresh follows.
            UploadEarlierRecordingsWhenReady(running);
            UploadThisLaunchsRecordingWhenReady();
            StopUploadsTheNetworkNoLongerAllows();
            SendWaitingFormsWhenDue(running);

            // Nothing to do when neither the Flock client nor the playtest's state has changed since the last frame, unless a
            // session is waiting only for a Flock session to reach the server. The settings are fixed in a build, and every
            // change of state here calls Refresh itself.
            if (!flockChanged && CurrentState() == _stateAtLastRefresh && !SessionCanStart(running))
                return;

            if (flockChanged)
            {
                ForgetPlaytestConfig();
                _flock = running;
                if (running != null)
                {
                    FlockEvents.OnSessionStarted -= HandleFlockSessionStarted;
                    FlockEvents.OnSessionStarted += HandleFlockSessionStarted;
                }
            }

            ProtokitePlaytestSettings settings = ProtokitePlaytestSettings.Load();
            ProtokitePlaytestConsentChoice consent = EffectiveConsent();
            // A fetch already on its way never gets here: its state has not changed, so the early return above skipped it.
            if (StatusFor(settings, running != null, consent, _configState, _playtestNoLongerCollecting) == ProtokitePlaytestStatus.FetchingPlaytestConfig)
            {
                StartPlaytestConfigFetch(running, settings);
            }

            // One session per launch: whatever became of it, a later sign-in or Flock session never starts another.
            if (StatusFor(settings, running != null, consent, _configState, _playtestNoLongerCollecting) == ProtokitePlaytestStatus.Ready)
                StartPlaytestSessionWhenAllowed(running, settings.ProtokiteApiUrl);

            _stateAtLastRefresh = CurrentState();
            ProtokitePlaytestStatus status = StatusFor(settings, running != null, consent, _configState, _playtestNoLongerCollecting);
            ReportStatus(status);
            UpdateConsentQuestion(status);
            UpdateFeedbackFormKeyWatcher(status, settings);
        }

        private static (ProtokitePlaytestConfigState, ProtokitePlaytestSessionState, bool, ProtokitePlaytestConsentChoice) CurrentState()
            => (_configState, _sessionState, _playtestNoLongerCollecting, EffectiveConsent());

        /// <summary>Stops everything the playtest has under way, for when the game is closing.</summary>
        internal static void Stop()
        {
            ForgetPlaytestConfig();
            _flock = null;
            CloseConsentQuestion();
            CloseFeedbackFormForTheLaunch();
            StopUploads();
            StopVideoForQuitting();
            StopMeasuringPerformance();
        }

        /// <summary>Puts the playtest back as a fresh launch finds it; statics outlive a Play Mode session when domain reload is off.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForNewLaunch()
        {
            Stop();
            ResetVideoForNewLaunch();
            ResetSessionForNewLaunch();
            ResetUploadsForNewLaunch();
            ResetHeavyAnalyticsForNewLaunch();
            ResetConsentForNewLaunch();
            ResetUploadNetworkForNewLaunch();
            ResetFormsForNewLaunch();
            _selfTest = null;
            _statusLastReported = null;
            _stateAtLastRefresh = null;
        }

        // Only a failure to reach Protokite is worth asking again: a refusal gives the same answer on a second try.
        private static void HandleFlockSessionStarted(string sessionId)
        {
            if (_configState != ProtokitePlaytestConfigState.Unavailable)
                return;
            ForgetPlaytestConfig();
            Refresh();
        }

        private static void StartPlaytestConfigFetch(FlockClient flock, ProtokitePlaytestSettings settings)
        {
            // The key the Flock SDK initialized with, and the version that names this build's playtest.
            string sentGameVersionId = VersionThatNamesThePlaytest(flock, settings);
            Dictionary<string, string> headers = HeadersNamingTheVersion(flock.GetGameHeaders(), sentGameVersionId);

            _configState = ProtokitePlaytestConfigState.Fetching;
            _configFetchCancel = new CancellationTokenSource();
            ProtokiteClient client = new ProtokiteClient(flock.RetryPolicy);
            _ = FetchPlaytestConfigAsync(client, settings.ProtokiteApiUrl, headers, sentGameVersionId, _timesConfigForgotten, _configFetchCancel.Token);
        }

        /// <summary>The version Protokite alone is asked by: the Playtest ID's; with none, a pt- Game Version's own ID; else none, which Protokite answers with the game's newest playtest.</summary>
        internal static string VersionThatNamesThePlaytest(FlockClient flock, ProtokitePlaytestSettings settings)
        {
            if (settings != null && settings.ChoosesAPlaytest)
                return settings.PlaytestVersionId;
            if (!IsAPlaytestVersionName(flock.GameVersion))
                return null;
            flock.GetGameHeaders().TryGetValue(GameVersionHeader, out string flockGameVersionId);
            return flockGameVersionId;
        }

        /// <summary>How Protokite names a playtest's Flock version: this, then the test's id.</summary>
        internal const string PlaytestVersionPrefix = "pt-";

        /// <summary>Whether a Game Version name is one Protokite gives a playtest: "pt-", letter for letter, then the test's id.</summary>
        internal static bool IsAPlaytestVersionName(string name)
            => name != null && name.Length > PlaytestVersionPrefix.Length && name.StartsWith(PlaytestVersionPrefix, StringComparison.Ordinal);

        /// <summary>The headers every later request about this launch's playtest goes with: the Flock SDK's key, and the version the playtest was loaded for.</summary>
        private static Dictionary<string, string> HeadersForTheLoadedPlaytest(FlockClient flock) => HeadersNamingTheVersion(flock.GetGameHeaders(), _configGameVersionId);

        private static async Task FetchPlaytestConfigAsync(ProtokiteClient client, string protokiteApiUrl, Dictionary<string, string> headers,
            string sentGameVersionId, int timesForgottenWhenSent, CancellationToken cancellationToken)
        {
            ProtokitePlaytestConfig config = null;
            Exception failure = null;
            try
            {
                config = await client.FetchPlaytestConfigAsync(protokiteApiUrl, headers, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // Stale: the Flock client this was fetched for shut down, or the config was forgotten, while it was on its way.
            if (timesForgottenWhenSent != _timesConfigForgotten)
                return;

            _configState = ConfigStateFor(config, failure, sentGameVersionId);
            _config = _configState == ProtokitePlaytestConfigState.Loaded ? config : null;
            // The version every later request about this launch's playtest is sent with (the session, its recording, a form): the newest
            // playtest's own when none was sent, so a playtest created later never takes the rest of this launch.
            _configIsTheNewest = _configState == ProtokitePlaytestConfigState.Loaded && string.IsNullOrEmpty(sentGameVersionId);
            _configGameVersionId = _configState != ProtokitePlaytestConfigState.Loaded ? null
                : _configIsTheNewest ? config.FlockGameVersionId : sentGameVersionId;
            _configProblem = failure != null
                ? failure.Message
                : _configState == ProtokitePlaytestConfigState.ForAnotherVersion
                    ? $"It named Game Version ID {config.FlockGameVersionId}; this build sent {(string.IsNullOrEmpty(sentGameVersionId) ? "none" : sentGameVersionId)}."
                    : "";
            _configFetchCancel = null;
            Refresh();
        }

        private static void ForgetPlaytestConfig()
        {
            _timesConfigForgotten++;
            _configState = ProtokitePlaytestConfigState.NotFetched;
            _config = null;
            _configProblem = "";
            _configGameVersionId = null;
            _configIsTheNewest = false;

            // Its retries would otherwise keep sending an ended Flock client's key. Cancelled after the count has moved,
            // so whatever the cancelled request still answers is already stale. Not disposed: the request may still read its token.
            _configFetchCancel?.Cancel();
            _configFetchCancel = null;
        }

        // Logged once per change. Loud when the studio turned playtesting on and something stops it, quiet otherwise.
        private static void ReportStatus(ProtokitePlaytestStatus status)
        {
            if (_statusLastReported == status)
                return;
            _statusLastReported = status;

            switch (status)
            {
                case ProtokitePlaytestStatus.Ready:
                    Debug.Log(LogPrefix + $"Playtesting is ready: playtest {_config.TestId} is loaded, with {DescribeFeatures(_config)}." +
                              (_configIsTheNewest ? " It is the game's newest playtest, as Playtest ID is empty in Protokite > Playtest > Settings; paste a playtest's ID there to choose it." : ""));
                    break;
                case ProtokitePlaytestStatus.ProtokiteApiUrlMissing:
                case ProtokitePlaytestStatus.ProtokiteApiUrlUnusable:
                case ProtokitePlaytestStatus.PlaytestNotLinked:
                case ProtokitePlaytestStatus.ProtokiteRefusedApiKey:
                case ProtokitePlaytestStatus.PlaytestConfigUnavailable:
                case ProtokitePlaytestStatus.PlaytestConfigForAnotherVersion:
                case ProtokitePlaytestStatus.PlaytestNoLongerCollecting:
                    Debug.LogWarning(LogPrefix + Describe(status) + (string.IsNullOrEmpty(_configProblem) ? "" : " " + _configProblem));
                    break;
                case ProtokitePlaytestStatus.PlaytestIdNotResolved:
                    Debug.LogWarning(LogPrefix + Describe(status) + $" Playtest ID is '{ProtokitePlaytestSettings.Load()?.PlaytestId}'.");
                    break;
                // The player's own choice or a question on its way: nothing for the studio to fix.
                case ProtokitePlaytestStatus.WaitingForPlayerConsent:
                case ProtokitePlaytestStatus.PlayerRefusedPlaytest:
                    Debug.Log(LogPrefix + Describe(status));
                    break;
            }
        }

        /// <summary>One or two sentences saying what a status means and what to change, in the words the playtest logs.</summary>
        public static string Describe(ProtokitePlaytestStatus status)
        {
            switch (status)
            {
                case ProtokitePlaytestStatus.TurnedOff:
                    return "Playtesting is turned off. Turn on Playtesting Enabled in Protokite > Playtest > Settings to collect playtest data.";
                case ProtokitePlaytestStatus.ProtokiteApiUrlMissing:
                    return "Playtesting Enabled is on, but Protokite API URL is empty. Set it in Protokite > Playtest > Settings.";
                case ProtokitePlaytestStatus.ProtokiteApiUrlUnusable:
                    return "Protokite API URL cannot be used: it must be an http:// or https:// address with a host, and contain no spaces. Fix it in Protokite > Playtest > Settings.";
                case ProtokitePlaytestStatus.WaitingForFlock:
                    return "Playtesting is set up and waiting for the Flock SDK to initialize.";
                case ProtokitePlaytestStatus.FetchingPlaytestConfig:
                    return "Playtesting is set up and fetching this build's playtest from Protokite.";
                case ProtokitePlaytestStatus.PlaytestNotLinked:
                    return "Protokite has no playtest for this build, so playtesting stays off: none is linked to the version it asks for or, with Playtest ID empty, the game has no playtest at all. Paste the ID from the playtest's page in Protokite into Playtest ID in Protokite > Playtest > Settings.";
                case ProtokitePlaytestStatus.ProtokiteRefusedApiKey:
                    return "Protokite refused the Flock API key, so playtesting stays off. Check the API key in Flock > Settings.";
                case ProtokitePlaytestStatus.PlaytestConfigUnavailable:
                    return "Could not fetch this build's playtest from Protokite, so playtesting is off for now. The game carries on, and the playtest is fetched again when the next Flock session starts.";
                case ProtokitePlaytestStatus.PlaytestConfigForAnotherVersion:
                    return "Protokite answered with the playtest of a different Game Version ID than this build sent, so playtesting stays off. A proxy that drops the X-Game-Version-ID header causes this.";
                case ProtokitePlaytestStatus.Ready:
                    return "Playtesting is ready: this build's playtest is loaded.";
                case ProtokitePlaytestStatus.PlaytestNoLongerCollecting:
                    return "This playtest has closed and takes no more sessions (Protokite answered HTTP 400), so playtesting is off until the game is launched again. Reopen the playtest in Protokite, or set Playtest ID to a playtest that is still running.";
                case ProtokitePlaytestStatus.WaitingForPlayerConsent:
                    return "This build's playtest is loaded, and nothing is collected until the player says what it may collect. The question is put on screen; a game can put it again with ProtokitePlaytest.AskForPlaytestConsent, or answer it with ProtokitePlaytest.SetPlaytestConsent. Turn off Ask The Player For Playtest Consent in Protokite > Playtest > Settings to collect without asking.";
                case ProtokitePlaytestStatus.PlayerRefusedPlaytest:
                    return "The player asked this playtest to collect nothing, so nothing is recorded, no play data is sent and no session is started, as with playtesting off; a feedback form the player sends themselves still goes. They can be asked again with ProtokitePlaytest.AskForPlaytestConsent.";
                case ProtokitePlaytestStatus.PlaytestIdNotResolved:
                    return "Playtest ID in Protokite > Playtest > Settings has not been resolved to a playtest, so no playtest is asked for and playtesting stays off. Open the settings in the editor while Flock can be reached: the Playtest ID is resolved there, and the settings say what Flock found for it.";
            }
            return "";
        }

        private static string DescribeFeatures(ProtokitePlaytestConfig config)
        {
            List<string> on = new List<string>();
            foreach (KeyValuePair<string, bool> feature in config.Features)
            {
                if (feature.Value)
                    on.Add(feature.Key);
            }
            on.Sort(StringComparer.Ordinal);
            string form = config.Form != null ? "a feedback form" : "no feedback form";
            return (on.Count == 0 ? "no features on" : "features " + string.Join(", ", on)) + " and " + form;
        }

        private static FlockClient RunningFlock() => FlockClient.IsInitialized ? FlockClient.Instance : null;

        // A config fetched under a Flock client that has since shut down belongs to nobody, even before the next Refresh.
        private static ProtokitePlaytestConfigState ConfigStateForRunningFlock()
            => ReferenceEquals(RunningFlock(), _flock) ? _configState : ProtokitePlaytestConfigState.NotFetched;

        // An absolute http or https address with a host and no spaces inside; spaces around it are allowed, as the Flock SDK
        // allows them in its own URL. The editor's setup check uses this rule too, so the two never disagree.
        internal static bool IsUsableApiUrl(string url)
        {
            foreach (char c in url)
            {
                if (char.IsWhiteSpace(c))
                    return false;
            }
            return Uri.TryCreate(url, UriKind.Absolute, out Uri parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrEmpty(parsed.Host);
        }
    }
}
