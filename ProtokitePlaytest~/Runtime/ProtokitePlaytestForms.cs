using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
using Flock.Http;
using UnityEngine;

namespace Protokite.Playtest
{
    public static partial class ProtokitePlaytest
    {
        /// <summary>How long after a failure that may pass the waiting forms are tried again, while the game runs.</summary>
        internal static readonly TimeSpan FormRetryInterval = TimeSpan.FromMinutes(2);

        private static ProtokitePlaytestPanel _formPanel;
        private static ProtokitePlaytestFormView _formView;
        private static string _openFormId;
        private static string _openFormGameVersionId;
        private static ProtokitePlaytestFormAnswers _unsentAnswers;
        private static string _unsentAnswersFormId;
        private static bool _pausedForTheForm;
        private static float _timeScaleBeforeTheForm = 1f;
        private static bool _loggedNowhereToShowTheForm;
        private static bool _warnedFormCursorHeldLocked;
        private static ProtokitePlaytestFormKeyWatcher _formKeyWatcher;

        private static bool _keptFormsDue = true;
        private static Task _sendingKeptForms;
        private static FlockClient _flockKeptFormsWereSentWith;
        private static DateTime? _keptFormsRetryAt;
        private static bool? _wasReachable;

        /// <summary>Where waiting forms are kept, when a test sets it; the game's persistent data folder otherwise.</summary>
        internal static string FeedbackFormsFolderForTesting;

        /// <summary>Whether the network is up, when a test sets it; the platform's own answer otherwise.</summary>
        internal static Func<bool> ReachabilityForTesting;

        /// <summary>The time now, when a test sets it.</summary>
        internal static Func<DateTime> ClockForTesting;

        /// <summary>The latest sending of the kept forms, for tests; null until one starts.</summary>
        internal static Task SendingKeptFormsForTesting => _sendingKeptForms;

        /// <summary>The form as drawn, for tests; null while it is not open.</summary>
        internal static ProtokitePlaytestFormView FeedbackFormForTesting => _formView;

        /// <summary>What listens for the form key, for tests; null while nothing does.</summary>
        internal static ProtokitePlaytestFormKeyWatcher FormKeyWatcherForTesting => _formKeyWatcher;

        internal static string FeedbackFormsFolder => FeedbackFormsFolderForTesting ?? Path.Combine(Application.persistentDataPath, "ProtokitePlaytest", "FeedbackForms");

        /// <summary>
        /// This build's published feedback form, or null when its playtest is not loaded or publishes none. Whatever the player's consent
        /// answer: a report is the player's own message. Main thread only.
        /// </summary>
        public static ProtokitePlaytestForm FeedbackForm => PlaytestIsLoaded(Status) ? _config?.Form : null;

        /// <summary>Whether there is a feedback form to open; a game reads it to leave its own "give feedback" entry out when there is none. Main thread only.</summary>
        public static bool CanOpenFeedbackForm => FeedbackForm != null;

        /// <summary>Whether the feedback form is on screen now.</summary>
        public static bool IsFeedbackFormOpen => _formPanel != null;

        /// <summary>Opens the feedback form over the game, with any answers left unsent this launch; false when there is none to open, one is open, the consent question is on screen, or nothing can be drawn. Main thread only.</summary>
        public static bool OpenFeedbackForm()
        {
            ProtokitePlaytestForm form = FeedbackForm;
            if (form == null || _formPanel != null)
                return false;
            if (_consentPanel != null)
            {
                Debug.Log(LogPrefix + "The feedback form cannot be opened while the playtest's consent question is on screen.");
                return false;
            }
            if (!ProtokitePlaytestPanel.CanBeDrawn)
            {
                if (!_loggedNowhereToShowTheForm)
                {
                    _loggedNowhereToShowTheForm = true;
                    Debug.LogWarning(LogPrefix + "The feedback form cannot be drawn here (batch mode, no graphics, or not playing). A game can send answers of its own with ProtokitePlaytest.SendFeedbackForm.");
                }
                return false;
            }

            ProtokitePlaytestFormAnswers answers = _unsentAnswers != null && _unsentAnswersFormId == form.Id ? _unsentAnswers : new ProtokitePlaytestFormAnswers();
            _unsentAnswers = null;
            // Kept from here, as a config fetched again while the form is open (a Flock restart) is gone for a moment.
            _openFormId = form.Id;
            _openFormGameVersionId = _configGameVersionId;
            string gameVersionId = _openFormGameVersionId;
            _formPanel = ProtokitePlaytestPanel.Open("Protokite Playtest Feedback");
            ProtokitePlaytestPanel panel = _formPanel;
            panel.Card.style.maxHeight = UnityEngine.UIElements.Length.Percent(90);
            _formView = new ProtokitePlaytestFormView(form, answers,
                filledIn => SendFromTheForm(form, gameVersionId, filledIn),
                () => CloseFeedbackForm(),
                () => CanSendTheRecording,
                StopRecordingAndSendIt,
                () => panel.TheGameKeepsTheCursorLocked,
                WarnThatTheGameHoldsTheCursorLockedOverTheForm);
            panel.Show(_formView.Root);

            ProtokitePlaytestSettings settings = ProtokitePlaytestSettings.Load();
            if (settings != null && settings.PauseTheGameWhileTheFormIsOpen && Time.timeScale != 0f)
            {
                _timeScaleBeforeTheForm = Time.timeScale;
                Time.timeScale = 0f;
                _pausedForTheForm = true;
            }
            Debug.Log(LogPrefix + "The playtest's feedback form is open.");
            return true;
        }

        /// <summary>Closes the feedback form without sending it; what was filled in comes back when it is opened again this launch. False when none is open.</summary>
        public static bool CloseFeedbackForm()
        {
            if (_formPanel == null)
                return false;
            _unsentAnswers = _formView.Answers;
            _unsentAnswersFormId = _openFormId;
            TakeTheFormOffTheScreen();
            LetAWaitingConsentQuestionOpen();
            Debug.Log(LogPrefix + "The playtest's feedback form is closed.");
            return true;
        }

        /// <summary>
        /// Sends a game's own filled-in form the way the playtest's form does: kept on this device, then sent, by a later launch if need be.
        /// False, with a warning, when there is no form, the answers have problems (<see cref="ProtokitePlaytestFormAnswers.FindProblems"/>), or nobody can be named. Main thread only.
        /// </summary>
        public static bool SendFeedbackForm(ProtokitePlaytestFormAnswers answers)
        {
            ProtokitePlaytestForm form = FeedbackForm;
            if (form == null)
            {
                Debug.LogWarning(LogPrefix + "The feedback form was not sent: this build's playtest is not loaded or publishes no feedback form. " + Describe(Status));
                return false;
            }
            if (answers == null)
            {
                Debug.LogWarning(LogPrefix + "The feedback form was not sent: no answers were given.");
                return false;
            }
            IReadOnlyList<ProtokitePlaytestFormProblem> problems = answers.FindProblems(form);
            if (problems.Count > 0)
            {
                List<string> named = new List<string>();
                foreach (ProtokitePlaytestFormProblem problem in problems)
                    named.Add($"'{problem.FieldId}': {problem.Message}");
                Debug.LogWarning(LogPrefix + "The feedback form was not sent, as Protokite would refuse it: " + string.Join(" ", named));
                return false;
            }
            bool kept = KeepFilledInForm(form, _configGameVersionId, answers, out string whyNot);
            if (!kept)
                Debug.LogWarning(LogPrefix + "The feedback form was not sent: " + whyNot);
            return kept;
        }

        /// <summary>Whether this launch's recording can be sent now: it is recording, and its Protokite session has started. One with no session cannot be uploaded at all.</summary>
        public static bool CanSendTheRecording
            => IsRecordingVideo && _sessionState == ProtokitePlaytestSessionState.Started && ProtokitePlaytestConsent.AllowsVideoRecording(EffectiveConsent());

        /// <summary>
        /// Stops this launch's recording for good and uploads it once its file is finished, while the player is still in the game: what
        /// the form's "Upload your recording" button does. Opening the form never does this on its own. False, changing nothing, when
        /// <see cref="CanSendTheRecording"/> is false. Main thread only.
        /// </summary>
        public static bool StopRecordingAndSendIt()
        {
            if (!CanSendTheRecording)
                return false;
            Debug.Log(LogPrefix + "The player asked for their recording to be sent.");
            _videoRecording.StopCapturing(ProtokitePlaytestVideoStopReason.PlayerAskedToSendIt);
            return true;
        }

        // The form's Send: answers Protokite would take, kept and on their way, and the form closed; or why not, shown on the form.
        private static string SendFromTheForm(ProtokitePlaytestForm form, string gameVersionId, ProtokitePlaytestFormAnswers answers)
        {
            if (!KeepFilledInForm(form, gameVersionId, answers, out string whyNot))
            {
                Debug.LogWarning(LogPrefix + "The feedback form was not sent: " + whyNot);
                return whyNot;
            }
            TakeTheFormOffTheScreen();
            LetAWaitingConsentQuestionOpen();
            return null;
        }

        /// <summary>Writes the form down to be sent, naming this launch's session when one has started, else the version the form was loaded under. False, with the reason, when it could not be kept.</summary>
        private static bool KeepFilledInForm(ProtokitePlaytestForm form, string formGameVersionId, ProtokitePlaytestFormAnswers answers, out string whyNot)
        {
            bool session = _sessionState == ProtokitePlaytestSessionState.Started;
            string gameVersionId = formGameVersionId;
            if (session)
                _sessionHeaders?.TryGetValue(GameVersionHeader, out gameVersionId);
            ProtokitePlaytestFormSubmission submission = new ProtokitePlaytestFormSubmission
            {
                PlaytestSessionId = session ? _playtestSessionId : "",
                // The address and version the session started with, as its recording uses; the settings' address otherwise.
                ProtokiteApiUrl = (session ? _sessionApiUrl : ProtokitePlaytestSettings.Load()?.ProtokiteApiUrl)?.Trim() ?? "",
                FlockGameVersionId = gameVersionId ?? "",
                Answers = answers.ToWire(form)
            };
            // Who the session was started as: the Steam id when the game set one, this install's device id otherwise.
            if (_steamId != null)
            {
                submission.SteamId = _steamId;
            }
            else
            {
                string deviceId = ReadDeviceId(out string whyNone);
                if (deviceId == null)
                {
                    whyNot = "nobody can be named as the one who filled it in: " + whyNone;
                    return false;
                }
                submission.DeviceId = deviceId;
            }

            string path = new ProtokitePlaytestKeptForms(FeedbackFormsFolder).Keep(submission, out string error);
            if (path == null)
            {
                whyNot = "the answers could not be kept on this device to be sent, as " + error;
                return false;
            }
            Debug.Log(LogPrefix + (session
                ? $"The feedback form is kept at {path} and sent to Protokite session {_playtestSessionId}."
                : $"The feedback form is kept at {path} and sent to Protokite; no Protokite session had started, so it names none."));
            _keptFormsDue = true;
            whyNot = null;
            return true;
        }

        private static void TakeTheFormOffTheScreen()
        {
            _formPanel?.Close();
            _formPanel = null;
            _formView = null;
            _openFormId = null;
            _openFormGameVersionId = null;
            // Only a pause of the form's own is undone: a game that changed the time scale meanwhile keeps its own.
            if (_pausedForTheForm && Time.timeScale == 0f)
                Time.timeScale = _timeScaleBeforeTheForm;
            _pausedForTheForm = false;
        }

        // A consent question asked while the form was open has waited for it. Never while the playtest stops.
        private static void LetAWaitingConsentQuestionOpen() => UpdateConsentQuestion(Status);

        private static void WarnThatTheGameHoldsTheCursorLockedOverTheForm()
        {
            if (_warnedFormCursorHeldLocked)
                return;
            _warnedFormCursorHeldLocked = true;
            Debug.LogWarning(LogPrefix + "A click on the playtest's feedback form was ignored: the game keeps locking the cursor, so each click lands at the screen's centre rather than where the player aimed. The form frees the cursor each frame while it is open; a game that locks it again every frame must stop while ProtokitePlaytest.IsFeedbackFormOpen is true.");
        }

        /// <summary>The form key was pressed: it opens the form, and closes it again.</summary>
        internal static void HandleFeedbackFormKey()
        {
            if (IsFeedbackFormOpen)
                CloseFeedbackForm();
            else
                OpenFeedbackForm();
        }

        // Listened for only while it could open something, so a game with no form, or no key, pays nothing each frame. Decided when the
        // status changes, which every change of what it depends on goes through.
        private static void UpdateFeedbackFormKeyWatcher(ProtokitePlaytestStatus status, ProtokitePlaytestSettings settings)
        {
            bool formToOpen = PlaytestIsLoaded(status) && _config?.Form != null;
            KeyCode key = formToOpen && settings != null ? settings.FeedbackFormKey : KeyCode.None;
            bool wanted = key != KeyCode.None && ProtokitePlaytestPanel.CanBeDrawn;
            if (!wanted)
            {
                StopListeningForTheFormKey();
                return;
            }
            if (_formKeyWatcher == null)
                _formKeyWatcher = ProtokitePlaytestFormKeyWatcher.Begin(key);
            else
                _formKeyWatcher.Key = key;
        }

        private static void StopListeningForTheFormKey()
        {
            if (_formKeyWatcher != null)
                UnityEngine.Object.Destroy(_formKeyWatcher.gameObject);
            _formKeyWatcher = null;
        }

        // Once a frame, before Refresh's early return. Kept forms go when Flock runs, for its API key: at a new Flock client, once a form
        // is kept, and, after a failure that may pass, when the network comes back or the retry is due. One sending at a time: a form
        // kept during one goes with the next.
        private static void SendWaitingFormsWhenDue(FlockClient running)
        {
            if (running == null)
                return;
            if (!ReferenceEquals(running, _flockKeptFormsWereSentWith))
            {
                _flockKeptFormsWereSentWith = running;
                _keptFormsDue = true;
            }
            // Only while a failed send left forms waiting: nothing else needs the network's state.
            if (_keptFormsRetryAt.HasValue)
            {
                bool reachable = NetworkIsReachable();
                bool cameBack = reachable && _wasReachable == false;
                _wasReachable = reachable;
                if (cameBack || Now() >= _keptFormsRetryAt.Value)
                {
                    _keptFormsRetryAt = null;
                    _wasReachable = null;
                    _keptFormsDue = true;
                }
            }
            if (!_keptFormsDue || (_sendingKeptForms != null && !_sendingKeptForms.IsCompleted))
                return;
            _keptFormsDue = false;
            _sendingKeptForms = SendWaitingFormsAsync(FeedbackFormsFolder, running.GetGameHeaders(), running.RetryPolicy, _uploadsCancel.Token);
        }

        private static async Task SendWaitingFormsAsync(string folder, Dictionary<string, string> launchHeaders, RetryPolicy retryPolicy,
            CancellationToken cancellationToken)
        {
            ProtokiteClient client = new ProtokiteClient(retryPolicy);
            // A launch that ends meanwhile cancels the token, and the next send throws before any request: that is the one stop.
            foreach (string path in new ProtokitePlaytestKeptForms(folder).FindWaiting())
            {
                FileStream claim = ProtokitePlaytestKeptForms.Claim(path);
                // Another launch is sending it, or it has gone since the folder was read.
                if (claim == null)
                    continue;
                bool keepGoing;
                try
                {
                    keepGoing = await SendOneWaitingFormAsync(client, path, claim, launchHeaders, cancellationToken);
                }
                finally
                {
                    claim.Dispose();
                }
                if (!keepGoing)
                    return;
            }
        }

        // One form: deleted once taken or refused for good, kept otherwise. False when the rest should wait for the next chance.
        private static async Task<bool> SendOneWaitingFormAsync(ProtokiteClient client, string path, FileStream claim,
            Dictionary<string, string> launchHeaders, CancellationToken cancellationToken)
        {
            ProtokitePlaytestFormSubmission submission;
            string whyNot;
            try
            {
                submission = ProtokitePlaytestFormSubmission.FromSavedJson(ProtokitePlaytestKeptForms.ReadClaimed(claim), out whyNot);
            }
            catch (Exception ex)
            {
                Debug.Log(LogPrefix + $"A feedback form kept at {path} could not be read now, so it stays for a later try: {ex.Message}");
                return true;
            }
            if (submission == null || !IsUsableApiUrl(submission.ProtokiteApiUrl.Trim()))
            {
                ForgetKeptForm(claim, path, "could never be sent: " + (whyNot ?? "its Protokite API URL is not an http or https address."));
                return true;
            }

            try
            {
                // This launch's API key, with the Game Version ID the form was filled in under: Protokite finds the playtest from it.
                string storedAs = await client.SubmitFeedbackFormAsync(submission.ProtokiteApiUrl, HeadersForTheSession(launchHeaders, submission.FlockGameVersionId),
                    submission, cancellationToken);
                if (!ProtokitePlaytestKeptForms.ForgetClaimed(claim, path))
                    Debug.LogWarning(LogPrefix + $"A feedback form was sent to Protokite (stored as {storedAs}) but its file {path} could not be deleted, so it is sent again later; Protokite keeps one answer per session, so one naming a session is replaced, not added.");
                else
                    Debug.Log(LogPrefix + $"A feedback form was sent to Protokite (stored as {storedAs}).");
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The launch ended: the form stays, and a later launch sends it.
                return false;
            }
            catch (Exception ex)
            {
                int? status = (ex as FlockException)?.StatusCode;
                if (status == 422 || status == 404)
                {
                    // Refused in a way that will not change: sending it at every launch would only be refused again.
                    string reason = (ex as FlockException)?.ServerMessage ?? ex.Message;
                    string question = ProtokitePlaytestFormSubmission.FindQuestionInRefusal(reason);
                    ForgetKeptForm(claim, path, $"was refused by Protokite (HTTP {status}{(question.Length > 0 ? $", the question '{question}'" : "")}): {reason}");
                    return true;
                }
                if (status == 401)
                {
                    // A launch with another key may be taken; this one's will not change, so it is not tried again this launch.
                    Debug.LogWarning(LogPrefix + "Protokite refused this build's Flock API key when sending a feedback form (HTTP 401), so the waiting forms are kept for a later launch. Check the API key in Flock > Settings.");
                    return false;
                }
                _keptFormsRetryAt = Now() + FormRetryInterval;
                if (AboutThisFormAlone(ex, status))
                {
                    Debug.Log(LogPrefix + $"Protokite could not take a feedback form now{(status.HasValue ? $" (HTTP {status})" : "")}, so it is kept and tried again later; the forms behind it are tried now: {ex.Message}");
                    return true;
                }
                Debug.Log(LogPrefix + $"A feedback form could not be sent now{(status.HasValue ? $" (HTTP {status})" : "")}, so the waiting forms are kept and tried again when the network comes back or in {FormRetryInterval.TotalMinutes:0} minutes: {ex.Message}");
                return false;
            }
        }

        // Whether Protokite itself answered this form with a failure, so the forms behind it may still go. No answer, a page that is not
        // Protokite's (a captive portal), a gateway, a limit or a refusal on the way (403: the route never sends one) says nothing about
        // the form, and the rest wait: an offline game does not try every form in turn.
        private static bool AboutThisFormAlone(Exception failure, int? status)
        {
            if (status.HasValue)
                return status != 403 && status != 408 && status != 429 && status != 502 && status != 503 && status != 504;
            return failure is FlockSerializationException unreadable && unreadable.Body != null && unreadable.Body.TrimStart().StartsWith("{", StringComparison.Ordinal);
        }

        private static void ForgetKeptForm(FileStream claim, string path, string because)
        {
            if (ProtokitePlaytestKeptForms.ForgetClaimed(claim, path))
                Debug.LogWarning(LogPrefix + $"A feedback form kept at {path} {because} It is deleted.");
            else
                Debug.LogWarning(LogPrefix + $"A feedback form kept at {path} {because} It could not be deleted, so it is tried again later.");
        }

        private static bool NetworkIsReachable()
            => ReachabilityForTesting?.Invoke() ?? Application.internetReachability != NetworkReachability.NotReachable;

        private static DateTime Now() => ClockForTesting?.Invoke() ?? DateTime.UtcNow;

        /// <summary>Once a frame: while the form is open, the cursor stays free so the player can answer.</summary>
        internal static void KeepFeedbackFormAnswerable() => _formPanel?.KeepCursorFree();

        private static void CloseFeedbackFormForTheLaunch()
        {
            if (_formPanel != null)
                TakeTheFormOffTheScreen();
            StopListeningForTheFormKey();
        }

        private static void ResetFormsForNewLaunch()
        {
            CloseFeedbackFormForTheLaunch();
            _unsentAnswers = null;
            _unsentAnswersFormId = null;
            _loggedNowhereToShowTheForm = false;
            _warnedFormCursorHeldLocked = false;
            _keptFormsDue = true;
            _sendingKeptForms = null;
            _flockKeptFormsWereSentWith = null;
            _keptFormsRetryAt = null;
            _wasReachable = null;
        }
    }

    /// <summary>
    /// Hears the form key, also while a text field has focus (measured in players): through the Input Manager where the game has it,
    /// and otherwise through Unity's immediate-mode GUI, which costs a game on the Input Manager 64 bytes a frame (measured). Alive
    /// only while there is a form to open.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class ProtokitePlaytestFormKeyWatcher : MonoBehaviour
    {
        private bool _held;

        internal KeyCode Key;

        internal static ProtokitePlaytestFormKeyWatcher Begin(KeyCode key)
        {
            GameObject host = new GameObject("Protokite Playtest Form Key") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(host);
            ProtokitePlaytestFormKeyWatcher watcher = host.AddComponent<ProtokitePlaytestFormKeyWatcher>();
            watcher.Key = key;
            // No layout pass: nothing is drawn, only key presses are read.
            watcher.useGUILayout = false;
            return watcher;
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        private void Update()
        {
            if (Input.GetKeyDown(Key))
                ProtokitePlaytest.HandleFeedbackFormKey();
        }
#else
        private void OnGUI()
        {
            Event current = Event.current;
            if (current != null)
                Heard(current.type, current.keyCode);
        }
#endif

        /// <summary>One key event. A held key repeats its key-down: one press, one toggle.</summary>
        internal void Heard(EventType type, KeyCode key)
        {
            if (key != Key)
                return;
            if (type == EventType.KeyDown && !_held)
            {
                _held = true;
                ProtokitePlaytest.HandleFeedbackFormKey();
            }
            else if (type == EventType.KeyUp)
            {
                _held = false;
            }
        }

        // A key let go while the game was in the background never reports its release.
        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
                _held = false;
        }
    }
}
