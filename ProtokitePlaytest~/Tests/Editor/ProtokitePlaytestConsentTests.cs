using System;
using System.IO;
using System.Text.RegularExpressions;
using Flock;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    /// <summary>The playtest's consent question: what each answer allows, the file it is kept in, and what the game reads from it.</summary>
    public class ProtokitePlaytestConsentTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";

        private ProtokitePlaytestSettingsForTests _settings;
        private string _folder;

        private static string Config(string features) =>
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"test-gvid\",\"features\":{"
            + features + "},\"form\":null}}";

        private const string EveryFeature = "\"video_recording\":true,\"heavy_analytics\":true,\"exception_capturing\":true,\"new_feature\":true";

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests(askThePlayerForPlaytestConsent: true);
            _folder = Path.Combine(Path.GetTempPath(), "protokite_consent_file_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ProtokitePlaytest.ResetForNewLaunch();
            if (File.Exists(_settings.ConsentFilePath))
                File.SetAttributes(_settings.ConsentFilePath, FileAttributes.Normal);
            _settings.Dispose();
            foreach (string file in Directory.GetFiles(_folder))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_folder, true);
        }

        private static FlockTestClient FlockWithConfig(string features = EveryFeature)
        {
            FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(Config(features))));
            ProtokitePlaytest.Refresh();
            return flock;
        }

        // The rules

        [TestCase(ProtokitePlaytestConsentChoice.NotAnswered, false, false)]
        [TestCase(ProtokitePlaytestConsentChoice.VideoAndPlayData, true, true)]
        [TestCase(ProtokitePlaytestConsentChoice.VideoOnly, true, false)]
        [TestCase(ProtokitePlaytestConsentChoice.PlayDataOnly, false, true)]
        [TestCase(ProtokitePlaytestConsentChoice.Nothing, false, false)]
        public void EachAnswerAllowsItsOwnHalvesAndNoOther(ProtokitePlaytestConsentChoice choice, bool video, bool playData)
        {
            Assert.AreEqual(video, ProtokitePlaytestConsent.AllowsVideoRecording(choice));
            Assert.AreEqual(playData, ProtokitePlaytestConsent.AllowsPlayData(choice));
            Assert.AreEqual(video, ProtokitePlaytestConsent.AllowsFeature(choice, ProtokitePlaytestFeatures.VideoRecording));
            Assert.AreEqual(playData, ProtokitePlaytestConsent.AllowsFeature(choice, ProtokitePlaytestFeatures.HeavyAnalytics));
            Assert.AreEqual(playData, ProtokitePlaytestConsent.AllowsFeature(choice, ProtokitePlaytestFeatures.ExceptionCapturing));
            Assert.AreEqual(video || playData, ProtokitePlaytestConsent.CollectsAnything(choice));
            Assert.AreEqual(choice != ProtokitePlaytestConsentChoice.NotAnswered, ProtokitePlaytestConsent.IsAnswered(choice), "Nothing is an answer");
        }

        [Test]
        public void AFeatureThisBuildDoesNotKnowNeedsTheAnswerThatAllowsEverything()
        {
            Assert.IsTrue(ProtokitePlaytestConsent.AllowsFeature(ProtokitePlaytestConsentChoice.VideoAndPlayData, "new_feature"));
            Assert.IsFalse(ProtokitePlaytestConsent.AllowsFeature(ProtokitePlaytestConsentChoice.VideoOnly, "new_feature"), "Not guessed into a half");
            Assert.IsFalse(ProtokitePlaytestConsent.AllowsFeature(ProtokitePlaytestConsentChoice.PlayDataOnly, "new_feature"), "Not guessed into a half");
            Assert.IsFalse(ProtokitePlaytestConsent.AllowsFeature(ProtokitePlaytestConsentChoice.VideoOnly, "Video_Recording"),
                "Another letter case is another feature, as the server spells them");
        }

        [TestCase(ProtokitePlaytestConsentChoice.NotAnswered, "not_answered")]
        [TestCase(ProtokitePlaytestConsentChoice.VideoAndPlayData, "video_and_play_data")]
        [TestCase(ProtokitePlaytestConsentChoice.VideoOnly, "video_only")]
        [TestCase(ProtokitePlaytestConsentChoice.PlayDataOnly, "play_data_only")]
        [TestCase(ProtokitePlaytestConsentChoice.Nothing, "nothing")]
        public void EachAnswerIsSpeltTheWayEverySessionSendsIt(ProtokitePlaytestConsentChoice choice, string wire)
        {
            Assert.AreEqual(wire, ProtokitePlaytestConsent.ToWire(choice));
            Assert.AreEqual(choice, ProtokitePlaytestConsent.FromWire(wire));
        }

        [TestCase("VIDEO_ONLY")]
        [TestCase("Video_Only")]
        [TestCase(" video_only")]
        [TestCase("")]
        [TestCase(null)]
        public void AnyOtherSpellingIsNotAnAnswer(string wire)
        {
            Assert.AreEqual(ProtokitePlaytestConsentChoice.NotAnswered, ProtokitePlaytestConsent.FromWire(wire), "The player is asked again rather than having an answer read into it");
        }

        [Test]
        public void TheQuestionOffersEachAnswerOnceInOrderAndSaysItIsThePlaytestsOwn()
        {
            CollectionAssert.AreEqual(new[]
                {
                    ProtokitePlaytestConsentChoice.VideoAndPlayData, ProtokitePlaytestConsentChoice.VideoOnly,
                    ProtokitePlaytestConsentChoice.PlayDataOnly, ProtokitePlaytestConsentChoice.Nothing
                },
                Array.ConvertAll(ProtokitePlaytestConsentQuestionView.Options, option => option.Choice));
            StringAssert.Contains("separate from any privacy or analytics choice the game itself asks you about", ProtokitePlaytestConsentQuestionView.Introduction);
            StringAssert.Contains("A feedback report you choose to send still reaches the studio",
                Array.Find(ProtokitePlaytestConsentQuestionView.Options, option => option.Choice == ProtokitePlaytestConsentChoice.Nothing).Explanation);
        }

        // The file

        [Test]
        public void AnAnswerIsSavedReadBackAndForgotten()
        {
            ProtokitePlaytestConsentFile file = new ProtokitePlaytestConsentFile(Path.Combine(_folder, "nested", "playtest_consent.json"));
            Assert.AreEqual(ProtokitePlaytestConsentChoice.NotAnswered, file.Read(), "No file, no answer");

            Assert.IsTrue(file.Save(ProtokitePlaytestConsentChoice.PlayDataOnly));
            StringAssert.Contains("\"playtest_consent\":\"play_data_only\"", File.ReadAllText(file.Path));
            Assert.AreEqual(ProtokitePlaytestConsentChoice.PlayDataOnly, new ProtokitePlaytestConsentFile(file.Path).Read());

            Assert.IsTrue(file.Save(ProtokitePlaytestConsentChoice.Nothing), "Nothing is saved like any other answer");
            Assert.AreEqual(ProtokitePlaytestConsentChoice.Nothing, file.Read());

            Assert.IsTrue(file.Save(ProtokitePlaytestConsentChoice.NotAnswered), "NotAnswered is asking to be asked again");
            Assert.IsFalse(File.Exists(file.Path), "The file goes rather than hold \"not answered\"");
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(file.Path), "*.tmp").Length, "No temporary file left");
        }

        [TestCase("{\"playtest_consent\":\"VIDEO_ONLY\"}")]
        [TestCase("{\"playtest_consent\":1}")]
        [TestCase("{\"answer\":\"video_only\"}")]
        [TestCase("{\"playtest_consent\":\"video_on")]
        [TestCase("")]
        public void AFileHoldingAnythingElseReadsAsNotAnswered(string contents)
        {
            string path = Path.Combine(_folder, "playtest_consent.json");
            File.WriteAllText(path, contents);
            Assert.AreEqual(ProtokitePlaytestConsentChoice.NotAnswered, new ProtokitePlaytestConsentFile(path).Read());
        }

        // A read-only file stops a replace on Windows only; macOS and Linux let it go ahead.
        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void ASaveThatFailsForgetsTheAnswerSavedBefore()
        {
            ProtokitePlaytestConsentFile file = new ProtokitePlaytestConsentFile(Path.Combine(_folder, "playtest_consent.json"));
            Assert.IsTrue(file.Save(ProtokitePlaytestConsentChoice.VideoAndPlayData));
            // A read-only file cannot be replaced, which stands in for a disk that refuses the write.
            File.SetAttributes(file.Path, FileAttributes.ReadOnly);

            Assert.IsFalse(file.Save(ProtokitePlaytestConsentChoice.Nothing));
            Assert.IsFalse(File.Exists(file.Path), "The answer the player replaced is not left for the next launch to collect under");
            Assert.AreEqual(ProtokitePlaytestConsentChoice.NotAnswered, file.Read());
            Assert.AreEqual(0, Directory.GetFiles(_folder, "*.tmp").Length, "Nor is the save's own temporary file");
        }

        [Test]
        public void ASaveSweepsOnlyOldTemporaryFilesWhileForgettingSweepsThemAll()
        {
            string path = Path.Combine(_folder, "playtest_consent.json");
            string old = path + ".0000000000000000000000000000000a.tmp";
            string fresh = path + ".0000000000000000000000000000000b.tmp";
            File.WriteAllText(old, "{\"playtest_consent\":\"video_only\"}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));
            File.WriteAllText(fresh, "{\"playtest_consent\":\"video_only\"}");

            ProtokitePlaytestConsentFile file = new ProtokitePlaytestConsentFile(path);
            Assert.IsTrue(file.Save(ProtokitePlaytestConsentChoice.PlayDataOnly));
            Assert.IsFalse(File.Exists(old), "A launch that ended mid-save left it");
            Assert.IsTrue(File.Exists(fresh), "A fresh one may be another launch saving right now");

            Assert.IsTrue(file.Forget());
            Assert.IsFalse(File.Exists(fresh), "Forgetting leaves nothing on disk that still holds the answer, however fresh");
        }

        [Test]
        public void ForgettingRemovesATemporaryFileWhateverItsTimeSays()
        {
            string path = Path.Combine(_folder, "playtest_consent.json");
            string datedAhead = path + ".0000000000000000000000000000000c.tmp";
            File.WriteAllText(datedAhead, "{\"playtest_consent\":\"video_only\"}");
            // A clock set wrong dates a save in the future.
            File.SetLastWriteTimeUtc(datedAhead, DateTime.UtcNow.AddHours(3));

            Assert.IsTrue(new ProtokitePlaytestConsentFile(path).Forget());
            Assert.IsFalse(File.Exists(datedAhead), "Nothing on disk still holds the answer the player took back");
        }

        // What the game reads

        [Test]
        public void ABuildThatAsksCollectsNothingUntilThePlayerAnswers()
        {
            using (FlockWithConfig())
            {
                Assert.AreEqual(ProtokitePlaytestStatus.WaitingForPlayerConsent, ProtokitePlaytest.Status);
                Assert.AreEqual(ProtokitePlaytestConsentChoice.NotAnswered, ProtokitePlaytest.PlaytestConsent);
                Assert.IsNull(ProtokitePlaytest.Config, "The config is the ready playtest's");
                Assert.IsFalse(ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording));
                Assert.IsFalse(ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.HeavyAnalytics));
                Assert.IsFalse(ProtokitePlaytest.IsConsentQuestionOpen, "Nothing can be drawn outside Play Mode; the warning says so");
            }
        }

        [TestCase(ProtokitePlaytestConsentChoice.VideoAndPlayData, ProtokitePlaytestStatus.Ready, true, true, true)]
        [TestCase(ProtokitePlaytestConsentChoice.VideoOnly, ProtokitePlaytestStatus.Ready, true, false, false)]
        [TestCase(ProtokitePlaytestConsentChoice.PlayDataOnly, ProtokitePlaytestStatus.Ready, false, true, false)]
        [TestCase(ProtokitePlaytestConsentChoice.Nothing, ProtokitePlaytestStatus.PlayerRefusedPlaytest, false, false, false)]
        public void AnAnswerDecidesTheStatusAndEachFeature(ProtokitePlaytestConsentChoice answer, ProtokitePlaytestStatus status, bool video,
            bool heavyAnalytics, bool newFeature)
        {
            using (FlockWithConfig())
            {
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(answer));
                Assert.AreEqual(answer, ProtokitePlaytest.PlaytestConsent);
                Assert.AreEqual(status, ProtokitePlaytest.Status);
                Assert.AreEqual(video, ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording));
                Assert.AreEqual(heavyAnalytics, ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.HeavyAnalytics));
                Assert.AreEqual(newFeature, ProtokitePlaytest.IsFeatureEnabled("new_feature"), "A feature nobody described to the player");
                Assert.AreEqual(answer == ProtokitePlaytestConsentChoice.Nothing, ProtokitePlaytest.Config == null, "Nothing reads like playtesting off");
            }
        }

        [Test]
        public void ANewLaunchReadsTheSavedAnswer()
        {
            using (FlockWithConfig())
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoOnly));
            ProtokitePlaytest.ResetForNewLaunch();
            using (FlockWithConfig())
            {
                Assert.AreEqual(ProtokitePlaytestConsentChoice.VideoOnly, ProtokitePlaytest.PlaytestConsent, "Read from the file, not remembered in memory");
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
            }
        }

        [Test]
        public void AnAnswerAlreadyGivenOutlivesABuildThatStopsAsking()
        {
            Assert.IsTrue(new ProtokitePlaytestConsentFile(_settings.ConsentFilePath).Save(ProtokitePlaytestConsentChoice.Nothing));
            _settings.Settings.AskThePlayerForPlaytestConsent = false;
            using (FlockWithConfig())
            {
                Assert.AreEqual(ProtokitePlaytestConsentChoice.Nothing, ProtokitePlaytest.PlaytestConsent);
                Assert.AreEqual(ProtokitePlaytestStatus.PlayerRefusedPlaytest, ProtokitePlaytest.Status, "The setting decides whether the question is put, never whether an answer counts");
            }
        }

        [Test]
        public void ABuildThatDoesNotAskCollectsWhatThePlaytestTurnsOn()
        {
            _settings.Settings.AskThePlayerForPlaytestConsent = false;
            using (FlockWithConfig())
            {
                Assert.AreEqual(ProtokitePlaytestConsentChoice.VideoAndPlayData, ProtokitePlaytest.PlaytestConsent);
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
                Assert.IsTrue(ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording));
            }
        }

        [Test]
        public void ForgettingTheAnswerAsksAgain()
        {
            using (FlockWithConfig())
            {
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoOnly));
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.NotAnswered));
                Assert.IsFalse(File.Exists(_settings.ConsentFilePath));
                Assert.AreEqual(ProtokitePlaytestStatus.WaitingForPlayerConsent, ProtokitePlaytest.Status);
            }
        }

        // A read-only file stops a replace on Windows only; macOS and Linux let it go ahead.
        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void AnAnswerThatCannotBeSavedCollectsNothingAndForgetsTheOneBefore()
        {
            using (FlockWithConfig())
            {
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoAndPlayData));
                File.SetAttributes(_settings.ConsentFilePath, FileAttributes.ReadOnly);
                LogAssert.Expect(LogType.Warning, new Regex(@"could not be saved to .*playtest_consent\.json, so any answer saved before is forgotten and they are asked again"));

                Assert.IsFalse(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.PlayDataOnly));
                Assert.AreEqual(ProtokitePlaytestConsentChoice.NotAnswered, ProtokitePlaytest.PlaytestConsent, "Not the answer before, nor the one refused");
                Assert.AreEqual(ProtokitePlaytestStatus.WaitingForPlayerConsent, ProtokitePlaytest.Status);
                Assert.IsFalse(File.Exists(_settings.ConsentFilePath), "The next launch asks, rather than collect under an answer the player replaced");
            }
        }

        [Test]
        public void TheEditorsAskThePlayerAgainForgetsTheSavedAnswer()
        {
            Assert.IsTrue(new ProtokitePlaytestConsentFile(_settings.ConsentFilePath).Save(ProtokitePlaytestConsentChoice.Nothing));
            LogAssert.Expect(LogType.Log, new Regex("is forgotten; the question is put again"));

            Protokite.Playtest.Editor.ProtokitePlaytestSettingsMenu.ForgetThePlayersConsentAnswer();

            Assert.IsFalse(File.Exists(_settings.ConsentFilePath), "So a developer sees the question again on the next Play");
            Assert.AreEqual(ProtokitePlaytestConsentChoice.NotAnswered, ProtokitePlaytest.PlaytestConsent);
        }

        [Test]
        public void AskingAgainNeedsALoadedPlaytest()
        {
            Assert.IsFalse(ProtokitePlaytest.AskForPlaytestConsent(), "No Flock, no playtest, nothing to ask about");
            using (FlockWithConfig())
            {
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.Nothing));
                Assert.IsFalse(ProtokitePlaytest.AskForPlaytestConsent(), "Loaded, but nothing can be drawn outside Play Mode");
                Assert.AreEqual(ProtokitePlaytestConsentChoice.Nothing, ProtokitePlaytest.PlaytestConsent, "Asking again changes nothing until the player chooses");
            }
        }
    }
}
