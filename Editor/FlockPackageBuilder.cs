using UnityEngine;
using UnityEditor;
using System.IO;

namespace Flock.Editor
{
    public class FlockPackageBuilder
    {
        private const string PackageName = "FlockSDK";
        private const string Version = "1.0.0";
        private const string OutputPath = "Build";

        [MenuItem("Flock/Build Package")]
        public static void BuildPackage()
        {
            // Create output directory if it doesn't exist
            if (!Directory.Exists(OutputPath))
            {
                Directory.CreateDirectory(OutputPath);
            }

            // Get all assets in the package
            string[] assets = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/") && 
                       (path.Contains("Runtime/") || 
                        path.Contains("Editor/") || 
                        path.Contains("Samples~/") || 
                        path.Contains("Documentation~/")));

            // Create the package
            string packagePath = Path.Combine(OutputPath, $"{PackageName}-{Version}.unitypackage");
            AssetDatabase.ExportPackage(assets, packagePath, ExportPackageOptions.Recurse);

            Debug.Log($"Package built successfully: {packagePath}");
            
            // Open the output folder
            EditorUtility.RevealInFinder(OutputPath);
        }

        [MenuItem("Flock/Build Package (Development)")]
        public static void BuildDevelopmentPackage()
        {
            // Create output directory if it doesn't exist
            if (!Directory.Exists(OutputPath))
            {
                Directory.CreateDirectory(OutputPath);
            }

            // Get all assets in the package
            string[] assets = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/") && 
                       (path.Contains("Runtime/") || 
                        path.Contains("Editor/") || 
                        path.Contains("Samples~/") || 
                        path.Contains("Documentation~/")));

            // Create the package with development options
            string packagePath = Path.Combine(OutputPath, $"{PackageName}-{Version}-dev.unitypackage");
            AssetDatabase.ExportPackage(
                assets, 
                packagePath, 
                ExportPackageOptions.Recurse | 
                ExportPackageOptions.IncludeDependencies | 
                ExportPackageOptions.Interactive
            );

            Debug.Log($"Development package built successfully: {packagePath}");
            
            // Open the output folder
            EditorUtility.RevealInFinder(OutputPath);
        }
    }
} 