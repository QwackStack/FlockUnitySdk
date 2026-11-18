using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Samples;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    /// <summary>The booth leaderboard sample against a local backend, judged on its tables afterwards. Run by hand:
    /// FLOCK_BOOTH_LEADERBOARD_LIVE names the file Qwacks/Libraries/Unity/booth-leaderboard/run_booth_leaderboard_live.py writes.</summary>
    public class FlockBoothLeaderboardLiveTests
    {
        private GameObject _host;
        private FlockBoothLeaderboard _board;
        private JObject _live;
        private JObject _results;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            string livePath = Environment.GetEnvironmentVariable("FLOCK_BOOTH_LEADERBOARD_LIVE");
            if (string.IsNullOrEmpty(livePath) || !File.Exists(livePath))
                Assert.Ignore("FLOCK_BOOTH_LEADERBOARD_LIVE names no file.");
            _live = JObject.Parse(File.ReadAllText(livePath));
            string resultsPath = (string)_live["results"];
            _results = File.Exists(resultsPath) ? JObject.Parse(File.ReadAllText(resultsPath)) : new JObject { ["run"] = _live["run"] };
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockInitConfig config = Resources.Load<FlockConfigAsset>("FlockConfig").ToInitConfig();
            StringAssert.StartsWith("http://127.0.0.1:8001", config.ApiUrl, "The local stack only, never a shared backend.");
            config.EnableOfflineCache = false;
            FlockClient.Create(config, new RecordingFlockLogger());
            _host = new GameObject("Booth leaderboard test");
            _board = _host.AddComponent<FlockBoothLeaderboard>();
            _board.ScoreTemplateId = (string)_live["template_id"];
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            if (_results != null)
                File.WriteAllText((string)_live["results"], _results.ToString());
            if (_host != null)
                UnityEngine.Object.DestroyImmediate(_host);
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
        }

        private string Name(string who) => who + (string)_live["run"];

        // Every entry the tables are judged on: its entry name and the time it should hold.
        private void Expect(string entryName, double seconds) => _results[entryName] = seconds;

        private static IEnumerator Await(Task task)
        {
            yield return FlockTestWait.UntilDone(task, 30f);
            if (task.IsFaulted)
                throw task.Exception.GetBaseException();
        }

        private static FlockException FailureOf(Task task) => task.Exception?.GetBaseException() as FlockException;

        [UnityTest, Explicit("Hits the local Flock backend; run by hand through run_booth_leaderboard_live.py.")]
        public IEnumerator Live_TheSameNameTwice_GivesTwoEntriesWithTheirOwnTimes()
        {
            Task<string> first = _board.RecordTimeAsync(Name("Ahmed"), 83.5);
            yield return Await(first);
            Task<string> second = _board.RecordTimeAsync("  " + Name("Ahmed") + " ", 71.25);
            yield return Await(second);

            StringAssert.IsMatch("^" + Name("Ahmed") + "#[0-9]{4}$", first.Result);
            StringAssert.IsMatch("^" + Name("Ahmed") + "#[0-9]{4}$", second.Result, "The typed name is trimmed.");
            Assert.AreNotEqual(first.Result, second.Result);
            Expect(first.Result, 83.5);
            Expect(second.Result, 71.25);

            Task<Standings> fastest = _board.FastestTimesAsync(200);
            yield return Await(fastest);
            List<string> names = fastest.Result.Items.ConvertAll(entry => entry.PlayerName);
            CollectionAssert.Contains(names, first.Result);
            CollectionAssert.Contains(names, second.Result);
            Assert.Less(names.IndexOf(second.Result), names.IndexOf(first.Result), "The faster time ranks first.");
        }

        [UnityTest, Explicit("Hits the local Flock backend; run by hand through run_booth_leaderboard_live.py.")]
        public IEnumerator Live_BothPlayersRecordedAtOnce()
        {
            Task<string> sara = _board.RecordTimeAsync(Name("Sara"), 60.5);
            Task<string> omar = _board.RecordTimeAsync(Name("Omar"), 90.75);
            yield return Await(Task.WhenAll(sara, omar));

            StringAssert.StartsWith(Name("Sara") + "#", sara.Result);
            StringAssert.StartsWith(Name("Omar") + "#", omar.Result);
            Expect(sara.Result, 60.5);
            Expect(omar.Result, 90.75);
        }

        [UnityTest, Explicit("Hits the local Flock backend; run by hand through run_booth_leaderboard_live.py.")]
        public IEnumerator Live_TheSameNameAtOnce_GivesTwoEntries()
        {
            Task<string> one = _board.RecordTimeAsync(Name("Lina"), 50);
            Task<string> two = _board.RecordTimeAsync(Name("Lina"), 55);
            yield return Await(Task.WhenAll(one, two));

            Assert.AreNotEqual(one.Result, two.Result);
            Expect(one.Result, 50);
            Expect(two.Result, 55);
        }

        [UnityTest, Explicit("Hits the local Flock backend; run by hand through run_booth_leaderboard_live.py.")]
        public IEnumerator Live_ATakenEntryName_PicksANewCode()
        {
            Queue<int> codes = new Queue<int>(new[] { 42, 42, 5151 });
            _board.PickCode = () => codes.Dequeue();

            Task<string> first = _board.RecordTimeAsync(Name("Clash"), 40);
            yield return Await(first);
            Task<string> second = _board.RecordTimeAsync(Name("Clash"), 45);
            yield return Await(second);

            Assert.AreEqual(Name("Clash") + "#0042", first.Result, "Four digits, no space before #.");
            Assert.AreEqual(Name("Clash") + "#5151", second.Result, "#0042 was taken, so a new code was picked.");
            Expect(first.Result, 40);
            Expect(second.Result, 45);
        }

        [UnityTest, Explicit("Hits the local Flock backend; run by hand through run_booth_leaderboard_live.py.")]
        public IEnumerator Live_EveryCodeTaken_FailsWithTheServersCode()
        {
            _board.PickCode = () => 42;
            Task<string> taken = _board.RecordTimeAsync(Name("Full"), 30);
            yield return Await(taken);
            Expect(taken.Result, 30);

            int asked = 0;
            _board.PickCode = () => { asked++; return 42; };
            Task<string> refused = _board.RecordTimeAsync(Name("Full"), 31);
            yield return FlockTestWait.UntilDone(refused, 30f);

            Assert.AreEqual(FlockErrorCode.PlayerNameAlreadyRegistered, FailureOf(refused)?.ErrorCode);
            Assert.IsFalse(string.IsNullOrEmpty(FailureOf(refused).Hint));
            Assert.AreEqual(5, asked, "Five codes are tried, then the server's refusal is given.");
        }

        [Test, Explicit("Hits the local Flock backend; run by hand through run_booth_leaderboard_live.py.")]
        public void Live_AnEmptyName_IsRefusedBeforeAnythingIsSent()
        {
            Task<string> refused = _board.RecordTimeAsync("   ", 10);
            Assert.IsTrue(refused.IsFaulted, "Refused at once, before any request.");
            Assert.IsInstanceOf<FlockValidationException>(FailureOf(refused));
            StringAssert.Contains("type a name", FailureOf(refused).Hint);
        }

        [UnityTest, Explicit("Hits the local Flock backend; run by hand through run_booth_leaderboard_live.py.")]
        public IEnumerator Live_AnUnknownBoardOrTemplate_FailsWithAFix()
        {
            _board.BoardName = "NoSuchBoard" + (string)_live["run"];
            Task<Standings> noBoard = _board.FastestTimesAsync();
            yield return FlockTestWait.UntilDone(noBoard, 30f);
            Assert.AreEqual(FlockErrorCode.LeaderboardNotFound, FailureOf(noBoard)?.ErrorCode);
            Assert.IsFalse(string.IsNullOrEmpty(FailureOf(noBoard).Hint));

            _board.ScoreTemplateId = "01ZZZZZZZZZZZZZZZZZZZZZZZZ";
            Task<string> noTemplate = _board.RecordTimeAsync(Name("Nobody"), 20);
            yield return FlockTestWait.UntilDone(noTemplate, 30f);
            Assert.IsInstanceOf<FlockValidationException>(FailureOf(noTemplate));
            StringAssert.Contains("gameplay", FailureOf(noTemplate).Hint);

            _board.ScoreTemplateId = "";
            Task<string> noId = _board.RecordTimeAsync(Name("Nobody"), 20);
            Assert.IsTrue(noId.IsFaulted, "Refused at once, before any request.");
            StringAssert.Contains("MatchTimeTemplate.SourceId", FailureOf(noId).Hint);
        }
    }
}
