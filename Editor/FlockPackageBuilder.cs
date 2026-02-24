using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

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
        private GUIStyle _headerStyle;

        private static readonly string PackageJsonPath = Path.Combine("Assets", "FlockUnitySdk", "package.json");

        [MenuItem("Qwacks/Package Builder")]
        public static void ShowWindow()
        {
            var window = GetWindow<FlockPackageBuilder>("Flock Package Builder");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        private void OnEnable()
        {
            LoadVersionFromPackageJson();
        }

        private void InitStyles()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 14,
                    alignment = TextAnchor.MiddleCenter,
                    margin = new RectOffset(0, 0, 8, 8)
                };
            }
        }

        private void OnGUI()
        {
            InitStyles();

            GUILayout.Space(10);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Flock SDK Package Builder", _headerStyle);
            EditorGUILayout.EndVertical();

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
            EditorGUILayout.Toggle("Runtime (required)", true);
            EditorGUI.EndDisabledGroup();

            _includeEditor = EditorGUILayout.Toggle("Editor", _includeEditor);
            _includeSamples = EditorGUILayout.Toggle("Samples", _includeSamples);
            _includeDocs = EditorGUILayout.Toggle("Documentation", _includeDocs);

            EditorGUILayout.EndVertical();

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

            if (!Directory.Exists(_outputPath))
            {
                Directory.CreateDirectory(_outputPath);
            }

            SaveVersionToPackageJson();

            string sdkRoot = "Assets/FlockUnitySdk/";

            var assets = AssetDatabase.GetAllAssetPaths()
                .Where(path =>
                {
                    if (!path.StartsWith(sdkRoot)) return false;

                    string relative = path.Substring(sdkRoot.Length);

                    if (relative.StartsWith("Runtime/")) return true;
                    if (relative.StartsWith("Editor/") && _includeEditor) return true;
                    if (relative.StartsWith("Samples~/") && _includeSamples) return true;
                    if (relative.StartsWith("Documentation~/") && _includeDocs) return true;
                    if (relative == "package.json") return true;
                    if (relative == "package.json.meta") return true;
                    if (relative == "CHANGELOG.md") return true;
                    if (relative == "LICENSE") return true;
                    if (relative == "README.md") return true;

                    return false;
                })
                .ToArray();

            if (assets.Length == 0)
            {
                ShowStatus("No assets found to export. Check the SDK root path.", MessageType.Error);
                return;
            }

            string filename = $"FlockSDK-{_version}.unitypackage";
            string packagePath = Path.Combine(_outputPath, filename);

            AssetDatabase.ExportPackage(assets, packagePath, ExportPackageOptions.Recurse);

            ShowStatus($"Package built: {filename} ({assets.Length} assets)", MessageType.Info);
            Debug.Log($"Flock SDK package built: {packagePath} ({assets.Length} assets)");
            EditorUtility.RevealInFinder(packagePath);
        }

        private void LoadVersionFromPackageJson()
        {
            if (!File.Exists(PackageJsonPath))
            {
                return;
            }

            try
            {
                string json = File.ReadAllText(PackageJsonPath);
                var obj = JObject.Parse(json);
                string version = obj["version"]?.ToString();
                if (!string.IsNullOrEmpty(version))
                {
                    _version = version;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to read package.json version: {ex.Message}");
            }
        }

        private void SaveVersionToPackageJson()
        {
            if (!File.Exists(PackageJsonPath)) return;

            try
            {
                string json = File.ReadAllText(PackageJsonPath);
                var obj = JObject.Parse(json);
                obj["version"] = _version;
                File.WriteAllText(PackageJsonPath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
                AssetDatabase.Refresh();
            }
            catch (System.Exception ex)
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
