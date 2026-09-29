using UnityEngine;

namespace Protokite.Playtest
{
    public static partial class ProtokitePlaytest
    {
        private static ProtokitePlaytestConsentChoice? _savedConsent;
        private static bool _askedToChangeConsent;
        private static ProtokitePlaytestPanel _consentPanel;
        private static bool _loggedNowhereToAskForConsent;
        private static bool _loggedHoldingBackEarlierRecordings;
        private static bool _warnedCursorHeldLocked;

        /// <summary>Where the player's answer is kept, when a test sets it; the game's persistent data folder otherwise.</summary>
        internal static string ConsentFilePathForTesting;

        /// <summary>
        /// The answer in force: the player's saved answer or, with none, NotAnswered in a build that asks and VideoAndPlayData in
        /// one that does not. An answer already given counts whether this build asks or not. Main thread only.
        /// </summary>
        public static ProtokitePlaytestConsentChoice PlaytestConsent => EffectiveConsent();

        /// <summary>Whether the consent question is on screen now.</summary>
        public static bool IsConsentQuestionOpen => _consentPanel != null;

        /// <summary>The question as drawn, for tests; null while it is not on screen.</summary>
        internal static UnityEngine.UIElements.VisualElement ConsentQuestionForTesting => _consentPanel?.View;

        /// <summary>
        /// Saves the player's answer and applies it at once, for a game asking in its own menu or a run nobody can answer on
        /// screen. NotAnswered forgets the answer, so the player is asked again. False, and nothing collected until an answer is
        /// saved, when the answer could not be saved. Main thread only.
        /// </summary>
        public static bool SetPlaytestConsent(ProtokitePlaytestConsentChoice choice)
        {
            ProtokitePlaytestConsentFile file = new ProtokitePlaytestConsentFile(ConsentFilePath);
            bool saved = file.Save(choice);
            _savedConsent = saved ? choice : ProtokitePlaytestConsentChoice.NotAnswered;
            if (saved)
            {
                _askedToChangeConsent = false;
                Debug.Log(LogPrefix + ProtokitePlaytestConsent.Describe(choice) + " The answer is kept in " + file.Path + ".");
            }
            else
            {
                Debug.LogWarning(LogPrefix + $"The player's answer about what this playtest may collect could not be saved to {file.Path}, so any answer saved before is forgotten and they are asked again. Nothing is collected until an answer is saved.");
            }

            if (!ProtokitePlaytestConsent.AllowsVideoRecording(EffectiveConsent()))
                WithdrawThisLaunchsRecording();
            // Decided again in full, even for the same answer: the question closes, and a session, video or measuring starts or stops.
            _stateAtLastRefresh = null;
            Refresh();
            return saved;
        }

        /// <summary>
        /// Puts the consent question on screen again, so the player can change their answer; the answer in force stays until they
        /// choose. True when the question is on screen. Only while this build's playtest is loaded. Main thread only.
        /// </summary>
        public static bool AskForPlaytestConsent()
        {
            if (!PlaytestIsLoaded(Status))
            {
                Debug.Log(LogPrefix + "There is nothing to ask the player about: no playtest is loaded. " + Describe(Status));
                return false;
            }
            _askedToChangeConsent = true;
            UpdateConsentQuestion(Status);
            return IsConsentQuestionOpen;
        }

        private static string ConsentFilePath => ConsentFilePathForTesting ?? ProtokitePlaytestConsentFile.DefaultPath;

        // Read once a launch: every feature check asks for it, and a file read each frame would be wasteful.
        private static ProtokitePlaytestConsentChoice SavedConsent()
        {
            if (!_savedConsent.HasValue)
                _savedConsent = new ProtokitePlaytestConsentFile(ConsentFilePath).Read();
            return _savedConsent.Value;
        }

        // The settings are loaded only while nobody has answered, since the video and heavy analytics ask every frame.
        private static ProtokitePlaytestConsentChoice EffectiveConsent()
        {
            ProtokitePlaytestConsentChoice saved = SavedConsent();
            if (ProtokitePlaytestConsent.IsAnswered(saved))
                return saved;
            return AsksThePlayer(ProtokitePlaytestSettings.Load())
                ? ProtokitePlaytestConsentChoice.NotAnswered
                : ProtokitePlaytestConsentChoice.VideoAndPlayData;
        }

        private static bool AsksThePlayer(ProtokitePlaytestSettings settings) => settings == null || settings.AskThePlayerForPlaytestConsent;

        // The playtest's config is this build's and the playtest still collects: the question has something to be about.
        private static bool PlaytestIsLoaded(ProtokitePlaytestStatus status)
            => status == ProtokitePlaytestStatus.Ready || status == ProtokitePlaytestStatus.WaitingForPlayerConsent
               || status == ProtokitePlaytestStatus.PlayerRefusedPlaytest;

        // Nothing of an earlier launch goes while this launch's question waits for its first answer, or while the answer is nothing.
        private static bool HoldingBackEarlierRecordings()
        {
            ProtokitePlaytestConsentChoice consent = EffectiveConsent();
            if (consent == ProtokitePlaytestConsentChoice.Nothing)
            {
                if (!_loggedHoldingBackEarlierRecordings)
                {
                    _loggedHoldingBackEarlierRecordings = true;
                    Debug.Log(LogPrefix + "Nothing an earlier launch recorded is being sent: the player has asked this playtest to collect nothing. What is waiting stays on disk, and goes only if they change that answer.");
                }
                return true;
            }
            if (ProtokitePlaytestConsent.IsAnswered(consent))
                return false;
            // Nobody has answered in a build that asks. Held while the question is on screen, and while the config that decides
            // whether it is put is still on its way: uploads are ready within frames, the config a moment later. A build with
            // playtesting off, or a playtest that cannot load, never puts it, and stranding their recordings is what uploading prevents.
            ProtokitePlaytestStatus status = Status;
            return status == ProtokitePlaytestStatus.FetchingPlaytestConfig || status == ProtokitePlaytestStatus.WaitingForPlayerConsent;
        }

        /// <summary>Opens the question when the playtest waits for an answer or the game asked to change it, and closes it otherwise.</summary>
        private static void UpdateConsentQuestion(ProtokitePlaytestStatus status)
        {
            bool wanted = status == ProtokitePlaytestStatus.WaitingForPlayerConsent || (_askedToChangeConsent && PlaytestIsLoaded(status));
            if (!wanted)
            {
                _askedToChangeConsent = false;
                CloseConsentQuestion();
                return;
            }
            if (_consentPanel != null)
                return;
            // One panel at a time: the question opens once the feedback form closes, so neither puts back a cursor the other freed.
            if (_formPanel != null)
                return;
            if (!ProtokitePlaytestPanel.CanBeDrawn)
            {
                if (!_loggedNowhereToAskForConsent)
                {
                    _loggedNowhereToAskForConsent = true;
                    Debug.LogWarning(LogPrefix + "This build's playtest is loaded, but the consent question cannot be drawn here (batch mode, no graphics, or not playing). Nothing is collected until it is answered. Answer it with ProtokitePlaytest.SetPlaytestConsent, or turn off Ask The Player For Playtest Consent in Protokite > Playtest > Settings.");
                }
                return;
            }
            _consentPanel = ProtokitePlaytestPanel.Open("Protokite Playtest Consent");
            ProtokitePlaytestPanel panel = _consentPanel;
            panel.Show(ProtokitePlaytestConsentQuestionView.Build(choice => SetPlaytestConsent(choice), () => panel.TheGameKeepsTheCursorLocked,
                WarnThatTheGameHoldsTheCursorLocked));
            Debug.Log(LogPrefix + "The playtest's consent question is on screen; nothing is collected until the player answers it.");
        }

        /// <summary>Once a frame: while the question is open, the cursor stays free so the player can answer.</summary>
        internal static void KeepConsentQuestionAnswerable() => _consentPanel?.KeepCursorFree();

        // A game that locks the cursor again every frame puts every click at the screen's centre: never taken as an answer.
        private static void WarnThatTheGameHoldsTheCursorLocked()
        {
            if (_warnedCursorHeldLocked)
                return;
            _warnedCursorHeldLocked = true;
            Debug.LogWarning(LogPrefix + "A click on the playtest's consent question was not taken as an answer: the game keeps locking the cursor, so each click lands at the screen's centre rather than where the player aimed. The question frees the cursor each frame while it is open; a game that locks it again every frame must stop while ProtokitePlaytest.IsConsentQuestionOpen is true.");
        }

        private static void CloseConsentQuestion()
        {
            _consentPanel?.Close();
            _consentPanel = null;
        }

        /// <summary>
        /// The player took the screen back: this launch's recording is never uploaded or kept. Its saved session goes at once, so no
        /// later launch sends it; a finished file is deleted now, one still being written once it is. One already uploading goes on.
        /// </summary>
        private static void WithdrawThisLaunchsRecording()
        {
            if (_recordingRun == null || _thisLaunchsUploadStarted)
                return;
            if (!_recordingRun.ForgetSession(out string error))
                Debug.LogWarning(LogPrefix + $"The session saved beside this launch's recording could not be removed ({error}); the recording is deleted instead of uploaded all the same.");
            if (_videoRecording != null)
            {
                // Deleted when its file is written, where an upload would have begun.
                if (_videoRecording.IsCapturing)
                    _videoRecording.StopCapturing(ProtokitePlaytestVideoStopReason.PlayerTookTheScreenBack);
                return;
            }
            DeleteWithdrawnRecording();
        }

        private static void DeleteWithdrawnRecording()
        {
            bool deleted = _recordingRun.DeleteEverything();
            // Let go either way: its session is gone, so a later launch deletes what is left rather than upload it.
            _recordingRun = null;
            if (deleted)
                Debug.Log(LogPrefix + "The player asked for the screen not to be recorded, so what this launch recorded was deleted instead of uploaded.");
            else
                Debug.LogWarning(LogPrefix + "The player asked for the screen not to be recorded, and what this launch recorded could not all be deleted now; it is not uploaded, and a later launch deletes the rest.");
        }

        private static void ResetConsentForNewLaunch()
        {
            CloseConsentQuestion();
            _savedConsent = null;
            _askedToChangeConsent = false;
            _loggedNowhereToAskForConsent = false;
            _loggedHoldingBackEarlierRecordings = false;
            _warnedCursorHeldLocked = false;
        }
    }
}
