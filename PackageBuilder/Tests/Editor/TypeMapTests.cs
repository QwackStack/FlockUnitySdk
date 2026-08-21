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

        // An optional field arrives with a trailing '?'. Before this was handled the type read as unknown and
        // the field was skipped entirely — a schema of only optional fields emitted nothing at all.
        [TestCase("datetime?", "DateTime?")]
        [TestCase("integer?", "int?")]
        [TestCase("boolean?", "bool?")]
        [TestCase("number?", "double?")]
        [TestCase("long?", "long?")]
        [TestCase("float?", "float?")]
        [TestCase("INTEGER?", "int?")]
        [TestCase("  datetime? ", "DateTime?")]
        public void MapsOptionalPrimitivesToNullable(string input, string expected)
            => Assert.AreEqual(expected, TypeMap.MapPrimitiveTypeString(input));

        // string is already a reference type — a second '?' would not compile on older language versions.
        [TestCase("string?", "string")]
        public void OptionalStringStaysPlainString(string input, string expected)
            => Assert.AreEqual(expected, TypeMap.MapPrimitiveTypeString(input));

        // An optional marker on something we still don't recognise is unknown, not "unknown?".
        [TestCase("guid?")]
        [TestCase("?")]
        public void OptionalUnknownIsStillUnknown(string input)
            => Assert.IsNull(TypeMap.MapPrimitiveTypeString(input));

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
