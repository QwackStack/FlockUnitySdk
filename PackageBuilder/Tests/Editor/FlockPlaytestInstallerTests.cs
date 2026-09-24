using System.IO;
using Flock.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // The Playtesting tab installs the playtest released with this Flock version, and says why when it cannot.
    public class FlockPlaytestInstallerTests
    {
        [TearDown]
        public void RestoreTheReleasePage() => FlockPlaytestInstaller.SetReleaseDownloadRootForTesting(null);

        [Test]
        public void TheDownloadIsTheReleaseAssetForThatExactVersion()
        {
            Assert.AreEqual(
                "https://github.com/QwackStack/FlockUnitySDK/releases/download/v1.42.0/ProtokitePlaytest-1.42.0.unitypackage",
                FlockPlaytestInstaller.UnityPackageUrl("1.42.0"));
        }

        [Test]
        public void ADownloadRootForTestingReplacesTheReleasePage()
        {
            FlockPlaytestInstaller.SetReleaseDownloadRootForTesting("http://127.0.0.1:8765/");
            Assert.AreEqual("http://127.0.0.1:8765/v2.0.1/ProtokitePlaytest-2.0.1.unitypackage", FlockPlaytestInstaller.UnityPackageUrl("2.0.1"));
        }

        [Test]
        public void TheInstalledVersionMustMatchFlocksExactly()
        {
            Assert.AreEqual(PlaytestInstallState.NotInstalled, FlockPlaytestInstaller.StateFor(null, "1.42.0"));
            Assert.AreEqual(PlaytestInstallState.InstalledAtFlocksVersion,
                FlockPlaytestInstaller.StateFor(new InstalledPlaytest { Version = "1.42.0" }, "1.42.0"));
            Assert.AreEqual(PlaytestInstallState.InstalledAtAnotherVersion,
                FlockPlaytestInstaller.StateFor(new InstalledPlaytest { Version = "1.41.0" }, "1.42.0"));
            Assert.AreEqual(PlaytestInstallState.InstalledAtAnotherVersion,
                FlockPlaytestInstaller.StateFor(new InstalledPlaytest { Version = null }, "1.42.0"), "An unreadable version is not a match");
        }

        [Test]
        public void OnlyASuccessfulDownloadIsImported()
        {
            Assert.IsNull(FlockPlaytestInstaller.DownloadFailureMessage(200, null, "1.42.0"));

            string missing = FlockPlaytestInstaller.DownloadFailureMessage(404, null, "1.42.0");
            StringAssert.Contains("No Protokite Playtest was released with Flock 1.42.0", missing);

            StringAssert.Contains("HTTP 500", FlockPlaytestInstaller.DownloadFailureMessage(500, null, "1.42.0"));
            StringAssert.Contains("HTTP 500", FlockPlaytestInstaller.DownloadFailureMessage(500, "HTTP/1.1 500 Internal Server Error", "1.42.0"));
            StringAssert.Contains("Failed to write data",
                FlockPlaytestInstaller.DownloadFailureMessage(200, "Failed to write data", "1.42.0"),
                "A file that could not be written whole is not imported, though the server answered 200");
            StringAssert.Contains("Cannot resolve destination host",
                FlockPlaytestInstaller.DownloadFailureMessage(0, "Cannot resolve destination host", "1.42.0"));
            Assert.IsNotNull(FlockPlaytestInstaller.DownloadFailureMessage(200, "Connection reset", "1.42.0"),
                "A transport error is a failure even when a status arrived");
            Assert.IsNotNull(FlockPlaytestInstaller.DownloadFailureMessage(0, null, "1.42.0"), "No status at all is not a success");
        }

        [Test]
        public void EveryResultButSuccessCarriesAnError()
        {
            Assert.IsNull(FlockPlaytestInstaller.DownloadError(UnityEngine.Networking.UnityWebRequest.Result.Success, null));
            Assert.AreEqual("disk full", FlockPlaytestInstaller.DownloadError(UnityEngine.Networking.UnityWebRequest.Result.DataProcessingError, "disk full"));
            Assert.AreEqual("timeout", FlockPlaytestInstaller.DownloadError(UnityEngine.Networking.UnityWebRequest.Result.ConnectionError, "timeout"));
            Assert.AreEqual("ProtocolError", FlockPlaytestInstaller.DownloadError(UnityEngine.Networking.UnityWebRequest.Result.ProtocolError, null),
                "A failure with no text is still a failure");
        }

        [Test]
        public void TheInstalledPlaytestIsFoundWithItsVersion()
        {
            InstalledPlaytest installed = FlockPlaytestInstaller.FindInstalled();
            if (installed == null)
                Assert.Ignore("This project has no Protokite Playtest installed.");

            string packageJson = Path.Combine(installed.RootFolder, "package.json");
            Assert.IsTrue(File.Exists(packageJson), "The root folder is the package's own: " + installed.RootFolder);
            Assert.AreEqual((string)JObject.Parse(File.ReadAllText(packageJson))["name"], FlockPlaytestInstaller.PackageName);
            Assert.AreEqual((string)JObject.Parse(File.ReadAllText(packageJson))["version"], installed.Version);
        }

        [Test]
        public void ThePlaytestIsReleasedAtFlocksVersion()
        {
            InstalledPlaytest installed = FlockPlaytestInstaller.FindInstalled();
            if (installed == null)
                Assert.Ignore("This project has no Protokite Playtest installed.");
            Assert.AreEqual(FlockPlaytestInstaller.FlockVersion, installed.Version,
                "The playtest in this repository carries the Flock SDK's version");
        }
    }
}
