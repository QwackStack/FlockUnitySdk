using System;
using System.Collections.Generic;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // NotificationProvider: schedule a dashboard template for the signed-in player, and cancel it.
    // Both routes are bearer-only and guard before the request. Schedule is deliberately NOT retried on an
    // ambiguous failure — a duplicate reminder is unreachable afterwards, since the game only ever learns one
    // scheduled id and there is no route to list what a player has pending.
    public class FlockNotificationProviderTests
    {
        // Always in the future, never a fixed date. TrackPending persists the server-echoed deliver_at and
        // LoadPendingSchedules drops whatever has already elapsed, so a hardcoded date empties the pending
        // list the day it passes and every "still pending" assertion silently reads 0. Declared before the
        // bodies below — static readonly fields initialise in textual order.
        private static readonly DateTime DeliverAt =
            new DateTime(DateTime.UtcNow.Year + 1, 8, 5, 12, 0, 0, DateTimeKind.Utc);

        private static readonly string DeliverAtWire = DeliverAt.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";

        private static readonly string ScheduledBody =
            "{\"result\":{\"id\":\"sched-1\",\"game_id\":\"g\",\"studio_id\":\"s\",\"player_id\":\"player-a\"," +
            "\"template_id\":\"tmpl-1\",\"variables\":{},\"channels\":[\"in_app\"],\"deliver_at\":\"" + DeliverAtWire + "\"," +
            "\"status\":\"pending\",\"source\":\"sdk\",\"notification_id\":null,\"delivered_at\":null,\"canceled_at\":null," +
            "\"created_at\":\"2026-08-04T00:00:00Z\",\"updated_at\":\"2026-08-04T00:00:00Z\"}}";

        // The client-facing template projection: id/name/category only, no authoring content.
        private const string TemplateBody =
            "{\"result\":{\"id\":\"tmpl-1\",\"name\":\"GameplayTest\",\"category\":\"gameplay\"}}";

        // Scheduling resolves the name to an id first, so both routes have to answer. One fragment covers the
        // catalog and by-name alike, and "notification_template" can't collide with "notification/schedule".
        private static FlockFakeTransport ScheduleTransport()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationTemplate, FlockFakeTransport.Ok(TemplateBody));
            transport.On(FlockEndpoints.NotificationSchedule, FlockFakeTransport.Ok(ScheduledBody));
            return transport;
        }

        // Specific route first: matching is first-Contains-wins, so "notification/schedule" registered ahead of
        // "notification/schedule/sched-1" swallows every cancel and the by-id response is never reached.
        private static FlockFakeTransport ScheduleTransport(FlockHttpResponse cancelById)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationScheduleById("sched-1"), cancelById);
            transport.On(FlockEndpoints.NotificationTemplate, FlockFakeTransport.Ok(TemplateBody));
            transport.On(FlockEndpoints.NotificationSchedule, FlockFakeTransport.Ok(ScheduledBody));
            return transport;
        }

        // JObject.Parse coerces an ISO-8601 value into a Date token, and casting that back to string renders it
        // in the machine's culture ("08/05/2027 12:00:00"), hiding what actually went on the wire.
        private static JObject ParseBody(string json)
        {
            using (JsonTextReader reader = new JsonTextReader(new System.IO.StringReader(json)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                return JObject.Load(reader);
            }
        }

        // ---- NOTIF-01: the caller passes a NAME; the resolved id is what reaches template_id ----
        // The schedule route resolves by id only, so the SDK looks the name up first. Sending the name
        // verbatim is what the live backend answered 404 "Notification template not found" on.
        [Test]
        public void Schedule_ResolvesNameAndSendsResolvedId()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));

                FlockHttpRequest sent = h.Transport.LastTo(FlockEndpoints.NotificationSchedule);
                Assert.IsNotNull(sent, "Schedule should have hit the schedule endpoint.");
                JObject body = JObject.Parse(sent.JsonBody);
                Assert.AreEqual("tmpl-1", (string)body["template_id"],
                    "template_id must carry the id the name resolved to, never the name itself.");
            }
        }

        // ---- NOTIF-01b: the name rides in the query, not a path segment ----
        // Template names carry spaces, colons and slashes, none of which survive a single path segment.
        [Test]
        public void Schedule_SendsTemplateNameAsQueryParam()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Notification.ScheduleAsync("Gameplay Test/A", DeliverAt));

                FlockHttpRequest lookup = h.Transport.LastTo("notification_template/by-name");
                Assert.IsNotNull(lookup, "Scheduling by name must resolve through the by-name route.");
                StringAssert.Contains("name=Gameplay%20Test%2FA", lookup.Url,
                    "The name must be escaped into the query, not appended as a path segment.");
            }
        }

        // ---- NOTIF-01c: locale is forwarded when given and omitted when not ----
        [Test]
        public void Schedule_ForwardsLocaleOnlyWhenGiven()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt, null,
                    FlockNotificationChannels.None, "fr"));
                StringAssert.Contains("locale=fr", h.Transport.LastTo("notification_template/by-name").Url);

                h.Run(() => h.Client.Notification.ScheduleAsync("OtherTemplate", DeliverAt));
                StringAssert.DoesNotContain("locale=", h.Transport.LastTo("notification_template/by-name").Url,
                    "Without a locale the parameter is omitted so the server's English-then-first-on-file fallback applies.");
            }
        }

        // ---- NOTIF-01d: the name→id lookup is memoized for the session ----
        [Test]
        public void Schedule_Twice_ResolvesNameOnce()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));
                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));

                Assert.AreEqual(1, h.Transport.CountTo("notification_template/by-name"),
                    "A repeated name must come from the memo rather than costing another round trip.");
                Assert.AreEqual(2, h.Transport.CountTo(FlockEndpoints.NotificationSchedule));
            }
        }

        // ---- NOTIF-01e: a name this game doesn't have never reaches the schedule route ----
        [Test]
        public void Schedule_UnknownName_FailsBeforeScheduling()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationTemplate, FlockFakeTransport.Status(404,
                "{\"detail\":{\"code\":\"notification_template.not_found\",\"message\":\"No active notification template with this name\"}}"));
            transport.On(FlockEndpoints.NotificationSchedule, FlockFakeTransport.Ok(ScheduledBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Notification.ScheduleAsync("Nope", DeliverAt)));
                Assert.IsFalse(h.Transport.Sent(FlockEndpoints.NotificationSchedule),
                    "An unresolvable name must fail before anything is scheduled.");
            }
        }

        // ---- NOTIF-01f: the template catalog is game-scoped, not player-scoped ----
        // Both template routes are API-key only — no bearer — so they must work signed out.
        [Test]
        public void GetTemplates_WorksSignedOut()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationTemplate, FlockFakeTransport.Ok(
                "{\"result\":[{\"id\":\"tmpl-1\",\"name\":\"GameplayTest\",\"category\":\"gameplay\"}," +
                "{\"id\":\"tmpl-2\",\"name\":\"Welcome\",\"category\":\"onboarding\"}]}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                List<NotificationTemplate> templates = h.Run(() => h.Client.Notification.GetTemplatesAsync());

                Assert.AreEqual(2, templates.Count);
                Assert.AreEqual("tmpl-1", templates[0].Id);
                Assert.AreEqual("GameplayTest", templates[0].Name);
                Assert.AreEqual("onboarding", templates[1].Category);
            }
        }

        // ---- NOTIF-02: deliver_at is UTC ISO-8601 ----
        [Test]
        public void Schedule_SendsDeliverAtAsUtcIso8601()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));

                JObject body = ParseBody(h.Transport.LastTo(FlockEndpoints.NotificationSchedule).JsonBody);
                string deliverAt = (string)body["deliver_at"];
                StringAssert.StartsWith(DeliverAt.ToString("yyyy-MM-ddTHH:mm:ss"), deliverAt);
                StringAssert.EndsWith("Z", deliverAt, "deliver_at must be sent as UTC, not local time.");
            }
        }

        // ---- NOTIF-03: a local-kind DateTime is converted, not sent as-is ----
        [Test]
        public void Schedule_ConvertsLocalTimeToUtc()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                DateTime local = DeliverAt.ToLocalTime();
                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", local));

                JObject body = ParseBody(h.Transport.LastTo(FlockEndpoints.NotificationSchedule).JsonBody);
                string deliverAt = (string)body["deliver_at"];
                Assert.AreEqual(DeliverAt, DateTime.Parse(deliverAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                    "A local DateTime must land on the wire as the same instant in UTC.");
            }
        }

        // ---- NOTIF-04: channels map to wire values, variables pass through ----
        [Test]
        public void Schedule_MapsChannelsToWireValues_AndSendsVariables()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Notification.ScheduleAsync(
                    "GameplayTest", DeliverAt,
                    new Dictionary<string, object> { { "player", "Ada" } },
                    FlockNotificationChannels.InApp | FlockNotificationChannels.Push));

                JObject body = JObject.Parse(h.Transport.LastTo(FlockEndpoints.NotificationSchedule).JsonBody);
                Assert.AreEqual(JTokenType.Array, body["channels"].Type);
                Assert.AreEqual("in_app", (string)body["channels"][0], "InApp must serialize as in_app, not the C# name.");
                Assert.AreEqual("push", (string)body["channels"][1]);
                Assert.AreEqual(JTokenType.Object, body["variables"].Type);
                Assert.AreEqual("Ada", (string)body["variables"]["player"], "Template placeholders are filled from variables.");
            }
        }

        // ---- NOTIF-05: optional members are omitted, not sent empty ----
        [Test]
        public void Schedule_OmitsVariablesAndChannelsWhenNotSupplied()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));

                JObject body = JObject.Parse(h.Transport.LastTo(FlockEndpoints.NotificationSchedule).JsonBody);
                Assert.IsNull(body["variables"], "Optional members are omitted rather than sent empty.");
                Assert.IsNull(body["channels"], "An empty channel list must be omitted so the template's own channels apply.");
            }
        }

        // ---- NOTIF-05b: the TimeSpan overload lands the same instant as computing UtcNow yourself ----
        [Test]
        public void Schedule_DelayOverload_ResolvesToUtcNowPlusDelay()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                DateTime before = DateTime.UtcNow;
                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", TimeSpan.FromHours(4)));
                DateTime after = DateTime.UtcNow;

                JObject body = JObject.Parse(h.Transport.LastTo(FlockEndpoints.NotificationSchedule).JsonBody);
                DateTime sent = DateTime.Parse((string)body["deliver_at"], null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

                Assert.GreaterOrEqual(sent, before.AddHours(4).AddSeconds(-5));
                Assert.LessOrEqual(sent, after.AddHours(4).AddSeconds(5));
            }
        }

        // ---- NOTIF-05c: None omits the field entirely so the template's own channels apply ----
        [Test]
        public void Schedule_NoChannels_OmitsFieldSoTemplateDecides()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt, null, FlockNotificationChannels.None));

                JObject body = JObject.Parse(h.Transport.LastTo(FlockEndpoints.NotificationSchedule).JsonBody);
                Assert.IsNull(body["channels"], "None must omit the field rather than send an empty array.");
            }
        }

        // ---- NOTIF-06: an ambiguous failure must NOT create a second, uncancellable reminder ----
        // Retries are raised above the harness default here, otherwise this assertion proves nothing.
        [Test]
        public void Schedule_ServerError_NotRetried()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationTemplate, FlockFakeTransport.Ok(TemplateBody));
            transport.On(FlockEndpoints.NotificationSchedule, FlockFakeTransport.Status(500, "{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport,
                config => config.RetryPolicy = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero }))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt)));
                Assert.AreEqual(1, h.Transport.CountTo(FlockEndpoints.NotificationSchedule),
                    "A re-sent schedule would create a duplicate reminder the game can never cancel.");
            }
        }

        // ---- NOTIF-07: the contrast — cancel IS idempotent, so it retries under the same policy ----
        [Test]
        public void Cancel_ServerError_IsRetried()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationScheduleById("sched-1"), FlockFakeTransport.Status(500, "{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport,
                config => config.RetryPolicy = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero }))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Notification.CancelScheduledAsync("sched-1")));
                Assert.Greater(h.Transport.CountTo(FlockEndpoints.NotificationScheduleById("sched-1")), 1,
                    "Cancelling twice is harmless, so it retries — this is what proves NOTIF-06 measures the idempotency flag.");
            }
        }

        // ---- NOTIF-08: cancel targets the scheduled id and unwraps the envelope ----
        [Test]
        public void Cancel_HitsScheduledIdUrl_AndUnwrapsEnvelope()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationScheduleById("sched-1"), FlockFakeTransport.Ok(ScheduledBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                ScheduledNotification r = h.Run(() => h.Client.Notification.CancelScheduledAsync("sched-1"));

                Assert.IsNotNull(r);
                Assert.AreEqual("sched-1", r.Id);
                Assert.AreEqual("pending", r.Status);
                Assert.IsNull(r.NotificationId, "A pending schedule has no inbox notification yet.");
            }
        }

        // ---- NOTIF-09: the envelope is real — a bare body must not be accepted ----
        [Test]
        public void Schedule_BareBodyWithoutEnvelope_Throws()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationTemplate, FlockFakeTransport.Ok(TemplateBody));
            transport.On(FlockEndpoints.NotificationSchedule, FlockFakeTransport.Ok("{\"id\":\"sched-1\",\"status\":\"pending\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt)),
                    "Schedule is enveloped; a root-level payload means the SDK type is wrong.");
            }
        }

        // ---- NOTIF-10: guards short-circuit before any request ----
        [Test]
        public void Schedule_EmptyTemplateName_ThrowsValidation_AndSendsNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Notification.ScheduleAsync("", DeliverAt)));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request.");
            }
        }

        [Test]
        public void Schedule_SignedOut_ThrowsAuth_AndSendsNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.Throws<FlockAuthException>(() => h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt)));
                Assert.AreEqual(0, transport.Requests.Count, "Auth guard short-circuits before any request.");
            }
        }

        // ================= notification data accessors =================
        // These go through a real inbox fetch on purpose. Deserializing into Dictionary<string, object>
        // is what turns a JSON 7 into a long and a nested object into a JObject — construct a Notification
        // by hand and the whole class of bug these accessors exist for disappears from the test.

        private class RewardPayload
        {
            [JsonProperty("kind")] public string Kind { get; set; }
            [JsonProperty("amount")] public int Amount { get; set; }
        }

        private const string RichDataInbox =
            "{\"items\":[{\"id\":\"n-1\",\"title\":\"t\",\"body\":\"b\",\"read_at\":null,\"data\":{" +
            "\"deep_link\":\"shop\",\"level\":7,\"ratio\":0.5,\"flag\":true," +
            "\"reward\":{\"kind\":\"gems\",\"amount\":100},\"tags\":[\"a\",\"b\"]}}]," +
            "\"total\":1,\"page\":1,\"limit\":50}";

        private static Notification FetchRichNotification(FlockFakeTransport transport, FlockTestClient h)
        {
            transport.On("notification?page=", FlockFakeTransport.Ok(RichDataInbox));
            h.LoginAs("player-a");
            h.SetReachable(true);
            return h.Run(() => h.Client.Notification.GetInboxAsync()).Items[0];
        }

        // ---- NOTIF-31: a JSON whole number arrives as long; (int) would throw, GetData must not ----
        [Test]
        public void GetData_ConvertsJsonNumberToInt()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Notification n = FetchRichNotification(transport, h);

                Assert.IsInstanceOf<long>(n.Data["level"], "Newtonsoft yields long here — this is the trap the accessors exist for.");
                Assert.AreEqual(7, n.GetData<int>("level"));
                Assert.AreEqual(7L, n.GetData<long>("level"));
            }
        }

        // ---- NOTIF-32: strings, decimals and bools all round-trip ----
        [Test]
        public void GetData_HandlesStringDecimalAndBool()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Notification n = FetchRichNotification(transport, h);

                Assert.AreEqual("shop", n.GetData<string>("deep_link"));
                Assert.AreEqual(0.5f, n.GetData<float>("ratio"), 0.0001f);
                Assert.IsTrue(n.GetData<bool>("flag"));
            }
        }

        // ---- NOTIF-33: nested objects and arrays become typed values, no JObject in the caller ----
        [Test]
        public void GetData_DeserializesNestedObjectAndArray()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Notification n = FetchRichNotification(transport, h);

                RewardPayload reward = n.GetData<RewardPayload>("reward");
                Assert.IsNotNull(reward, "A nested object arrives as JObject; the accessor must convert it.");
                Assert.AreEqual("gems", reward.Kind);
                Assert.AreEqual(100, reward.Amount);

                List<string> tags = n.GetData<List<string>>("tags");
                CollectionAssert.AreEqual(new[] { "a", "b" }, tags);
            }
        }

        // ---- NOTIF-34: missing key, wrong shape and null Data all fall back rather than throw ----
        [Test]
        public void GetData_MissingOrWrongShape_FallsBackWithoutThrowing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Notification n = FetchRichNotification(transport, h);

                Assert.AreEqual("none", n.GetData<string>("nope", "none"), "Missing key returns the fallback.");
                Assert.IsFalse(n.TryGetData<int>("deep_link", out int _), "\"shop\" is not an int — report false, don't throw.");
                Assert.AreEqual(0, n.GetData<int>("nope"), "Default fallback for a value type is default(T).");

                Notification empty = new Notification();
                Assert.IsFalse(empty.TryGetData<string>("anything", out string _), "Null Data must not throw.");
                Assert.AreEqual("fb", empty.GetData<string>("anything", "fb"));
            }
        }

        // ---- NOTIF-35: the whole payload can be taken as one typed object ----
        [Test]
        public void GetDataAs_DeserializesWholePayload()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On("notification?page=", FlockFakeTransport.Ok(
                "{\"items\":[{\"id\":\"n-1\",\"data\":{\"kind\":\"gems\",\"amount\":100}}],\"total\":1,\"page\":1,\"limit\":50}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Notification n = h.Run(() => h.Client.Notification.GetInboxAsync()).Items[0];
                RewardPayload payload = n.GetDataAs<RewardPayload>();

                Assert.IsNotNull(payload);
                Assert.AreEqual("gems", payload.Kind);
                Assert.AreEqual(100, payload.Amount);
                Assert.IsNull(new Notification().GetDataAs<RewardPayload>(), "Empty Data yields default, not an exception.");
            }
        }

        // ================= device tokens (push) =================

        private const string DeviceTokenBody =
            "{\"result\":{\"id\":\"dt-1\",\"player_id\":\"player-a\",\"game_id\":\"g\",\"platform\":\"android\"," +
            "\"is_active\":true,\"last_seen_at\":null,\"created_at\":\"2026-08-04T00:00:00Z\"}}";

        // ---- NOTIF-26: platform reaches the wire as the API's lowercase value, with the token verbatim ----
        [Test]
        public void RegisterDeviceToken_SendsPlatformAndTokenVerbatim()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.DeviceTokenRegister, FlockFakeTransport.Ok(DeviceTokenBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                DeviceToken registered = h.Run(() => h.Client.Notification.RegisterDeviceTokenAsync(
                    FlockDevicePlatform.Android, "fcm-token-abc"));

                JObject body = JObject.Parse(h.Transport.LastTo(FlockEndpoints.DeviceTokenRegister).JsonBody);
                Assert.AreEqual("android", (string)body["platform"], "Wire value is lowercase, not the C# enum name.");
                Assert.AreEqual("fcm-token-abc", (string)body["token"]);
                Assert.AreEqual("dt-1", registered.Id);
                Assert.IsTrue(registered.IsActive);
            }
        }

        // ---- NOTIF-27: every platform maps to the value the API declares ----
        [Test]
        public void DevicePlatform_MapsToApiWireValues()
        {
            Assert.AreEqual("android", FlockDevicePlatform.Android.ToWireValue());
            Assert.AreEqual("ios", FlockDevicePlatform.Ios.ToWireValue());
            Assert.AreEqual("web", FlockDevicePlatform.Web.ToWireValue());
        }

        // ---- NOTIF-28: auto-detect refuses a platform the backend has no value for ----
        // EditMode runs as WindowsEditor/OSXEditor, so this exercises the desktop refusal for free.
        [Test]
        public void RegisterDeviceToken_AutoDetect_RefusesUnsupportedPlatform()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.DeviceTokenRegister, FlockFakeTransport.Ok(DeviceTokenBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Notification.RegisterDeviceTokenAsync("fcm-token-abc")),
                    "Desktop/Editor has no device platform in the API — refuse rather than guess a value that fails silently at delivery.");
                Assert.AreEqual(0, transport.Requests.Count, "Refusal short-circuits before any request.");
            }
        }

        // ---- NOTIF-29: unregister unwraps the scalar and posts the token ----
        [Test]
        public void UnregisterDeviceToken_PostsTokenAndReturnsDeactivated()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.DeviceTokenUnregister, FlockFakeTransport.Ok("{\"result\":{\"deactivated\":true}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                bool deactivated = h.Run(() => h.Client.Notification.UnregisterDeviceTokenAsync("fcm-token-abc"));

                Assert.IsTrue(deactivated);
                JObject body = JObject.Parse(h.Transport.LastTo(FlockEndpoints.DeviceTokenUnregister).JsonBody);
                Assert.AreEqual("fcm-token-abc", (string)body["token"]);
            }
        }

        // ---- NOTIF-30: guards short-circuit ----
        [Test]
        public void DeviceToken_GuardsShortCircuitBeforeAnyRequest()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.Throws<FlockAuthException>(() => h.Run(() => h.Client.Notification.RegisterDeviceTokenAsync(FlockDevicePlatform.Ios, "t")));

                h.LoginAs("player-a");
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Notification.RegisterDeviceTokenAsync(FlockDevicePlatform.Ios, "")));
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Notification.UnregisterDeviceTokenAsync("")));

                Assert.AreEqual(0, transport.Requests.Count);
            }
        }

        // ================= pending-schedule tracking =================
        // /v1 has no route to list or read back a schedule, so the SDK persists what it created.

        // ---- NOTIF-20: a successful schedule becomes trackable; a successful cancel stops being tracked ----
        [Test]
        public void PendingSchedules_TrackedOnSchedule_AndDroppedOnCancel()
        {
            FlockFakeTransport transport = ScheduleTransport(FlockFakeTransport.Ok(ScheduledBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.AreEqual(0, h.Client.Notification.GetPendingSchedules().Count);

                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));

                List<PendingSchedule> pending = h.Client.Notification.GetPendingSchedules();
                Assert.AreEqual(1, pending.Count);
                Assert.AreEqual("sched-1", pending[0].Id);
                Assert.AreEqual("GameplayTest", pending[0].TemplateName, "The name the caller scheduled by is kept so entries are tellable apart.");
                Assert.AreEqual("tmpl-1", pending[0].TemplateId, "The id it resolved to is kept alongside the name.");

                h.Run(() => h.Client.Notification.CancelScheduledAsync("sched-1"));
                Assert.AreEqual(0, h.Client.Notification.GetPendingSchedules().Count);
            }
        }

        // ---- NOTIF-21: a failed schedule must not leave a phantom entry ----
        [Test]
        public void PendingSchedules_NotTrackedWhenScheduleFails()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationTemplate, FlockFakeTransport.Ok(TemplateBody));
            transport.On(FlockEndpoints.NotificationSchedule, FlockFakeTransport.Status(500, "{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt)));
                Assert.AreEqual(0, h.Client.Notification.GetPendingSchedules().Count,
                    "Nothing was scheduled, so nothing may be tracked.");
            }
        }

        // ---- NOTIF-22: delivery can only be inferred from the clock, so past entries stop being pending ----
        [Test]
        public void PendingSchedules_DropEntriesWhoseDeliveryTimeHasPassed()
        {
            string pastBody = ScheduledBody.Replace(DeliverAtWire, "2020-01-01T00:00:00Z");
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationTemplate, FlockFakeTransport.Ok(TemplateBody));
            transport.On(FlockEndpoints.NotificationSchedule, FlockFakeTransport.Ok(pastBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));

                Assert.AreEqual(0, h.Client.Notification.GetPendingSchedules().Count,
                    "A delivery time in the past means it is no longer cancellable.");
            }
        }

        // ---- NOTIF-23: an id the server no longer recognises is dropped, not left blocking the batch ----
        [Test]
        public void CancelAll_DropsEntriesTheServerRejectsPermanently()
        {
            // Already delivered or already cancelled server-side. Armed up front rather than mid-test, because
            // the by-id route has to be registered ahead of the general schedule route to be reachable at all.
            FlockFakeTransport transport = ScheduleTransport(FlockFakeTransport.Status(404, "{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));

                int cancelled = h.Run(() => h.Client.Notification.CancelAllScheduledAsync());

                Assert.AreEqual(0, cancelled, "Nothing was actually cancelled server-side.");
                Assert.AreEqual(0, h.Client.Notification.GetPendingSchedules().Count,
                    "A permanently-rejected id isn't pending either, so it must not linger forever.");
            }
        }

        // ---- NOTIF-24: pending schedules are per-player on a shared device ----
        [Test]
        public void PendingSchedules_ArePlayerScoped()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));
                Assert.AreEqual(1, h.Client.Notification.GetPendingSchedules().Count);

                h.LoginAs("player-b");
                Assert.AreEqual(0, h.Client.Notification.GetPendingSchedules().Count,
                    "player-b must not see, or be able to cancel, player-a's reminders.");
            }
        }

        // ---- NOTIF-25b: nor may it discard ANOTHER player's cancellable ids ----
        // The snapshot scope is shared by every player on the device. Deleting it wholesale and restoring only the
        // signed-in player's rows orphans everyone else's reminders: they still fire server-side and can no longer
        // be cancelled. NOTIF-25 passed throughout because it only ever had one player.
        [Test]
        public void ClearCache_KeepsOtherPlayersPendingSchedules()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                h.LoginAs("player-a");
                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));
                Assert.AreEqual(1, h.Client.Notification.GetPendingSchedules().Count, "Player A has a reminder pending.");

                // Player B signs in on the same device and the game clears cache from a settings screen.
                h.LoginAs("player-b");
                h.Client.Notification.ClearCache();

                h.LoginAs("player-a");
                Assert.AreEqual(1, h.Client.Notification.GetPendingSchedules().Count,
                    "Player A's reminder must survive another player clearing cache — it is the only handle on cancelling it.");
            }
        }

        // ---- NOTIF-25: clearing cached reads must not discard cancellable ids ----
        [Test]
        public void ClearCache_KeepsPendingSchedules()
        {
            FlockFakeTransport transport = ScheduleTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                h.Run(() => h.Client.Notification.ScheduleAsync("GameplayTest", DeliverAt));

                h.Client.Notification.ClearCache();

                Assert.AreEqual(1, h.Client.Notification.GetPendingSchedules().Count,
                    "Pending schedules are state, not cache — clearing reads must not orphan a reminder.");
            }
        }

        // ================= inbox =================

        // The list route returns PaginationResponse at the ROOT — unlike its four siblings, it is not
        // GenericResponse-enveloped. Typing it as an envelope would silently yield an empty page, not an error.
        private const string InboxBody =
            "{\"items\":[" +
            "{\"id\":\"n-1\",\"title\":\"Welcome\",\"body\":\"Hi\",\"type\":\"news\",\"severity\":\"info\"," +
            "\"data\":{\"deep_link\":\"shop\"},\"read_at\":null,\"campaign_id\":null}," +
            "{\"id\":\"n-2\",\"title\":\"Sale\",\"body\":\"50% off\",\"type\":\"news\",\"severity\":\"info\"," +
            "\"data\":{},\"read_at\":\"2026-08-03T00:00:00Z\",\"campaign_id\":\"c-1\"}]," +
            "\"total\":2,\"page\":1,\"limit\":50}";

        // Received-event fixtures. The inbox route is root-shaped, and pages come back newest-first.
        private const string InboxSeedBody =
            "{\"items\":[{\"id\":\"n-1\",\"title\":\"Old\",\"created_at\":\"2026-08-01T00:00:00Z\"}]," +
            "\"total\":1,\"page\":1,\"limit\":50}";

        private const string InboxOneNewBody =
            "{\"items\":[" +
            "{\"id\":\"n-2\",\"title\":\"New\",\"created_at\":\"2026-08-02T00:00:00Z\"}," +
            "{\"id\":\"n-1\",\"title\":\"Old\",\"created_at\":\"2026-08-01T00:00:00Z\"}]," +
            "\"total\":2,\"page\":1,\"limit\":50}";

        private const string InboxTwoNewBody =
            "{\"items\":[" +
            "{\"id\":\"n-3\",\"title\":\"Newest\",\"created_at\":\"2026-08-03T00:00:00Z\"}," +
            "{\"id\":\"n-2\",\"title\":\"New\",\"created_at\":\"2026-08-02T00:00:00Z\"}," +
            "{\"id\":\"n-1\",\"title\":\"Old\",\"created_at\":\"2026-08-01T00:00:00Z\"}]," +
            "\"total\":3,\"page\":1,\"limit\":50}";

        // ---- NOTIF-41: the by-name read is public surface in its own right, not just a step inside schedule ----
        // Signed out on purpose: the route is API-key scoped, so a bearer guard here would be wrong.
        [Test]
        public void GetTemplateByName_ReturnsProjection_AndMemoizes()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationTemplate, FlockFakeTransport.Ok(TemplateBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                NotificationTemplate first = h.Run(() => h.Client.Notification.GetTemplateByNameAsync("GameplayTest"));
                NotificationTemplate second = h.Run(() => h.Client.Notification.GetTemplateByNameAsync("GameplayTest"));

                Assert.AreEqual("tmpl-1", first.Id);
                Assert.AreEqual("GameplayTest", first.Name);
                Assert.AreEqual("gameplay", first.Category);
                Assert.AreEqual(first.Id, second.Id);
                Assert.AreEqual(1, h.Transport.CountTo("notification_template/by-name"),
                    "The second read must come from the session memo, not another round trip.");
            }
        }

        // ---- NOTIF-42: ResolveTemplateIdAsync is the id accessor callers get instead of an id-taking schedule ----
        [Test]
        public void ResolveTemplateId_ReturnsTheResolvedId()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationTemplate, FlockFakeTransport.Ok(TemplateBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.AreEqual("tmpl-1",
                    h.Run(() => h.Client.Notification.ResolveTemplateIdAsync("GameplayTest")));
            }
        }

        // ---- NOTIF-43: RegisterThisDeviceAsync refuses where no OS push token exists ----
        // The Editor has no APNs/FCM, so the documented contract is an explanatory throw, not a silent no-op
        // or a token registered under a guessed platform.
        [Test]
        public void RegisterThisDevice_OffDevice_ThrowsAndSendsNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.DeviceTokenRegister, FlockFakeTransport.Ok(DeviceTokenBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Throws<FlockValidationException>(
                    () => h.Run(() => h.Client.Notification.RegisterThisDeviceAsync()));
                Assert.IsFalse(h.Transport.Sent(FlockEndpoints.DeviceTokenRegister),
                    "Nothing may be registered when the OS supplied no token.");
            }
        }

        // ---- NOTIF-36: the first fetch for a player seeds silently ----
        // A player with an existing inbox must not get one event per historical row on launch.
        [Test]
        public void Received_FirstFetchSeedsSilently()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On("notification?page=", FlockFakeTransport.Ok(InboxSeedBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                List<string> raised = new List<string>();
                Action<Notification> handler = n => raised.Add(n.Id);
                FlockEvents.OnNotificationReceived += handler;
                try
                {
                    h.Run(() => h.Client.Notification.GetInboxAsync());
                    CollectionAssert.IsEmpty(raised, "An existing inbox must not replay as a burst of events.");
                }
                finally { FlockEvents.OnNotificationReceived -= handler; }
            }
        }

        // ---- NOTIF-37: anything newer than the watermark raises exactly once ----
        [Test]
        public void Received_RaisesOncePerNewNotification()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence("notification?page=",
                FlockFakeTransport.Ok(InboxSeedBody),
                FlockFakeTransport.Ok(InboxOneNewBody),
                FlockFakeTransport.Ok(InboxOneNewBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                List<string> raised = new List<string>();
                Action<Notification> handler = n => raised.Add(n.Id);
                FlockEvents.OnNotificationReceived += handler;
                try
                {
                    h.Run(() => h.Client.Notification.GetInboxAsync());   // seeds
                    h.Run(() => h.Client.Notification.GetInboxAsync());   // n-2 is new
                    h.Run(() => h.Client.Notification.GetInboxAsync());   // same page again

                    CollectionAssert.AreEqual(new[] { "n-2" }, raised,
                        "Only the notification newer than the watermark raises, and only on its first sighting.");
                }
                finally { FlockEvents.OnNotificationReceived -= handler; }
            }
        }

        // ---- NOTIF-38: several new at once arrive oldest-first ----
        // The page is newest-first, so raising in page order would hand the game its history backwards.
        [Test]
        public void Received_MultipleNew_ArriveOldestFirst()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence("notification?page=",
                FlockFakeTransport.Ok(InboxSeedBody),
                FlockFakeTransport.Ok(InboxTwoNewBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                List<string> raised = new List<string>();
                Action<Notification> handler = n => raised.Add(n.Id);
                FlockEvents.OnNotificationReceived += handler;
                try
                {
                    h.Run(() => h.Client.Notification.GetInboxAsync());
                    h.Run(() => h.Client.Notification.GetInboxAsync());

                    CollectionAssert.AreEqual(new[] { "n-2", "n-3" }, raised);
                }
                finally { FlockEvents.OnNotificationReceived -= handler; }
            }
        }

        // ---- NOTIF-39: the watermark is state, not cache, so ClearCache must not replay ----
        [Test]
        public void Received_WatermarkSurvivesClearCache()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence("notification?page=",
                FlockFakeTransport.Ok(InboxSeedBody),
                FlockFakeTransport.Ok(InboxOneNewBody),
                FlockFakeTransport.Ok(InboxOneNewBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                List<string> raised = new List<string>();
                Action<Notification> handler = n => raised.Add(n.Id);
                FlockEvents.OnNotificationReceived += handler;
                try
                {
                    h.Run(() => h.Client.Notification.GetInboxAsync());
                    h.Run(() => h.Client.Notification.GetInboxAsync());
                    h.Client.Notification.ClearCache();
                    h.Run(() => h.Client.Notification.GetInboxAsync());

                    CollectionAssert.AreEqual(new[] { "n-2" }, raised,
                        "Dropping cache must not make already-surfaced notifications arrive again.");
                }
                finally { FlockEvents.OnNotificationReceived -= handler; }
            }
        }

        // ---- NOTIF-40: the summary read surfaces arrivals too, not just the inbox list ----
        [Test]
        public void Received_AlsoRaisedFromSummary()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.NotificationSummary,
                FlockFakeTransport.Ok("{\"result\":{\"unread_count\":1,\"items\":[" +
                    "{\"id\":\"n-1\",\"title\":\"Old\",\"created_at\":\"2026-08-01T00:00:00Z\"}]}}"),
                FlockFakeTransport.Ok("{\"result\":{\"unread_count\":2,\"items\":[" +
                    "{\"id\":\"n-2\",\"title\":\"New\",\"created_at\":\"2026-08-02T00:00:00Z\"}," +
                    "{\"id\":\"n-1\",\"title\":\"Old\",\"created_at\":\"2026-08-01T00:00:00Z\"}]}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                List<string> raised = new List<string>();
                Action<Notification> handler = n => raised.Add(n.Id);
                FlockEvents.OnNotificationReceived += handler;
                try
                {
                    h.Run(() => h.Client.Notification.GetSummaryAsync());
                    h.Run(() => h.Client.Notification.GetSummaryAsync());

                    CollectionAssert.AreEqual(new[] { "n-2" }, raised);
                }
                finally { FlockEvents.OnNotificationReceived -= handler; }
            }
        }

        // ---- NOTIF-11: root-level pagination parses, and read state derives from read_at ----
        [Test]
        public void GetInbox_ParsesRootLevelPage_AndDerivesReadState()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On("notification?page=", FlockFakeTransport.Ok(InboxBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                PaginatedResponse<Notification> page = h.Run(() => h.Client.Notification.GetInboxAsync());

                Assert.AreEqual(2, page.Total);
                Assert.AreEqual(2, page.Items.Length, "The list route is NOT enveloped — an envelope type would parse to an empty page.");
                Assert.IsFalse(page.Items[0].IsRead, "read_at null means unread.");
                Assert.IsTrue(page.Items[1].IsRead, "read_at set means read.");
                Assert.AreEqual("shop", (string)page.Items[0].Data["deep_link"], "data carries the deep-link payload.");
            }
        }

        // ---- NOTIF-12: unread_only is a flag, omitted unless asked for ----
        [Test]
        public void GetInbox_UnreadOnly_TogglesQueryFlag()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On("notification?page=", FlockFakeTransport.Ok(InboxBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Notification.GetInboxAsync());
                Assert.IsFalse(h.Transport.LastTo("notification?page=").Url.Contains("unread_only"),
                    "Default reads everything, so the flag is omitted.");

                h.Run(() => h.Client.Notification.GetInboxAsync(true, 2, 10));
                string url = h.Transport.LastTo("notification?page=").Url;
                Assert.IsTrue(url.Contains("unread_only=true"), url);
                Assert.IsTrue(url.Contains("page=2") && url.Contains("limit=10"), url);
            }
        }

        // ---- NOTIF-13: the scalar envelope unwraps to an int and raises the badge event ----
        [Test]
        public void GetUnreadCount_UnwrapsScalar_AndRaisesEvent()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationUnreadCount, FlockFakeTransport.Ok("{\"result\":{\"count\":7}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                List<int> raised = new List<int>();
                Action<int> handler = c => raised.Add(c);
                FlockEvents.OnUnreadCountChanged += handler;
                try
                {
                    int count = h.Run(() => h.Client.Notification.GetUnreadCountAsync());

                    Assert.AreEqual(7, count);
                    CollectionAssert.AreEqual(new[] { 7 }, raised);
                }
                finally { FlockEvents.OnUnreadCountChanged -= handler; }
            }
        }

        // ---- NOTIF-14: the event is edge-triggered, so a repeated refresh doesn't spam a badge ----
        [Test]
        public void UnreadCount_EventOnlyFiresWhenTheValueChanges()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.NotificationUnreadCount,
                FlockFakeTransport.Ok("{\"result\":{\"count\":3}}"),
                FlockFakeTransport.Ok("{\"result\":{\"count\":3}}"),
                FlockFakeTransport.Ok("{\"result\":{\"count\":5}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                List<int> raised = new List<int>();
                Action<int> handler = c => raised.Add(c);
                FlockEvents.OnUnreadCountChanged += handler;
                try
                {
                    h.Run(() => h.Client.Notification.GetUnreadCountAsync());
                    h.Run(() => h.Client.Notification.GetUnreadCountAsync());
                    h.Run(() => h.Client.Notification.GetUnreadCountAsync());

                    CollectionAssert.AreEqual(new[] { 3, 5 }, raised, "An unchanged count must not re-raise.");
                }
                finally { FlockEvents.OnUnreadCountChanged -= handler; }
            }
        }

        // ---- NOTIF-15: summary carries both halves and refreshes the badge ----
        [Test]
        public void GetSummary_ReturnsCountAndItems_AndRaisesEvent()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationSummary,
                FlockFakeTransport.Ok("{\"result\":{\"unread_count\":4,\"items\":[{\"id\":\"n-1\",\"title\":\"Hi\",\"read_at\":null}]}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                List<int> raised = new List<int>();
                Action<int> handler = c => raised.Add(c);
                FlockEvents.OnUnreadCountChanged += handler;
                try
                {
                    NotificationSummary summary = h.Run(() => h.Client.Notification.GetSummaryAsync(5));

                    Assert.AreEqual(4, summary.UnreadCount);
                    Assert.AreEqual(1, summary.Items.Count);
                    Assert.IsTrue(h.Transport.LastTo(FlockEndpoints.NotificationSummary).Url.Contains("limit=5"));
                    CollectionAssert.AreEqual(new[] { 4 }, raised);
                }
                finally { FlockEvents.OnUnreadCountChanged -= handler; }
            }
        }

        // ---- NOTIF-16: mark-read posts to the per-id route with a body the server can parse ----
        [Test]
        public void MarkRead_PostsToIdRoute_WithEmptyObjectBody()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationReadById("n-1"),
                FlockFakeTransport.Ok("{\"result\":{\"id\":\"n-1\",\"title\":\"Hi\",\"read_at\":\"2026-08-04T00:00:00Z\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Notification read = h.Run(() => h.Client.Notification.MarkReadAsync("n-1"));

                Assert.IsTrue(read.IsRead);
                FlockHttpRequest sent = h.Transport.LastTo(FlockEndpoints.NotificationReadById("n-1"));
                Assert.AreEqual("POST", sent.Method);
                Assert.AreEqual("{}", sent.JsonBody, "Bodyless route: send an empty object, not a bare `null` body.");
            }
        }

        // ---- NOTIF-17: read-all returns the changed count and drives the badge to zero ----
        [Test]
        public void MarkAllRead_ReturnsUpdatedCount_AndRaisesZero()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationReadAll, FlockFakeTransport.Ok("{\"result\":{\"updated\":6}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                List<int> raised = new List<int>();
                Action<int> handler = c => raised.Add(c);
                FlockEvents.OnUnreadCountChanged += handler;
                try
                {
                    int updated = h.Run(() => h.Client.Notification.MarkAllReadAsync());

                    Assert.AreEqual(6, updated);
                    CollectionAssert.AreEqual(new[] { 0 }, raised, "Nothing is unread after read-all.");
                }
                finally { FlockEvents.OnUnreadCountChanged -= handler; }
            }
        }

        // ---- NOTIF-18: one player's inbox must never be served to the next player on a shared device ----
        [Test]
        public void GetInbox_CacheIsPlayerScoped_AcrossPlayerSwitch()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On("notification?page=", FlockFakeTransport.Ok(InboxBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                Assert.AreEqual(2, h.Run(() => h.Client.Notification.GetInboxAsync()).Total);

                h.LoginAs("player-b");
                h.SetReachable(false);
                transport.GoOffline();

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Notification.GetInboxAsync()),
                    "player-b must not be served player-a's cached inbox.");
            }
        }

        // ---- NOTIF-19: the inbox is bearer-only ----
        [Test]
        public void GetInbox_SignedOut_ThrowsAuth_AndSendsNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.Throws<FlockAuthException>(() => h.Run(() => h.Client.Notification.GetInboxAsync()));
                Assert.AreEqual(0, transport.Requests.Count, "Auth guard short-circuits before any request.");
            }
        }

        [Test]
        public void Cancel_EmptyId_ThrowsValidation_AndSendsNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Notification.CancelScheduledAsync("")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request.");
            }
        }

        // GET notification/schedule returns PaginationResponse at the ROOT — no {"result":...} envelope.
        private const string TwoPendingBody =
            "{\"items\":[" +
            "{\"id\":\"sch-1\",\"player_id\":\"player-a\",\"template_id\":\"tpl-1\",\"status\":\"pending\",\"deliver_at\":\"2026-09-01T00:00:00Z\"}," +
            "{\"id\":\"sch-2\",\"player_id\":\"player-a\",\"template_id\":\"tpl-2\",\"status\":\"pending\",\"deliver_at\":\"2026-09-02T00:00:00Z\"}" +
            "],\"total\":2,\"page\":1,\"limit\":100}";

        // ---- NOTIF-10: the schedule list is a server read, so it sees schedules this install never made ----
        [Test]
        public void GetScheduled_ParsesRootPagination_AndDefaultsToPending()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationSchedule, FlockFakeTransport.Ok(TwoPendingBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                PaginatedResponse<ScheduledNotification> result = h.Run(() => h.Client.Notification.GetScheduledAsync());

                Assert.AreEqual(2, result.Items.Length);
                Assert.AreEqual("sch-1", result.Items[0].Id);
                Assert.AreEqual(2, result.Total);
                Assert.IsTrue(transport.Sent("status=pending"), "Defaults to the pending filter.");
            }
        }

        [Test]
        public void GetScheduled_NotSignedIn_ThrowsAuth_AndSendsNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.Throws<FlockAuthException>(() => h.Run(() => h.Client.Notification.GetScheduledAsync()));
                Assert.AreEqual(0, transport.Requests.Count, "Auth guard short-circuits before any request.");
            }
        }

        // ---- NOTIF-11: cancel-all works off the SERVER list, which is what fixes the reinstall / second-device case ----
        [Test]
        public void CancelAll_CancelsSchedulesThisInstallNeverTracked()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationScheduleById("sch-1"), FlockFakeTransport.Ok("{\"result\":{\"id\":\"sch-1\"}}"));
            transport.On(FlockEndpoints.NotificationScheduleById("sch-2"), FlockFakeTransport.Ok("{\"result\":{\"id\":\"sch-2\"}}"));
            transport.On(FlockEndpoints.NotificationSchedule, FlockFakeTransport.Ok(TwoPendingBody));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                // Nothing was scheduled through this install, so the local list is empty by construction.
                int cancelled = h.Run(() => h.Client.Notification.CancelAllScheduledAsync());

                Assert.AreEqual(2, cancelled, "Both server-side schedules are cancelled despite no local bookkeeping.");
                Assert.IsTrue(transport.Sent("notification/schedule/sch-1"));
                Assert.IsTrue(transport.Sent("notification/schedule/sch-2"));
            }
        }

        // ---- NOTIF-11b: if the server list can't be read, fall back to local bookkeeping rather than cancelling nothing ----
        [Test]
        public void CancelAll_ServerListUnreachable_FallsBackToLocalList()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.NotificationSchedule, FlockFakeTransport.Status(503, "{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                // Local list is empty here, so this asserts the fallback path runs and returns cleanly rather than throwing.
                int cancelled = h.Run(() => h.Client.Notification.CancelAllScheduledAsync());

                Assert.AreEqual(0, cancelled);
            }
        }
    }
}
