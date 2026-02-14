using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;

namespace Flock.Editor
{
    public class FlockPackageBuilder : EditorWindow
    {
        private const string PackageName = "FlockSDK";
        private string version = "0.1.0";
        private string outputPath = "Build";
        private bool includeMetaFiles = true;
        private bool includeDependencies = false;
        private Vector2 scrollPosition;

        [MenuItem("Qwacks/Package Builder")]
        public static void ShowWindow()
        {
            var window = GetWindow<FlockPackageBuilder>("Flock Package Builder");
            window.minSize = new Vector2(450, 400);
            window.Show();
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            GUILayout.Space(10);

            // Header
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("📦 Flock SDK Package Builder", EditorStyles.boldLabel);
            GUILayout.Label("Build Unity packages for distribution", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            GUILayout.Space(15);

            // Settings
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("⚙️ Package Settings", EditorStyles.boldLabel);
            GUILayout.Space(5);

            version = EditorGUILayout.TextField(new GUIContent("Version", "Package version number"), version);
            outputPath = EditorGUILayout.TextField(new GUIContent("Output Path", "Where to save the package"), outputPath);

            GUILayout.Space(5);
            includeMetaFiles = EditorGUILayout.Toggle(new GUIContent("Include Meta Files", "Include .meta files in package"), includeMetaFiles);
            includeDependencies = EditorGUILayout.Toggle(new GUIContent("Include Dependencies", "Include package dependencies"), includeDependencies);

            EditorGUILayout.EndVertical();

            GUILayout.Space(10);

            // Info Section
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("ℹ️ Package Info", EditorStyles.boldLabel);
            GUILayout.Space(5);

            string[] assetsToInclude = GetPackageAssets();
            EditorGUILayout.LabelField("Files to Include:", assetsToInclude.Length.ToString());

            if (assetsToInclude.Length == 0)
            {
                EditorGUILayout.HelpBox("⚠️ No FlockUnitySdk assets found in Assets folder!", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField("Package Name:", $"{PackageName}-{version}.unitypackage");
            }

            EditorGUILayout.EndVertical();

            GUILayout.Space(15);

            // Validation
            bool canBuild = ValidateSettings(out string validationMessage);

            if (!canBuild)
            {
                EditorGUILayout.HelpBox(validationMessage, MessageType.Error);
            }

            // Build Buttons
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = canBuild;
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("📦 Build Package", GUILayout.Height(35)))
            {
                BuildPackage(false);
            }
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;

            GUI.enabled = canBuild;
            if (GUILayout.Button("🔧 Build (Interactive)", GUILayout.Height(35), GUILayout.Width(150)))
            {
                BuildPackage(true);
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            GUILayout.Space(5);

            if (GUILayout.Button("📁 Open Output Folder", GUILayout.Height(30)))
            {
                OpenOutputFolder();
            }

            GUILayout.Space(10);

            EditorGUILayout.EndScrollView();
        }

        private string[] GetPackageAssets()
        {
            var assets = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/FlockUnitySdk/") &&
                       !path.Contains("/PackageBuilder") &&
                       !path.Contains("/.git") &&
                       !path.Contains("/.github") &&
                       !path.Contains("/.idea") &&
                       (includeMetaFiles || !path.EndsWith(".meta"))).ToList();

            // Ensure Resources/FlockConfig.asset is included if it exists
            string configPath = "Assets/Resources/FlockConfig.asset";
            if (System.IO.File.Exists(configPath) && !assets.Contains(configPath))
            {
                assets.Add(configPath);
                if (includeMetaFiles)
                {
                    string metaPath = configPath + ".meta";
                    if (System.IO.File.Exists(metaPath))
                    {
                        assets.Add(metaPath);
                    }
                }
            }

            return assets.ToArray();
        }

        private bool ValidateSettings(out string message)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                message = "⚠️ Version is required!";
                return false;
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                message = "⚠️ Output path is required!";
                return false;
            }

            string[] assets = GetPackageAssets();
            if (assets.Length == 0)
            {
                message = "⚠️ No assets found to include in package!";
                return false;
            }

            message = "";
            return true;
        }

        private void BuildPackage(bool interactive)
        {
            try
            {
                // Create output directory if it doesn't exist
                if (!Directory.Exists(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                }

                // Get all assets in the package
                string[] assets = GetPackageAssets();

                if (assets.Length == 0)
                {
                    EditorUtility.DisplayDialog("Build Failed", "No assets found to include in the package!", "OK");
                    return;
                }

                // Build export options
                ExportPackageOptions options = ExportPackageOptions.Recurse;

                if (includeDependencies)
                {
                    options |= ExportPackageOptions.IncludeDependencies;
                }

                if (interactive)
                {
                    options |= ExportPackageOptions.Interactive;
                }

                // Create the package
                string packagePath = Path.Combine(outputPath, $"{PackageName}-{version}.unitypackage");

                Debug.Log($"Building package with {assets.Length} assets...");
                AssetDatabase.ExportPackage(assets, packagePath, options);

                if (File.Exists(packagePath))
                {
                    Debug.Log($"✅ Package built successfully: {packagePath}");

                    if (EditorUtility.DisplayDialog("Build Successful",
                        $"Package built successfully!\n\nLocation: {packagePath}\n\nOpen output folder?",
                        "Yes", "No"))
                    {
                        EditorUtility.RevealInFinder(packagePath);
                    }
                }
                else
                {
                    Debug.LogWarning("Package build completed but file not found. It may have been cancelled.");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to build package: {ex.Message}");
                EditorUtility.DisplayDialog("Build Failed", $"Error building package:\n\n{ex.Message}", "OK");
            }
        }

        private void OpenOutputFolder()
        {
            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            EditorUtility.RevealInFinder(outputPath);
        }
    }
}
