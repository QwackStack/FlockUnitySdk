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
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    /// <summary>On a phone, the question about upload networks as the driver puts it after the consent question, on the same panel, answered through its own buttons. Needs a windowed editor.</summary>
    public class ProtokitePlaytestUploadNetworkPanelTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string Answer =
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"asked\",\"flock_game_version_id\":\"test-gvid\",\"features\":{\"video_recording\":true},\"form\":null}}";
        private const string NetworkQuestionName = "protokite-upload-network-question";
        private const string ConsentQuestionName = "protokite-consent-question";

        private ProtokitePlaytestSettings _settings;
        private bool _wasEnabled;
        private string _oldUrl;
        private bool _recordedOnAndroidBefore;
        private int _androidBitrateBefore;
        private int _windowsBitrateBefore;
        private string _folder;
        private ProtokitePlaytestConsentForPlayModeTests _answers;
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
            _recordedOnAndroidBefore = _settings.RecordVideoOnAndroid;
            _androidBitrateBefore = _settings.AndroidVideoBitrateKbps;
            _windowsBitrateBefore = _settings.VideoBitrateKbps;
            _settings.PlaytestingEnabled = true;
            _settings.ProtokiteApiUrl = "http://protokite.test";
            _settings.RecordVideoOnAndroid = true;
            // Unlike each other, so a figure read off the Windows section shows.
            _settings.AndroidVideoBitrateKbps = 4000;
            _settings.VideoBitrateKbps = 1500;
            _answers = new ProtokitePlaytestConsentForPlayModeTests(_settings, askThePlayer: true);
            // A phone that offers a hardware encoder, so it is asked; no codec is made, since this editor has none.
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = true;
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () => new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(
                new List<ProtokitePlaytestEncoderFound> { new ProtokitePlaytestEncoderFound { Name = "c2.stand.in.avc.encoder", InHardware = true, OnAPhone = true } }, null);
            ProtokitePlaytest.VideoEncoderForTesting = () => null;
            ProtokitePlaytest.ResetForNewLaunch();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_networks_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Path.Combine(_folder, "Recordings");

            ProtokitePlaytestDriver.StartWithTheGame();
            _flock = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(Answer)));
            yield return Until(() => QuestionOnScreen() == ConsentQuestionName, "The consent question is put once the playtest loads");
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
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = false;
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = null;
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            ProtokitePlaytest.VideoEncoderForTesting = null;
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            _answers?.Dispose();
            _answers = null;
            if (_settings != null)
            {
                _settings.PlaytestingEnabled = _wasEnabled;
                _settings.ProtokiteApiUrl = _oldUrl;
                _settings.RecordVideoOnAndroid = _recordedOnAndroidBefore;
                _settings.AndroidVideoBitrateKbps = _androidBitrateBefore;
                _settings.VideoBitrateKbps = _windowsBitrateBefore;
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

        // Anything thrown or logged as an error while the panel changes question.
        private void HeardWhileOnScreen(string message, string stack, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error)
                _heardWhileOnScreen.Add(type + ": " + message);
        }

        private static string QuestionOnScreen() => ProtokitePlaytest.ConsentQuestionForTesting?.name;

        private static IEnumerator Until(Func<bool> done, string what, float seconds = 5f)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(done(), what + $" within {seconds} s");
        }

        private static IEnumerator ReadTheQuestion()
        {
            DateTime until = DateTime.UtcNow.AddSeconds(ProtokitePlaytestPanel.SecondsBeforeAnAnswerCounts + 0.2);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        private static Button ButtonNamed(string name)
        {
            VisualElement question = ProtokitePlaytest.ConsentQuestionForTesting;
            Assert.IsNotNull(question, "Precondition: a question is on screen");
            Button button = question.Q<Button>(name);
            Assert.IsNotNull(button, "A button named " + name);
            return button;
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

        private static IEnumerator AnswerConsent(ProtokitePlaytestConsentChoice choice)
        {
            yield return ReadTheQuestion();
            Press(ButtonNamed(ProtokitePlaytestConsentQuestionView.ButtonName(choice)));
        }

        private static List<UIDocument> PanelsOnScreen()
        {
            List<UIDocument> panels = new List<UIDocument>();
            foreach (UIDocument document in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (document != null && document.gameObject.name == "Protokite Playtest Consent")
                    panels.Add(document);
            }
            return panels;
        }

        [UnityTest]
        public IEnumerator AnAnswerThatLetsTheScreenBeRecordedIsFollowedOnTheSamePanelByTheQuestionAboutNetworks()
        {
            List<UIDocument> before = PanelsOnScreen();
            Assert.AreEqual(1, before.Count, "Precondition: the consent question's one panel");
            yield return AnswerConsent(ProtokitePlaytestConsentChoice.VideoOnly);
            yield return Until(() => QuestionOnScreen() == NetworkQuestionName, "The question about networks follows");
            Assert.IsTrue(ProtokitePlaytest.IsConsentQuestionOpen, "The panel stays open between the two questions");
            List<UIDocument> after = PanelsOnScreen();
            Assert.AreEqual(1, after.Count);
            Assert.AreSame(before[0], after[0], "The same panel, its question changed: a new one would take the freed cursor for the game's own");
            Assert.AreEqual(ProtokitePlaytestConsentChoice.VideoOnly, ProtokitePlaytest.PlayersConsentAnswer, "The consent answer is saved at once");

            VisualElement question = ProtokitePlaytest.ConsentQuestionForTesting;
            StringAssert.Contains(ProtokitePlaytestUploadNetworkQuestionView.Heading, question.Q<Label>().text);
            Assert.AreSame(question, question.focusController?.focusedElement, "Nothing is selected, so the game's Submit key answers nothing");
            Button wifiOnly = ButtonNamed(ProtokitePlaytestUploadNetworkQuestionView.ButtonName(ProtokitePlaytestUploadNetworkChoice.WiFiOnly));
            Button mobileData = ButtonNamed(ProtokitePlaytestUploadNetworkQuestionView.ButtonName(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData));
            Assert.AreEqual(ProtokitePlaytestUploadNetworkQuestionView.WiFiOnlyTitle, wifiOnly.Q<Label>().text);
            Assert.AreEqual(ProtokitePlaytestUploadNetworkQuestionView.WiFiAndMobileDataTitle, mobileData.Q<Label>().text);
            StringAssert.Contains("about 30 MB of your mobile data", mobileData.Query<Label>().Last().text, "The cost said at the phone's own bitrate, 4000 kbps");
            Assert.Greater(wifiOnly.layout.width, 100f, "The button is laid out, not collapsed");

            yield return ReadTheQuestion();
            Press(wifiOnly);
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "The panel closes once both are answered");
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, ProtokitePlaytest.PlayersUploadNetworkAnswer);
            StringAssert.Contains("\"playtest_upload_network\":\"wifi_only\"", File.ReadAllText(_answers.UploadNetworkFilePath));
            Assert.IsEmpty(_heardWhileOnScreen, "Changing question logged: " + string.Join(" | ", _heardWhileOnScreen));
        }

        [UnityTest]
        public IEnumerator APressTheMomentTheQuestionAboutNetworksAppearsIsNotAnAnswer()
        {
            yield return AnswerConsent(ProtokitePlaytestConsentChoice.VideoAndPlayData);
            yield return Until(() => QuestionOnScreen() == NetworkQuestionName, "The question about networks follows");

            // A second press on the consent question's button lands here at once.
            Press(ButtonNamed(ProtokitePlaytestUploadNetworkQuestionView.ButtonName(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData)));
            yield return null;
            Assert.AreEqual(NetworkQuestionName, QuestionOnScreen(), "Still asking");
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.NotAnswered, ProtokitePlaytest.PlayersUploadNetworkAnswer);

            yield return ReadTheQuestion();
            Press(ButtonNamed(ProtokitePlaytestUploadNetworkQuestionView.ButtonName(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData)));
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "Answered once read");
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, ProtokitePlaytest.PlayersUploadNetworkAnswer);
        }

        [UnityTest]
        public IEnumerator AnAnswerThatDoesNotLetTheScreenBeRecordedClosesThePanel()
        {
            yield return AnswerConsent(ProtokitePlaytestConsentChoice.PlayDataOnly);
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "Nothing is recorded, so nothing is asked about uploading it");
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.NotAnswered, ProtokitePlaytest.PlayersUploadNetworkAnswer);
        }

        [UnityTest]
        public IEnumerator AskingAgainPutsBothQuestionsAndTheAnswersBeforeHoldUntilChanged()
        {
            yield return AnswerConsent(ProtokitePlaytestConsentChoice.VideoOnly);
            yield return Until(() => QuestionOnScreen() == NetworkQuestionName, "The question about networks follows");
            yield return ReadTheQuestion();
            Press(ButtonNamed(ProtokitePlaytestUploadNetworkQuestionView.ButtonName(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData)));
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "Both answered");

            Assert.IsTrue(ProtokitePlaytest.AskForPlaytestConsent());
            yield return Until(() => QuestionOnScreen() == ConsentQuestionName, "Asked again, from the consent question");
            yield return AnswerConsent(ProtokitePlaytestConsentChoice.VideoAndPlayData);
            yield return Until(() => QuestionOnScreen() == NetworkQuestionName, "Then about networks, though answered before");
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, ProtokitePlaytest.PlayersUploadNetworkAnswer, "The answer before holds until the player chooses");
            yield return ReadTheQuestion();
            Press(ButtonNamed(ProtokitePlaytestUploadNetworkQuestionView.ButtonName(ProtokitePlaytestUploadNetworkChoice.WiFiOnly)));
            yield return Until(() => !ProtokitePlaytest.IsConsentQuestionOpen, "Answered again");
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, ProtokitePlaytest.PlayersUploadNetworkAnswer);
        }
    }
}
