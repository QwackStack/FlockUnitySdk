// Copied into Assets/Editor of a project holding the SDK, Unity IAP 4 and Docs~/recipes/FlockUnityIap4Store.cs, by tests.yml's
// Unity IAP 4 job and by Qwacks' check_iap4_recipe.py. Unity runs a method only once every script compiled, so exit 0 means the recipe compiled.
using UnityEditor;
using UnityEngine;

public static class FlockIap4RecipeCheck
{
    public static void Compiled()
    {
        // Named, so a recipe renamed or left out fails to compile here rather than passing with nothing checked.
        string recipe = typeof(FlockUnityIap4Store).Name;
        UnityEditor.PackageManager.PackageInfo purchasing = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.purchasing/package.json");
        string version = purchasing?.version ?? "not installed";
        // The recipe is for games on Unity IAP 4: a manifest that resolved another major version checked nothing.
        bool onIap4 = version.StartsWith("4.");
        Debug.Log($"{recipe} compiled against Unity IAP {version}" + (onIap4 ? "." : ", which is not Unity IAP 4."));
        EditorApplication.Exit(onIap4 ? 0 : 1);
    }
}
