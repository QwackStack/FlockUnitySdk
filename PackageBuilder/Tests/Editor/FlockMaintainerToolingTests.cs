using System.Linq;
using System.Reflection;
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
    }
}
