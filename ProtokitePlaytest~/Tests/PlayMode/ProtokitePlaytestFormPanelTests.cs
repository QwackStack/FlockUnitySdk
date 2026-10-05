using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using Flock;
using Flock.Http;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    /// <summary>The feedback form as the driver draws it, filled in and sent through its own controls. Needs a windowed editor.</summary>
    public class ProtokitePlaytestFormPanelTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string FormRoute = "/game/sdk/feedback-form";

        private const string FormJson =
            "{\"id\":\"form-1\",\"test_id\":\"t\",\"game_id\":\"g\",\"title\":\"How was this playtest?\",\"description\":\"Tell the studio.\",\"is_published\":true,\"fields\":["
            + "{\"id\":\"title\",\"type\":\"text\",\"label\":\"Short title\",\"required\":true,\"help_text\":null,\"options\":[]},"
            + "{\"id\":\"rating\",\"type\":\"rating\",\"label\":\"How was this session?\",\"required\":true,\"help_text\":null,\"options\":[]},"
            + "{\"id\":\"steps\",\"type\":\"textarea\",\"label\":\"Steps to reproduce\",\"required\":false,\"help_text\":\"What you did first.\",\"options\":[]}],"
            + "\"created_at\":\"2026-09-29T10:00:00\",\"updated_at\":\"2026-09-29T10:00:00\"}";

        private static string Config(bool form) =>
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"test-gvid\",\"features\":{},\"form\":"
            + (form ? FormJson : "null") + "}}";

        private const string Stored = "{\"error\":{\"code\":null},\"result\":{\"id\":\"resp-1\"}}";

        private ProtokitePlaytestSettings _settings;
        private bool _wasEnabled;
        private string _oldUrl;
        private KeyCode _oldKey;
        private bool _oldPause;
        private string _folder;
        private string _formsBefore;
        private ProtokitePlaytestConsentForPlayModeTests _consent;
        private FlockTestClient _flock;
        private float _timeScaleBefore;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (Application.isBatchMode)
                Assert.Ignore("A batchmode editor draws nothing, so there is no form to fill in; run the PlayMode tests in a windowed editor.");
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
            _oldKey = _settings.FeedbackFormKey;
            _oldPause = _settings.PauseTheGameWhileTheFormIsOpen;
            _timeScaleBefore = Time.timeScale;
            _settings.PlaytestingEnabled = true;
            _settings.ProtokiteApiUrl = "http://protokite.test";
            _settings.FeedbackFormKey = KeyCode.F9;
            _settings.PauseTheGameWhileTheFormIsOpen = false;
            _consent = new ProtokitePlaytestConsentForPlayModeTests(_settings);
            ProtokitePlaytest.ResetForNewLaunch();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_form_panel_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Path.Combine(_folder, "Recordings");
            _formsBefore = ProtokitePlaytest.FeedbackFormsFolderForTesting;
            ProtokitePlaytest.FeedbackFormsFolderForTesting = Path.Combine(_folder, "FeedbackForms");
            ProtokitePlaytestDriver.StartWithTheGame();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ProtokitePlaytestDriver driver in Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>())
                Object.Destroy(driver.gameObject);
            _flock?.Dispose();
            _flock = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            ProtokitePlaytest.ResetForNewLaunch();
            Time.timeScale = _timeScaleBefore;
            Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)), "The finishing pass ended before its folder is deleted");
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            ProtokitePlaytest.FeedbackFormsFolderForTesting = _formsBefore;
            _consent?.Dispose();
            _consent = null;
            if (_settings != null)
            {
                _settings.PlaytestingEnabled = _wasEnabled;
                _settings.ProtokiteApiUrl = _oldUrl;
                _settings.FeedbackFormKey = _oldKey;
                _settings.PauseTheGameWhileTheFormIsOpen = _oldPause;
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

        private IEnumerator ThePlaytestLoads(bool withForm = true)
        {
            _flock = FlockTestClient.Create(new FlockFakeTransport()
                .On(FormRoute, FlockFakeTransport.Ok(Stored))
                .On(ConfigRoute, FlockFakeTransport.Ok(Config(withForm))));
            yield return Until(() => ProtokitePlaytest.Status == ProtokitePlaytestStatus.Ready, "The playtest loads");
        }

        private static IEnumerator Until(Func<bool> done, string what, float seconds = 5f)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(done(), what + $" within {seconds} s");
        }

        // Send and Close ignore a press in the form's first moment.
        private static IEnumerator ReadTheForm()
        {
            DateTime until = DateTime.UtcNow.AddSeconds(ProtokitePlaytestPanel.SecondsBeforeAnAnswerCounts + 0.2);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        private static VisualElement Form()
        {
            Assert.IsNotNull(ProtokitePlaytest.FeedbackFormForTesting, "Precondition: the form is on screen");
            return ProtokitePlaytest.FeedbackFormForTesting.Root;
        }

        // The way a keyboard or pad presses a focused button: the button's own click runs.
        private static void Press(string buttonName)
        {
            Button button = Form().Q<Button>(buttonName);
            Assert.IsNotNull(button, "A button named " + buttonName);
            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }
        }

        private static void Key(VisualElement target, KeyCode key, char character = '\0')
        {
            using (KeyDownEvent down = KeyDownEvent.GetPooled(character, key, EventModifiers.None))
            {
                down.target = target;
                target.SendEvent(down);
            }
        }

        private string[] Waiting()
        {
            string forms = ProtokitePlaytest.FeedbackFormsFolderForTesting;
            return Directory.Exists(forms) ? Directory.GetFiles(forms, "*.json") : new string[0];
        }

        [UnityTest]
        public IEnumerator TheFormIsDrawnFromTheConfigAndWhatIsTypedIsWhatIsSent()
        {
            yield return ThePlaytestLoads();
            Assert.IsTrue(ProtokitePlaytest.OpenFeedbackForm());
            Assert.IsTrue(ProtokitePlaytest.IsFeedbackFormOpen);
            yield return null;

            VisualElement form = Form();
            Label heading = form.Q<Label>();
            Assert.AreEqual("How was this playtest?", heading.text);
            Assert.IsNotNull(heading.resolvedStyle.unityFontDefinition.font ?? (Object)heading.resolvedStyle.unityFontDefinition.fontAsset, "The text has a font; no theme gives one");
            TextField title = form.Q<TextField>(ProtokitePlaytestFormView.AnswerName("title"));
            VisualElement box = title.Q(className: TextField.inputUssClassName);
            Assert.Greater(box.layout.width, 100f, "The box a player types into takes room: with no theme it had none, and a click missed it (measured)");
            Assert.Greater(box.layout.height, 10f);

            title.value = "It crashed on level two";
            form.Q<TextField>(ProtokitePlaytestFormView.AnswerName("steps")).value = "Jump twice";
            Press(ProtokitePlaytestFormView.ChoiceName("rating", 2));
            yield return ReadTheForm();
            Press(ProtokitePlaytestFormView.SendButtonName);
            Assert.IsFalse(ProtokitePlaytest.IsFeedbackFormOpen, "Sent, so closed: nothing for the player to wait for");
            yield return Until(() => _flock.Transport.CountTo(FormRoute) == 1 && Waiting().Length == 0, "The form went");
            JObject answers = (JObject)JObject.Parse(_flock.Transport.LastTo(FormRoute).JsonBody)["answers"];
            Assert.AreEqual("It crashed on level two", (string)answers["title"]);
            Assert.AreEqual(3, (int)answers["rating"]);
            Assert.AreEqual("Jump twice", (string)answers["steps"]);
        }

        [UnityTest]
        public IEnumerator ProblemsAreShownOnceSendingIsTriedAndTheFormStaysOpen()
        {
            yield return ThePlaytestLoads();
            ProtokitePlaytest.OpenFeedbackForm();
            yield return ReadTheForm();
            Press(ProtokitePlaytestFormView.SendButtonName);
            yield return null;
            Assert.IsTrue(ProtokitePlaytest.IsFeedbackFormOpen);
            Label problem = Form().Q<Label>(ProtokitePlaytestFormView.ProblemName("title"));
            Assert.AreEqual(DisplayStyle.Flex, problem.resolvedStyle.display);
            Assert.AreEqual(0, Waiting().Length);
        }

        [UnityTest]
        public IEnumerator APressTheMomentTheFormAppearsDoesNothing()
        {
            yield return ThePlaytestLoads();
            ProtokitePlaytest.OpenFeedbackForm();
            Press(ProtokitePlaytestFormView.CloseButtonName);
            Assert.IsTrue(ProtokitePlaytest.IsFeedbackFormOpen, "A player still clicking at the game did not mean it");
            yield return ReadTheForm();
            Press(ProtokitePlaytestFormView.CloseButtonName);
            Assert.IsFalse(ProtokitePlaytest.IsFeedbackFormOpen);
        }

        // Focuses the field, gives it text as typing would, and presses Escape in it; what the field holds afterwards.
        private static IEnumerator TypeThenEscape(TextField field, string text)
        {
            field.Focus();
            yield return null;
            VisualElement focused = (VisualElement)field.panel.focusController.focusedElement;
            Assert.IsNotNull(focused, "Precondition: the field has focus");
            field.value = text;
            yield return null;
            Key(focused, KeyCode.Escape);
            yield return null;
        }

        [UnityTest]
        public IEnumerator EscapeLeavesATextFieldWithoutPuttingBackWhatItHeld()
        {
            yield return ThePlaytestLoads();
            ProtokitePlaytest.OpenFeedbackForm();
            yield return null;

            // The control: a field on the same panel but outside the form, where Escape does what Unity makes it do.
            TextField outside = new TextField { name = "control" };
            // Above the panel's root, so it takes no font from it; on Unity 2021.3 a field with none throws as it draws its cursor (measured).
            outside.style.unityFontDefinition = Form().resolvedStyle.unityFontDefinition;
            Form().panel.visualTree.Add(outside);
            yield return TypeThenEscape(outside, "abc");
            if (outside.value != "")
            {
                outside.RemoveFromHierarchy();
                Assert.Ignore("This editor does not deliver key presses to a text field (its Game view may not have focus), so Escape's revert cannot be shown here; it is measured in players.");
            }

            TextField title = Form().Q<TextField>(ProtokitePlaytestFormView.AnswerName("title"));
            yield return TypeThenEscape(title, "abc");
            Assert.AreEqual("abc", title.value, "Escape only leaves the field");
            Assert.AreEqual("abc", ProtokitePlaytest.FeedbackFormForTesting.Answers.GetText("title"));
            Assert.IsTrue(ProtokitePlaytest.IsFeedbackFormOpen);
            outside.RemoveFromHierarchy();
        }

        [UnityTest]
        public IEnumerator AnswersLeftUnsentComeBackWhenTheFormOpensAgainAndSendingClearsThem()
        {
            yield return ThePlaytestLoads();
            ProtokitePlaytest.OpenFeedbackForm();
            yield return null;
            Form().Q<TextField>(ProtokitePlaytestFormView.AnswerName("title")).value = "half written";
            Assert.IsTrue(ProtokitePlaytest.CloseFeedbackForm());
            Assert.AreEqual(0, Waiting().Length, "Closing sends nothing");

            ProtokitePlaytest.OpenFeedbackForm();
            yield return null;
            Assert.AreEqual("half written", Form().Q<TextField>(ProtokitePlaytestFormView.AnswerName("title")).value);
            Press(ProtokitePlaytestFormView.ChoiceName("rating", 0));
            yield return ReadTheForm();
            Press(ProtokitePlaytestFormView.SendButtonName);
            Assert.IsFalse(ProtokitePlaytest.IsFeedbackFormOpen);

            ProtokitePlaytest.OpenFeedbackForm();
            yield return null;
            Assert.AreEqual("", Form().Q<TextField>(ProtokitePlaytestFormView.AnswerName("title")).value, "What was sent is not offered again");
        }

        // Flock restarts while the form is open: the new client fetches the config again, held here until released.
        private FlockFakeTransport RestartFlockWithTheConfigHeld()
        {
            _flock.Dispose();
            FlockFakeTransport transport = new FlockFakeTransport()
                .On(FormRoute, FlockFakeTransport.Ok(Stored))
                .On(ConfigRoute, FlockFakeTransport.Ok(Config(true)));
            transport.GateNext(ConfigRoute);
            _flock = FlockTestClient.Create(transport);
            return transport;
        }

        [UnityTest]
        public IEnumerator AnswersLeftUnsentComeBackAfterAFlockRestart()
        {
            yield return ThePlaytestLoads();
            ProtokitePlaytest.OpenFeedbackForm();
            yield return null;
            Form().Q<TextField>(ProtokitePlaytestFormView.AnswerName("title")).value = "typed before the restart";
            FlockFakeTransport transport = RestartFlockWithTheConfigHeld();
            yield return null;
            yield return null;
            Assert.IsNull(ProtokitePlaytest.FeedbackForm, "Precondition: the config is being fetched again");
            ProtokitePlaytest.CloseFeedbackForm();

            transport.ReleaseGate();
            yield return Until(() => ProtokitePlaytest.CanOpenFeedbackForm, "The config loaded again");
            ProtokitePlaytest.OpenFeedbackForm();
            yield return null;
            Assert.AreEqual("typed before the restart", Form().Q<TextField>(ProtokitePlaytestFormView.AnswerName("title")).value);
        }

        [UnityTest]
        public IEnumerator AFormSentWhileTheConfigIsFetchedAgainGoesUnderTheVersionItWasLoadedUnder()
        {
            yield return ThePlaytestLoads();
            ProtokitePlaytest.OpenFeedbackForm();
            yield return null;
            Form().Q<TextField>(ProtokitePlaytestFormView.AnswerName("title")).value = "sent mid-restart";
            Press(ProtokitePlaytestFormView.ChoiceName("rating", 1));
            FlockFakeTransport transport = RestartFlockWithTheConfigHeld();
            yield return null;
            yield return null;
            Assert.IsNull(ProtokitePlaytest.FeedbackForm, "Precondition: the config is being fetched again");
            yield return ReadTheForm();
            Press(ProtokitePlaytestFormView.SendButtonName);
            Assert.IsFalse(ProtokitePlaytest.IsFeedbackFormOpen, "Sent");

            yield return Until(() => transport.CountTo(FormRoute) == 1, "The form went");
            transport.LastTo(FormRoute).Headers.TryGetValue("X-Game-Version-ID", out string version);
            Assert.AreEqual("test-gvid", version, "Without it, Protokite would file the form under the game's newest playtest");
            transport.ReleaseGate();
        }

        [UnityTest]
        public IEnumerator OnePanelAtATimeAndTheConsentQuestionWaitsForTheFormToClose()
        {
            yield return ThePlaytestLoads();
            Assert.IsTrue(ProtokitePlaytest.OpenFeedbackForm());
            Assert.IsFalse(ProtokitePlaytest.OpenFeedbackForm(), "One form at a time");
            Assert.IsFalse(ProtokitePlaytest.AskForPlaytestConsent(), "Not over the form");
            yield return null;
            yield return null;
            Assert.IsFalse(ProtokitePlaytest.IsConsentQuestionOpen);

            ProtokitePlaytest.CloseFeedbackForm();
            Assert.IsTrue(ProtokitePlaytest.IsConsentQuestionOpen, "Asked while the form was open, so put once it closed");
            LogAssert.Expect(LogType.Log, new Regex("cannot be opened while the playtest's consent question is on screen"));
            Assert.IsFalse(ProtokitePlaytest.OpenFeedbackForm());
        }

        [UnityTest]
        public IEnumerator TheGameIsPausedOnlyWhenSetAndOnlyItsOwnPauseIsUndone()
        {
            yield return ThePlaytestLoads();
            Time.timeScale = 1f;
            ProtokitePlaytest.OpenFeedbackForm();
            Assert.AreEqual(1f, Time.timeScale, "Off by default");
            ProtokitePlaytest.CloseFeedbackForm();

            _settings.PauseTheGameWhileTheFormIsOpen = true;
            Time.timeScale = 0.5f;
            ProtokitePlaytest.OpenFeedbackForm();
            Assert.AreEqual(0f, Time.timeScale);
            ProtokitePlaytest.CloseFeedbackForm();
            Assert.AreEqual(0.5f, Time.timeScale, "Put back as the game had it");

            ProtokitePlaytest.OpenFeedbackForm();
            Time.timeScale = 2f;
            ProtokitePlaytest.CloseFeedbackForm();
            Assert.AreEqual(2f, Time.timeScale, "The game changed it meanwhile, so it keeps its own");
        }

        [UnityTest]
        public IEnumerator TheFormKeyOpensAndClosesItAndAHeldKeyCountsOnce()
        {
            yield return ThePlaytestLoads();
            ProtokitePlaytestFormKeyWatcher watcher = ProtokitePlaytest.FormKeyWatcherForTesting;
            Assert.IsNotNull(watcher, "Listened for while there is a form to open");
            watcher.Heard(EventType.KeyDown, KeyCode.F8);
            Assert.IsFalse(ProtokitePlaytest.IsFeedbackFormOpen, "Only its own key");
            watcher.Heard(EventType.KeyDown, KeyCode.F9);
            Assert.IsTrue(ProtokitePlaytest.IsFeedbackFormOpen);
            watcher.Heard(EventType.KeyDown, KeyCode.F9);
            Assert.IsTrue(ProtokitePlaytest.IsFeedbackFormOpen, "A held key repeats its press");
            watcher.Heard(EventType.KeyUp, KeyCode.F9);
            watcher.Heard(EventType.KeyDown, KeyCode.F9);
            Assert.IsFalse(ProtokitePlaytest.IsFeedbackFormOpen, "The same key closes it");
        }

        [UnityTest]
        public IEnumerator NoKeyIsListenedForWithoutAFormToOpen()
        {
            yield return ThePlaytestLoads(withForm: false);
            yield return null;
            Assert.IsNull(ProtokitePlaytest.FormKeyWatcherForTesting);
            Assert.IsFalse(ProtokitePlaytest.OpenFeedbackForm());
        }

        [UnityTest]
        public IEnumerator NoKeyIsListenedForWhenTheSettingIsNone()
        {
            _settings.FeedbackFormKey = KeyCode.None;
            yield return ThePlaytestLoads();
            yield return null;
            Assert.IsNull(ProtokitePlaytest.FormKeyWatcherForTesting);
            Assert.IsTrue(ProtokitePlaytest.OpenFeedbackForm(), "The game can still open it");
        }

        [UnityTest]
        public IEnumerator AClickWhileTheGameHoldsTheCursorLockedIsIgnored()
        {
            yield return ThePlaytestLoads();
            GameObject game = new GameObject("Game locking its cursor", typeof(CursorLockedEveryFrame));
            try
            {
                ProtokitePlaytest.OpenFeedbackForm();
                yield return ReadTheForm();
                if (Cursor.lockState != CursorLockMode.Locked)
                    Assert.Ignore("This editor does not let a test lock the cursor (its Game view may not have focus).");
                LogAssert.Expect(LogType.Warning, new Regex("A click on the playtest's feedback form was ignored: the game keeps locking the cursor"));
                Button close = Form().Q<Button>(ProtokitePlaytestFormView.CloseButtonName);
                Vector2 at = close.worldBound.center;
                using (PointerDownEvent down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = at, button = 0, clickCount = 1 }))
                {
                    down.target = close;
                    close.SendEvent(down);
                }
                using (PointerUpEvent up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = at, button = 0, clickCount = 1 }))
                {
                    up.target = close;
                    close.SendEvent(up);
                }
                yield return null;
                Assert.IsTrue(ProtokitePlaytest.IsFeedbackFormOpen);
            }
            finally
            {
                Object.Destroy(game);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
    }
}
