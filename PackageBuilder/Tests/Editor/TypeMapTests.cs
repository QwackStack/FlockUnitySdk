using Flock.Editor.Codegen;
using NUnit.Framework;

namespace Flock.Tests
{
    // Covers TypeMap.MapPrimitiveTypeString — the primitive backend-type → C#-type mapping.
    public class TypeMapTests
    {
        [TestCase("string", "string")]
        [TestCase("integer", "int")]
        [TestCase("int", "int")]
        [TestCase("long", "long")]
        [TestCase("int64", "long")]
        [TestCase("float", "float")]
        [TestCase("number", "double")]
        [TestCase("double", "double")]
        [TestCase("boolean", "bool")]
        [TestCase("bool", "bool")]
        [TestCase("datetime", "DateTime")]
        [TestCase("date", "DateTime")]
        [TestCase("timestamp", "DateTime")]
        public void MapsKnownPrimitives(string input, string expected)
            => Assert.AreEqual(expected, TypeMap.MapPrimitiveTypeString(input));

        [TestCase("INTEGER", "int")]
        [TestCase("  String  ", "string")]
        public void IsCaseAndWhitespaceInsensitive(string input, string expected)
            => Assert.AreEqual(expected, TypeMap.MapPrimitiveTypeString(input));

        // Composites are walked structurally by SchemaPropertyEmitter, not mapped here.
        [TestCase("object")]
        [TestCase("list")]
        [TestCase("dict")]
        [TestCase("guid")]
        [TestCase("")]
        [TestCase(null)]
        public void ReturnsNullForNonPrimitives(string input)
            => Assert.IsNull(TypeMap.MapPrimitiveTypeString(input));
    }
}
