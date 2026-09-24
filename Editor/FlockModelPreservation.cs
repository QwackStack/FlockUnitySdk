using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;

namespace Flock.Editor
{
    /// <summary>Keeps the SDK's models and the game's generated code whole in player builds, at any managed stripping level.</summary>
    // Newtonsoft reaches these by reflection, so Medium and High stripping would remove their constructors and getters.
    // A link.xml inside a UPM package is not read by the linker (measured), so the list is handed over on every build.
    internal sealed class FlockModelPreservation : IUnityLinkerProcessor
    {
        internal const string RuntimeAssemblyName = "Flock.Runtime";
        internal const string GeneratedNamespace = "Flock.Generated";

        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            return WriteLinkXml(Path.Combine(Directory.GetCurrentDirectory(), "Library", "Flock", "link.xml"),
                FindGeneratedNamespaces(PlayerAssemblies()));
        }

        // Members older editors require on this interface; newer ones no longer call them.
        public void OnBeforeRun(BuildReport report, UnityLinkerBuildPipelineData data)
        {
        }

        public void OnAfterRun(BuildReport report, UnityLinkerBuildPipelineData data)
        {
        }

        internal static string WriteLinkXml(string path, IReadOnlyDictionary<string, SortedSet<string>> generatedNamespacesByAssembly)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, BuildLinkXml(generatedNamespacesByAssembly));
            return path;
        }

        // The loaded assemblies that the player build compiles, so editor-only code is never listed.
        private static IEnumerable<Assembly> PlayerAssemblies()
        {
            HashSet<string> playerNames = new HashSet<string>(
                UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Player).Select(assembly => assembly.name), StringComparer.Ordinal);
            return AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic && playerNames.Contains(assembly.GetName().Name));
        }

        /// <summary>Each assembly holding generated code, with the generated namespaces it holds.</summary>
        internal static Dictionary<string, SortedSet<string>> FindGeneratedNamespaces(IEnumerable<Assembly> assemblies)
        {
            Dictionary<string, SortedSet<string>> found = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (Assembly assembly in assemblies)
            {
                // Assemblies made at runtime are never part of a player build, and some cannot list their types.
                if (assembly.IsDynamic)
                    continue;
                foreach (Type type in LoadableTypes(assembly))
                {
                    string space = type.Namespace;
                    if (space == null || !(space == GeneratedNamespace || space.StartsWith(GeneratedNamespace + ".", StringComparison.Ordinal)))
                        continue;
                    string name = assembly.GetName().Name;
                    if (!found.TryGetValue(name, out SortedSet<string> spaces))
                    {
                        spaces = new SortedSet<string>(StringComparer.Ordinal);
                        found.Add(name, spaces);
                    }
                    spaces.Add(space);
                }
            }
            return found;
        }

        internal static string BuildLinkXml(IReadOnlyDictionary<string, SortedSet<string>> generatedNamespacesByAssembly)
        {
            XElement linker = new XElement("linker",
                new XElement("assembly", new XAttribute("fullname", RuntimeAssemblyName), new XAttribute("preserve", "all")));
            // Only the generated namespaces: preserving the game's whole assembly would switch stripping off for the game.
            foreach (KeyValuePair<string, SortedSet<string>> assembly in generatedNamespacesByAssembly.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                linker.Add(new XElement("assembly",
                    new XAttribute("fullname", assembly.Key),
                    new XAttribute("ignoreIfMissing", "1"),
                    assembly.Value.Select(space => new XElement("namespace", new XAttribute("fullname", space), new XAttribute("preserve", "all")))));
            }
            return new XDocument(linker).ToString();
        }

        // An assembly whose types cannot all be loaded still yields the ones that can.
        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
        }
    }
}
