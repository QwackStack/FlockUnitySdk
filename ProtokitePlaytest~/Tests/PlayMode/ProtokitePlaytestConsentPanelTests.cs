using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Flock;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    /// <summary>The consent question as the driver puts it on screen, answered through its own buttons. Needs a windowed editor.</summary>
    public class ProtokitePlaytestConsentPanelTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string Answer =
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"asked\",\"flock_game_version_id\":\"test-gvid\",\"features\":{},\"form\":null}}";

        private ProtokitePlaytestSettings _settings;
        private bool _wasEnabled;
        private string _oldUrl;
        private string _folder;
        private ProtokitePlaytestConsentForPlayModeTests _consent;
        private FlockTestClient _flock;
        private readonly List<string> _heardWhileOnScreen = new List<string>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (Application.isBatchMode)
                Assert.Ignore("A batchmode editor draws nothing, so there is no question to answer; run the PlayMode tests in a windowed editor.");
            _heardWhileOnScreen.Clear();
            Application.logMessageReceived += HeardWhileOnScreen;
            foreach (ProtokitePlaytestDriver driver in Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>())
                Object.Destroy(driver.gameObject);
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            yield return null;

            _settings = ProtokitePlaytestSettings.Load();
            if (_settings == null)
                Assert.Ignore("This project has no playtest settings; create them with Protokite > Playtest > Settings.");
            _wasEnabled = _settings.PlaytestingEnabled;
            _oldUrl = _settings.ProtokiteApiUrl;
            _settings.PlaytestingEnabled = true;
            _settings.ProtokiteApiUrl = "http://protokite.test";
            _consent = new ProtokitePlaytestConsentForPlayModeTests(_settings, askThePlayer: true);
            ProtokitePlaytest.ResetForNewLaunch();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_asked_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Path.Combine(_folder, "Recordings");

            ProtokitePlaytestDriver.StartWithTheGame();
            _flock = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(Answer)));
            yield return Until(() => ProtokitePlaytest.IsConsentQuestionOpen, "The question is put once the playtest loads");
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= HeardWhileOnScreen;
            foreach (ProtokitePlaytestDriver driver in Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>())
                Object.Destroy(driver.gameObject);
            _flock?.Dispose();
            _flock = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)), "The finishing pass ended before its folder is deleted");
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            _consent?.Dispose();
            _consent = null;
            if (_settings != null)
            {
                _settings.PlaytestingEnabled = _wasEnabled;
                _settings.ProtokiteApiUrl = _oldUrl;
            }
            try
            {
                if (_folder != null && Directory.Exists(_folder))
                    Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        // Unity's warning that a panel "will not render properly", and anything thrown or logged as an error.
        private void HeardWhileOnScreen(string message, string stack, LogType type)
        {
            if ((type == LogType.Warning && message.Contains("Theme Style Sheet")) || type == LogType.Exception || type == LogType.Error)
                _heardWhileOnScreen.Add(type + ": " + message);
        }

        private static IEnumerator Until(Func<bool> done, string what, float seconds = 5f)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(done(), what + $" within {seconds} s");
        }

        private static Button ButtonFor(ProtokitePlaytestConsentChoice choice)
        {
            VisualElement question = ProtokitePlaytest.ConsentQuestionForTesting;
            Assert.IsNotNull(question, "Precondition: the question is on screen");
            Button button = question.Q<Button>(ProtokitePlaytestConsentQuestionView.ButtonName(choice));
            Assert.IsNotNull(button, "A button for " + choice);
            return button;
        }

        // A player reads before answering: a press in the question's first moment is ignored.
        private static IEnumerator ReadTheQuestion()
        {
            DateTime until = DateTime.UtcNow.AddSeconds(ProtokitePlaytestPanel.SecondsBeforeAnAnswerCounts + 0.2);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        // The way a keyboard or pad presses a focused button: the button's own click runs.
        private static void Press(Button button)
        {
            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }
        }

        [UnityTest]
        public IEnumerator TheQuestionIsDrawnWithEveryAnswerInItsOwnWords()
        {
            yield return null;
            VisualElement question = ProtokitePlaytest.ConsentQuestionForTesting;
            Assert.AreEqual(ProtokitePlaytestStatus.WaitingForPlayerConsent, ProtokitePlaytest.Status);
            StringAssert.Contains(ProtokitePlaytestConsentQuestionView.Heading, question.Q<Label>().text);

            foreach (ProtokitePlaytestConsentQuestionView.Option option in ProtokitePlaytestConsentQuestionView.Options)
            {
                Button button = ButtonFor(option.Choice);
                Label title = button.Q<Label>();
                Assert.AreEqual(option.Title, title.text);
                Assert.IsNotNull(title.resolvedStyle.unityFontDefinition.font ?? (Object)title.resolvedStyle.unityFontDefinition.fontAsset,
                    "The text has a font to be drawn with; no theme gives one");
                Assert.Greater(title.layout.height, 1f, "The title takes room on screen");
                Assert.Greater(button.layout.width, 100f, "The button is laid out, not collapsed");
            }
            Assert.IsTrue(question.panel != null && question.panel.contextType == ContextType.Player, "Drawn in the game, not an editor window");
            PanelSettings packageSettings = Resources.Load<PanelSettings>(ProtokitePlaytestPanel.PanelSettingsResource);
            Assert.IsNotNull(packageSettings, "The package's panel settings asset is found under Resources");
            Assert.IsNotNull(packageSettings.themeStyleSheet, "The asset's theme resolves, so two missing themes cannot pass as the same one");
            foreach (UIDocument document in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (document.gameObject.name == "Protokite Playtest Consent")
                {
                    Assert.AreNotSame(packageSettings, document.panelSettings, "Each panel draws with a copy, so closing it never destroys the package's asset");
                    Assert.AreSame(packageSettings.themeStyleSheet, document.panelSettings.themeStyleSheet,
                        "The panel's theme is the package's imported one: an empty theme made in code throws on Unity 2021.3");
                }
            }
        }

        [UnityTest]
        public IEnumerator TheQuestionIsDrawnWithNoThemeWarningAndNothingThrown()
        {
            for (int frame = 0; frame < 10; frame++)
                yield return null;
            Assert.IsTrue(ProtokitePlaytest.IsConsentQuestionOpen, "Precondition: the question has been on screen for every one of those frames");
            Assert.IsEmpty(_heardWhileOnScreen, "Opening and drawing the question logged: " + string.Join(" | ", _heardWhileOnScreen));
        }

        [UnityTest]
        public IEnumerator NoAnswerIsSelectedSoTheGamesSubmitKeyPressesNothing()
        {
            yield return null;
            VisualElement question = ProtokitePlaytest.ConsentQuestionForTesting;
            Assert.AreSame(question, question.focusController?.focusedElement,
                "The question itself holds focus: a selected answer would be pressed by the game's own Submit key, Space by default");

            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = question;
                question.SendEvent(submit);
            }
            yield return ReadTheQuestion();
            Assert.IsTrue(ProtokitePlaytest.IsConsentQuestionOpen);
            Assert.AreEqual(ProtokitePlaytestConsentChoice.NotAnswered, ProtokitePlaytest.PlaytestConsent, "Submit on the question itself answers nothing");
        }

        [UnityTest]
        public IEnumerator EachButtonGivesItsOwnAnswerSavesItAndClosesTheQuestion()
        {
            foreach (ProtokitePlaytestConsentQuestionView.Option option in ProtokitePlaytestConsentQuestionView.Options)
            {
                if (!ProtokitePlaytest.IsConsentQuestionOpen)
                    Assert.IsTrue(ProtokitePlaytest.AskForPlaytestConsent(), "Put again so the player can change their answer");
                yield return ReadTheQuestion();
                Press(ButtonFor(option.Choice));
                yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "The question closes once answered");

                Assert.AreEqual(option.Choice, ProtokitePlaytest.PlaytestConsent, $"'{option.Title}' gives its own answer");
                StringAssert.Contains("\"playtest_consent\":\"" + ProtokitePlaytestConsent.ToWire(option.Choice) + "\"", File.ReadAllText(_consent.ConsentFilePath));
            }
            Assert.AreEqual(ProtokitePlaytestStatus.PlayerRefusedPlaytest, ProtokitePlaytest.Status, "The last answer offered is nothing");
        }

        // A mouse press and release on the button, as the pointer would make them.
        private static void Click(Button button)
        {
            Vector2 at = button.worldBound.center;
            using (PointerDownEvent down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = at, button = 0, clickCount = 1 }))
            {
                down.target = button;
                button.SendEvent(down);
            }
            using (PointerUpEvent up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = at, button = 0, clickCount = 1 }))
            {
                up.target = button;
                button.SendEvent(up);
            }
        }

        [UnityTest]
        public IEnumerator AClickWhileTheGameHoldsTheCursorLockedIsNotAnAnswer()
        {
            GameObject game = new GameObject("Game locking its cursor", typeof(CursorLockedEveryFrame));
            try
            {
                yield return ReadTheQuestion();
                if (Cursor.lockState != CursorLockMode.Locked)
                    Assert.Ignore("This editor does not let a test lock the cursor (its Game view may not have focus).");

                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("was not taken as an answer: the game keeps locking the cursor"));
                Click(ButtonFor(ProtokitePlaytestConsentChoice.Nothing));
                yield return null;
                yield return null;
                Assert.IsTrue(ProtokitePlaytest.IsConsentQuestionOpen, "With the cursor held at the centre, a click lands where the player never aimed");
                Assert.AreEqual(ProtokitePlaytestConsentChoice.NotAnswered, ProtokitePlaytest.PlaytestConsent);
            }
            finally
            {
                Object.Destroy(game);
            }

            yield return null;
            yield return null;
            Click(ButtonFor(ProtokitePlaytestConsentChoice.Nothing));
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "Answered once the game stops locking it");
            Assert.AreEqual(ProtokitePlaytestConsentChoice.Nothing, ProtokitePlaytest.PlaytestConsent);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        [UnityTest]
        public IEnumerator APanelWhoseLaunchEndedLeavesTheCursorAsTheNewLaunchHasIt()
        {
            // The question from set-up frees the cursor while it is open, so it is answered first.
            yield return ReadTheQuestion();
            Press(ButtonFor(ProtokitePlaytestConsentChoice.PlayDataOnly));
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "Precondition: answered");

            // A panel opened while the game had confined the cursor, whose objects then went with its launch (a Play Mode session ending).
            Cursor.lockState = CursorLockMode.Confined;
            yield return null;
            if (Cursor.lockState != CursorLockMode.Confined)
                Assert.Ignore("This editor does not let a test confine the cursor (its Game view may not have focus).");
            ProtokitePlaytestPanel ended = ProtokitePlaytestPanel.Open("Protokite Playtest Ended Launch");
            foreach (UIDocument document in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (document.gameObject.name == "Protokite Playtest Ended Launch")
                    Object.DestroyImmediate(document.gameObject);
            }

            Cursor.lockState = CursorLockMode.None;
            ended.Close();
            Assert.AreEqual(CursorLockMode.None, Cursor.lockState, "The ended launch's cursor is not this launch's");
        }

        [UnityTest]
        public IEnumerator APressTheMomentTheQuestionAppearsIsNotAnAnswer()
        {
            yield return ReadTheQuestion();
            Press(ButtonFor(ProtokitePlaytestConsentChoice.PlayDataOnly));
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "Precondition: answered");

            // Put again, and pressed at once, as a player still clicking at the game would.
            Assert.IsTrue(ProtokitePlaytest.AskForPlaytestConsent());
            Press(ButtonFor(ProtokitePlaytestConsentChoice.VideoAndPlayData));
            yield return null;
            yield return null;
            Assert.IsTrue(ProtokitePlaytest.IsConsentQuestionOpen, "A press before the player could have read the question is not an answer");
            Assert.AreEqual(ProtokitePlaytestConsentChoice.PlayDataOnly, ProtokitePlaytest.PlaytestConsent);

            yield return ReadTheQuestion();
            Press(ButtonFor(ProtokitePlaytestConsentChoice.VideoAndPlayData));
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "Answered once read");
            Assert.AreEqual(ProtokitePlaytestConsentChoice.VideoAndPlayData, ProtokitePlaytest.PlaytestConsent);
        }

        [UnityTest]
        public IEnumerator TheCursorIsFreeWhileTheQuestionIsOpenAndTheGamesOwnAfter()
        {
            // The question already holds the cursor as the game had it when it opened; close and reopen it with the game's own lock.
            yield return ReadTheQuestion();
            Press(ButtonFor(ProtokitePlaytestConsentChoice.VideoAndPlayData));
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "Precondition: answered");
            Cursor.lockState = CursorLockMode.Confined;
            Cursor.visible = false;
            yield return null;
            if (Cursor.lockState != CursorLockMode.Confined)
                Assert.Ignore("This editor does not let a test confine the cursor (its Game view may not have focus).");

            Assert.IsTrue(ProtokitePlaytest.AskForPlaytestConsent());
            yield return null;
            yield return null;
            Assert.AreEqual(CursorLockMode.None, Cursor.lockState, "Free, or a game that locks it leaves the player no way to answer");
            Assert.IsTrue(Cursor.visible);

            yield return ReadTheQuestion();
            Press(ButtonFor(ProtokitePlaytestConsentChoice.VideoOnly));
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "Answered");
            Assert.AreEqual(CursorLockMode.Confined, Cursor.lockState, "Put back as the game had it");
            Assert.IsFalse(Cursor.visible);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
