using Flock;
using Flock.Tests.Support;
using NUnit.Framework;
using Protokite.Playtest.Editor;
using UnityEditor;
using UnityEngine;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestStatusTests
    {
        private static ProtokitePlaytestSettings Settings(bool enabled, string url)
        {
            ProtokitePlaytestSettings settings = ScriptableObject.CreateInstance<ProtokitePlaytestSettings>();
            settings.PlaytestingEnabled = enabled;
            settings.ProtokiteApiUrl = url;
            return settings;
        }

        [Test]
        public void NewSettingsHavePlaytestingOffAndTheProductionApiUrl()
        {
            ProtokitePlaytestSettings settings = ScriptableObject.CreateInstance<ProtokitePlaytestSettings>();
            Assert.IsFalse(settings.PlaytestingEnabled, "Off until a studio switches it on");
            Assert.AreEqual("https://api-protokite.qwacks.com", settings.ProtokiteApiUrl, "Production, so a studio never types it");
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void NoSettingsReadsAsTurnedOff()
        {
            Assert.AreEqual(ProtokitePlaytestStatus.TurnedOff, ProtokitePlaytest.StatusFor(null, true));
        }

        [TestCase(false, "http://localhost:8020", true, ProtokitePlaytestStatus.TurnedOff)]
        [TestCase(true, "", true, ProtokitePlaytestStatus.ProtokiteApiUrlMissing)]
        [TestCase(true, "   ", true, ProtokitePlaytestStatus.ProtokiteApiUrlMissing)]
        [TestCase(true, null, true, ProtokitePlaytestStatus.ProtokiteApiUrlMissing)]
        [TestCase(true, "localhost:8020", true, ProtokitePlaytestStatus.ProtokiteApiUrlUnusable)]
        [TestCase(true, "ftp://protokite.example", true, ProtokitePlaytestStatus.ProtokiteApiUrlUnusable)]
        [TestCase(true, "not a url", true, ProtokitePlaytestStatus.ProtokiteApiUrlUnusable)]
        [TestCase(true, "http://localhost:8020", false, ProtokitePlaytestStatus.WaitingForFlock)]
        [TestCase(true, "http://localhost:8020", true, ProtokitePlaytestStatus.FetchingPlaytestConfig)]
        [TestCase(true, "  https://api.protokite.example/  ", true, ProtokitePlaytestStatus.FetchingPlaytestConfig)]
        [TestCase(true, "http://local host:8020", true, ProtokitePlaytestStatus.ProtokiteApiUrlUnusable)]
        public void StatusFollowsTheSettingsAndFlock(bool enabled, string url, bool flockIsRunning, ProtokitePlaytestStatus expected)
        {
            ProtokitePlaytestSettings settings = Settings(enabled, url);
            Assert.AreEqual(expected, ProtokitePlaytest.StatusFor(settings, flockIsRunning));
            Object.DestroyImmediate(settings);
        }

        // Drives the status a game reads: the project's own settings asset and the real Flock client. Flock clears every
        // event subscription when it shuts down, so a status kept up to date by events would stay wrong after a restart.
        [Test]
        public void StatusReadsTheProjectSettingsAndFollowsFlockStartingAndStopping()
        {
            bool assetExisted = AssetDatabase.LoadAssetAtPath<ProtokitePlaytestSettings>(ProtokitePlaytestSettings.AssetPath) != null;
            ProtokitePlaytestSettings settings = ProtokitePlaytestSettingsMenu.FindOrCreateSettings();
            bool wasEnabled = settings.PlaytestingEnabled;
            string oldUrl = settings.ProtokiteApiUrl;
            try
            {
                settings.PlaytestingEnabled = false;
                Assert.AreEqual(ProtokitePlaytestStatus.TurnedOff, ProtokitePlaytest.Status, "The project's switch is read");

                settings.PlaytestingEnabled = true;
                settings.ProtokiteApiUrl = "http://localhost:8020";
                Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
                Assert.AreEqual(ProtokitePlaytestStatus.WaitingForFlock, ProtokitePlaytest.Status);

                using (FlockTestClient.Create(new FlockFakeTransport()))
                    Assert.AreEqual(ProtokitePlaytestStatus.FetchingPlaytestConfig, ProtokitePlaytest.Status, "Running, with no config fetched yet");
                Assert.AreEqual(ProtokitePlaytestStatus.WaitingForFlock, ProtokitePlaytest.Status, "After Flock shuts down");

                using (FlockTestClient.Create(new FlockFakeTransport()))
                    Assert.AreEqual(ProtokitePlaytestStatus.FetchingPlaytestConfig, ProtokitePlaytest.Status, "After Flock starts again");
            }
            finally
            {
                if (assetExisted)
                {
                    settings.PlaytestingEnabled = wasEnabled;
                    settings.ProtokiteApiUrl = oldUrl;
                    AssetDatabase.SaveAssets();
                }
                else
                {
                    AssetDatabase.DeleteAsset(ProtokitePlaytestSettings.AssetPath);
                }
            }
        }

        [Test]
        public void TheSettingsMenuCreatesTheAssetWithPlaytestingOffWhenThereIsNone()
        {
            if (AssetDatabase.LoadAssetAtPath<ProtokitePlaytestSettings>(ProtokitePlaytestSettings.AssetPath) != null)
                Assert.Ignore("This project already has playtest settings; the creation case needs a project without them.");

            try
            {
                ProtokitePlaytestSettings created = ProtokitePlaytestSettingsMenu.FindOrCreateSettings();
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<ProtokitePlaytestSettings>(ProtokitePlaytestSettings.AssetPath), "Created where Resources finds it");
                Assert.AreSame(created, ProtokitePlaytestSettings.Load(), "Found by the game at runtime");
                Assert.IsFalse(created.PlaytestingEnabled);
                // Without its script the saved asset shows as "missing script" and loads as nothing on the next launch.
                Assert.IsNotNull(MonoScript.FromScriptableObject(created), "The asset points at its script");
                Assert.AreSame(created, ProtokitePlaytestSettingsMenu.FindOrCreateSettings(), "A second call returns the same asset");
            }
            finally
            {
                AssetDatabase.DeleteAsset(ProtokitePlaytestSettings.AssetPath);
            }
        }
    }
}
