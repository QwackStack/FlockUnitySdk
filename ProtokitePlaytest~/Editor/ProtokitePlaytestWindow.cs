using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Protokite.Playtest.Editor
{
    /// <summary>Protokite > Playtest > Setup Checks And Test Video: what in this project stands in a playtest's way, a test video, the consent answer and form while testing, and the live self-test.</summary>
    internal sealed class ProtokitePlaytestWindow : EditorWindow
    {
        /// <summary>The menu path, also used by the Flock settings window's Playtesting tab.</summary>
        public const string MenuPath = "Protokite/Playtest/Setup Checks And Test Video";

        private const string FlockSettingsMenuPath = "Flock/Settings";
        private const int LongestTestVideoSeconds = 3600;
        internal const float StartingWidth = 540f;
        internal const float StartingHeight = 720f;

        [SerializeField] private int testVideoSeconds = 10;
        [SerializeField] private string closedPlaytestGameVersionId = "";

        // The last self-test started here, for the line under its button.
        [NonSerialized] private Task<ProtokitePlaytestSelfTestReport> _selfTest;

        // Not kept through a script reload: a question on its way then is dropped, and asked again on the next draw.
        [NonSerialized] private ProtokitePlaytestGameVersionQuestions _flock;
        [NonSerialized] private string _testVideoRefused;
        // Read at each Layout event and kept for the events after it, so a layout and what it draws agree.
        [NonSerialized] private ProtokitePlaytestSetupInput _input;
        [NonSerialized] private List<ProtokitePlaytestSetupCheck> _checks;
        [NonSerialized] private string _playtestIdSaid;
        [NonSerialized] private bool _playtestIdAsking;
        private Vector2 _scroll;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            bool alreadyOpen = HasOpenInstances<ProtokitePlaytestWindow>();
            ProtokitePlaytestWindow window = GetWindow<ProtokitePlaytestWindow>("Protokite Playtest");
            // A new window opens centred on the editor, big enough to read every check without scrolling.
            if (!alreadyOpen)
                window.position = StartingPosition(EditorGUIUtility.GetMainWindowPosition());
        }

        internal static Rect StartingPosition(Rect editor)
        {
            float width = Mathf.Min(StartingWidth, editor.width);
            float height = Mathf.Min(StartingHeight, editor.height);
            return new Rect(editor.x + (editor.width - width) / 2f, editor.y + (editor.height - height) / 2f, width, height);
        }

        private void OnEnable()
        {
            _flock = new ProtokitePlaytestGameVersionQuestions();
            _flock.Answered += RepaintIfOpen;
            ProtokitePlaytestIdResolver.Answered += RepaintIfOpen;
        }

        private void OnDisable()
        {
            // The Playtest ID's question is the editor's, shared with the settings inspector, so it is left to land.
            ProtokitePlaytestIdResolver.Answered -= RepaintIfOpen;
            if (_flock == null)
                return;
            _flock.Answered -= RepaintIfOpen;
            _flock.Stop();
        }

        // A test video's progress is shown while the game runs.
        private void OnInspectorUpdate()
        {
            if (Application.isPlaying)
                Repaint();
        }

        private void RepaintIfOpen()
        {
            if (this != null)
                Repaint();
        }

        private void OnGUI()
        {
            if (_input == null || _checks == null || Event.current.type == EventType.Layout)
            {
                _input = ProtokitePlaytestSetupInput.FromProject();
                // Asked once a typed value is committed, not for every letter.
                if (!EditorGUIUtility.editingTextField)
                {
                    if (_input.ChoosesAPlaytest)
                        ProtokitePlaytestIdResolver.ResolveForAView(ProtokitePlaytestSettings.Load(), ProtokitePlaytestSetupInput.LoadFlockSettings());
                    else
                        _flock.AskIfChanged(_input);
                }
                _checks = ProtokitePlaytestSetupChecks.Evaluate(_input, _flock.Answer, ProtokitePlaytestIdResolver.Answer);
                _playtestIdAsking = ProtokitePlaytestIdResolver.IsAsking;
                _playtestIdSaid = PlaytestIdQuestionSaid(_input, ProtokitePlaytestIdResolver.Answer, _playtestIdAsking);
            }
            ProtokitePlaytestSetupInput input = _input;
            List<ProtokitePlaytestSetupCheck> checks = _checks;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Setup checks", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("What a build of this project needs for a playtest. Each failed check says what to change.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space();
            foreach (ProtokitePlaytestSetupCheck check in checks)
            {
                DrawCheck(check);
                if (check.Id == ProtokitePlaytestSetupChecks.WhichPlaytestCheck)
                {
                    if (input.ChoosesAPlaytest)
                        DrawPlaytestIdQuestion();
                    else
                        DrawFlockQuestion(input);
                }
                EditorGUILayout.Space();
            }

            EditorGUILayout.Space();
            DrawTestVideo();
            EditorGUILayout.Space();
            DrawWhileTesting();
            EditorGUILayout.Space();
            DrawSelfTest();
            EditorGUILayout.EndScrollView();
        }

        private void DrawSelfTest()
        {
            EditorGUILayout.LabelField("Live self-test", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Checks this build's playtest against Protokite, each check paired with a request Protokite must refuse. It signs nobody in, so sign a " +
                "player in first, and it uploads this Play's recording, so run it in a Play of its own.", EditorStyles.wordWrappedMiniLabel);
            closedPlaytestGameVersionId = EditorGUILayout.TextField(
                new GUIContent("Closed Playtest Version ID", "Optional: the Game Version ID of a closed playtest of this game, to check that it takes no session."),
                closedPlaytestGameVersionId);
            using (new EditorGUI.DisabledScope(!Application.isPlaying || ProtokitePlaytestSelfTest.IsRunning))
            {
                if (GUILayout.Button("Run Live Self-Test"))
                {
                    _selfTest = ProtokitePlaytestSelfTest.RunAsync(string.IsNullOrEmpty(closedPlaytestGameVersionId) ? null : closedPlaytestGameVersionId);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.LabelField(SelfTestNote(Application.isPlaying, ProtokitePlaytestSelfTest.IsRunning, _selfTest), EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>The line under Run Live Self-Test: when it can run, that it is running, or how the last run came out.</summary>
        internal static string SelfTestNote(bool playing, bool running, Task<ProtokitePlaytestSelfTestReport> last)
        {
            if (running)
                return "Running; each step is logged to the Console as it ends.";
            if (last != null && last.Status == TaskStatus.RanToCompletion)
            {
                ProtokitePlaytestSelfTestReport report = last.Result;
                return report.NotRunBecause != null
                    ? "The last self-test did not run: " + report.NotRunBecause
                    : $"Last run {report.RunId}: {report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped. Each step is in the Console.";
            }
            return playing ? "Sign a player in, then run it." : "Enter Play Mode and sign a player in to run it.";
        }

        // The same controls every event, enabled or not, so a layout and what it draws agree.
        private static void DrawWhileTesting()
        {
            EditorGUILayout.LabelField("While testing", EditorStyles.boldLabel);

            EditorGUILayout.LabelField("This machine's answer to the consent question: " + ObjectNames.NicifyVariableName(ProtokitePlaytest.PlayersConsentAnswer.ToString()) +
                ". To the question about upload networks, asked on a phone: " + UploadNetworkAnswerAsRead(ProtokitePlaytest.PlayersUploadNetworkAnswer) + ".",
                EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button(new GUIContent("Forget This Machine's Answer", "Both answers are kept on this machine and count in every later Play; forgetting them makes the next Play ask again.")))
            {
                ProtokitePlaytestSettingsMenu.ForgetThePlayersConsentAnswer();
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button(new GUIContent("Open Feedback Form", "Opens the form over the game the way its key does.")))
                {
                    ProtokitePlaytestSettingsMenu.OpenTheFeedbackForm();
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.LabelField(FeedbackFormNote(Application.isPlaying, ProtokitePlaytest.CanOpenFeedbackForm, ProtokitePlaytest.IsFeedbackFormOpen, ProtokitePlaytest.IsConsentQuestionOpen),
                EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>The line under Open Feedback Form, saying when it can open.</summary>
        internal static string UploadNetworkAnswerAsRead(ProtokitePlaytestUploadNetworkChoice answer)
        {
            switch (answer)
            {
                case ProtokitePlaytestUploadNetworkChoice.WiFiOnly: return "Wi-Fi only";
                case ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData: return "Wi-Fi and mobile data";
                default: return "Not answered";
            }
        }

        internal static string FeedbackFormNote(bool playing, bool canOpen, bool isOpen, bool consentQuestionOpen)
        {
            if (!playing)
                return "Enter Play Mode to open the feedback form.";
            if (isOpen)
                return "The feedback form is open.";
            if (consentQuestionOpen)
                return "The consent question is on screen; the form opens once it is answered.";
            return canOpen ? "Opens the form the playtest published, over the game." : "This build's playtest is not loaded, or publishes no form. " + ProtokitePlaytest.Describe(ProtokitePlaytest.Status);
        }

        private void DrawCheck(ProtokitePlaytestSetupCheck check)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUIContent icon = EditorGUIUtility.IconContent(check.Passed ? "TestPassed" : "TestFailed");
            EditorGUILayout.LabelField(new GUIContent(" " + check.Title, icon.image), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(check.Detail, EditorStyles.wordWrappedLabel);
            switch (check.Fix)
            {
                case ProtokitePlaytestSetupFix.OpenPlaytestSettings:
                    if (GUILayout.Button("Open Playtest Settings"))
                        ProtokitePlaytestSettingsMenu.OpenSettings();
                    break;
                case ProtokitePlaytestSetupFix.OpenFlockSettings:
                    if (GUILayout.Button("Open Flock Settings"))
                        EditorApplication.ExecuteMenuItem(FlockSettingsMenuPath);
                    break;
                case ProtokitePlaytestSetupFix.UseTheSuggestedGameVersion:
                    if (GUILayout.Button($"Set Game Version To {check.SuggestedGameVersion}"))
                        ProtokitePlaytestSetupChecks.UseTheSuggestedGameVersion(ProtokitePlaytestSetupInput.LoadFlockSettings(), check);
                    break;
                case ProtokitePlaytestSetupFix.OpenBuildProfiles:
                    if (GUILayout.Button("Open Build Settings"))
                        BuildPlayerWindow.ShowBuildPlayerWindow();
                    break;
                case ProtokitePlaytestSetupFix.ResolveThePlaytestId:
                    using (new EditorGUI.DisabledScope(_playtestIdAsking))
                    {
                        if (GUILayout.Button("Resolve Playtest ID"))
                        {
                            ProtokitePlaytestIdResolver.ResolveForAView(ProtokitePlaytestSettings.Load(), ProtokitePlaytestSetupInput.LoadFlockSettings(), askAgain: true);
                            // What is drawn below changed after this event was laid out: the next Layout draws it.
                            GUIUtility.ExitGUI();
                        }
                    }
                    break;
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawFlockQuestion(ProtokitePlaytestSetupInput input)
        {
            string said;
            if (!ProtokitePlaytestGameVersionLookup.CanAsk(input))
                said = "Not checked with Flock: set the API URL, API key and Game Version in Flock > Settings.";
            else if (_flock.IsAsking)
                said = "Checking with Flock...";
            else if (_flock.Answer == null || _flock.Answer.AskedFor != ProtokitePlaytestGameVersionLookup.KeyFor(input))
                said = "Not checked with Flock yet.";
            else if (_flock.Answer.Problem != null)
                said = "Could not check with Flock: " + _flock.Answer.Problem;
            else
                said = "Checked with Flock.";

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(said, EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(_flock.IsAsking || !ProtokitePlaytestGameVersionLookup.CanAsk(input)))
            {
                if (GUILayout.Button("Check Again", GUILayout.Width(100)))
                    _flock.AskIfChanged(input, askAgain: true);
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>The line under the Playtest ID's check: whether Flock is being asked, has not been, or what came of it.</summary>
        internal static string PlaytestIdQuestionSaid(ProtokitePlaytestSetupInput input, ProtokitePlaytestIdAnswer answer, bool asking)
        {
            if (asking)
                return "Checking with Flock...";
            if (answer == null || answer.AskedFor != ProtokitePlaytestIdLookup.KeyFor(input.FlockApiUrl, input.FlockApiKey, input.PlaytestId))
                return "Not checked with Flock in this editor session yet.";
            return answer.Problem != null ? "Could not check with Flock: " + answer.Problem : "Checked with Flock.";
        }

        private void DrawPlaytestIdQuestion()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(_playtestIdSaid, EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(_playtestIdAsking))
            {
                if (GUILayout.Button("Check Again", GUILayout.Width(100)))
                {
                    ProtokitePlaytestIdResolver.ResolveForAView(ProtokitePlaytestSettings.Load(), ProtokitePlaytestSetupInput.LoadFlockSettings(), askAgain: true);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTestVideo()
        {
            EditorGUILayout.LabelField("Test video", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Records the Game view with the playtest's video settings, with no playtest needed. It is never uploaded, and is kept on this machine " +
                "until making room for a later recording deletes it. 64-bit Windows editors only.", EditorStyles.wordWrappedMiniLabel);

            ProtokitePlaytestTestVideoState state = ProtokitePlaytest.TestVideoState;
            bool busy = state == ProtokitePlaytestTestVideoState.WaitingToStart || state == ProtokitePlaytestTestVideoState.Recording
                        || state == ProtokitePlaytestTestVideoState.Finishing;
            testVideoSeconds = Mathf.Clamp(EditorGUILayout.IntField("Length (seconds)", testVideoSeconds), 1, LongestTestVideoSeconds);
            using (new EditorGUI.DisabledScope(!Application.isPlaying || busy))
            {
                if (GUILayout.Button("Record Test Video"))
                {
                    _testVideoRefused = ProtokitePlaytest.RecordTestVideo(testVideoSeconds, out string whyNot) ? null : whyNot;
                    // What is drawn below changed after this event was laid out: the next Layout draws it.
                    GUIUtility.ExitGUI();
                }
            }

            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("Enter Play Mode to record a test video.", MessageType.None);
            else if (_testVideoRefused != null && !busy)
                EditorGUILayout.HelpBox("No test video is recorded: " + _testVideoRefused, MessageType.Warning);
            DrawTestVideoState(state);

            string folder = ProtokitePlaytest.TestVideosFolder;
            if (Directory.Exists(folder) && GUILayout.Button("Show Test Videos Folder"))
                EditorUtility.RevealInFinder(folder);
        }

        private static void DrawTestVideoState(ProtokitePlaytestTestVideoState state)
        {
            switch (state)
            {
                case ProtokitePlaytestTestVideoState.WaitingToStart:
                    EditorGUILayout.HelpBox("Starting at the end of this frame, once the recordings earlier launches left are gone through.", MessageType.Info);
                    break;
                case ProtokitePlaytestTestVideoState.Recording:
                    EditorGUILayout.HelpBox("Recording to " + ProtokitePlaytest.TestVideoPartPath, MessageType.Info);
                    break;
                case ProtokitePlaytestTestVideoState.Finishing:
                    EditorGUILayout.HelpBox("Finishing the file.", MessageType.Info);
                    break;
                case ProtokitePlaytestTestVideoState.NotRecorded:
                    EditorGUILayout.HelpBox("No test video is recorded: " + ProtokitePlaytest.TestVideoProblem, MessageType.Warning);
                    break;
                case ProtokitePlaytestTestVideoState.Finished:
                    ProtokitePlaytestVideoRecordingSummary summary = ProtokitePlaytest.FinishedTestVideo;
                    if (summary.FilePath == null)
                    {
                        EditorGUILayout.HelpBox(summary.Error != null ? "The test video could not be written: " + summary.Error : "The test video stopped before any frame was captured.",
                            summary.Error != null ? MessageType.Warning : MessageType.Info);
                        break;
                    }
                    EditorGUILayout.HelpBox($"Saved {summary.VideoSeconds:0.0} seconds, {summary.FramesWritten} frames, to {summary.FilePath}" +
                        (summary.Error != null ? ". It could not be written to the end: " + summary.Error : "."), summary.Error != null ? MessageType.Warning : MessageType.Info);
                    if (GUILayout.Button("Show Test Video"))
                        EditorUtility.RevealInFinder(summary.FilePath);
                    break;
            }
        }
    }
}
