using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Flock.Editor;
using NUnit.Framework;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Flock.Tests
{
    // The release builders ship with every git install of the SDK, so nothing in the SDK may put them in a studio's menus.
    public class FlockMaintainerToolingTests
    {
        [Test]
        public void TheSdkShowsStudiosNoQwacksDevMenu()
        {
            string[] menuItems = typeof(FlockPlaytestInstaller).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .SelectMany(method => method.GetCustomAttributes<MenuItem>())
                .Select(item => item.menuItem)
                .ToArray();

            Assert.IsNotEmpty(menuItems, "Control: the SDK's own menu items are found");
            CollectionAssert.Contains(menuItems, "Flock/Settings", "Control: the SDK's own menu items are found");
            Assert.IsEmpty(menuItems.Where(item => item.StartsWith("Qwacks Dev")).ToArray());
        }

        [Test]
        public void TheReleaseCarriesEveryProviderTheEditorTheSamplesAndTheDocs()
        {
            FlockPackageBuilder.PackageContents release = FlockPackageBuilder.ReleaseContents("9.8.7", "release-out");

            Assert.AreEqual("9.8.7", release.Version);
            Assert.AreEqual("release-out", release.OutputFolder);
            Assert.IsTrue(release.IncludeEditor, "The editor ships");
            Assert.IsTrue(release.IncludeSamples, "The samples ship");
            Assert.IsTrue(release.IncludeDocs, "The docs ship");
            Assert.IsNotEmpty(FlockProviderManifest.Providers, "Control: there are providers to ship");
            foreach (FlockProviderManifest.Entry entry in FlockProviderManifest.Providers)
                Assert.IsTrue(release.SelectedProviders.TryGetValue(entry.Id, out bool selected) && selected, entry.Id + " ships in the release");
        }

        [Test]
        public void TheReleaseFolderIsTheArgumentAfterReleaseOut()
        {
            Assert.AreEqual("/github/workspace/out", FlockPackageBuilder.ReleaseFolderFromArguments(
                new[] { "Unity", "-batchmode", "-releaseOut", "/github/workspace/out", "-quit" }));
            Assert.IsNull(FlockPackageBuilder.ReleaseFolderFromArguments(new[] { "Unity", "-batchmode" }), "No flag, no folder");
            Assert.IsNull(FlockPackageBuilder.ReleaseFolderFromArguments(new[] { "Unity", "-releaseOut" }), "A flag with nothing after it");
            Assert.IsNull(FlockPackageBuilder.ReleaseFolderFromArguments(new[] { "Unity", "-releaseOut", " " }), "A blank folder");
            Assert.IsNull(FlockPackageBuilder.ReleaseFolderFromArguments(new[] { "Unity", "-releaseOut", "-quit" }), "Another flag is not a folder");
        }

        [System.Serializable]
        private class AssemblyDefinition
        {
            public string name;
            public string[] references;
        }

        private static AssemblyDefinition Read(string asmdef) => UnityEngine.JsonUtility.FromJson<AssemblyDefinition>(File.ReadAllText(asmdef));

        // A sample left in a release whose provider was taken out would name code that is not there, where a game has the netcode.
        [Test]
        public void ASampleBuiltOnAProvidersCode_LeavesTheReleaseWithThatProvider()
        {
            PackageInfo package = PackageInfo.FindForAssetPath("Packages/com.flock.sdk");
            Assert.IsNotNull(package, "Control: the SDK package is in this project");
            string root = package.resolvedPath.Replace('\\', '/').TrimEnd('/') + "/";
            Dictionary<string, FlockProviderManifest.Entry> providerOf = new Dictionary<string, FlockProviderManifest.Entry>();
            foreach (string asmdef in Directory.GetFiles(root + "Runtime", "*.asmdef", SearchOption.AllDirectories))
            {
                string relative = asmdef.Replace('\\', '/').Substring(root.Length);
                FlockProviderManifest.Entry owner = FlockProviderManifest.Providers.FirstOrDefault(entry => entry.Folders != null && entry.Folders.Any(relative.StartsWith));
                if (owner != null)
                    providerOf[Read(asmdef).name] = owner;
            }
            Assert.IsTrue(providerOf.ContainsKey("Flock.Multiplayer.Netcode"), "Control: the netcode adapter is found inside Multiplayer's folders");

            int samplesBuiltOnAProvider = 0;
            foreach (string asmdef in Directory.GetFiles(root + "Samples", "*.asmdef", SearchOption.AllDirectories))
            {
                string relative = asmdef.Replace('\\', '/').Substring(root.Length);
                foreach (string reference in Read(asmdef).references ?? new string[0])
                {
                    if (!providerOf.TryGetValue(reference, out FlockProviderManifest.Entry owner))
                        continue;
                    samplesBuiltOnAProvider++;
                    Assert.IsTrue(owner.Folders.Any(relative.StartsWith), $"{relative} uses {reference}, so it must leave a release with the {owner.Id} provider");
                }
            }
            Assert.Greater(samplesBuiltOnAProvider, 0, "Control: a sample built on a provider's code is found");
        }

        [Test]
        public void ThePlaytestReleaseShipsItsSampleAndNotItsTests()
        {
            PackageInfo package = PackageInfo.FindForAssetPath("Packages/" + FlockPlaytestInstaller.PackageName);
            if (package == null)
                Assert.Ignore("The Protokite Playtest package is not in this project, so there is no release to build.");

            string[] files = FlockPlaytestPackageBuilder.ShippedFiles(package.resolvedPath);

            Assert.IsTrue(files.Any(file => file.StartsWith("Samples/") && file.EndsWith(".cs")), "The sample's script ships");
            Assert.IsTrue(files.Any(file => file.StartsWith("Runtime/")), "Control: the runtime ships");
            Assert.IsFalse(files.Any(file => file.StartsWith("Tests/")), "The tests stay in the repository");
        }

        [Test]
        public void TheReleasesPanelSettingsNameTheThemeTheReleaseCarries()
        {
            PackageInfo package = PackageInfo.FindForAssetPath("Packages/" + FlockPlaytestInstaller.PackageName);
            if (package == null)
                Assert.Ignore("The Protokite Playtest package is not in this project, so there is no release to build.");
            string source = package.resolvedPath;
            Dictionary<string, string> newGuids = FlockPlaytestPackageBuilder.NewGuidsByOldGuid(source, FlockPlaytestPackageBuilder.ShippedFiles(source));
            string themeGuid = Regex.Match(File.ReadAllText(Path.Combine(source, "Runtime/Resources/ProtokitePlaytestPanelTheme.tss.meta")), @"^guid:\s+(\w+)", RegexOptions.Multiline).Groups[1].Value;
            string settings = File.ReadAllText(Path.Combine(source, "Runtime/Resources/ProtokitePlaytestPanelSettings.asset"));
            StringAssert.Contains("guid: " + themeGuid, settings, "Control: the package's own settings name its theme");

            // Measured: the release gave the theme a new GUID and left the settings naming the old one, so its panels had no theme.
            string staged = FlockPlaytestPackageBuilder.WithNewGuids(settings, newGuids);
            Assert.AreNotEqual(themeGuid, newGuids[themeGuid], "Control: the release gives the theme a GUID of its own");
            StringAssert.Contains("guid: " + newGuids[themeGuid], staged);
            StringAssert.DoesNotContain(themeGuid, staged);
            StringAssert.Contains("guid: 0000000000000000e000000000000000", staged, "Unity's own script reference is left as it is");
        }
    }
}
