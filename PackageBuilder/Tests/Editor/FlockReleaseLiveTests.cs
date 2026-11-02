using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Logging;
using Flock.Models;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    /// <summary>The release's checks against a live backend that the player smoke leaves out, each a new device player: player data written and read back, a write queued offline sent on reconnect, an event delivered, and a game config read. Run by hand with the backend's FlockConfig.asset (production's test game for a release, through Qwacks/Libraries/Unity/production-sanity/run_release_live.py).</summary>
    public class FlockReleaseLiveTests
    {
        private const string UnknownId = "01ZZZZZZZZZZZZZZZZZZZZZZZZ";
        private string _runFolder;
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            _runFolder = FlockAnalyticsLaunches.FolderForTesting;
            _folder = Path.Combine(Path.GetTempPath(), "flock_release_live_" + Guid.NewGuid().ToString("N"));
            FlockAnalyticsLaunches.FolderForTesting = _folder;
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
            {
                FlockClient.Instance.Authentication.Logout();
                FlockClient.Shutdown();
            }
            FlockAnalyticsLaunches.FolderForTesting = _runFolder;
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; run manually.")]
        public IEnumerator Live_PlayerData_IsWrittenAndReadBack_AndAFieldTheTemplateLacksIsRefused()
        {
            Task<PlayerData> signedIn = SignInWithFirstRow();
            yield return Await(signedIn);
            PlayerData row = signedIn.Result;
            DataField number = FirstNumber(row.Data);
            Assume.That(number, Is.Not.Null, "The player's first data row holds no number to write.");

            long written = number.GetValue<long>() + 7;
            number.Value = written;
            yield return Await(FlockClient.Instance.Commands.UpdatePlayerDataAsync(row.Id, row.Data));
            Task<long> stored = ReadFromServer(row.Id, number.FieldName);
            yield return Await(stored);
            Assert.AreEqual(written, stored.Result, "The server holds the written value.");

            Task<PlayerData> refused = FlockClient.Instance.Commands.UpdatePlayerDataFieldAsync(row.Id, "flock_release_live_not_a_field", "x");
            yield return Settle(refused);
            Assert.AreEqual(FlockErrorCode.GameCommandTemplateValidationFailed, (refused.Exception?.GetBaseException() as FlockException)?.ErrorCode);
        }

        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; run manually.")]
        public IEnumerator Live_AWriteQueuedOffline_IsSentOnReconnect()
        {
            Task<PlayerData> signedIn = SignInWithFirstRow();
            yield return Await(signedIn);
            PlayerData row = signedIn.Result;
            DataField number = FirstNumber(row.Data);
            Assume.That(number, Is.Not.Null, "The player's first data row holds no number to write.");
            long before = number.GetValue<long>();

            FlockClient.Instance.ReachabilityProbe = () => false;
            number.Value = before + 11;
            yield return Await(FlockClient.Instance.Commands.UpdatePlayerDataAsync(row.Id, row.Data));
            Task<long> whileOffline = ReadFromServer(row.Id, number.FieldName);
            yield return Await(whileOffline);
            Assert.AreEqual(before, whileOffline.Result, "Queued while offline, so not sent yet.");

            FlockClient.Instance.ReachabilityProbe = () => true;
            yield return Await(FlockClient.Instance.Commands.FlushPendingWritesAsync());
            Task<long> afterReconnect = ReadFromServer(row.Id, number.FieldName);
            yield return Await(afterReconnect);
            Assert.AreEqual(before + 11, afterReconnect.Result, "Sent once the device was back online.");
        }

        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; run manually.")]
        public IEnumerator Live_AnEvent_IsDelivered_AndANameTooLongIsRefusedBeforeSending()
        {
            Task<PlayerData> signedIn = SignInWithFirstRow();
            yield return Await(signedIn);

            Assert.IsTrue(FlockClient.Instance.Analytics.TrackEvent("release_live_check", new Dictionary<string, object> { { "step", 1 } }, "release_live"));
            Assert.IsFalse(FlockClient.Instance.Analytics.TrackEvent(new string('n', 201)), "Refused before it could fail the batch.");
            Task<FlockFlushResult> flushed = FlockClient.Instance.Analytics.FlushAsync();
            yield return Await(flushed);
            Assert.GreaterOrEqual(flushed.Result.Sent, 1, "The server took the event.");
            Assert.AreEqual(0, flushed.Result.Refused, flushed.Result.FirstRefusal);
        }

        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; run manually.")]
        public IEnumerator Live_AGameConfig_IsRead_AndAnUnknownOneIsRefused()
        {
            Task<PlayerData> signedIn = SignInWithFirstRow();
            yield return Await(signedIn);
            // The game's configs, as codegen lists them for the generated ids.
            string url = $"{FlockClient.Instance.GetVersionedApiUrl()}/{FlockEndpoints.GameConfig}?page=1&limit=100";
            Task<JObject> configs = FlockHttpClient.GetAsync<JObject>(url, FlockClient.Instance.GetGameHeaders(), CancellationToken.None);
            yield return Await(configs);
            JToken first = configs.Result.SelectToken("$..result[0].id") ?? configs.Result.SelectToken("$..items[0].id");
            Assume.That(first, Is.Not.Null, "This game has no config to read.");

            Task<JToken> read = FlockClient.Instance.Config.GetByConfigIdAsync<JToken>((string)first);
            yield return Await(read);
            Assert.IsNotNull(read.Result, "The config's values were read.");

            Task<JToken> unknown = FlockClient.Instance.Config.GetByConfigIdAsync<JToken>(UnknownId);
            yield return Settle(unknown);
            // The values come from the config's patch first, so the patch route answers for an id no config has.
            Assert.AreEqual(FlockErrorCode.GamePatchGameConfigNotFound, (unknown.Exception?.GetBaseException() as FlockException)?.ErrorCode);
        }

        // Starts the client from FlockConfig.asset and registers a new device player; answers the player's first data row.
        private async Task<PlayerData> SignInWithFirstRow()
        {
            FlockConfigAsset asset = Resources.Load<FlockConfigAsset>("FlockConfig");
            if (asset == null || !asset.IsValid(out string _) || string.IsNullOrEmpty(asset.gameVersionId))
                Assert.Ignore("No usable FlockConfig.asset (missing fields or unresolved Game Version ID).");
            FlockInitConfig config = asset.ToInitConfig();
            config.OfflineCacheDirectory = Path.Combine(_folder, "snapshots");
            FlockClient.Create(config, new UnityFlockLogger());
            await FlockClient.Instance.Authentication.RegisterWithDeviceAsync("flock-release-live-" + Guid.NewGuid().ToString("N"));
            PaginatedResponse<PlayerData> rows = await FlockClient.Instance.Player.GetAllDataAsync();
            Assume.That(rows.Items.Length, Is.GreaterThan(0), "A new player of this game has no data row.");
            return rows.Items[0];
        }

        // The first integer or number field, top level or inside an object field.
        private static DataField FirstNumber(IList<DataField> fields)
        {
            foreach (DataField field in fields)
            {
                if (field.Type == "integer")
                    return field;
                if (field.Value is IList<DataField> inner && FirstNumber(inner) is DataField nested)
                    return nested;
            }
            return null;
        }

        // Read past every cache the SDK keeps: the server's row, the field found by name at any depth.
        private static async Task<long> ReadFromServer(string rowId, string fieldName)
        {
            string url = $"{FlockClient.Instance.GetVersionedApiUrl()}/{FlockEndpoints.PlayerDataById(rowId)}";
            JObject row = await FlockHttpClient.GetAsync<JObject>(url, FlockClient.Instance.GetBaseHeaders(), CancellationToken.None);
            JToken field = row.SelectToken($"$..[?(@.field_name == '{fieldName}')].value");
            Assert.IsNotNull(field, $"The server's row has no field {fieldName}.");
            return field.Value<long>();
        }

        private static IEnumerator Await(Task task, float seconds = 30f)
        {
            yield return Settle(task, seconds);
            if (!task.IsFaulted)
                yield break;
            // An Assume, Ignore or Assert inside the awaited code keeps its own result.
            Exception failure = task.Exception.GetBaseException();
            if (failure is ResultStateException)
                throw failure;
            Assert.Fail(failure.ToString());
        }

        private static IEnumerator Settle(Task task, float seconds = 30f)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(task.IsCompleted, $"Ended within {seconds} s");
        }
    }
}
