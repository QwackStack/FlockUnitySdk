using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;

namespace Flock.Editor
{
    //TODO this needs a re-make where it can include features , separate asmdefs and possibly find a different way to do this as it is very unity dependant
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
            var assets = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/") && 
                       (path.Contains("Runtime/") || 
                        path.Contains("Editor/") || 
                        path.Contains("Samples~/") || 
                        path.Contains("Documentation~/"))).ToArray();

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
                        path.Contains("Documentation~/"))).ToArray();

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