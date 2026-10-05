using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Flock.Editor
{
    public class FlockPackageBuilder : EditorWindow
    {
        private string _version = "1.0.0";
        private string _outputPath = "Build";
        private bool _includeEditor = true;
        private bool _includeSamples = true;
        private bool _includeDocs = true;
        private string _statusMessage = "";
        private MessageType _statusType = MessageType.None;
        private double _statusTimer;
        private GUIStyle _titleStyle;
        private GUIStyle _subtitleStyle;
        private GUIStyle _logoPlaceholderStyle;
        private Texture2D _flockLogo;

        // Provider id -> checked. Initialized lazily so new manifest entries auto-show.
        private Dictionary<string, bool> _providerSelection;
        private Vector2 _providersScroll;

        // Where files end up inside the consumer's project after they import the .unitypackage.
        // The build stages source here so AssetDatabase.ExportPackage bakes these paths into the
        // package — this is also the legacy in-Assets layout, so when source already lives here
        // we skip staging entirely.
        private const string StagingRoot = "Assets/FlockSDK/";
        private const string StagingRuntimeRsp = StagingRoot + "Runtime/csc.rsp";
        private const string StagingEditorRsp = StagingRoot + "Editor/csc.rsp";
        private const string StagingSamplesRsp = StagingRoot + "Samples/QuickStart/csc.rsp";
        private const string PackageName = "com.flock.sdk";

        // SDK-maintainer-only files. Excluded from every build regardless of provider
        // selection — consumers shouldn't see the Package Builder UI or the Qwacks Dev
        // menu, and the manifest is only useful to the builder itself.
        private static readonly string[] BuilderInternalFiles =
        {
            "Editor/FlockPackageBuilder.cs",
            "Editor/FlockPlaytestPackageBuilder.cs",
            "Editor/FlockProviderManifest.cs",
        };

        // No menu item here: a git install ships this file, so the maintainers' own project adds Qwacks Dev > Package Builder.
        public static void ShowWindow()
        {
            FlockPackageBuilder window = GetWindow<FlockPackageBuilder>("Flock Package Builder");
            window.minSize = new Vector2(420, 480);
            window.Show();
        }

        private void OnEnable()
        {
            LoadLogos();
            LoadVersionFromPackageJson();
            EnsureProviderSelectionInitialized();
        }

        private void EnsureProviderSelectionInitialized()
        {
            if (_providerSelection == null)
                _providerSelection = new Dictionary<string, bool>();

            // All providers default to checked. New manifest entries land here too.
            foreach (FlockProviderManifest.Entry entry in FlockProviderManifest.Providers)
            {
                if (!_providerSelection.ContainsKey(entry.Id))
                    _providerSelection[entry.Id] = true;
            }
        }

        private void InitStyles()
        {
            if (_titleStyle != null) return;

            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter,
                margin = new RectOffset(0, 0, 4, 0)
            };
            _subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                wordWrap = true
            };
            _logoPlaceholderStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
        }

        private void OnGUI()
        {
            InitStyles();
            EnsureProviderSelectionInitialized();

            GUILayout.Space(10);

            DrawHeader();
            GUILayout.Space(10);

            // Version
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Version", EditorStyles.boldLabel);
            GUILayout.Space(4);

            _version = EditorGUILayout.TextField(
                new GUIContent("Package Version", "Semantic version (e.g. 1.2.3). Syncs with package.json on build."),
                _version);

            EditorGUILayout.EndVertical();

            GUILayout.Space(8);

            // Output
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Output", EditorStyles.boldLabel);
            GUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            _outputPath = EditorGUILayout.TextField(
                new GUIContent("Output Path", "Directory where the .unitypackage will be saved"),
                _outputPath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string selected = EditorUtility.OpenFolderPanel("Select Output Folder", _outputPath, "");
                if (!string.IsNullOrEmpty(selected))
                {
                    _outputPath = selected;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            GUILayout.Space(8);

            // Contents
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Include", EditorStyles.boldLabel);
            GUILayout.Space(4);

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.Toggle("Runtime Core (required)", true);
            EditorGUI.EndDisabledGroup();

            _includeEditor = EditorGUILayout.Toggle("Editor", _includeEditor);
            _includeSamples = EditorGUILayout.Toggle("Samples", _includeSamples);
            _includeDocs = EditorGUILayout.Toggle("Documentation", _includeDocs);

            EditorGUILayout.EndVertical();

            GUILayout.Space(8);

            // Providers
            DrawProviderSelection();

            GUILayout.Space(10);

            // Status
            if (!string.IsNullOrEmpty(_statusMessage) && EditorApplication.timeSinceStartup < _statusTimer)
            {
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
                GUILayout.Space(5);
            }

            // Build button
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("Build Package", GUILayout.Height(35)))
            {
                BuildPackage();
            }
            GUI.backgroundColor = Color.white;
        }
        private void LoadLogos()
        {
            _flockLogo = QwacksEditorWindow.FindTextureByName("FlockLogo");
        }

        private void OnFocus()
        {
            LoadLogos();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            DrawLogoSlot(_flockLogo, "Flock", "FlockLogo.png");
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginVertical();
            GUILayout.Space(5);
            GUILayout.Label("Package Builder", _titleStyle);
            GUILayout.Label("Build .unitypackage releases of the Flock SDK", _subtitleStyle);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawLogoSlot(Texture2D logo, string label, string expectedFilename)
        {
            const float size = 56f;
            EditorGUILayout.BeginVertical(GUILayout.Width(size + 12));
            GUILayout.Space(4);

            Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));

            if (logo != null)
            {
                GUI.DrawTexture(rect, logo, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.12f));
                GUI.Label(rect, $"Drop\n{label}", _logoPlaceholderStyle);
                if (rect.Contains(Event.current.mousePosition))
                    GUI.tooltip = $"Add {expectedFilename} to the project (any folder).";
            }

            EditorGUILayout.EndVertical();
        }
        private void DrawProviderSelection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Providers", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("All", EditorStyles.miniButtonLeft, GUILayout.Width(40)))
                SetAllProviders(true);
            if (GUILayout.Button("None", EditorStyles.miniButtonRight, GUILayout.Width(50)))
                SetAllProviders(false);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                "Toggling a provider also toggles anything it depends on (or anything that depends on it).",
                EditorStyles.miniLabel);
            GUILayout.Space(2);

            _providersScroll = EditorGUILayout.BeginScrollView(_providersScroll, GUILayout.MaxHeight(180));
            foreach (FlockProviderManifest.Entry entry in FlockProviderManifest.Providers)
            {
                bool current = _providerSelection[entry.Id];
                string depHint = entry.DependsOn != null && entry.DependsOn.Length > 0
                    ? "  (requires: " + string.Join(", ", entry.DependsOn.Select(d => FormatId(d))) + ")"
                    : "";

                GUIContent label = new GUIContent(entry.DisplayName + depHint, entry.Description);
                bool next = EditorGUILayout.ToggleLeft(label, current);
                if (next != current)
                    SetProviderSelected(entry.Id, next);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private static string FormatId(string id)
        {
            FlockProviderManifest.Entry e = FlockProviderManifest.Find(id);
            return e != null ? e.DisplayName : id;
        }

        private void SetAllProviders(bool value)
        {
            foreach (FlockProviderManifest.Entry entry in FlockProviderManifest.Providers)
                _providerSelection[entry.Id] = value;
        }

        // Toggling propagates: turning ON pulls in deps; turning OFF pushes out dependents.
        // This is what stops the user shipping a Shop without the Analytics it calls into.
        private void SetProviderSelected(string id, bool value)
        {
            _providerSelection[id] = value;

            HashSet<string> propagate = new HashSet<string>();
            if (value)
                FlockProviderManifest.CollectDependenciesOf(id, propagate);
            else
                FlockProviderManifest.CollectDependentsOf(id, propagate);

            foreach (string other in propagate)
                _providerSelection[other] = value;
        }

        private void BuildPackage()
        {
            if (string.IsNullOrWhiteSpace(_version))
            {
                ShowStatus("Version is required.", MessageType.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(_outputPath))
            {
                ShowStatus("Output path is required.", MessageType.Error);
                return;
            }

            string sourceRoot = LocateSourceRoot();
            if (string.IsNullOrEmpty(sourceRoot))
            {
                ShowStatus(
                    $"Could not find SDK source. Expected the '{PackageName}' UPM package or '{StagingRoot}'.",
                    MessageType.Error);
                return;
            }

            SaveVersionToPackageJson(sourceRoot.Replace('\\', '/').TrimEnd('/'));

            string built = Build(new PackageContents(_version, _outputPath, _includeEditor, _includeSamples, _includeDocs, _providerSelection),
                out string problem);
            if (built == null)
            {
                ShowStatus(problem, MessageType.Error);
                return;
            }
            ShowStatus($"Package built: {Path.GetFileName(built)}", MessageType.Info);
            EditorUtility.RevealInFinder(built);
        }

        /// <summary>What one build puts in the package: its version and folder, the optional parts, and which providers ship.</summary>
        internal sealed class PackageContents
        {
            public string Version { get; }
            public string OutputFolder { get; }
            public bool IncludeEditor { get; }
            public bool IncludeSamples { get; }
            public bool IncludeDocs { get; }
            public IReadOnlyDictionary<string, bool> SelectedProviders { get; }

            public PackageContents(string version, string outputFolder, bool includeEditor, bool includeSamples, bool includeDocs,
                IDictionary<string, bool> selectedProviders)
            {
                Version = version;
                OutputFolder = outputFolder;
                IncludeEditor = includeEditor;
                IncludeSamples = includeSamples;
                IncludeDocs = includeDocs;
                SelectedProviders = new Dictionary<string, bool>(selectedProviders);
            }
        }

        /// <summary>What a release carries: every provider, the editor, the samples and the docs, at this version.</summary>
        internal static PackageContents ReleaseContents(string version, string outputFolder)
            => new PackageContents(version, outputFolder, true, true, true,
                FlockProviderManifest.Providers.ToDictionary(entry => entry.Id, entry => true));

        /// <summary>For batch mode: -executeMethod Flock.Editor.FlockPackageBuilder.BuildReleaseFromCommandLine -releaseOut &lt;folder&gt;; exits 0 only when both release packages were built.</summary>
        internal static void BuildReleaseFromCommandLine()
        {
            string folder = ReleaseFolderFromArguments(Environment.GetCommandLineArgs());
            if (folder == null)
            {
                Debug.LogError("No release folder: pass -releaseOut <folder> after -executeMethod Flock.Editor.FlockPackageBuilder.BuildReleaseFromCommandLine.");
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(BuildRelease(folder) ? 0 : 1);
        }

        /// <summary>The folder after -releaseOut, or null when there is none (nothing after it, a blank, or another flag).</summary>
        internal static string ReleaseFolderFromArguments(string[] arguments)
        {
            int index = Array.IndexOf(arguments, "-releaseOut");
            if (index < 0 || index + 1 >= arguments.Length)
                return null;
            string folder = arguments[index + 1];
            return string.IsNullOrWhiteSpace(folder) || folder.StartsWith("-", StringComparison.Ordinal) ? null : folder;
        }

        /// <summary>Builds FlockSDK-&lt;version&gt;.unitypackage and the Protokite Playtest's into this folder, at package.json's version; true when both were built.</summary>
        internal static bool BuildRelease(string outputFolder)
        {
            string version = VersionInPackageJson();
            if (string.IsNullOrEmpty(version))
            {
                Debug.LogError($"The Flock SDK package was not built: its package.json version could not be read. Expected the '{PackageName}' package in this project.");
                return false;
            }
            string core = Build(ReleaseContents(version, outputFolder), out string problem);
            if (core == null)
            {
                Debug.LogError("The Flock SDK package was not built: " + problem);
                return false;
            }
            // The playtest builder logs its own reason when it builds nothing.
            return FlockPlaytestPackageBuilder.Build(outputFolder) != null;
        }

        // Stages the SDK under Assets/FlockSDK/, exports it and cleans up; returns the file written, or null with the reason.
        private static string Build(PackageContents contents, out string problem)
        {
            problem = null;
            string sourceRoot = LocateSourceRoot();
            if (string.IsNullOrEmpty(sourceRoot))
            {
                problem = $"Could not find SDK source. Expected the '{PackageName}' UPM package or '{StagingRoot}'.";
                return null;
            }
            Directory.CreateDirectory(contents.OutputFolder);

            string normalizedSource = sourceRoot.Replace('\\', '/').TrimEnd('/');
            string normalizedStaging = StagingRoot.TrimEnd('/');
            bool sourceIsStaging = string.Equals(normalizedSource, normalizedStaging, StringComparison.OrdinalIgnoreCase);

            HashSet<string> excludedRelpaths = new HashSet<string>();
            HashSet<string> excludedFolders = new HashSet<string>();
            List<string> defines = new List<string>();
            CollectProviderExclusions(contents.SelectedProviders, excludedRelpaths, excludedFolders, defines);

            if (sourceIsStaging)
            {
                // Legacy mode: SDK already lives at Assets/FlockSDK/. Mutate the rsp in place
                // and export the existing tree — same behavior as the old builder, plus the
                // Editor rsp so editor-side code (e.g. codegen) sees the same FLOCK_NO_<ID>
                // defines as runtime.
                WriteOrDeleteRsp(StagingRuntimeRsp, defines);
                WriteOrDeleteRsp(StagingEditorRsp, defines);
                if (contents.IncludeSamples)
                    WriteOrDeleteRsp(StagingSamplesRsp, defines);
                AssetDatabase.Refresh();
                return ExportFromStaging(contents, excludedRelpaths, excludedFolders, out problem);
            }

            // UPM mode: stage source under Assets/FlockSDK/, export, then clean up.
            // LockReloadAssemblies suppresses the (transient) "duplicate asmdef name" compile
            // error that would otherwise fire while both the staged copy and the live package
            // coexist. Cleanup + Refresh in finally restores the project to a clean state even
            // if export throws.
            EditorApplication.LockReloadAssemblies();
            try
            {
                CleanStagingArea();
                StageFiles(normalizedSource, contents, excludedRelpaths, excludedFolders);
                // Without the Editor folder there is no build hook to keep the models, but a link.xml under Assets is read.
                if (!contents.IncludeEditor)
                    File.WriteAllText(StagingRoot + "Runtime/link.xml", FlockModelPreservation.BuildLinkXml(new Dictionary<string, SortedSet<string>>()));
                WriteOrDeleteRsp(StagingRuntimeRsp, defines);
                if (contents.IncludeEditor)
                    WriteOrDeleteRsp(StagingEditorRsp, defines);
                if (contents.IncludeSamples)
                    WriteOrDeleteRsp(StagingSamplesRsp, defines);
                AssetDatabase.Refresh();
                return ExportFromStaging(contents, excludedRelpaths, excludedFolders, out problem);
            }
            finally
            {
                CleanStagingArea();
                AssetDatabase.Refresh();
                EditorApplication.UnlockReloadAssemblies();
            }
        }

        private static string ExportFromStaging(PackageContents contents, HashSet<string> excludedRelpaths, HashSet<string> excludedFolders,
            out string problem)
        {
            problem = null;
            string[] assets = AssetDatabase.GetAllAssetPaths()
                .Where(path =>
                {
                    if (!path.StartsWith(StagingRoot)) return false;

                    string relative = path.Substring(StagingRoot.Length);

                    if (IsExcluded(relative, excludedRelpaths, excludedFolders)) return false;

                    if (relative.StartsWith("Runtime/")) return true;
                    if (relative.StartsWith("Editor/") && contents.IncludeEditor) return true;
                    if (relative.StartsWith("Samples~/") && contents.IncludeSamples) return true;
                    if (relative.StartsWith("Samples/") && contents.IncludeSamples) return true;
                    if (relative.StartsWith("Documentation~/") && contents.IncludeDocs) return true;
                    if (relative == "package.json") return true;
                    if (relative == "package.json.meta") return true;
                    if (relative == "CHANGELOG.md") return true;
                    if (relative == "LICENSE.md") return true;
                    if (relative == "README.md") return true;

                    return false;
                })
                .ToArray();

            if (assets.Length == 0)
            {
                problem = "No assets found to export. Check the SDK source location.";
                return null;
            }

            string packagePath = Path.Combine(contents.OutputFolder, $"FlockSDK-{contents.Version}.unitypackage");
            AssetDatabase.ExportPackage(assets, packagePath, ExportPackageOptions.Recurse);
            Debug.Log($"Flock SDK package built: {packagePath} ({assets.Length} assets)");
            return packagePath;
        }

        // Locate the SDK source on disk. Prefer the UPM package — that lets you develop the
        // SDK as embedded / local-file / git-URL without dropping source under Assets/.
        // Falls back to the legacy Assets/FlockSDK/ layout for projects that still vendor the
        // SDK directly inside Assets.
        private static string LocateSourceRoot()
        {
            PackageInfo pkg = PackageInfo.FindForAssembly(typeof(FlockPackageBuilder).Assembly);
            if (pkg != null && !string.IsNullOrEmpty(pkg.assetPath))
                return pkg.assetPath;

            string staging = StagingRoot.TrimEnd('/');
            if (Directory.Exists(staging))
                return staging;

            return null;
        }

        // Copies the SDK source tree into Assets/FlockSDK/. Each .meta file gets a fresh,
        // deterministic GUID so it doesn't collide with the live package's GUID in the
        // AssetDatabase. The asmdefs in this SDK reference each other by name (not GUID), so
        // the GUID remap doesn't break any inter-asset references.
        private static void StageFiles(string sourceRoot, PackageContents contents, HashSet<string> excludedRelpaths, HashSet<string> excludedFolders)
        {
            foreach (string rel in EnumerateSourceFilesRelative(sourceRoot, contents, excludedRelpaths, excludedFolders))
            {
                string src = Path.Combine(sourceRoot, rel);
                string dst = StagingRoot + rel.Replace('\\', '/');
                Directory.CreateDirectory(Path.GetDirectoryName(dst));

                if (rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    string content = File.ReadAllText(src);
                    string newGuid = DeterministicGuid(dst);
                    string updated = Regex.Replace(content, @"^(guid:\s+)\w+", "${1}" + newGuid, RegexOptions.Multiline);
                    File.WriteAllText(dst, updated);
                }
                else
                {
                    File.Copy(src, dst, overwrite: true);
                }
            }
        }

        private static IEnumerable<string> EnumerateSourceFilesRelative(string sourceRoot, PackageContents contents, HashSet<string> excludedRelpaths,
            HashSet<string> excludedFolders)
        {
            // Whitelisted top-level files (and their .meta companions).
            string[] rootFiles = { "package.json", "CHANGELOG.md", "LICENSE.md", "README.md" };
            foreach (string f in rootFiles)
            {
                if (File.Exists(Path.Combine(sourceRoot, f))) yield return f;
                string m = f + ".meta";
                if (File.Exists(Path.Combine(sourceRoot, m))) yield return m;
            }

            // Always: Runtime tree + its top-level folder meta.
            foreach (string p in WalkTree(sourceRoot, "Runtime", excludedRelpaths, excludedFolders)) yield return p;
            if (File.Exists(Path.Combine(sourceRoot, "Runtime.meta"))) yield return "Runtime.meta";

            // Optional: Editor tree.
            if (contents.IncludeEditor)
            {
                foreach (string p in WalkTree(sourceRoot, "Editor", excludedRelpaths, excludedFolders)) yield return p;
                if (File.Exists(Path.Combine(sourceRoot, "Editor.meta"))) yield return "Editor.meta";
            }

            // Optional: Samples tree. A normal (non-tilde) folder so it ships in the
            // .unitypackage and lands in the consumer's Assets/.
            if (contents.IncludeSamples)
            {
                foreach (string p in WalkTree(sourceRoot, "Samples", excludedRelpaths, excludedFolders)) yield return p;
                if (File.Exists(Path.Combine(sourceRoot, "Samples.meta"))) yield return "Samples.meta";
            }

            // The ~-suffixed dirs (Samples~ / Documentation~) are still skipped: Unity ignores
            // them in the AssetDatabase, so they wouldn't make it into the .unitypackage anyway.
        }

        private static IEnumerable<string> WalkTree(string sourceRoot, string subdir, HashSet<string> excludedRelpaths, HashSet<string> excludedFolders)
        {
            string fullDir = Path.Combine(sourceRoot, subdir);
            if (!Directory.Exists(fullDir)) yield break;

            int prefix = sourceRoot.Length + 1;
            foreach (string absPath in Directory.GetFiles(fullDir, "*", SearchOption.AllDirectories))
            {
                string rel = absPath.Substring(prefix).Replace('\\', '/');
                if (IsExcluded(rel, excludedRelpaths, excludedFolders)) continue;
                yield return rel;
            }
        }

        private static bool IsExcluded(string rel, HashSet<string> excludedRelpaths, HashSet<string> excludedFolders)
        {
            if (excludedRelpaths.Contains(rel)) return true;
            foreach (string folder in excludedFolders)
            {
                if (rel.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
                // Drop the folder's own .meta companion too — Unity treats `Foo.meta` as the
                // sidecar for the `Foo` directory.
                string folderTrimmed = folder.TrimEnd('/');
                if (rel.Equals(folderTrimmed + ".meta", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void CleanStagingArea()
        {
            string stagingDir = StagingRoot.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(stagingDir))
            {
                AssetDatabase.DeleteAsset(stagingDir);
            }
            // Belt-and-suspenders for the case where AssetDatabase doesn't yet know about the
            // folder (e.g. files were just dropped via raw IO and no Refresh has happened).
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, true);
            }
            string metaFile = stagingDir + ".meta";
            if (File.Exists(metaFile))
            {
                File.Delete(metaFile);
            }
        }

        // Stable across builds so a consumer who upgrades from one .unitypackage to the next
        // keeps the same GUID per file (their existing references survive the upgrade).
        private static string DeterministicGuid(string stagedAssetPath)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("FlockSDK:" + stagedAssetPath));
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void CollectProviderExclusions(IReadOnlyDictionary<string, bool> selectedProviders, HashSet<string> excludedRelpaths,
            HashSet<string> excludedFolders, List<string> defines)
        {
            foreach (string f in BuilderInternalFiles)
            {
                excludedRelpaths.Add(f);
                excludedRelpaths.Add(f + ".meta");
            }

            foreach (FlockProviderManifest.Entry entry in FlockProviderManifest.Providers)
            {
                bool selected = selectedProviders.TryGetValue(entry.Id, out bool v) && v;
                if (selected) continue;

                defines.Add("FLOCK_NO_" + entry.Id);
                if (entry.Files != null)
                {
                    foreach (string f in entry.Files)
                    {
                        excludedRelpaths.Add(f);
                        excludedRelpaths.Add(f + ".meta");
                    }
                }
                if (entry.Folders != null)
                {
                    foreach (string folder in entry.Folders)
                    {
                        // Normalize to forward-slash + trailing slash so prefix checks are uniform.
                        string normalized = folder.Replace('\\', '/').TrimEnd('/') + "/";
                        excludedFolders.Add(normalized);
                    }
                }
            }
        }

        // Per-asmdef compile-flags file. Unity's C# compiler honors a csc.rsp placed alongside
        // an asmdef, applying its flags only to that assembly. When all providers are selected
        // we delete the rsp entirely so the default state is byte-for-byte identical to having
        // no Package Builder feature at all.
        private static void WriteOrDeleteRsp(string rspPath, List<string> defines)
        {
            if (defines.Count == 0)
            {
                if (File.Exists(rspPath))
                    File.Delete(rspPath);
                string metaPath = rspPath + ".meta";
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
                return;
            }

            // No header comments: Unity's csc.rsp pipeline doesn't strip `#`-style comments,
            // so the compiler tokenizes each word as a source file path and errors on every
            // one. Keep the file to just the directives the compiler actually accepts.
            StringBuilder sb = new StringBuilder();
            foreach (string d in defines)
                sb.AppendLine("-define:" + d);

            Directory.CreateDirectory(Path.GetDirectoryName(rspPath));
            File.WriteAllText(rspPath, sb.ToString());
        }

        private void LoadVersionFromPackageJson()
        {
            string version = VersionInPackageJson();
            if (!string.IsNullOrEmpty(version))
                _version = version;
        }

        // The SDK's own package.json version, or null when it cannot be read.
        private static string VersionInPackageJson()
        {
            string sourceRoot = LocateSourceRoot();
            if (string.IsNullOrEmpty(sourceRoot)) return null;

            string path = Path.Combine(sourceRoot, "package.json");
            if (!File.Exists(path)) return null;

            try
            {
                return JObject.Parse(File.ReadAllText(path))["version"]?.ToString();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to read package.json version: {ex.Message}");
                return null;
            }
        }

        private void SaveVersionToPackageJson(string sourceRoot)
        {
            string path = Path.Combine(sourceRoot, "package.json");
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                JObject obj = JObject.Parse(json);
                obj["version"] = _version;
                File.WriteAllText(path, obj.ToString(Newtonsoft.Json.Formatting.Indented));
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Could not update package.json version: {ex.Message}");
            }
        }

        private void ShowStatus(string message, MessageType type)
        {
            _statusMessage = message;
            _statusType = type;
            _statusTimer = EditorApplication.timeSinceStartup + 5;
            Repaint();
        }
    }
}
