using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Flock.Config;
using NUnit.Framework;
using Protokite.Playtest.Editor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    /// <summary>Each setup check against a project state that fails it and one that passes it, read from real settings objects.</summary>
    public class ProtokitePlaytestSetupChecksTests
    {
        internal static readonly string TestId = Id("7E");
        internal static readonly string PlaytestVersionName = "pt-" + TestId;
        internal static readonly string PlaytestVersionId = Id("AB");
        internal static readonly string ReleaseVersionId = Id("CD");

        private readonly List<Object> _made = new List<Object>();

        /// <summary>A Flock ID's shape: 26 characters of Crockford's base 32, ending as the test says.</summary>
        internal static string Id(string ending) => "01KX" + new string('0', 22 - ending.Length) + ending;

        [TearDown]
        public void TearDown()
        {
            foreach (Object made in _made)
                Object.DestroyImmediate(made);
            _made.Clear();
        }

        private ProtokitePlaytestSettings Playtest(bool enabled = true, string url = "https://protokite.example.com", bool askThePlayer = true)
        {
            ProtokitePlaytestSettings settings = ScriptableObject.CreateInstance<ProtokitePlaytestSettings>();
            _made.Add(settings);
            settings.PlaytestingEnabled = enabled;
            settings.ProtokiteApiUrl = url;
            settings.AskThePlayerForPlaytestConsent = askThePlayer;
            return settings;
        }

        private FlockConfigAsset Flock(string gameVersion, string gameVersionId)
        {
            FlockConfigAsset flock = ScriptableObject.CreateInstance<FlockConfigAsset>();
            _made.Add(flock);
            flock.apiUrl = "https://flock.example.com";
            flock.apiKey = "key-1";
            flock.gameVersion = gameVersion;
            flock.gameVersionId = gameVersionId;
            return flock;
        }

        private ProtokitePlaytestSetupInput ReadyProject() => ProtokitePlaytestSetupInput.From(Playtest(), Flock(PlaytestVersionName, PlaytestVersionId), BuildTarget.StandaloneWindows64, "x64");

        private static ProtokitePlaytestSetupCheck Check(ProtokitePlaytestSetupInput input, string id, ProtokitePlaytestGameVersionAnswer answer = null)
            => ProtokitePlaytestSetupChecks.Evaluate(input, answer).Single(check => check.Id == id);

        internal static ProtokitePlaytestGameVersionAnswer AnswerFor(ProtokitePlaytestSetupInput input, bool nameFound, string nameResolvesTo, string pastedId = null,
            string nameOfPastedId = null, bool resolvesBack = false, string problem = null)
            => new ProtokitePlaytestGameVersionAnswer
            {
                AskedFor = ProtokitePlaytestGameVersionLookup.KeyFor(input),
                Problem = problem,
                NameFound = nameFound,
                NameResolvesTo = nameResolvesTo,
                PastedId = pastedId,
                PastedIdFound = nameOfPastedId != null,
                NameOfPastedId = nameOfPastedId,
                NameOfPastedIdResolvesBack = resolvesBack
            };

        [Test]
        public void AProjectSetUpForAPlaytestPassesEveryCheck()
        {
            ProtokitePlaytestSetupInput input = ReadyProject();
            List<ProtokitePlaytestSetupCheck> checks = ProtokitePlaytestSetupChecks.Evaluate(input, AnswerFor(input, true, PlaytestVersionId));
            CollectionAssert.AreEqual(new[]
            {
                ProtokitePlaytestSetupChecks.PlaytestingCheck, ProtokitePlaytestSetupChecks.ProtokiteApiUrlCheck, ProtokitePlaytestSetupChecks.WhichPlaytestCheck,
                ProtokitePlaytestSetupChecks.VideoCheck
            }, checks.Select(check => check.Id).ToArray(), "The four checks, in the order they are fixed");
            foreach (ProtokitePlaytestSetupCheck check in checks)
            {
                Assert.IsTrue(check.Passed, check.Title + ": " + check.Detail);
                Assert.AreEqual(ProtokitePlaytestSetupFix.None, check.Fix, "Nothing to fix: " + check.Id);
            }
        }

        // ---- The switch

        [Test]
        public void NoPlaytestSettingsFailsTheSwitch()
        {
            ProtokitePlaytestSetupCheck check = Check(ProtokitePlaytestSetupInput.From(null, Flock(PlaytestVersionName, PlaytestVersionId), BuildTarget.StandaloneWindows64, ""),
                ProtokitePlaytestSetupChecks.PlaytestingCheck);
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("No playtest settings", check.Title, "Saying there are none, which is not the same fix as turning the switch on");
            StringAssert.Contains("Protokite > Playtest > Settings", check.Detail);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenPlaytestSettings, check.Fix);
        }

        [Test]
        public void PlaytestingTurnedOffFailsTheSwitchAndOnPassesIt()
        {
            // The consent setting is the opposite of the switch in both, so reading one for the other fails.
            ProtokitePlaytestSetupCheck off = Check(ProtokitePlaytestSetupInput.From(Playtest(enabled: false, askThePlayer: true), Flock(PlaytestVersionName, PlaytestVersionId),
                BuildTarget.StandaloneWindows64, ""), ProtokitePlaytestSetupChecks.PlaytestingCheck);
            Assert.IsFalse(off.Passed);
            Assert.AreEqual(ProtokitePlaytest.Describe(ProtokitePlaytestStatus.TurnedOff), off.Detail, "In the words the game's log uses");
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenPlaytestSettings, off.Fix);

            ProtokitePlaytestSetupCheck on = Check(ProtokitePlaytestSetupInput.From(Playtest(enabled: true, askThePlayer: false), Flock(PlaytestVersionName, PlaytestVersionId),
                BuildTarget.StandaloneWindows64, ""), ProtokitePlaytestSetupChecks.PlaytestingCheck);
            Assert.IsTrue(on.Passed, on.Detail);
        }

        // ---- The Protokite API URL

        [TestCase("")]
        [TestCase("   ")]
        public void AnEmptyProtokiteUrlFails(string url)
        {
            ProtokitePlaytestSetupCheck check = Check(ProtokitePlaytestSetupInput.From(Playtest(url: url), null, BuildTarget.StandaloneWindows64, ""),
                ProtokitePlaytestSetupChecks.ProtokiteApiUrlCheck);
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("Protokite API URL is empty", check.Title);
            Assert.AreEqual(ProtokitePlaytest.Describe(ProtokitePlaytestStatus.ProtokiteApiUrlMissing), check.Detail);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenPlaytestSettings, check.Fix);
        }

        [TestCase("ftp://protokite.example.com")]
        [TestCase("https://protokite.example .com")]
        [TestCase("http://")]
        [TestCase("protokite.example.com")]
        public void AProtokiteUrlThatCannotBeUsedFailsQuotingIt(string url)
        {
            ProtokitePlaytestSetupCheck check = Check(ProtokitePlaytestSetupInput.From(Playtest(url: url), null, BuildTarget.StandaloneWindows64, ""),
                ProtokitePlaytestSetupChecks.ProtokiteApiUrlCheck);
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("Protokite API URL cannot be used", check.Title);
            StringAssert.StartsWith(ProtokitePlaytest.Describe(ProtokitePlaytestStatus.ProtokiteApiUrlUnusable), check.Detail);
            StringAssert.Contains($"'{url}'", check.Detail, "Quoted, so a space shows");
        }

        [TestCase("https://api-protokite.qwacks.com")]
        [TestCase("  http://localhost:8020/  ")]
        public void AUsableProtokiteUrlPasses(string url)
        {
            ProtokitePlaytestSetupCheck check = Check(ProtokitePlaytestSetupInput.From(Playtest(url: url), null, BuildTarget.StandaloneWindows64, ""),
                ProtokitePlaytestSetupChecks.ProtokiteApiUrlCheck);
            Assert.IsTrue(check.Passed, check.Detail);
            StringAssert.Contains(url.Trim(), check.Detail);
        }

        [Test]
        public void TheUrlCheckAgreesWithTheGameOnEveryUrl()
        {
            foreach (string url in new[] { "", " ", "https://a.example", " https://a.example ", "https://a .example", "ftp://a.example", "http://", "a.example", "https://a.example:8020/x" })
            {
                ProtokitePlaytestSettings settings = Playtest(url: url);
                ProtokitePlaytestStatus game = ProtokitePlaytest.StatusFor(settings, false, ProtokitePlaytestConsentChoice.VideoAndPlayData);
                bool gameTakesIt = game != ProtokitePlaytestStatus.ProtokiteApiUrlMissing && game != ProtokitePlaytestStatus.ProtokiteApiUrlUnusable;
                ProtokitePlaytestSetupCheck check = Check(ProtokitePlaytestSetupInput.From(settings, null, BuildTarget.StandaloneWindows64, ""),
                    ProtokitePlaytestSetupChecks.ProtokiteApiUrlCheck);
                Assert.AreEqual(gameTakesIt, check.Passed, $"'{url}': the game says {game}");
            }
        }

        [Test]
        public void NoPlaytestSettingsFailsTheUrlToo()
        {
            ProtokitePlaytestSetupCheck check = Check(ProtokitePlaytestSetupInput.From(null, null, BuildTarget.StandaloneWindows64, ""), ProtokitePlaytestSetupChecks.ProtokiteApiUrlCheck);
            Assert.IsFalse(check.Passed);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenPlaytestSettings, check.Fix);
        }

        // ---- The Game Version

        private ProtokitePlaytestSetupCheck GameVersion(string name, string id, Func<ProtokitePlaytestSetupInput, ProtokitePlaytestGameVersionAnswer> answer = null)
        {
            ProtokitePlaytestSetupInput input = ProtokitePlaytestSetupInput.From(Playtest(), Flock(name, id), BuildTarget.StandaloneWindows64, "");
            return Check(input, ProtokitePlaytestSetupChecks.WhichPlaytestCheck, answer?.Invoke(input));
        }

        [Test]
        public void NoFlockSettingsFailsTheGameVersion()
        {
            ProtokitePlaytestSetupCheck check = Check(ProtokitePlaytestSetupInput.From(Playtest(), null, BuildTarget.StandaloneWindows64, ""), ProtokitePlaytestSetupChecks.WhichPlaytestCheck);
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("No Flock settings", check.Title);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenFlockSettings, check.Fix);
        }

        [TestCase("")]
        [TestCase("  ")]
        public void AnEmptyGameVersionFails(string name)
        {
            ProtokitePlaytestSetupCheck check = GameVersion(name, PlaytestVersionId);
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("Game Version is empty", check.Title);
            StringAssert.Contains("Playtest ID in Protokite > Playtest > Settings", check.Detail, "Naming where the playtest is chosen now");
        }

        [Test]
        public void APlaytestNameResolvedToItsIdPasses()
        {
            ProtokitePlaytestSetupCheck notAsked = GameVersion(PlaytestVersionName, PlaytestVersionId);
            Assert.IsTrue(notAsked.Passed, notAsked.Detail);
            StringAssert.Contains("has not been checked with Flock yet", notAsked.Detail, "Passing on what the settings hold, and saying so");

            ProtokitePlaytestSetupCheck confirmed = GameVersion(PlaytestVersionName, PlaytestVersionId, input => AnswerFor(input, true, PlaytestVersionId));
            Assert.IsTrue(confirmed.Passed, confirmed.Detail);
            StringAssert.Contains("Flock resolves it to that ID", confirmed.Detail);
        }

        [Test]
        public void AnIdPastedIntoGameVersionIsFlaggedAndTheNameThatResolvesToItSuggested()
        {
            ProtokitePlaytestSetupCheck beforeFlockAnswers = GameVersion(PlaytestVersionId, ReleaseVersionId);
            Assert.IsFalse(beforeFlockAnswers.Passed);
            Assert.AreEqual("Game Version holds an ID, not a name", beforeFlockAnswers.Title);
            StringAssert.Contains(ReleaseVersionId, beforeFlockAnswers.Detail, "Naming the ID a build still sends");
            Assert.IsNull(beforeFlockAnswers.SuggestedGameVersion, "Nothing suggested before Flock has said");

            ProtokitePlaytestSetupCheck answered = GameVersion(PlaytestVersionId, ReleaseVersionId,
                input => AnswerFor(input, false, null, PlaytestVersionId, PlaytestVersionName, resolvesBack: true));
            Assert.IsFalse(answered.Passed);
            Assert.AreEqual(PlaytestVersionName, answered.SuggestedGameVersion);
            Assert.AreEqual(PlaytestVersionId, answered.SuggestedGameVersionId);
            Assert.AreEqual(ProtokitePlaytestSetupFix.UseTheSuggestedGameVersion, answered.Fix);
            StringAssert.Contains($"set Game Version to {PlaytestVersionName}", answered.Detail);
        }

        [Test]
        public void AnIdWhoseNameDoesNotResolveBackInThisGameIsFlaggedWithNothingSuggested()
        {
            ProtokitePlaytestSetupCheck check = GameVersion(PlaytestVersionId, ReleaseVersionId,
                input => AnswerFor(input, false, null, PlaytestVersionId, PlaytestVersionName, resolvesBack: false));
            Assert.IsFalse(check.Passed);
            Assert.IsNull(check.SuggestedGameVersion);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenFlockSettings, check.Fix);
            StringAssert.Contains("belongs to another game", check.Detail);
        }

        [Test]
        public void ANameThatLooksLikeAnIdButResolvesIsTreatedAsAName()
        {
            ProtokitePlaytestSetupCheck check = GameVersion(PlaytestVersionId, ReleaseVersionId, input => AnswerFor(input, true, ReleaseVersionId));
            Assert.AreEqual("The game's newest playtest", check.Title, "A real name, only not a playtest's");
        }

        [Test]
        public void TheGamesOwnVersionWithNoPlaytestIdJoinsTheNewestPlaytest()
        {
            ProtokitePlaytestSetupCheck check = GameVersion("1.0.0", ReleaseVersionId);
            Assert.IsTrue(check.Passed, check.Detail);
            Assert.AreEqual("The game's newest playtest", check.Title);
            StringAssert.Contains("Playtest ID in Protokite > Playtest > Settings is empty", check.Detail, "Saying why");
            StringAssert.Contains("joins the game's newest playtest", check.Detail);
            StringAssert.Contains("Paste a playtest's ID into Playtest ID to choose it", check.Detail, "and how to choose another");
            Assert.AreEqual(ProtokitePlaytestSetupFix.None, check.Fix);
        }

        [TestCase("PT-01KX0000000000000000007E")]
        [TestCase(" pt-01KX0000000000000000007E")]
        [TestCase("pt-")]
        public void AGameVersionMeantAsAPlaytestsNameThatIsNotOneFails(string name)
        {
            ProtokitePlaytestSetupCheck check = GameVersion(name, ReleaseVersionId);
            Assert.IsFalse(check.Passed, "It would join the newest playtest, not the one meant");
            Assert.AreEqual("Game Version is not a playtest's name, letter for letter", check.Title);
            StringAssert.Contains($"'{name}'", check.Detail, "Quoted as it is, so a space or the letter case shows");
            StringAssert.Contains("joins the game's newest playtest instead", check.Detail);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenPlaytestSettings, check.Fix);
        }

        [Test]
        public void AnIdPastedOverTheResolvedIdIsFlaggedAndItsNameSuggested()
        {
            // The trap a live harness was left in: the name is the release's, the ID a build sends was pasted from the test page.
            ProtokitePlaytestSetupCheck check = GameVersion("1.0.0", PlaytestVersionId,
                input => AnswerFor(input, true, ReleaseVersionId, PlaytestVersionId, PlaytestVersionName, resolvesBack: true));
            Assert.IsFalse(check.Passed);
            Assert.AreEqual(PlaytestVersionName, check.SuggestedGameVersion);
            Assert.AreEqual(PlaytestVersionId, check.SuggestedGameVersionId);
            StringAssert.Contains($"set Game Version to {PlaytestVersionName}, and every resolve keeps that ID", check.Detail);
        }

        [Test]
        public void APlaytestNameWithNoResolvedIdFails()
        {
            ProtokitePlaytestSetupCheck check = GameVersion(PlaytestVersionName, "");
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("Game Version is not resolved", check.Title);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenFlockSettings, check.Fix);
        }

        [Test]
        public void APlaytestNameFlockHasNoVersionOfFails()
        {
            ProtokitePlaytestSetupCheck check = GameVersion(PlaytestVersionName, PlaytestVersionId, input => AnswerFor(input, false, null));
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("No version has this name", check.Title);
        }

        [Test]
        public void ResolvedIdThatIsNotTheNamesFails()
        {
            ProtokitePlaytestSetupCheck check = GameVersion(PlaytestVersionName, ReleaseVersionId, input => AnswerFor(input, true, PlaytestVersionId));
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("The build sends another version's ID", check.Title);
            StringAssert.Contains(PlaytestVersionId, check.Detail);
            StringAssert.Contains(ReleaseVersionId, check.Detail);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenFlockSettings, check.Fix, "Resolved in Flock > Settings");
        }

        [Test]
        public void AnOldIdUnderANewPlaytestNameIsFixedByResolvingTheName()
        {
            // The name was changed to a newer playtest's and not resolved yet: the ID held is the older playtest's.
            string olderId = Id("0D");
            string olderName = "pt-" + Id("0E");
            ProtokitePlaytestSetupCheck check = GameVersion(PlaytestVersionName, olderId,
                input => AnswerFor(input, true, PlaytestVersionId, olderId, olderName, resolvesBack: true));
            Assert.IsFalse(check.Passed);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenFlockSettings, check.Fix, "The name the developer set is resolved, never put back to the older playtest's");
            Assert.IsNull(check.SuggestedGameVersion);
            StringAssert.Contains($"if that is the playtest meant, set Game Version to {olderName} instead", check.Detail, "The older playtest is still named");
        }

        [Test]
        public void OnlyAPlaytestsNameIsEverSuggested()
        {
            // A release's ID held under a newer release's name: its name resolves back, and is no playtest's.
            ProtokitePlaytestSetupCheck staleRelease = GameVersion("1.0.1", ReleaseVersionId,
                input => AnswerFor(input, true, Id("11"), ReleaseVersionId, "1.0.0", resolvesBack: true));
            Assert.IsNull(staleRelease.SuggestedGameVersion);
            Assert.AreEqual("The game's newest playtest", staleRelease.Title, "A release's name with no Playtest ID joins the newest playtest; Flock's own window flags the drift");
            StringAssert.DoesNotContain("set Game Version to 1.0.0", staleRelease.Detail);

            // A release's ID pasted into Game Version.
            ProtokitePlaytestSetupCheck pastedRelease = GameVersion(ReleaseVersionId, PlaytestVersionId,
                input => AnswerFor(input, false, null, ReleaseVersionId, "1.0.0", resolvesBack: true));
            Assert.IsFalse(pastedRelease.Passed);
            Assert.IsNull(pastedRelease.SuggestedGameVersion);
            StringAssert.Contains("It is the ID of the version named 1.0.0, which is not a playtest's version", pastedRelease.Detail);
        }

        [Test]
        public void FlockNotAnsweringLeavesThePassOnTheSettingsAndSaysSo()
        {
            ProtokitePlaytestSetupCheck check = GameVersion(PlaytestVersionName, PlaytestVersionId, input => AnswerFor(input, false, null, problem: "Flock could not be reached."));
            Assert.IsTrue(check.Passed, "No answer is not a failure of the settings");
            StringAssert.Contains("could not be checked with Flock: Flock could not be reached.", check.Detail);
        }

        [Test]
        public void AnAnswerGivenForOtherSettingsIsNotUsed()
        {
            ProtokitePlaytestSetupInput before = ProtokitePlaytestSetupInput.From(Playtest(), Flock(PlaytestVersionId, ReleaseVersionId), BuildTarget.StandaloneWindows64, "");
            ProtokitePlaytestGameVersionAnswer answer = AnswerFor(before, false, null, PlaytestVersionId, PlaytestVersionName, resolvesBack: true);
            // The developer typed another ID since Flock was asked.
            ProtokitePlaytestSetupInput after = ProtokitePlaytestSetupInput.From(Playtest(), Flock(Id("EF"), ReleaseVersionId), BuildTarget.StandaloneWindows64, "");
            ProtokitePlaytestSetupCheck check = Check(after, ProtokitePlaytestSetupChecks.WhichPlaytestCheck, answer);
            Assert.IsFalse(check.Passed);
            Assert.IsNull(check.SuggestedGameVersion, "An old answer's name is never offered for new settings");
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenFlockSettings, check.Fix);
        }

        // ---- The Playtest ID

        private ProtokitePlaytestSetupInput WithPlaytestId(string typed, string resolvedFrom, string resolvedVersionId, string gameVersion = "1.0.0",
            bool resolvedWithTheseFlockSettings = true)
        {
            ProtokitePlaytestSettings settings = Playtest();
            FlockConfigAsset flock = Flock(gameVersion, ReleaseVersionId);
            settings.PlaytestId = typed;
            settings.KeepResolvedPlaytestVersion(resolvedFrom, resolvedVersionId,
                resolvedWithTheseFlockSettings ? ProtokitePlaytestIdLookup.FlockSettingsFingerprint(flock.apiUrl, flock.apiKey) : "another environment");
            return ProtokitePlaytestSetupInput.From(settings, flock, BuildTarget.StandaloneWindows64, "");
        }

        private static ProtokitePlaytestSetupCheck WhichPlaytest(ProtokitePlaytestSetupInput input, ProtokitePlaytestIdAnswer answer = null)
            => ProtokitePlaytestSetupChecks.Evaluate(input, null, answer).Single(check => check.Id == ProtokitePlaytestSetupChecks.WhichPlaytestCheck);

        private static ProtokitePlaytestIdAnswer PlaytestIdAnswer(ProtokitePlaytestSetupInput input, string versionId, string whyNot = null, string problem = null)
            => new ProtokitePlaytestIdAnswer
            {
                AskedFor = ProtokitePlaytestIdLookup.KeyFor(input.FlockApiUrl, input.FlockApiKey, input.PlaytestId),
                VersionId = versionId,
                VersionName = versionId != null ? PlaytestVersionName : null,
                WhyNoPlaytest = whyNot,
                Problem = problem
            };

        [Test]
        public void AResolvedPlaytestIdPassesWhateverTheGameVersion()
        {
            ProtokitePlaytestSetupInput input = WithPlaytestId(TestId, TestId, PlaytestVersionId);
            ProtokitePlaytestSetupCheck notAsked = WhichPlaytest(input);
            Assert.IsTrue(notAsked.Passed, notAsked.Detail);
            Assert.AreEqual("Playtest ID chooses this build's playtest", notAsked.Title);
            StringAssert.Contains(PlaytestVersionId, notAsked.Detail);
            StringAssert.Contains("the Flock SDK keeps Game Version '1.0.0'", notAsked.Detail, "Saying the game keeps its own version");
            StringAssert.Contains("has not been checked with Flock again", notAsked.Detail);

            ProtokitePlaytestSetupCheck confirmed = WhichPlaytest(input, PlaytestIdAnswer(input, PlaytestVersionId));
            Assert.IsTrue(confirmed.Passed, confirmed.Detail);
            StringAssert.Contains("Flock names it " + PlaytestVersionName, confirmed.Detail);

            // Control: the same Game Version with no Playtest ID is the newest playtest's pass, so this pass is the Playtest ID's.
            Assert.AreEqual("The game's newest playtest", GameVersion("1.0.0", ReleaseVersionId).Title);
            Assert.IsTrue(WhichPlaytest(WithPlaytestId(TestId, TestId, PlaytestVersionId, gameVersion: "")).Passed, "The Game Version is not judged here");
        }

        [Test]
        public void APlaytestIdResolvedForAnotherValueIsNotResolved()
        {
            ProtokitePlaytestSetupCheck check = WhichPlaytest(WithPlaytestId(TestId, Id("01"), PlaytestVersionId));
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("Playtest ID is not resolved yet", check.Title);
            StringAssert.Contains("a build with Playtesting Enabled on is refused", check.Detail);
            Assert.AreEqual(ProtokitePlaytestSetupFix.ResolveThePlaytestId, check.Fix);
        }

        [Test]
        public void APlaytestIdThatFindsNoPlaytestFails()
        {
            ProtokitePlaytestSetupInput input = WithPlaytestId(TestId, TestId, "");
            ProtokitePlaytestSetupCheck check = WhichPlaytest(input, PlaytestIdAnswer(input, null, whyNot: "No playtest of this game has the ID X."));
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("No playtest has this ID", check.Title);
            StringAssert.StartsWith("No playtest of this game has the ID X.", check.Detail);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenPlaytestSettings, check.Fix);
        }

        [Test]
        public void FlockNotAnsweringKeepsAResolvedPlaytestIdAndSaysSo()
        {
            ProtokitePlaytestSetupInput resolved = WithPlaytestId(TestId, TestId, PlaytestVersionId);
            ProtokitePlaytestSetupCheck kept = WhichPlaytest(resolved, PlaytestIdAnswer(resolved, null, problem: "Flock could not be reached."));
            Assert.IsTrue(kept.Passed, "No answer is not a failure of the settings");
            StringAssert.Contains("could not be checked with Flock again: Flock could not be reached.", kept.Detail);

            ProtokitePlaytestSetupInput unresolved = WithPlaytestId(TestId, "", "");
            ProtokitePlaytestSetupCheck notYet = WhichPlaytest(unresolved, PlaytestIdAnswer(unresolved, null, problem: "Flock could not be reached."));
            Assert.IsFalse(notYet.Passed);
            Assert.AreEqual("Playtest ID is not resolved", notYet.Title);
            Assert.AreEqual(ProtokitePlaytestSetupFix.ResolveThePlaytestId, notYet.Fix);
        }

        [Test]
        public void APlaytestIdResolvedWithOtherFlockSettingsFails()
        {
            ProtokitePlaytestSetupInput input = WithPlaytestId(TestId, TestId, PlaytestVersionId, resolvedWithTheseFlockSettings: false);
            ProtokitePlaytestSetupCheck check = WhichPlaytest(input);
            Assert.IsFalse(check.Passed, check.Detail);
            Assert.AreEqual("Playtest ID was resolved with other Flock settings", check.Title);
            StringAssert.Contains("is refused until it is resolved with these", check.Detail);
            Assert.AreEqual(ProtokitePlaytestSetupFix.ResolveThePlaytestId, check.Fix);
            ProtokitePlaytestSetupCheck offline = WhichPlaytest(input, PlaytestIdAnswer(input, null, problem: "Flock could not be reached."));
            Assert.IsFalse(offline.Passed, "Flock not answering never makes another environment's version this one's");
        }

        [Test]
        public void APlaytestIdWithNoFlockSettingsToAskWithFails()
        {
            ProtokitePlaytestSettings settings = Playtest();
            settings.PlaytestId = TestId;
            ProtokitePlaytestSetupCheck check = WhichPlaytest(ProtokitePlaytestSetupInput.From(settings, null, BuildTarget.StandaloneWindows64, ""));
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("Playtest ID cannot be resolved", check.Title);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenFlockSettings, check.Fix);
        }

        [Test]
        public void AnAnswerForAnotherPlaytestIdIsNotUsed()
        {
            ProtokitePlaytestSetupInput before = WithPlaytestId(Id("01"), "", "");
            ProtokitePlaytestIdAnswer answer = PlaytestIdAnswer(before, PlaytestVersionId);
            ProtokitePlaytestSetupInput after = WithPlaytestId(TestId, "", "");
            ProtokitePlaytestSetupCheck check = WhichPlaytest(after, answer);
            Assert.AreEqual("Playtest ID is not resolved yet", check.Title);
            StringAssert.Contains("Checking it with Flock", check.Detail, "An old answer's playtest is never said to be this ID's");
        }

        [TestCase("01KX0000000000000000007EAB", true)]
        [TestCase("01kx0000000000000000007eab", true)]
        [TestCase(" 01KX0000000000000000007EAB ", true)]
        [TestCase("01KX000000000000000000007E", true)]
        [TestCase("01KX0000000000000000007EA", false)]
        [TestCase("01KX0000000000000000007EABC", false)]
        [TestCase("01KX000000000000000000007L", false)]
        [TestCase("pt-01KX0000000000000000007E", false)]
        [TestCase("1.0.0", false)]
        public void AnIdIsRecognisedByItsShape(string value, bool looksLikeAnId)
            => Assert.AreEqual(looksLikeAnId, ProtokitePlaytestSetupChecks.LooksLikeAnId(value));

        // ---- Video on the build target

        [TestCase(BuildTarget.StandaloneWindows64, "", true)]
        [TestCase(BuildTarget.StandaloneWindows64, "x64", true)]
        [TestCase(BuildTarget.StandaloneWindows64, "x64ARM64", false)]
        [TestCase(BuildTarget.StandaloneWindows64, "ARM64", false)]
        [TestCase(BuildTarget.StandaloneWindows64, "x86", false)]
        [TestCase(BuildTarget.StandaloneWindows, "", false)]
        [TestCase(BuildTarget.StandaloneOSX, "x64", false)]
        [TestCase(BuildTarget.StandaloneLinux64, "x64", false)]
        [TestCase(BuildTarget.WebGL, "x64", false)]
        [TestCase(BuildTarget.Android, "", true)]
        [TestCase(BuildTarget.Android, "x64", true)]
        [TestCase(BuildTarget.iOS, "x64", false)]
        public void OnlyAndroidAndA64BitWindowsX64BuildRecordVideo(BuildTarget target, string architecture, bool records)
        {
            ProtokitePlaytestSetupCheck check = Check(ProtokitePlaytestSetupInput.From(Playtest(), Flock(PlaytestVersionName, PlaytestVersionId), target, architecture),
                ProtokitePlaytestSetupChecks.VideoCheck);
            Assert.AreEqual(records, check.Passed, check.Title);
            if (records)
                return;
            StringAssert.Contains(target == BuildTarget.StandaloneWindows64 ? architecture : target.ToString(), check.Title, "Naming what it is built for");
            StringAssert.Contains("still runs the playtest, without video", check.Detail, "Saying video is the only thing missing");
            StringAssert.Contains("Windows, x64, or to Android", check.Detail, "Naming the builds that record");
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenBuildProfiles, check.Fix);
        }

        [Test]
        public void AnAndroidBuildRecordsWithThePhonesOwnEncoder()
        {
            ProtokitePlaytestSetupInput input = ProtokitePlaytestSetupInput.From(Playtest(), Flock(PlaytestVersionName, PlaytestVersionId), BuildTarget.Android, "");
            // What this PC offers, as the window fills it in; a Windows build's check names it.
            input.ThisPcEncoder = "a stand-in graphics card encoder";
            ProtokitePlaytestSetupCheck check = Check(input, ProtokitePlaytestSetupChecks.VideoCheck);
            Assert.IsTrue(check.Passed, check.Detail);
            StringAssert.Contains("The build target is Android", check.Detail);
            StringAssert.Contains("the phone's own hardware H.264 encoder", check.Detail);
            StringAssert.Contains("x86 Android devices are not measured", check.Detail, "Saying what no run has proven");
            StringAssert.Contains("Android Allow Software Encoder", check.Detail, "Naming the setting for a phone without one");
            StringAssert.Contains("only a test video in an Android player", check.Detail, "An editor test video proves nothing of a phone");
            StringAssert.DoesNotContain("On this PC, Windows offers", check.Detail, "This PC's encoder is not offered as a phone's");
        }

        [Test]
        public void AnAndroidBuildWithVideoTurnedOffSaysSoAndOpensTheSettings()
        {
            ProtokitePlaytestSettings playtest = Playtest();
            playtest.RecordVideoOnAndroid = false;
            ProtokitePlaytestSetupCheck check = Check(ProtokitePlaytestSetupInput.From(playtest, Flock(PlaytestVersionName, PlaytestVersionId), BuildTarget.Android, ""),
                ProtokitePlaytestSetupChecks.VideoCheck);
            Assert.IsFalse(check.Passed);
            Assert.AreEqual("Players built for Android record no video", check.Title);
            StringAssert.Contains("Record Video On Android is off", check.Detail);
            StringAssert.Contains("Everything else in the playtest still runs", check.Detail);
            Assert.AreEqual(ProtokitePlaytestSetupFix.OpenPlaytestSettings, check.Fix);

            ProtokitePlaytestSetupCheck windows = Check(ProtokitePlaytestSetupInput.From(playtest, Flock(PlaytestVersionName, PlaytestVersionId), BuildTarget.StandaloneWindows64, "x64"),
                ProtokitePlaytestSetupChecks.VideoCheck);
            Assert.IsTrue(windows.Passed, "The switch is Android's alone");
        }

        [Test]
        public void AProjectWithNoPlaytestSettingsReadsAndroidVideoAsOn()
        {
            Assert.IsTrue(ProtokitePlaytestSetupInput.From(null, null, BuildTarget.Android, "").RecordVideoOnAndroid, "As a new settings asset starts");
        }

        [Test]
        public void TheVideoCheckSaysWhatRecordsOnThisPcBesideItsAnswer()
        {
            ProtokitePlaytestSetupInput input = ProtokitePlaytestSetupInput.From(Playtest(), Flock(PlaytestVersionName, PlaytestVersionId), BuildTarget.StandaloneWindows64, "x64");
            input.ThisPcEncoder = "NVIDIA H.264 Encoder MFT (on the graphics card)";
            ProtokitePlaytestSetupCheck check = Check(input, ProtokitePlaytestSetupChecks.VideoCheck);
            Assert.IsTrue(check.Passed);
            StringAssert.Contains("Allow Software Encoder", check.Detail, "A player's PC with no graphics card encoder is named, with the setting");
            StringAssert.EndsWith("Windows offers NVIDIA H.264 Encoder MFT (on the graphics card) first; a test video shows whether it records.", check.Detail,
                "Offered, not promised: a listed encoder may still fail to start");

            input.ThisPcEncoder = null;
            input.WhyThisPcRecordsNoVideo = ProtokitePlaytestVideoEncoders.NoGraphicsCardEncoder;
            check = Check(input, ProtokitePlaytestSetupChecks.VideoCheck);
            Assert.IsTrue(check.Passed, "This PC is not a player's: the build still records on PCs that have an encoder");
            StringAssert.Contains("This PC records no video, so it records no test video: " + ProtokitePlaytestVideoEncoders.NoGraphicsCardEncoder, check.Detail);
        }

#if UNITY_EDITOR_WIN
        [Test]
        public void ReadingTheProjectAsksWindowsWhatRecordsOnThisPc()
        {
            // What this PC offers stands in, so the test is about reading the project, not about this PC's graphics card.
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () => new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(
                new List<ProtokitePlaytestEncoderFound> { new ProtokitePlaytestEncoderFound { Name = "H264 Encoder MFT" }, new ProtokitePlaytestEncoderFound { Name = "A Card's Encoder", InHardware = true } }, null);
            try
            {
                ProtokitePlaytestSetupInput input = ProtokitePlaytestSetupInput.FromProject();
                Assert.IsNull(input.WhyThisPcRecordsNoVideo);
                Assert.AreEqual("A Card's Encoder (on the graphics card)", input.ThisPcEncoder, "A graphics card encoder is named before the software one");

                ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
                ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () => new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(null, ProtokitePlaytestVideoEncoders.MediaFoundationMissing);
                input = ProtokitePlaytestSetupInput.FromProject();
                Assert.AreEqual(ProtokitePlaytestVideoEncoders.MediaFoundationMissing, input.WhyThisPcRecordsNoVideo);
                Assert.IsNull(input.ThisPcEncoder);
            }
            finally
            {
                ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = null;
                ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            }
        }
#endif

        [Test]
        public void TheCheckAndTheGameAgreeWhichProcessorsRecord()
        {
            // The runtime leaves the encoder out of every Windows build that is not x64; the check names the same builds.
            Assert.IsTrue(ProtokitePlaytestSetupChecks.RecordsVideo(BuildTarget.StandaloneWindows64, "x64"));
            Assert.IsFalse(ProtokitePlaytestSetupChecks.RecordsVideo(BuildTarget.StandaloneWindows64, "ARM64"));
            Assert.IsFalse(ProtokitePlaytestSetupChecks.RecordsVideo(BuildTarget.StandaloneWindows, "x64"));
        }

        // ---- Reading the project

        [Test]
        public void EverySettingIsReadFromItsOwnField()
        {
            // Every value differs from every other, so reading one field for another fails.
            FlockConfigAsset flock = Flock("pt-name", "id-value");
            flock.apiUrl = "https://flock-url.example";
            flock.apiKey = "key-value";
            ProtokitePlaytestSetupInput input = ProtokitePlaytestSetupInput.From(Playtest(enabled: true, url: "https://protokite-url.example", askThePlayer: false), flock,
                BuildTarget.WebGL, "ARM64");
            Assert.IsTrue(input.PlaytestSettingsFound);
            Assert.IsTrue(input.PlaytestingEnabled);
            Assert.AreEqual("https://protokite-url.example", input.ProtokiteApiUrl);
            Assert.IsTrue(input.FlockSettingsFound);
            Assert.AreEqual("https://flock-url.example", input.FlockApiUrl);
            Assert.AreEqual("key-value", input.FlockApiKey);
            Assert.AreEqual("pt-name", input.GameVersion);
            Assert.AreEqual("id-value", input.GameVersionId);
            Assert.AreEqual(BuildTarget.WebGL, input.BuildTarget);
            Assert.AreEqual("ARM64", input.WindowsArchitecture);

            ProtokitePlaytestSetupInput off = ProtokitePlaytestSetupInput.From(Playtest(enabled: false, askThePlayer: true), null, BuildTarget.Android, null);
            Assert.IsFalse(off.PlaytestingEnabled, "The switch, never the consent setting beside it");
            Assert.IsFalse(off.FlockSettingsFound);
            Assert.AreEqual("", off.WindowsArchitecture);
        }

        [Test]
        public void TheProjectsOwnSettingsAreWhatIsRead()
        {
            FlockConfigAsset flock = ProtokitePlaytestSetupInput.LoadFlockSettings();
            string made = null;
            if (flock == null)
            {
                // A project with no Flock settings of its own gets them for this test only.
                made = "Assets/ProtokitePlaytestSetupTest/Resources/" + ProtokitePlaytestSetupInput.FlockSettingsResourceName + ".asset";
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(made));
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<FlockConfigAsset>(), made);
                flock = ProtokitePlaytestSetupInput.LoadFlockSettings();
            }
            string flockAsItWas = EditorJsonUtility.ToJson(flock);
            try
            {
                using (ProtokitePlaytestSettingsForTests playtest = new ProtokitePlaytestSettingsForTests(playtestingEnabled: true, protokiteApiUrl: "https://read-from-project.example"))
                {
                    ProtokitePlaytestSetupInput input;
                    // Changed in memory only, and put back before the playtest settings are saved, so the Flock settings on disk are never written.
                    try
                    {
                        flock.gameVersion = "pt-read-from-project";
                        flock.gameVersionId = "id-read-from-project";
                        input = ProtokitePlaytestSetupInput.FromProject();
                    }
                    finally
                    {
                        EditorJsonUtility.FromJsonOverwrite(flockAsItWas, flock);
                    }
                    Assert.AreEqual("https://read-from-project.example", input.ProtokiteApiUrl, "The playtest settings a build carries");
                    Assert.IsTrue(input.PlaytestingEnabled);
                    Assert.AreEqual("pt-read-from-project", input.GameVersion, "The Flock settings a build carries");
                    Assert.AreEqual("id-read-from-project", input.GameVersionId);
                    Assert.AreEqual(EditorUserBuildSettings.activeBuildTarget, input.BuildTarget, "The platform the project builds for now");
                    Assert.AreEqual(EditorUserBuildSettings.GetPlatformSettings(BuildPipeline.GetBuildTargetName(BuildTarget.StandaloneWindows64), "Architecture"),
                        input.WindowsArchitecture);
                }
            }
            finally
            {
                EditorJsonUtility.FromJsonOverwrite(flockAsItWas, flock);
                if (made != null)
                    AssetDatabase.DeleteAsset("Assets/ProtokitePlaytestSetupTest");
            }
        }

        // ---- Putting it right

        [Test]
        public void TheSuggestionSetsTheNameAndTheIdItResolvesTo()
        {
            FlockConfigAsset flock = Flock(PlaytestVersionId, ReleaseVersionId);
            ProtokitePlaytestSetupInput input = ProtokitePlaytestSetupInput.From(Playtest(), flock, BuildTarget.StandaloneWindows64, "x64");
            ProtokitePlaytestSetupCheck check = Check(input, ProtokitePlaytestSetupChecks.WhichPlaytestCheck,
                AnswerFor(input, false, null, PlaytestVersionId, PlaytestVersionName, resolvesBack: true));

            Assert.IsTrue(ProtokitePlaytestSetupChecks.UseTheSuggestedGameVersion(flock, check));
            Assert.AreEqual(PlaytestVersionName, flock.gameVersion);
            Assert.AreEqual(PlaytestVersionId, flock.gameVersionId, "The ID a build sends is the one the name resolves to, not the one resolved before");
            ProtokitePlaytestSetupInput after = ProtokitePlaytestSetupInput.From(Playtest(), flock, BuildTarget.StandaloneWindows64, "x64");
            Assert.IsTrue(Check(after, ProtokitePlaytestSetupChecks.WhichPlaytestCheck, AnswerFor(after, true, PlaytestVersionId)).Passed, "And the check then passes");
        }

        [Test]
        public void TheSuggestionSavesTheFlockSettingsAndNoOtherAsset()
        {
            const string folder = "Assets/ProtokitePlaytestSaveTest";
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                FlockConfigAsset made = ScriptableObject.CreateInstance<FlockConfigAsset>();
                made.gameVersion = PlaytestVersionId;
                made.gameVersionId = ReleaseVersionId;
                AssetDatabase.CreateAsset(made, folder + "/Flock.asset");
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<FlockConfigAsset>(), folder + "/Other.asset");
                // Both imported before anything is edited, so no import still to come reloads an edit from disk.
                AssetDatabase.ImportAsset(folder + "/Flock.asset", ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(folder + "/Other.asset", ImportAssetOptions.ForceSynchronousImport);
                FlockConfigAsset flock = AssetDatabase.LoadAssetAtPath<FlockConfigAsset>(folder + "/Flock.asset");
                FlockConfigAsset other = AssetDatabase.LoadAssetAtPath<FlockConfigAsset>(folder + "/Other.asset");
                byte[] otherBefore = System.IO.File.ReadAllBytes(folder + "/Other.asset");
                // A developer's edit to another asset, not saved yet.
                other.gameVersion = "unsaved-edit";
                EditorUtility.SetDirty(other);

                ProtokitePlaytestSetupInput input = ProtokitePlaytestSetupInput.From(Playtest(), flock, BuildTarget.StandaloneWindows64, "x64");
                ProtokitePlaytestSetupCheck check = Check(input, ProtokitePlaytestSetupChecks.WhichPlaytestCheck,
                    AnswerFor(input, false, null, PlaytestVersionId, PlaytestVersionName, resolvesBack: true));
                Assert.IsTrue(ProtokitePlaytestSetupChecks.UseTheSuggestedGameVersion(flock, check));

                StringAssert.Contains(PlaytestVersionName, System.IO.File.ReadAllText(folder + "/Flock.asset"), "The Flock settings are saved");
                CollectionAssert.AreEqual(otherBefore, System.IO.File.ReadAllBytes(folder + "/Other.asset"), "Another asset's unsaved edit is not");
                Assert.IsTrue(EditorUtility.IsDirty(other), "It is still the developer's to save or not");
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void ACheckWithNoSuggestionChangesNothing()
        {
            FlockConfigAsset flock = Flock("1.0.0", ReleaseVersionId);
            ProtokitePlaytestSetupCheck check = Check(ProtokitePlaytestSetupInput.From(Playtest(), flock, BuildTarget.StandaloneWindows64, ""), ProtokitePlaytestSetupChecks.WhichPlaytestCheck);
            Assert.IsFalse(ProtokitePlaytestSetupChecks.UseTheSuggestedGameVersion(flock, check));
            Assert.AreEqual("1.0.0", flock.gameVersion);
            Assert.AreEqual(ReleaseVersionId, flock.gameVersionId);
        }

        // ---- The menu

        [Test]
        public void TheFlockSettingsWindowOpensMenuItemsThatExist()
        {
            HashSet<string> menuItems = new HashSet<string>(typeof(ProtokitePlaytestWindow).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .SelectMany(method => method.GetCustomAttributes<MenuItem>())
                .Where(item => !item.validate)
                .Select(item => item.menuItem));
            Type installer = Type.GetType("Flock.Editor.FlockPlaytestInstaller, Flock.Editor");
            Assert.IsNotNull(installer, "Precondition: the Flock SDK's editor is in this project");
            foreach (string field in new[] { "SettingsMenuPath", "SetupWindowMenuPath" })
            {
                string path = (string)installer.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                Assert.IsNotNull(path, field);
                CollectionAssert.Contains(menuItems, path, $"The Playtesting tab's {field} opens a menu item of this package");
            }
        }

        [Test]
        public void ThePlaytestMenuHoldsTheSettingsAndTheSetupWindowOnly()
        {
            List<string> menuItems = typeof(ProtokitePlaytestWindow).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .SelectMany(method => method.GetCustomAttributes<MenuItem>())
                .Where(item => !item.validate)
                .Select(item => item.menuItem)
                .ToList();

            // Forgetting the consent answer and opening the form are buttons in the setup window, each saying what it does.
            CollectionAssert.AreEquivalent(new[] { ProtokitePlaytestSettingsMenu.MenuPath, ProtokitePlaytestWindow.MenuPath }, menuItems);
        }

        [Test]
        public void TheFormButtonsNoteSaysWhenItCanOpen()
        {
            StringAssert.Contains("Enter Play Mode", ProtokitePlaytestWindow.FeedbackFormNote(false, false, false, false));
            StringAssert.Contains("is open", ProtokitePlaytestWindow.FeedbackFormNote(true, true, true, false));
            StringAssert.Contains("Opens the form", ProtokitePlaytestWindow.FeedbackFormNote(true, true, false, false));
            StringAssert.Contains("publishes no form", ProtokitePlaytestWindow.FeedbackFormNote(true, false, false, false), "Saying why it cannot open");
            // The form does not open over the consent question, so the note says what it waits for.
            StringAssert.Contains("once it is answered", ProtokitePlaytestWindow.FeedbackFormNote(true, true, false, true));
        }

        [Test]
        public void TheSelfTestNoteSaysWhenItCanRunAndHowTheLastRunWent()
        {
            StringAssert.Contains("Enter Play Mode", ProtokitePlaytestWindow.SelfTestNote(false, false, null));
            StringAssert.Contains("Sign a player in", ProtokitePlaytestWindow.SelfTestNote(true, false, null));
            StringAssert.Contains("Running", ProtokitePlaytestWindow.SelfTestNote(true, true, null));

            ProtokitePlaytestSelfTestReport ran = new ProtokitePlaytestSelfTestReport("run1", null);
            ran.Add(new ProtokitePlaytestSelfTestStep("a", ProtokitePlaytestSelfTestOutcome.Passed, ""));
            ran.Add(new ProtokitePlaytestSelfTestStep("b", ProtokitePlaytestSelfTestOutcome.Failed, ""));
            StringAssert.Contains("Last run run1: 1 passed, 1 failed, 0 skipped", ProtokitePlaytestWindow.SelfTestNote(true, false, System.Threading.Tasks.Task.FromResult(ran)));
            StringAssert.Contains("did not run: no Flock", ProtokitePlaytestWindow.SelfTestNote(true, false,
                System.Threading.Tasks.Task.FromResult(new ProtokitePlaytestSelfTestReport("", "no Flock"))));
        }

        [Test]
        public void TheWindowsIconsAreTheEditorsOwn()
        {
            Assert.IsNotNull(EditorGUIUtility.IconContent("TestPassed").image);
            Assert.IsNotNull(EditorGUIUtility.IconContent("TestFailed").image);
        }

        [Test]
        public void TheWindowStartsCentredOnTheEditorAndFitsInsideIt()
        {
            Rect big = ProtokitePlaytestWindow.StartingPosition(new Rect(100, 50, 1920, 1080));
            Assert.AreEqual(new Rect(100 + (1920 - ProtokitePlaytestWindow.StartingWidth) / 2f, 50 + (1080 - ProtokitePlaytestWindow.StartingHeight) / 2f,
                ProtokitePlaytestWindow.StartingWidth, ProtokitePlaytestWindow.StartingHeight), big);

            Rect small = ProtokitePlaytestWindow.StartingPosition(new Rect(-800, 20, 500, 400));
            Assert.AreEqual(new Rect(-800, 20, 500, 400), small, "An editor smaller than the window gets a window its own size");
        }
    }
}
