using System.Collections.Generic;
using Flock.Exceptions;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // A route with nothing to read succeeds on a 2xx with no body or a JSON body; a read still needs its body.
    public class FlockEmptySuccessTests
    {
        private const string Url = "https://test.invalid/v1/no-body-route";

        private static FlockHttpResponse NoContent() => FlockFakeTransport.Status(204, null);

        [TestCase(204, null)]
        [TestCase(200, "")]
        [TestCase(201, "{\"ok\":true}")]
        public void ASendWithNothingToReadSucceedsOnEvery2xx(int status, string body)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => FlockFakeTransport.Status(status, body));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.DoesNotThrow(() => h.Run(() => FlockHttpClient.PostAsync(Url, new { probe = true })), "POST");
                Assert.DoesNotThrow(() => h.Run(() => FlockHttpClient.PutAsync(Url, new { probe = true })), "PUT");
                Assert.DoesNotThrow(() => h.Run(() => FlockHttpClient.PatchAsync(Url, new { probe = true })), "PATCH");
                Assert.DoesNotThrow(() => h.Run(() => FlockHttpClient.DeleteAsync(Url)), "DELETE");
                Assert.AreEqual(4, transport.CountTo("no-body-route"), "Each was sent once");
            }
        }

        [Test]
        public void ASendWithNothingToReadFailsOnA2xxThatIsNotJson()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => FlockFakeTransport.Status(200, "<html>captive portal</html>"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockSerializationException error = Assert.Throws<FlockSerializationException>(
                    () => h.Run(() => FlockHttpClient.PostAsync(Url, new { probe = true })));
                Assert.AreEqual("<html>captive portal</html>", error.Body);
                Assert.Throws<FlockSerializationException>(() => h.Run(() => FlockHttpClient.PatchAsync(Url, new { probe = true })), "PATCH");
            }
        }

        [Test]
        public void ASendWithNothingToReadStillSendsItsBody()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => NoContent());
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.Run(() => FlockHttpClient.PatchAsync(Url, new { ended_at = "2026-09-24T00:00:00Z" }));
                FlockHttpRequest sent = transport.LastTo("no-body-route");
                Assert.AreEqual("PATCH", sent.Method);
                Assert.AreEqual("{\"ended_at\":\"2026-09-24T00:00:00Z\"}", sent.JsonBody);
            }
        }

        [Test]
        public void AReadStillFailsOnA2xxWithNoBody()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => NoContent());
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockSerializationException error = Assert.Throws<FlockSerializationException>(
                    () => h.Run(() => FlockHttpClient.PostAsync<Dictionary<string, object>>(Url, new { probe = true })));
                StringAssert.Contains("Empty response", error.Message);
            }
        }

        [Test]
        public void ASendWithNothingToReadKeepsEveryRefusal()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                transport.Default(request => FlockFakeTransport.Status(500, ""));
                Assert.Throws<FlockNetworkException>(() => h.Run(() => FlockHttpClient.PostAsync(Url, new { })), "500");
                transport.Default(request => FlockFakeTransport.Coded(422, "game_command.template_validation_failed"));
                Assert.Throws<FlockValidationException>(() => h.Run(() => FlockHttpClient.PostAsync(Url, new { })), "422");
                transport.Default(request => FlockFakeTransport.Status(401, ""));
                Assert.Throws<FlockAuthException>(() => h.Run(() => FlockHttpClient.PostAsync(Url, new { })), "401");
                transport.Default(request => FlockFakeTransport.Offline());
                Assert.Throws<FlockNetworkException>(() => h.Run(() => FlockHttpClient.PostAsync(Url, new { })), "never reached");
                transport.Default(request => FlockFakeTransport.Timeout());
                Assert.Throws<FlockNetworkException>(() => h.Run(() => FlockHttpClient.PostAsync(Url, new { })), "timed out");
            }
        }

        [Test]
        public void ATransactionIsRecordedWhenTheServerAnswers204()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.AnalyticsTransactions, NoContent());
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.DoesNotThrow(() => h.Run(() => h.Client.Analytics.RecordTransactionAsync(1.0)));
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.AnalyticsTransactions));
            }
        }

        [Test]
        public void ATransactionTheServerRefusesStillFails()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.AnalyticsTransactions, FlockFakeTransport.Status(500, ""));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.Throws<FlockNetworkException>(() => h.Run(() => h.Client.Analytics.RecordTransactionAsync(1.0)));
            }
        }

        [Test]
        public void QueuedLogEventsAreSentNotDroppedWhenTheServerAnswers204()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => NoContent());
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.Client.Analytics.LogException("P-8 empty success", "at Probe.Run()");
                h.Run(() => h.Client.Analytics.FlushAsync());
                FlockHttpRequest batch = transport.LastTo(FlockEndpoints.LogEvent);
                Assert.IsNotNull(batch, "The batch was sent");
                StringAssert.Contains("P-8 empty success", batch.JsonBody);
                Assert.IsFalse(h.Logger.Warnings.Exists(line => line.Contains("dropped")), "Nothing was dropped: " + string.Join(" | ", h.Logger.Warnings));
            }
        }

        [Test]
        public void QueuedLogEventsAreKeptWhenTheAnswerIsNotTheServers()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => FlockFakeTransport.Status(200, "<html>captive portal</html>"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.Client.Analytics.LogException("kept past a captive portal", "at Probe.Run()");
                h.Run(() => h.Client.Analytics.FlushAsync());
                Assert.IsTrue(transport.AllTo(FlockEndpoints.LogEvent).Exists(r => r.JsonBody.Contains("kept past a captive portal")), "The batch was sent");
                Assert.IsFalse(h.Logger.Warnings.Exists(line => line.Contains("dropped")), "Nothing was dropped: " + string.Join(" | ", h.Logger.Warnings));

                transport.Default(request => NoContent());
                h.Run(() => h.Client.Analytics.FlushAsync());
                Assert.AreEqual(2, transport.AllTo(FlockEndpoints.LogEvent).FindAll(r => r.JsonBody.Contains("kept past a captive portal")).Count,
                    "The same entry was sent again once the server answered");
            }
        }

        [Test]
        public void ASessionEndIsDeliveredWhenTheServerAnswers204()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => request.Method == "PATCH"
                ? NoContent()
                : FlockFakeTransport.Ok("{\"session_id\":\"srv-session-1\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-1");
                // The session exists once analytics is initialized, which sign-in does in a game.
                h.Run(() => h.Client.Analytics.InitializeAsync(System.Threading.CancellationToken.None));
                h.Run(() => h.Client.Analytics.StartSessionAsync());
                h.Run(() => h.Client.Analytics.EndSessionAsync());
                Assert.AreEqual(1, transport.AllTo(FlockEndpoints.AnalyticsSessionById("srv-session-1")).FindAll(r => r.Method == "PATCH").Count, "The end was sent once");
                Assert.IsFalse(h.Logger.Warnings.Exists(line => line.Contains("dropped") || line.Contains("Failed to end session")),
                    "The end was not dropped or failed: " + string.Join(" | ", h.Logger.Warnings));
                Assert.IsTrue(h.Logger.Infos.Exists(line => line.Contains("Session ended on server")), "The end was delivered");
            }
        }
    }
}
