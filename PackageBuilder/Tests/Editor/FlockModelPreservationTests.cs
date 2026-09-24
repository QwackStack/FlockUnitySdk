using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Flock.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;

namespace Flock.Generated.PreservationTestModels
{
    // Stands in for generated code: a model Newtonsoft fills by reflection, in a generated namespace of this assembly.
    public sealed class GeneratedModelStandIn
    {
        public string Name { get; set; }
    }
}

namespace Flock.GeneratedLookalike
{
    // Shares the prefix but is not a generated namespace.
    public sealed class NotGenerated
    {
    }
}

namespace MyGame.Flock.Generated
{
    // Ends in the generated name but is the game's own namespace.
    public sealed class AlsoNotGenerated
    {
    }
}

namespace Flock.Tests.Editor
{
    public class FlockModelPreservationTests
    {
        private static XDocument Parse(string xml) => XDocument.Parse(xml);

        private static IEnumerable<XElement> Assemblies(XDocument document) => document.Root.Elements("assembly");

        [Test]
        public void TheSdkAssemblyIsKeptWhole()
        {
            XDocument document = Parse(FlockModelPreservation.BuildLinkXml(new Dictionary<string, SortedSet<string>>()));
            XElement sdk = Assemblies(document).Single(element => (string)element.Attribute("fullname") == "Flock.Runtime");
            Assert.AreEqual("all", (string)sdk.Attribute("preserve"));
            Assert.AreEqual(1, Assemblies(document).Count(), "With no generated code, only the SDK is listed");
        }

        [Test]
        public void OnlyTheGeneratedNamespacesOfTheGamesAssemblyAreKept()
        {
            Dictionary<string, SortedSet<string>> generated = new Dictionary<string, SortedSet<string>>
            {
                { "Assembly-CSharp", new SortedSet<string> { "Flock.Generated.Player", "Flock.Generated.Configs" } },
            };
            XDocument document = Parse(FlockModelPreservation.BuildLinkXml(generated));
            XElement game = Assemblies(document).Single(element => (string)element.Attribute("fullname") == "Assembly-CSharp");
            Assert.IsNull(game.Attribute("preserve"), "The game's assembly itself is not kept whole, or stripping would be off for the whole game");
            Assert.AreEqual("1", (string)game.Attribute("ignoreIfMissing"), "An assembly that is not in this build must not fail it");
            CollectionAssert.AreEqual(new[] { "Flock.Generated.Configs", "Flock.Generated.Player" },
                game.Elements("namespace").Select(element => (string)element.Attribute("fullname")).ToArray());
            Assert.IsTrue(game.Elements("namespace").All(element => (string)element.Attribute("preserve") == "all"), "Every member, not only the type");
        }

        [Test]
        public void GeneratedNamespacesAreFoundWhereverTheyCompiled()
        {
            Dictionary<string, SortedSet<string>> found = FlockModelPreservation.FindGeneratedNamespaces(new[] { typeof(FlockModelPreservationTests).Assembly });
            string thisAssembly = typeof(FlockModelPreservationTests).Assembly.GetName().Name;
            Assert.IsTrue(found.ContainsKey(thisAssembly), "The assembly holding generated code is found, whatever its name");
            CollectionAssert.AreEqual(new[] { "Flock.Generated.PreservationTestModels" }, found[thisAssembly].ToArray(),
                "Lookalike namespaces are not taken for generated ones");
        }

        [Test]
        public void TheFileTheBuildIsGivenIsWrittenAndListsOnlyPlayerCode()
        {
            string path = new FlockModelPreservation().GenerateAdditionalLinkXmlFile(null, null);
            Assert.IsTrue(File.Exists(path), "The linker is handed a file that exists: " + path);
            XDocument document = XDocument.Load(path);
            Assert.IsTrue(Assemblies(document).Any(element => (string)element.Attribute("fullname") == "Flock.Runtime"));
            Assert.IsFalse(document.Descendants("namespace").Any(element => (string)element.Attribute("fullname") == "Flock.Generated.PreservationTestModels"),
                "This test assembly is editor-only, so its stand-in is never listed");

            // Wherever this project's player code holds generated classes, the file names their namespaces.
            HashSet<string> playerAssemblies = new HashSet<string>(UnityEditor.Compilation.CompilationPipeline
                .GetAssemblies(UnityEditor.Compilation.AssembliesType.Player).Select(assembly => assembly.name));
            IEnumerable<string> expected = System.AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && playerAssemblies.Contains(assembly.GetName().Name))
                .SelectMany(assembly => assembly.GetTypes())
                .Select(type => type.Namespace)
                .Where(space => space != null && space.StartsWith("Flock.Generated.", System.StringComparison.Ordinal))
                .Distinct();
            foreach (string space in expected)
                Assert.IsTrue(document.Descendants("namespace").Any(element => (string)element.Attribute("fullname") == space), "Listed: " + space);
        }

        [Test]
        public void TheBuildFindsTheProcessor()
        {
            CollectionAssert.Contains(TypeCache.GetTypesDerivedFrom<IUnityLinkerProcessor>().ToList(), typeof(FlockModelPreservation));
        }
    }
}
