using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Flock.Editor
{
    /// <summary>Where the Protokite Playtest package is in this project, if anywhere.</summary>
    internal sealed class InstalledPlaytest
    {
        /// <summary>The package's version, read from its package.json.</summary>
        public string Version;
        /// <summary>The package's root folder as the project sees it, e.g. "Assets/ProtokitePlaytest" or "Packages/com.protokite.playtest".</summary>
        public string RootFolder;
    }

    /// <summary>What the Playtesting tab offers, given what is installed.</summary>
    internal enum PlaytestInstallState
    {
        NotInstalled,
        InstalledAtFlocksVersion,
        InstalledAtAnotherVersion
    }

    /// <summary>Installs, updates and removes the Protokite Playtest package for Flock's Playtesting tab. The playtest's runtime never depends on this.</summary>
    internal static class FlockPlaytestInstaller
    {
        internal const string PackageName = "com.protokite.playtest";
        internal const string AssemblyName = "Protokite.Playtest";
        internal const string SettingsMenuPath = "Protokite/Playtest/Settings";
        private const string ReleaseDownloadRoot = "https://github.com/QwackStack/FlockUnitySDK/releases/download";

        private const int DownloadTimeoutSeconds = 120;

        private static string _releaseDownloadRootForTesting;

        /// <summary>The Flock SDK version this editor is running, which is the playtest version the tab installs.</summary>
        internal static string FlockVersion => FlockSdkVersion.Current;

        /// <summary>Points downloads at a local server; null restores the GitHub release page.</summary>
        internal static void SetReleaseDownloadRootForTesting(string root) => _releaseDownloadRootForTesting = root;

        /// <summary>The playtest package released beside this Flock version.</summary>
        internal static string UnityPackageUrl(string version)
        {
            string root = _releaseDownloadRootForTesting ?? ReleaseDownloadRoot;
            return $"{root.TrimEnd('/')}/v{version}/ProtokitePlaytest-{version}.unitypackage";
        }

        internal static PlaytestInstallState StateFor(InstalledPlaytest installed, string flockVersion)
        {
            if (installed == null)
                return PlaytestInstallState.NotInstalled;
            return string.Equals(installed.Version, flockVersion, StringComparison.Ordinal)
                ? PlaytestInstallState.InstalledAtFlocksVersion
                : PlaytestInstallState.InstalledAtAnotherVersion;
        }

        /// <summary>The installed package, found from its assembly definition wherever it was put; null when there is none.</summary>
        internal static InstalledPlaytest FindInstalled()
        {
            string asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(AssemblyName);
            if (string.IsNullOrEmpty(asmdefPath))
                return null;

            // The runtime assembly sits in <root>/Runtime/.
            string rootFolder = Path.GetDirectoryName(Path.GetDirectoryName(asmdefPath))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(rootFolder))
                return null;
            return new InstalledPlaytest { RootFolder = rootFolder, Version = ReadVersion(Path.Combine(rootFolder, "package.json")) };
        }

        private static string ReadVersion(string packageJsonPath)
        {
            try
            {
                return File.Exists(packageJsonPath) ? (string)JObject.Parse(File.ReadAllText(packageJsonPath))["version"] : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Whether this editor session imported the playtest; the script reload that follows the import clears it.</summary>
        internal static bool InstalledThisSession { get; private set; }

        /// <summary>The error a finished download reports, or null when it succeeded. Any result other than success counts, a failed disk write included.</summary>
        internal static string DownloadError(UnityWebRequest.Result result, string error)
        {
            if (result == UnityWebRequest.Result.Success)
                return null;
            return string.IsNullOrEmpty(error) ? result.ToString() : error;
        }

        /// <summary>What to tell the studio when a download ends without a whole file; null means the download succeeded.</summary>
        internal static string DownloadFailureMessage(long statusCode, string downloadError, string version)
        {
            if (statusCode >= 200 && statusCode < 300 && string.IsNullOrEmpty(downloadError))
                return null;
            if (statusCode == 404)
                return $"No Protokite Playtest was released with Flock {version}. Update the Flock SDK, or install the playtest from the release page.";
            if (!string.IsNullOrEmpty(downloadError) && (statusCode < 300 || statusCode == 0))
                return $"Downloading Protokite Playtest {version} failed: {downloadError}. Check your connection and free disk space, then try again.";
            if (statusCode > 0)
                return $"Downloading Protokite Playtest {version} failed (HTTP {statusCode}). Try again, or install it from the release page.";
            return $"Downloading Protokite Playtest {version} failed. Check your connection and try again.";
        }

        /// <summary>
        /// Downloads the playtest released with this Flock version and imports it, in one call with a progress bar the studio
        /// can cancel. It does not return until it is done: a download left running across a script reload would be dropped,
        /// because the reload clears everything that was waiting for it.
        /// </summary>
        /// <param name="replacing">An installed copy under Assets/ to delete once the new one has downloaded, so files a newer version dropped do not linger.</param>
        internal static void Install(Action<string, MessageType> report, InstalledPlaytest replacing = null)
        {
            string version = FlockVersion;
            // In the project's Temp folder, which the editor clears when it closes: the import may still be reading it after this returns.
            string file = Path.GetFullPath(Path.Combine("Temp", $"ProtokitePlaytest-{version}-{Guid.NewGuid():N}.unitypackage"));
            try
            {
                using (UnityWebRequest download = new UnityWebRequest(UnityPackageUrl(version), UnityWebRequest.kHttpVerbGET))
                {
                    download.downloadHandler = new DownloadHandlerFile(file) { removeFileOnAbort = true };
                    download.timeout = DownloadTimeoutSeconds;
                    UnityWebRequestAsyncOperation sending = download.SendWebRequest();
                    while (!sending.isDone)
                    {
                        if (EditorUtility.DisplayCancelableProgressBar("Protokite Playtest", $"Downloading version {version}...", download.downloadProgress))
                        {
                            download.Abort();
                            report("The Protokite Playtest download was cancelled.", MessageType.Warning);
                            return;
                        }
                        System.Threading.Thread.Sleep(50);
                    }

                    string failure = DownloadFailureMessage(download.responseCode, DownloadError(download.result, download.error), version);
                    if (failure != null)
                    {
                        report(failure, MessageType.Error);
                        return;
                    }
                }

                EditorUtility.ClearProgressBar();
                // Only now that the new version is on disk: a failed download leaves the old one working.
                if (replacing != null && replacing.RootFolder.StartsWith("Assets/", StringComparison.Ordinal))
                    AssetDatabase.DeleteAsset(replacing.RootFolder);
                // Imported without the dialog: this is the one package, at the version the studio asked for.
                AssetDatabase.ImportPackage(file, false);
                InstalledThisSession = true;
                report($"Protokite Playtest {version} is installed. Unity compiles it next; then open its settings to switch it on.", MessageType.Info);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>Removes the installed package, however it was installed. The project's playtest settings asset is kept.</summary>
        internal static void Remove(InstalledPlaytest installed)
        {
            if (installed.RootFolder.StartsWith("Assets/", StringComparison.Ordinal))
            {
                AssetDatabase.DeleteAsset(installed.RootFolder);
                return;
            }

            PackageInfo package = PackageInfo.FindForAssetPath(installed.RootFolder);
            if (package != null && package.source == UnityEditor.PackageManager.PackageSource.Embedded)
            {
                // An embedded package is a folder in the project's Packages/, which Package Manager does not delete.
                FileUtil.DeleteFileOrDirectory(package.resolvedPath);
                UnityEditor.PackageManager.Client.Resolve();
                return;
            }
            UnityEditor.PackageManager.Client.Remove(PackageName);
        }
    }
}
