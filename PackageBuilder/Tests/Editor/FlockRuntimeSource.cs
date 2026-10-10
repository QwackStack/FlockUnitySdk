using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    /// <summary>Reads the package's own Runtime source, for tests that fail a pattern the code must not use.</summary>
    internal static class FlockRuntimeSource
    {
        /// <summary>Each code line (comments left out) that matches, as "file:line: text", in every Runtime file but the ones allowed to.</summary>
        internal static List<string> LinesMatching(Regex pattern, params string[] filesAllowedTo)
        {
            List<string> found = new List<string>();
            string runtime = Folder();
            foreach (string file in Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories))
            {
                if (System.Array.IndexOf(filesAllowedTo, Path.GetFileName(file)) >= 0)
                    continue;
                found.AddRange(Matches(file, pattern, runtime));
            }
            return found;
        }

        /// <summary>Each code line of one Runtime file that matches, for a scan's own control.</summary>
        internal static List<string> LinesMatchingIn(string fileName, Regex pattern)
        {
            string runtime = Folder();
            return Matches(Directory.GetFiles(runtime, fileName, SearchOption.AllDirectories)[0], pattern, runtime);
        }

        private static string Folder()
        {
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FlockClient).Assembly);
            if (package == null)
                Assert.Ignore("The SDK is not installed as a package here, so its source cannot be read.");
            return Path.Combine(package.resolvedPath, "Runtime");
        }

        private static List<string> Matches(string file, Regex pattern, string runtime)
        {
            List<string> found = new List<string>();
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("//") && pattern.IsMatch(lines[i]))
                    found.Add($"{file.Substring(runtime.Length + 1)}:{i + 1}: {lines[i].Trim()}");
            }
            return found;
        }
    }
}
