using System;
using System.Collections.Generic;
using System.IO;
using Flock;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // Asset metadata offline behaviour (ASST): the asset index is persisted to the snapshot store on the online
    // fetch, then served from disk on a later offline launch. The FlockAssetCache file cache itself is covered by
    // FlockAssetProviderTests. "Offline" here is the reachability seam.
    public class FlockAssetOfflineTests
    {
        private const string SharedPrefix = "flock_asset_";

        // Launch 1 fetches the asset list online (persisting the disk index); returns the shared cache dir.
        private static string FetchOnlineThenShutdown(params string[] assetIds)
        {
            string sharedDir = Path.Combine(Path.GetTempPath(), SharedPrefix + Guid.NewGuid().ToString("N"));

            List<string> items = new List<string>();
            foreach (string id in assetIds)
                items.Add("{\"id\":\"" + id + "\"}");

            FlockFakeTransport t1 = new FlockFakeTransport();
            t1.On(FlockEndpoints.Asset, FlockFakeTransport.Ok("{\"result\":[" + string.Join(",", items) + "]}"));
            FlockTestClient h1 = FlockTestClient.Create(t1, config => config.OfflineCacheDirectory = sharedDir);
            h1.SetReachable(true);
            List<AssetSchema> online = h1.Run(() => h1.Client.Asset.GetAllAsync());
            Assert.AreEqual(assetIds.Length, online.Count, "Online fetch returns the full list.");
            FlockClient.Shutdown(); // relaunch: leave the disk index intact (do NOT Dispose -> would delete the dir)
            return sharedDir;
        }

        // ---- ASST-03: metadata served from the disk index on a later offline launch ----
        [Test]
        public void GetAll_Offline_ServedFromDiskIndex_NoNetwork()
        {
            string sharedDir = FetchOnlineThenShutdown("a1", "a2");
            try
            {
                FlockFakeTransport t2 = new FlockFakeTransport();
                using (FlockTestClient h2 = FlockTestClient.Create(t2, config => config.OfflineCacheDirectory = sharedDir))
                {
                    h2.SetReachable(false);

                    List<AssetSchema> offline = h2.Run(() => h2.Client.Asset.GetAllAsync());

                    Assert.AreEqual(2, offline.Count, "Metadata served from the disk index while offline.");
                    Assert.AreEqual(0, t2.CountTo(FlockEndpoints.Asset), "No network fetch when offline with a disk index.");
                }
            }
            finally
            {
                if (Directory.Exists(sharedDir))
                    Directory.Delete(sharedDir, true);
            }
        }

        // ---- ASST-04: after the catalog is loaded offline, a by-id read is served without the network ----
        [Test]
        public void GetById_Offline_AfterCatalogLoad_ServedWithoutNetwork()
        {
            string sharedDir = FetchOnlineThenShutdown("a1", "a2");
            try
            {
                FlockFakeTransport t2 = new FlockFakeTransport();
                using (FlockTestClient h2 = FlockTestClient.Create(t2, config => config.OfflineCacheDirectory = sharedDir))
                {
                    h2.SetReachable(false);

                    // Load the catalog from disk (offline) — proven by GetAll_Offline — then read a single asset.
                    h2.Run(() => h2.Client.Asset.GetAllAsync());
                    AssetSchema a = h2.Run(() => h2.Client.Asset.GetByIdAsync("a1"));

                    Assert.IsNotNull(a, "By-id read served from the offline-loaded catalog.");
                    Assert.AreEqual(0, t2.CountTo(FlockEndpoints.AssetById("a1")), "No network for a by-id read after the catalog is loaded offline.");
                }
            }
            finally
            {
                if (Directory.Exists(sharedDir))
                    Directory.Delete(sharedDir, true);
            }
        }

        // ---- ASST: cold cache + offline -> throws (nothing to serve) ----
        [Test]
        public void GetAll_ColdOffline_Throws()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                transport.GoOffline(); // transport-level failure, no disk index yet

                Assert.Catch<FlockNetworkException>(() => h.Run(() => h.Client.Asset.GetAllAsync()));
            }
        }
    }
}
