using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using Protokite.Playtest.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    /// <summary>What the window asks Flock about the Game Version, against a Flock of the test's own that answers by the name or ID each request carries.</summary>
    public class ProtokitePlaytestGameVersionLookupTests
    {
        private const string ByName = "/v1/game_version/by-name/";
        private const string ById = "/v1/game_version";

        private static readonly string PlaytestVersionName = ProtokitePlaytestSetupChecksTests.PlaytestVersionName;
        private static readonly string PlaytestVersionId = ProtokitePlaytestSetupChecksTests.PlaytestVersionId;
        private static readonly string ReleaseVersionId = ProtokitePlaytestSetupChecksTests.ReleaseVersionId;
        private static readonly string OtherGamesVersionId = ProtokitePlaytestSetupChecksTests.Id("EF");

        private readonly List<Object> _made = new List<Object>();
        private FlockFakeTransport _flock;

        // This game's versions by name, and every game's by ID: Flock names an ID of any game, and resolves a name in this game only.
        private Dictionary<string, string> _thisGamesVersions;
        private Dictionary<string, string> _everyGamesVersions;

        [SetUp]
        public void SetUp()
        {
            _thisGamesVersions = new Dictionary<string, string> { { PlaytestVersionName, PlaytestVersionId }, { "1.0.0", ReleaseVersionId } };
            _everyGamesVersions = new Dictionary<string, string>
            {
                { PlaytestVersionId, PlaytestVersionName }, { ReleaseVersionId, "1.0.0" }, { OtherGamesVersionId, "pt-" + ProtokitePlaytestSetupChecksTests.Id("99") }
            };
            _flock = new FlockFakeTransport().Default(Answer);
            FlockHttpClient.Configure(_flock);
        }

        [TearDown]
        public void TearDown()
        {
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            foreach (Object made in _made)
                Object.DestroyImmediate(made);
            _made.Clear();
        }

        private FlockHttpResponse Answer(FlockHttpRequest request)
        {
            if (!request.Headers.TryGetValue("X-Flock-API-Key", out string key) || key != "key-1")
                return FlockFakeTransport.Coded(401, "game.invalid_api_key");
            int byName = request.Url.IndexOf(ByName, StringComparison.Ordinal);
            if (byName >= 0)
            {
                string name = Uri.UnescapeDataString(request.Url.Substring(byName + ByName.Length));
                return _thisGamesVersions.TryGetValue(name, out string id) ? Version(id, name) : FlockFakeTransport.Coded(404, "game_version.game_version_by_name_not_found");
            }
            if (request.Url.EndsWith(ById, StringComparison.Ordinal))
            {
                request.Headers.TryGetValue("X-Game-Version-ID", out string id);
                return id != null && _everyGamesVersions.TryGetValue(id, out string name) ? Version(id, name) : FlockFakeTransport.Coded(404, "game_version.game_version_not_found");
            }
            return FlockFakeTransport.Status(404, "{\"detail\":\"Not Found\"}");
        }

        // The route's own body, copied off the local Flock (2026-09-30): the envelope, and a result carrying features the spec leaves out.
        private static FlockHttpResponse Version(string id, string name)
            => FlockFakeTransport.Ok("{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":{\"id\":\"" + id + "\",\"name\":\"" + name +
                                     "\",\"release_type\":\"preview\",\"env\":\"dev\",\"features\":{\"heavy_analytics\":true,\"video_recording\":true}," +
                                     "\"created_at\":\"2026-09-30T08:00:00.123456\",\"updated_at\":\"2026-09-30T08:00:00.123456\"}}");

        private ProtokitePlaytestSetupInput Project(string gameVersion, string gameVersionId, string apiKey = "key-1")
        {
            FlockConfigAsset flock = ScriptableObject.CreateInstance<FlockConfigAsset>();
            _made.Add(flock);
            flock.apiUrl = "https://flock.example.com/";
            flock.apiKey = apiKey;
            flock.gameVersion = gameVersion;
            flock.gameVersionId = gameVersionId;
            return ProtokitePlaytestSetupInput.From(null, flock, BuildTarget.StandaloneWindows64, "");
        }

        // The fake answers at once, so the whole question has run by the time it returns.
        private static ProtokitePlaytestGameVersionAnswer Ask(ProtokitePlaytestSetupInput input)
        {
            Task<ProtokitePlaytestGameVersionAnswer> asking = ProtokitePlaytestGameVersionLookup.AskAsync(input, CancellationToken.None);
            Assert.IsTrue(asking.IsCompleted, "Precondition: the fake answered at once");
            return asking.Result;
        }

        [Test]
        public void APlaytestNameIsResolvedByNameWithTheSettingsOwnKey()
        {
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project(PlaytestVersionName, PlaytestVersionId));
            Assert.IsNull(answer.Problem);
            Assert.IsTrue(answer.NameFound);
            Assert.AreEqual(PlaytestVersionId, answer.NameResolvesTo);
            Assert.IsNull(answer.PastedId, "Nothing looks pasted when the name resolves to the ID the settings hold");
            Assert.AreEqual(1, _flock.Requests.Count, "One question is enough");
            Assert.AreEqual("https://flock.example.com/v1/game_version/by-name/" + PlaytestVersionName, _flock.Requests[0].Url);
            Assert.AreEqual("GET", _flock.Requests[0].Method);
        }

        [Test]
        public void AnIdPastedIntoGameVersionIsNamedAndTheNameResolvedBack()
        {
            ProtokitePlaytestSetupInput input = Project(PlaytestVersionId, ReleaseVersionId);
            ProtokitePlaytestGameVersionAnswer answer = Ask(input);
            Assert.IsNull(answer.Problem);
            Assert.IsFalse(answer.NameFound, "No version is named with an ID");
            Assert.AreEqual(PlaytestVersionId, answer.PastedId);
            Assert.AreEqual(PlaytestVersionName, answer.NameOfPastedId);
            Assert.IsTrue(answer.NameOfPastedIdResolvesBack);
            Assert.AreEqual(PlaytestVersionName, answer.SuggestedName);

            FlockHttpRequest byId = _flock.Requests.Single(request => request.Url.EndsWith(ById, StringComparison.Ordinal));
            Assert.AreEqual(PlaytestVersionId, byId.Headers["X-Game-Version-ID"], "The ID pasted into Game Version is the one asked about, not the ID the settings hold");
            Assert.AreEqual("key-1", byId.Headers["X-Flock-API-Key"]);
            Assert.IsTrue(_flock.Requests.Any(request => request.Url.EndsWith(ByName + PlaytestVersionName, StringComparison.Ordinal)), "The name was resolved back");

            ProtokitePlaytestSetupCheck check = ProtokitePlaytestSetupChecks.Evaluate(input, answer).Single(c => c.Id == ProtokitePlaytestSetupChecks.GameVersionCheck);
            Assert.AreEqual(PlaytestVersionName, check.SuggestedGameVersion);
            Assert.AreEqual(PlaytestVersionId, check.SuggestedGameVersionId);
        }

        [Test]
        public void APastedIdWithSpacesAroundItIsAskedAboutWithout()
        {
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project(" " + PlaytestVersionId + " ", ReleaseVersionId));
            Assert.AreEqual(PlaytestVersionId, answer.PastedId);
            Assert.AreEqual(PlaytestVersionName, answer.SuggestedName);
        }

        [Test]
        public void APastedIdInLowerCaseIsAskedAboutAsFlockWritesIt()
        {
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project(PlaytestVersionId.ToLowerInvariant(), ReleaseVersionId));
            Assert.AreEqual(PlaytestVersionId, _flock.Requests.Single(request => request.Url.EndsWith(ById, StringComparison.Ordinal)).Headers["X-Game-Version-ID"]);
            Assert.AreEqual(PlaytestVersionName, answer.SuggestedName);
            Assert.AreEqual(PlaytestVersionId, answer.PastedId, "And the ID suggested with it is the one Flock resolves the name to");
        }

        [Test]
        public void A404ThatIsNotTheRoutesOwnIsAProblemNotANo()
        {
            // A wrong API URL, or something on the way: a 404 without the route's code says nothing about the version.
            _flock = new FlockFakeTransport().Default(request => FlockFakeTransport.Status(404, "{\"detail\":\"Not Found\"}"));
            FlockHttpClient.Configure(_flock);
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project(PlaytestVersionName, PlaytestVersionId));
            StringAssert.StartsWith("Flock answered HTTP 404.", answer.Problem);
            Assert.IsFalse(answer.NameFound);
            Assert.IsNull(answer.PastedId, "Nothing more is asked once one question failed");
            Assert.AreEqual(1, _flock.Requests.Count);
        }

        [Test]
        public void AnotherGamesIdIsNamedButNothingIsSuggested()
        {
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project(OtherGamesVersionId, ReleaseVersionId));
            Assert.IsTrue(answer.PastedIdFound, "Flock names an ID of any game");
            Assert.IsNotNull(answer.NameOfPastedId);
            Assert.IsFalse(answer.NameOfPastedIdResolvesBack, "That name is no version of this game");
            Assert.IsNull(answer.SuggestedName);
        }

        [Test]
        public void AnotherGamesIdWhoseNameThisGameAlsoHasIsSaidToBeAnotherGames()
        {
            // As measured on the local Flock: another game has a version named 1.0.0, and so does this game, with another ID.
            string otherGamesRelease = ProtokitePlaytestSetupChecksTests.Id("F1");
            _everyGamesVersions[otherGamesRelease] = "1.0.0";
            ProtokitePlaytestSetupInput input = Project(otherGamesRelease, ReleaseVersionId);
            ProtokitePlaytestGameVersionAnswer answer = Ask(input);
            Assert.IsTrue(answer.PastedIdFound, "Flock names an ID of any game");
            Assert.AreEqual("1.0.0", answer.NameOfPastedId);
            Assert.IsFalse(answer.NameOfPastedIdResolvesBack, "1.0.0 in this game is another version, so the name is not this ID's here");
            StringAssert.Contains("belongs to another game",
                ProtokitePlaytestSetupChecks.Evaluate(input, answer).Single(c => c.Id == ProtokitePlaytestSetupChecks.GameVersionCheck).Detail);
        }

        [Test]
        public void AnIdFlockDoesNotKnowIsSaidToBeNoVersionsId()
        {
            ProtokitePlaytestSetupInput input = Project(ProtokitePlaytestSetupChecksTests.Id("42"), ReleaseVersionId);
            ProtokitePlaytestGameVersionAnswer answer = Ask(input);
            Assert.IsNull(answer.Problem, "A 404 is an answer, not a problem");
            Assert.IsFalse(answer.PastedIdFound);
            Assert.IsNull(answer.SuggestedName);
            StringAssert.Contains("Flock has no version with that ID",
                ProtokitePlaytestSetupChecks.Evaluate(input, answer).Single(c => c.Id == ProtokitePlaytestSetupChecks.GameVersionCheck).Detail);
        }

        [Test]
        public void AnIdPastedOverTheResolvedIdIsNamed()
        {
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project("1.0.0", PlaytestVersionId));
            Assert.IsTrue(answer.NameFound);
            Assert.AreEqual(ReleaseVersionId, answer.NameResolvesTo);
            Assert.AreEqual(PlaytestVersionId, answer.PastedId, "The ID the settings hold, which the name does not resolve to");
            Assert.AreEqual(PlaytestVersionName, answer.SuggestedName);
        }

        [Test]
        public void ANameFlockHasNoVersionOfIsAnAnswer()
        {
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project("pt-" + ProtokitePlaytestSetupChecksTests.Id("77"), ""));
            Assert.IsNull(answer.Problem);
            Assert.IsFalse(answer.NameFound);
            Assert.IsNull(answer.PastedId, "No ID is held, and the name is no ID");
        }

        [Test]
        public void ARefusedKeyIsSaidToBeTheKey()
        {
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project(PlaytestVersionName, PlaytestVersionId, apiKey: "wrong-key"));
            Assert.AreEqual("Flock refused the API key in Flock > Settings (HTTP 401).", answer.Problem);
            Assert.AreEqual("wrong-key", _flock.Requests[0].Headers["X-Flock-API-Key"], "The counter-case sent the wrong key, and was refused for it");
            Assert.IsNull(answer.SuggestedName);
        }

        [Test]
        public void A403FromSomethingOnTheWayIsNotBlamedOnTheKey()
        {
            _flock = new FlockFakeTransport().Default(request => FlockFakeTransport.Status(403, "<html>Forbidden</html>"));
            FlockHttpClient.Configure(_flock);
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project(PlaytestVersionName, PlaytestVersionId));
            StringAssert.StartsWith("Flock answered HTTP 403.", answer.Problem);
            StringAssert.DoesNotContain("API key", answer.Problem);
        }

        [Test]
        public void ASuccessWithoutAVersionIsAProblemNotANo()
        {
            _flock = new FlockFakeTransport().Default(request => FlockFakeTransport.Ok("{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":null}"));
            FlockHttpClient.Configure(_flock);
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project(PlaytestVersionName, PlaytestVersionId));
            Assert.AreEqual("Flock's answer could not be read. Flock answered without a Game Version", answer.Problem, "Something answered, so it is not said to be out of reach");
            Assert.IsFalse(answer.NameFound);
        }

        [Test]
        public void FlockOutOfReachIsAProblem()
        {
            _flock.GoOffline();
            ProtokitePlaytestGameVersionAnswer answer = Ask(Project(PlaytestVersionName, PlaytestVersionId));
            StringAssert.StartsWith("Flock could not be reached.", answer.Problem);
        }

        [Test]
        public void NothingIsAskedWithoutAnUrlAKeyAndAGameVersion()
        {
            ProtokitePlaytestGameVersionQuestions questions = new ProtokitePlaytestGameVersionQuestions();
            questions.AskIfChanged(Project(PlaytestVersionName, PlaytestVersionId, apiKey: " "));
            questions.AskIfChanged(Project("", PlaytestVersionId));
            Assert.IsEmpty(_flock.Requests);
            Assert.IsFalse(questions.IsAsking);
        }

        [Test]
        public void TheSameSettingsAreAskedAboutOnceUntilAskedAgain()
        {
            ProtokitePlaytestSetupInput input = Project(PlaytestVersionName, PlaytestVersionId);
            ProtokitePlaytestGameVersionQuestions questions = new ProtokitePlaytestGameVersionQuestions();
            questions.AskIfChanged(input);
            questions.AskIfChanged(Project(PlaytestVersionName, PlaytestVersionId));
            Assert.AreEqual(1, _flock.Requests.Count, "A redraw with the same settings asks nothing");
            Assert.IsNotNull(questions.Answer);
            questions.AskIfChanged(input, askAgain: true);
            Assert.AreEqual(2, _flock.Requests.Count, "Check Again asks again");
        }

        [UnityTest]
        public IEnumerator AnAnswerForSettingsThatHaveChangedSinceIsDropped()
        {
            _flock.GateNext(ByName + PlaytestVersionName);
            ProtokitePlaytestGameVersionQuestions questions = new ProtokitePlaytestGameVersionQuestions();
            int answered = 0;
            questions.Answered += () => answered++;

            questions.AskIfChanged(Project(PlaytestVersionName, PlaytestVersionId));
            Task held = questions.LastQuestionForTesting;
            Assert.IsTrue(questions.IsAsking, "Precondition: the first answer is held");
            // The developer changes Game Version while Flock is still answering.
            ProtokitePlaytestSetupInput changed = Project("1.0.0", ReleaseVersionId);
            questions.AskIfChanged(changed);
            Assert.AreEqual(ProtokitePlaytestGameVersionLookup.KeyFor(changed), questions.Answer?.AskedFor, "Precondition: the new question was answered");
            Assert.AreEqual(1, answered);

            _flock.ReleaseGate();
            yield return FlockTestWait.Until(() => held.IsCompleted, "the held answer landed");
            Assert.AreEqual(1, answered, "The held answer, landing last, is not kept");
            Assert.AreEqual(ProtokitePlaytestGameVersionLookup.KeyFor(changed), questions.Answer.AskedFor, "The answer kept is the latest question's");
            Assert.IsFalse(questions.IsAsking);
        }

        [UnityTest]
        public IEnumerator ClosingTheWindowDropsTheAnswerOnItsWay()
        {
            _flock.GateNext(ByName + PlaytestVersionName);
            ProtokitePlaytestGameVersionQuestions questions = new ProtokitePlaytestGameVersionQuestions();
            int answered = 0;
            questions.Answered += () => answered++;
            questions.AskIfChanged(Project(PlaytestVersionName, PlaytestVersionId));
            Task held = questions.LastQuestionForTesting;
            questions.Stop();
            _flock.ReleaseGate();
            yield return FlockTestWait.Until(() => held.IsCompleted, "the held answer landed");
            Assert.AreEqual(0, answered);
            Assert.IsNull(questions.Answer);
        }
    }
}
