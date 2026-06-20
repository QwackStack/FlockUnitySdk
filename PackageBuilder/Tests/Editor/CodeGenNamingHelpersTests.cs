using System.Collections.Generic;
using Flock.Editor.Codegen;
using NUnit.Framework;

namespace Flock.Tests
{
    // Covers the naming helpers that turn backend field/template names into valid, collision-free C#.
    public class CodeGenNamingHelpersTests
    {
        [Test]
        public void ToPascalCase_SnakeCase()
            => Assert.AreEqual("PlayerName", CodeGenNamingHelpers.ToPascalCase("player_name"));

        [Test]
        public void ToPascalCase_PreservesInnerCaps()
            => Assert.AreEqual("PlayerHP", CodeGenNamingHelpers.ToPascalCase("playerHP"));

        [Test]
        public void ToPascalCase_LeadingDigitGetsUnderscore()
            => Assert.AreEqual("_2fast", CodeGenNamingHelpers.ToPascalCase("2fast"));

        [Test]
        public void ToPascalCase_SymbolsOnly_Unnamed()
            => Assert.AreEqual("Unnamed", CodeGenNamingHelpers.ToPascalCase("___"));

        [Test]
        public void ToPascalCase_NullOrEmpty_Unnamed()
        {
            Assert.AreEqual("Unnamed", CodeGenNamingHelpers.ToPascalCase(null));
            Assert.AreEqual("Unnamed", CodeGenNamingHelpers.ToPascalCase(""));
        }

        [Test]
        public void UnDuplicate_FirstUseReturnsBase()
        {
            HashSet<string> used = new HashSet<string>();
            Assert.AreEqual("Foo", CodeGenNamingHelpers.UnDuplicate("Foo", used));
        }

        [Test]
        public void UnDuplicate_CollisionsGetNumericSuffix()
        {
            HashSet<string> used = new HashSet<string>();
            Assert.AreEqual("Foo", CodeGenNamingHelpers.UnDuplicate("Foo", used));
            Assert.AreEqual("Foo_2", CodeGenNamingHelpers.UnDuplicate("Foo", used));
            Assert.AreEqual("Foo_3", CodeGenNamingHelpers.UnDuplicate("Foo", used));
        }

        [Test]
        public void EscapeStringLiteral_EscapesQuote()
            => Assert.AreEqual("\\\"", CodeGenNamingHelpers.EscapeStringLiteral("\""));

        [Test]
        public void EscapeStringLiteral_EscapesBackslash()
            => Assert.AreEqual("\\\\", CodeGenNamingHelpers.EscapeStringLiteral("\\"));

        [Test]
        public void EscapeStringLiteral_EscapesNewline()
            => Assert.AreEqual("\\n", CodeGenNamingHelpers.EscapeStringLiteral("\n"));

        [Test]
        public void SanitizeForLineComment_ReplacesNewlineWithSpace()
            => Assert.AreEqual("a b", CodeGenNamingHelpers.SanitizeForLineComment("a\nb"));
    }
}
