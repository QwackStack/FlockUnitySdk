using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Flock.Exceptions;
using Flock.Http;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // The multiplayer entry point's setup: finding a matchmaking queue by name from the game's queue list, and the jitter of
    // the calls the SDK repeats. The repeating calls themselves run on real frames, in Flock.Tests.PlayMode.
    public class FlockMultiplayerSetupTests
    {
        private const string Queues = "matchmaking/queues";

        // The shape the backend answers (measured 2026-10-05 and 2026-10-07): enveloped, oldest first, every field present.
        private static string QueueList(params string[] idsAndNames)
        {
            StringBuilder rows = new StringBuilder();
            for (int i = 0; i < idsAndNames.Length; i += 2)
            {
                if (rows.Length > 0)
                    rows.Append(',');
                rows.Append("{\"id\":\"").Append(idsAndNames[i]).Append("\",\"name\":\"").Append(idsAndNames[i + 1])
                    .Append("\",\"game_id\":\"01KVCSANMD9V0CRW85ADE1WJA1\",\"min_players\":3,\"max_players\":3,")
                    .Append("\"allowed_segments\":[],\"criteria\":{},\"data\":{},")
                    .Append("\"created_at\":\"2026-10-05T17:34:09.925902\",\"updated_at\":\"2026-10-05T17:34:09.925907\"}");
            }
            return "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":[" + rows + "]}";
        }

        private static string FindId(FlockTestClient h, string name)
            => h.Run(() => h.Client.Multiplayer.FindQueueIdAsync(name, CancellationToken.None));

        private static FlockValidationException FindFails(FlockTestClient h, string name)
            => Assert.Throws<FlockValidationException>(() => FindId(h, name));

        private static void AssertOnlyTheQueueListWasAsked(FlockFakeTransport transport)
        {
            foreach (FlockHttpRequest request in transport.Requests)
                Assert.IsTrue(request.Url.Contains(Queues), "Only the queue list is asked; no search starts: " + request.Url);
        }

        [Test]
        public void ANameIsFoundFromOneReadOfTheList()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(QueueList("01M46J2MG533AXD49SBPTEPBRM", "spike-three", "01M4AFMDXH35GY8S6MWXQ7AYBD", "casual")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", FindId(h, "casual"));
                Assert.AreEqual("01M46J2MG533AXD49SBPTEPBRM", FindId(h, "spike-three"));
                Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", FindId(h, "casual"));

                Assert.AreEqual(1, transport.CountTo(Queues), "The list is read once for every name it holds");
                FlockHttpRequest read = transport.LastTo(Queues);
                Assert.AreEqual("GET", read.Method);
                StringAssert.EndsWith("/v1/matchmaking/queues?limit=200", read.Url, "The read asks for as many queues as the backend lists");
                Assert.AreEqual("test-key", read.Headers["X-Flock-API-Key"]);
            }
        }

        [Test]
        public void ANameIsFoundSignedOut_WithNoSignInSent()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "casual")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.IsFalse(h.Client.IsAuthenticated, "Precondition: nobody is signed in");

                Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", FindId(h, "casual"));
                Assert.IsFalse(transport.LastTo(Queues).Headers.ContainsKey("Authorization"));
            }
        }

        [Test]
        public void AMissingName_FailsWithTheQueueCodeAndTheName_WithoutASearch()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "casual")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                FlockValidationException refused = FindFails(h, "ranked");

                Assert.AreEqual(FlockErrorCode.MatchmakingQueueNotFound, refused.ErrorCode);
                Assert.AreEqual("matchmaking.queue_not_found", refused.Code);
                Assert.IsNull(refused.StatusCode, "Refused by the SDK, not by the server");
                StringAssert.Contains("\"ranked\"", refused.Message, "The message names the queue the game asked for");
                StringAssert.Contains("Create a queue with this name", refused.Hint);
                AssertOnlyTheQueueListWasAsked(transport);
                Assert.AreEqual(1, transport.CountTo(Queues), "A miss on the first read needs no second one");
            }
        }

        [Test]
        public void AQueueAddedAfterTheFirstRead_IsFoundByReadingAgain()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(Queues,
                FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "casual")),
                FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "casual", "01M4AFMHXE9GPHJCKV9X2GSY6N", "ranked")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", FindId(h, "casual"));
                Assert.AreEqual("01M4AFMHXE9GPHJCKV9X2GSY6N", FindId(h, "ranked"));
                Assert.AreEqual(2, transport.CountTo(Queues), "A name missing from the kept list reads the list again");

                Assert.AreEqual("01M4AFMHXE9GPHJCKV9X2GSY6N", FindId(h, "ranked"));
                Assert.AreEqual(2, transport.CountTo(Queues), "The newer list is kept");
            }
        }

        [Test]
        public void AMissAfterAFoundName_ReadsOnceMore_ThenFails()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "casual")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FindId(h, "casual");
                FindFails(h, "ranked");
                FindFails(h, "ranked");

                Assert.AreEqual(3, transport.CountTo(Queues), "Each miss costs one read of the list, never a search");
                AssertOnlyTheQueueListWasAsked(transport);
            }
        }

        [Test]
        public void ANameMatchesLetterCaseExactly_AndTheHintNamesTheOtherSpelling()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "Casual")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockValidationException refused = FindFails(h, "casual");

                StringAssert.Contains("\"Casual\"", refused.Hint);
                StringAssert.Contains("letter case", refused.Hint);
                Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", FindId(h, "Casual"));
            }
        }

        [Test]
        public void ANameWithSpaces_IsFoundAsWritten()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "Casual 2v2", "01M4AFMHXE9GPHJCKV9X2GSY6N", "Casual")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", FindId(h, "Casual 2v2"));
                FindFails(h, "Casual 2v2 ");
            }
        }

        [Test]
        public void TwoQueuesOfOneName_TheOlderIsUsed_AndOneWarningNamesBoth()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            // Two queues named alike, oldest first, as the backend listed them (measured 2026-10-07).
            transport.On(Queues, FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "mm02-dup", "01M4AFMHXE9GPHJCKV9X2GSY6N", "mm02-dup")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", FindId(h, "mm02-dup"));
                Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", FindId(h, "mm02-dup"));

                List<string> warnings = h.Logger.Warnings.Where(w => w.Contains("mm02-dup")).ToList();
                Assert.AreEqual(1, warnings.Count, "One warning a launch for a repeated name");
                StringAssert.Contains("01M4AFMDXH35GY8S6MWXQ7AYBD", warnings[0]);
                StringAssert.Contains("01M4AFMHXE9GPHJCKV9X2GSY6N", warnings[0]);
            }
        }

        [Test]
        public void AFullListThatLacksTheName_SaysTheListMayBeCut()
        {
            List<string> idsAndNames = new List<string>();
            for (int i = 0; i < 200; i++)
            {
                idsAndNames.Add("01M4AFMDXH35GY8S6MWXQ7A" + i.ToString("000"));
                idsAndNames.Add("queue-" + i);
            }
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(QueueList(idsAndNames.ToArray())));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7A199", FindId(h, "queue-199"));

                StringAssert.Contains("200 oldest", FindFails(h, "queue-200").Hint);
            }
        }

        [Test]
        public void AListOneShortOfFull_DoesNotSayItMayBeCut()
        {
            List<string> idsAndNames = new List<string>();
            for (int i = 0; i < 199; i++)
            {
                idsAndNames.Add("01M4AFMDXH35GY8S6MWXQ7A" + i.ToString("000"));
                idsAndNames.Add("queue-" + i);
            }
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(QueueList(idsAndNames.ToArray())));
            using (FlockTestClient h = FlockTestClient.Create(transport))
                StringAssert.DoesNotContain("oldest", FindFails(h, "queue-200").Hint);
        }

        [Test]
        public void AFailedRead_IsNotKept_SoTheNextFindReadsAgain()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(Queues,
                FlockFakeTransport.Status(503, "{}"),
                FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "casual")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockNetworkException failed = Assert.Throws<FlockNetworkException>(() => FindId(h, "casual"));
                Assert.AreEqual(503, failed.StatusCode, "The read's own failure, not a missing queue");

                Assert.AreEqual("01M4AFMDXH35GY8S6MWXQ7AYBD", FindId(h, "casual"));
                Assert.AreEqual(2, transport.CountTo(Queues));
            }
        }

        [Test]
        public void AnEmptyName_IsRefusedWithoutARequest()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "casual")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.Throws<FlockValidationException>(() => FindId(h, ""));
                Assert.Throws<FlockValidationException>(() => FindId(h, null));
                Assert.AreEqual(0, transport.Requests.Count);
            }
        }

        [Test]
        public void AFindAlreadyGivenUp_AsksNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Queues, FlockFakeTransport.Ok(QueueList("01M4AFMDXH35GY8S6MWXQ7AYBD", "casual")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            using (CancellationTokenSource givenUp = new CancellationTokenSource())
            {
                givenUp.Cancel();

                Assert.Catch<OperationCanceledException>(() => h.Run(() => h.Client.Multiplayer.FindQueueIdAsync("casual", givenUp.Token)));
                Assert.AreEqual(0, transport.Requests.Count);
            }
        }

        [Test]
        public void ACallStoppedByAHandlerEarlierInTheSameFrame_SendsNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => FlockFakeTransport.Ok("{\"n\":1}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockRepeatingCalls calls = new FlockRepeatingCalls(h.Client, () => 0.5);
                string url = h.Client.GetVersionedApiUrl();
                FlockRepeatingCall first = null;
                FlockRepeatingCall second = null;
                // Each answer stops the other call; the fake answers at once, so whichever goes first hands its answer over in this frame.
                first = calls.Start<Dictionary<string, object>>("first", TimeSpan.FromMilliseconds(10),
                    _ => FlockHttpClient.GetAsync<Dictionary<string, object>>(url + "/probe/first", h.Client.GetBaseHeaders(), CancellationToken.None),
                    _ => second.Stop(), null);
                second = calls.Start<Dictionary<string, object>>("second", TimeSpan.FromMilliseconds(10),
                    _ => FlockHttpClient.GetAsync<Dictionary<string, object>>(url + "/probe/second", h.Client.GetBaseHeaders(), CancellationToken.None),
                    _ => first.Stop(), null);
                Thread.Sleep(50);

                calls.SendThoseDue();

                Assert.AreEqual(1, transport.CountTo("probe/first") + transport.CountTo("probe/second"),
                    "Both were due, and the one stopped by the other's answer sends nothing");
            }
        }

        [Test]
        public void TheEntryPointIsOnTheClient()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.IsNotNull(h.Client.Multiplayer);
                Assert.AreSame(h.Client.Multiplayer, ((Flock.Interfaces.IFlockClient)h.Client).Multiplayer);
            }
        }

        [TestCase(0.0, 0.75)]
        [TestCase(0.5, 1.0)]
        [TestCase(1.0, 1.25)]
        public void AWaitIsItsIntervalJitteredByAQuarterEitherWay(double roll, double fraction)
        {
            TimeSpan wait = FlockRepeatingCalls.JitteredWait(TimeSpan.FromSeconds(20), roll);
            Assert.AreEqual(20 * fraction, wait.TotalSeconds, 0.0001);
        }

        [Test]
        public void TheSharedJitterVaries()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockRepeatingCalls calls = h.Client.Multiplayer.RepeatingCalls;
                List<double> firstWaits = new List<double>();
                for (int i = 0; i < 8; i++)
                {
                    FlockRepeatingCall call = calls.Start<bool>("call-" + i, TimeSpan.FromSeconds(1000), _ => null, null, null);
                    firstWaits.Add(call.DueAt.TotalSeconds);
                    call.Stop();
                }

                // Eight waits a thousand seconds long from one source that varies differ by far more than the moments between them.
                Assert.Greater(firstWaits.Max() - firstWaits.Min(), 10.0, "The default jitter source gives different waits");
                Assert.IsTrue(firstWaits.All(w => w >= 750 && w <= 1250.5), "Each wait stays within a quarter of the interval");
            }
        }
    }
}
