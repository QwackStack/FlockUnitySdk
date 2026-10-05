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
