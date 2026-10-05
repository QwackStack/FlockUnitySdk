using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    /// <summary>A signed download link lasts minutes while an asset record is kept all session and between launches: a refused link is fetched fresh, once.</summary>
    public class FlockAssetLinkRefreshTests
    {
        private const string AssetId = "asset-1";
        private const string Expired = "<Error><Code>AccessDenied</Code><Message>Request has expired</Message></Error>";

        private FlockLocalStorage _storage;
        private FlockTestClient _h;
        private string _cacheFolder;

        [SetUp]
        public void SetUp()
        {
#if UNITY_2022_1_OR_NEWER
            // Unity refuses plain http only from 2022.1.
            if (PlayerSettings.insecureHttpOption == InsecureHttpOption.NotAllowed)
                Assert.Ignore("This project refuses plain http, which the storage stand-in on this machine answers on.");
#endif
            _storage = new FlockLocalStorage();
            _storage.AnswerFor("stale", 403, Expired);
            _storage.AnswerFor("fresh", 200, "the asset");
            _cacheFolder = Path.Combine(Path.GetTempPath(), "flock_asset_links_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            _storage?.Dispose();
            if (Directory.Exists(_cacheFolder))
                Directory.Delete(_cacheFolder, true);
        }

        private static string Record(string url, string updatedAt = "2026-09-01T00:00:00Z")
            => "{\"id\":\"" + AssetId + "\",\"name\":\"banner\",\"extension_type\":\"txt\",\"s3_download_url\":\"" + url + "\",\"updated_at\":\"" + updatedAt + "\"}";

        // The list hands out a link that has expired; the record read on its own hands out the one given.
        private void Start(string freshRecordUrl, FlockHttpResponse byIdAnswer = null)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            // The by-id route first: the fake answers the first route whose fragment the address contains.
            transport.On(FlockEndpoints.AssetById(AssetId), byIdAnswer ?? FlockFakeTransport.Ok("{\"result\":" + Record(freshRecordUrl, "2026-09-02T00:00:00Z") + "}"));
            transport.On(FlockEndpoints.Asset, FlockFakeTransport.Ok("{\"result\":[" + Record(_storage.Url + "stale") + "]}"));
            _h = FlockTestClient.Create(transport, config => config.AssetCacheDirectory = _cacheFolder);
            _h.SetReachable(true);
        }

        private AssetSchema ListedRecord() => _h.Run(() => _h.Client.Asset.GetAllAsync())[0];

        private int RecordReads => _h.Transport.CountTo(FlockEndpoints.AssetById(AssetId));

        [UnityTest]
        public IEnumerator AnExpiredLinkIsFetchedAgainAndTheDownloadSucceeds()
        {
            Start(_storage.Url + "fresh");
            Task<string> download = _h.Client.Asset.DownloadAsync<string>(ListedRecord());
            yield return FlockTestWait.Until(() => download.IsCompleted, "Ended within 20 s", 20);

            Assert.AreEqual("the asset", download.Result);
            CollectionAssert.AreEqual(new List<string> { "/stale", "/fresh" }, _storage.Paths());
            Assert.AreEqual(1, RecordReads, "The record was read once, for its fresh link");
            Assert.IsTrue(_h.Client.Asset.IsCached(AssetId, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)), "Kept under the fresh record's version");
        }

        [UnityTest]
        public IEnumerator TheFreshRecordIsKeptSoTheNextReadNeedsNoRequest()
        {
            Start(_storage.Url + "fresh");
            Task<string> download = _h.Client.Asset.DownloadAsync<string>(ListedRecord());
            yield return FlockTestWait.Until(() => download.IsCompleted, "Ended within 20 s", 20);
            Assert.AreEqual(1, RecordReads, "Precondition");

            AssetSchema kept = _h.Run(() => _h.Client.Asset.GetByIdAsync(AssetId));
            Assert.AreEqual(_storage.Url + "fresh", kept.S3DownloadUrl);
            Assert.AreEqual(1, RecordReads, "Answered from the record kept, with no request");
        }

        [UnityTest]
        public IEnumerator ALinkRefusedAgainAfterAFreshOneIsNotTriedAThirdTime()
        {
            Start(_storage.Url + "stale/again");
            Task<string> download = _h.Client.Asset.DownloadAsync<string>(ListedRecord());
            yield return FlockTestWait.Until(() => download.IsCompleted, "Ended within 20 s", 20);

            FlockNetworkException refused = download.Exception?.GetBaseException() as FlockNetworkException;
            Assert.IsNotNull(refused, "The refusal is reported: " + download.Exception);
            Assert.AreEqual(403, refused.StatusCode);
            Assert.AreEqual(2, _storage.Count, "The expired link, then the fresh one, and no more");
            Assert.AreEqual(1, RecordReads);
        }

        [UnityTest]
        public IEnumerator AnObjectStorageDoesNotHaveIsNotFetchedAgain()
        {
            _storage.AnswerFor("missing", 404, "<Error><Code>NoSuchKey</Code></Error>");
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.AssetById(AssetId), FlockFakeTransport.Ok("{\"result\":" + Record(_storage.Url + "fresh") + "}"));
            transport.On(FlockEndpoints.Asset, FlockFakeTransport.Ok("{\"result\":[" + Record(_storage.Url + "missing") + "]}"));
            _h = FlockTestClient.Create(transport, config => config.AssetCacheDirectory = _cacheFolder);
            _h.SetReachable(true);

            Task<string> download = _h.Client.Asset.DownloadAsync<string>(ListedRecord());
            yield return FlockTestWait.Until(() => download.IsCompleted, "Ended within 20 s", 20);

            Assert.AreEqual(404, (download.Exception?.GetBaseException() as FlockNetworkException)?.StatusCode, "The storage's own answer: " + download.Exception);
            Assert.AreEqual(0, RecordReads, "Only a refused link means the record went stale");
        }

        [UnityTest]
        public IEnumerator WhenTheFreshLinkCannotBeFetchedTheDownloadsOwnRefusalIsReported()
        {
            Start(null, FlockFakeTransport.Status(500, "{\"detail\":\"boom\"}"));
            Task<string> download = _h.Client.Asset.DownloadAsync<string>(ListedRecord());
            yield return FlockTestWait.Until(() => download.IsCompleted, "Ended within 20 s", 20);

            Assert.AreEqual(403, (download.Exception?.GetBaseException() as FlockNetworkException)?.StatusCode,
                "The failure of what the caller asked for, not of the fetch behind it: " + download.Exception);
            Assert.AreEqual(1, RecordReads, "Precondition: a fresh record was asked for");
            Assert.AreEqual(1, _storage.Count);
        }

        [UnityTest]
        public IEnumerator APreloadFetchesAnExpiredLinkAgain()
        {
            Start(_storage.Url + "fresh");
            Task preload = _h.Client.Asset.PreloadAsync(ListedRecord());
            yield return FlockTestWait.Until(() => preload.IsCompleted, "Ended within 20 s", 20);

            Assert.IsFalse(preload.IsFaulted, "Preloaded: " + preload.Exception);
            CollectionAssert.AreEqual(new List<string> { "/stale", "/fresh" }, _storage.Paths());
            Assert.IsTrue(_h.Client.Asset.IsCached(AssetId, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)));
        }
    }
}
