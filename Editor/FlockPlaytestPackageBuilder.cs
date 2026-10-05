using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Flock.Editor
{
    /// <summary>Maintainer tooling: builds ProtokitePlaytest-&lt;version&gt;.unitypackage, which lands in a studio's Assets/ProtokitePlaytest.</summary>
    internal static class FlockPlaytestPackageBuilder
    {
        private const string StagingRoot = "Assets/ProtokitePlaytest/";
        private const string PackagePath = "Packages/" + FlockPlaytestInstaller.PackageName;

        // Only what a studio needs: the tests stay in the repository.
        private static readonly string[] ShippedFolders = { "Runtime", "Editor", "Samples" };
        private static readonly string[] ShippedRootFiles = { "package.json", "README.md", "CHANGELOG.md", "LICENSE.md" };
        private static readonly Regex MetaGuid = new Regex(@"^(guid:\s+)(\w+)", RegexOptions.Multiline);
        // How a Unity text asset (a .asset, .prefab or scene) names another file.
        private static readonly Regex GuidReference = new Regex(@"(guid:\s*)([0-9a-f]{32})");

        /// <summary>Asks for a folder and builds there; the maintainers' own project calls it from Qwacks Dev > Build Protokite Playtest Package.</summary>
        internal static void BuildFromMenu()
        {
            string folder = EditorUtility.SaveFolderPanel("Save ProtokitePlaytest .unitypackage to", "", "");
            if (string.IsNullOrEmpty(folder))
                return;
            string built = Build(folder);
            EditorUtility.DisplayDialog("Protokite Playtest", built != null ? "Built " + built : "The build failed; see the Console.", "OK");
        }

        /// <summary>For batch mode: -executeMethod Flock.Editor.FlockPlaytestPackageBuilder.BuildFromCommandLine -playtestOut &lt;folder&gt;.</summary>
        internal static void BuildFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-playtestOut");
            string folder = index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.GetFullPath("Builds");
            EditorApplication.Exit(Build(folder) != null ? 0 : 1);
        }

        /// <summary>Stages the playtest under Assets/, exports it and cleans up; returns the file written, or null.</summary>
        internal static string Build(string outputFolder)
        {
            PackageInfo package = PackageInfo.FindForAssetPath(PackagePath);
            if (package == null || string.IsNullOrEmpty(package.resolvedPath))
            {
                Debug.LogError($"Protokite Playtest is not in this project's packages, so there is nothing to build. Add {FlockPlaytestInstaller.PackageName} to Packages/manifest.json.");
                return null;
            }

            string source = package.resolvedPath;
            string version = (string)JObject.Parse(File.ReadAllText(Path.Combine(source, "package.json")))["version"];
            Directory.CreateDirectory(outputFolder);
            string output = Path.Combine(outputFolder, $"ProtokitePlaytest-{version}.unitypackage");

            // The staged copy and the live package define the same assemblies until the staging is removed.
            EditorApplication.LockReloadAssemblies();
            try
            {
                CleanStaging();
                string[] shipped = ShippedFiles(source);
                Dictionary<string, string> newGuids = NewGuidsByOldGuid(source, shipped);
                foreach (string relative in shipped)
                    StageFile(source, relative, newGuids);
                AssetDatabase.Refresh();

                string[] assets = AssetDatabase.GetAllAssetPaths().Where(path => path.StartsWith(StagingRoot, StringComparison.Ordinal)).ToArray();
                AssetDatabase.ExportPackage(assets, output, ExportPackageOptions.Default);
                Debug.Log($"Built {output} ({assets.Length} assets).");
                return output;
            }
            finally
            {
                CleanStaging();
                AssetDatabase.Refresh();
                EditorApplication.UnlockReloadAssemblies();
            }
        }

        /// <summary>Every file the release carries, relative to the package folder.</summary>
        internal static string[] ShippedFiles(string source)
        {
            int prefix = source.Length + 1;
            return ShippedRootFiles.SelectMany(file => new[] { file, file + ".meta" })
                .Concat(ShippedFolders.Select(folder => folder + ".meta"))
                .Concat(ShippedFolders.SelectMany(folder => Directory.Exists(Path.Combine(source, folder))
                    ? Directory.GetFiles(Path.Combine(source, folder), "*", SearchOption.AllDirectories).Select(path => path.Substring(prefix).Replace('\\', '/'))
                    : Enumerable.Empty<string>()))
                .Where(relative => File.Exists(Path.Combine(source, relative)))
                .ToArray();
        }

        /// <summary>Each shipped .meta's GUID, mapped to the one its staged copy is given.</summary>
        internal static Dictionary<string, string> NewGuidsByOldGuid(string source, IEnumerable<string> shipped)
        {
            Dictionary<string, string> newGuids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string relative in shipped.Where(file => file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            {
                Match guid = MetaGuid.Match(File.ReadAllText(Path.Combine(source, relative)));
                if (guid.Success)
                    newGuids[guid.Groups[2].Value] = GuidFor(StagingRoot + relative);
            }
            return newGuids;
        }

        /// <summary>A Unity text asset with each shipped file it names given that file's new GUID; Unity's own and other packages' are left as they are.</summary>
        internal static string WithNewGuids(string content, IReadOnlyDictionary<string, string> newGuids)
        {
            return GuidReference.Replace(content, reference => newGuids.TryGetValue(reference.Groups[2].Value, out string renamed)
                ? reference.Groups[1].Value + renamed
                : reference.Value);
        }

        // Each .meta gets a GUID made from its staged path, so a studio updating from one release to the next keeps the same GUIDs,
        // and an asset naming another shipped file by GUID is given its new one, or it would name a file the release does not carry.
        private static void StageFile(string source, string relative, IReadOnlyDictionary<string, string> newGuids)
        {
            string from = Path.Combine(source, relative);
            string destination = StagingRoot + relative;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(destination, MetaGuid.Replace(File.ReadAllText(from), "${1}" + GuidFor(destination)));
            else if (IsUnityTextAsset(from))
                File.WriteAllText(destination, WithNewGuids(File.ReadAllText(from), newGuids));
            else
                File.Copy(from, destination, true);
        }

        // Unity writes its text assets as YAML, each starting with this line.
        private static bool IsUnityTextAsset(string path)
        {
            byte[] start = new byte[5];
            using (FileStream file = File.OpenRead(path))
                return file.Read(start, 0, start.Length) == start.Length && Encoding.ASCII.GetString(start) == "%YAML";
        }

        private static string GuidFor(string stagedPath)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("ProtokitePlaytest:" + stagedPath));
                StringBuilder text = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        private static void CleanStaging()
        {
            string folder = StagingRoot.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder))
                AssetDatabase.DeleteAsset(folder);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            if (File.Exists(folder + ".meta"))
                File.Delete(folder + ".meta");
        }
    }
}
