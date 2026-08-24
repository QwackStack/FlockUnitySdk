using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Flock.Editor;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests
{
    // Locks the compile-error hint: a missing codegen accessor is a COMPILE error, so no runtime
    // FlockException can ever fire for it. These messages are real Roslyn text as Unity reports it.
    public class FlockCodegenHintTests
    {
        private const string MissingAccessor =
            "Assets/Scripts/Progress.cs(24,45): error CS1061: 'PlayerProvider' does not contain a definition for " +
            "'GetPlayerProgressAsync' and no accessible extension method 'GetPlayerProgressAsync' accepting a first " +
            "argument of type 'PlayerProvider' could be found (are you missing a using directive or an assembly reference?)";

        private static List<FlockMissingSymbol> Find(params string[] messages)
            => FlockCodegenHintClassifier.FindMissingGeneratedSymbols(messages);

        // The reporter dedupes within a compilation pass; start every test on a fresh pass.
        [SetUp]
        public void SetUp() => FlockCodegenCompileHint.ResetPass();

        [Test]
        public void MissingAccessorOnProvider_IsRecognised()
        {
            List<FlockMissingSymbol> missing = Find(MissingAccessor);

            Assert.AreEqual(1, missing.Count);
            Assert.AreEqual("PlayerProvider", missing[0].Owner);
            Assert.AreEqual("GetPlayerProgressAsync", missing[0].Name);
        }

        [Test]
        public void NamespaceQualifiedOwner_IsMatchedByShortName()
        {
            List<FlockMissingSymbol> missing = Find(
                "Assets/A.cs(1,1): error CS1061: 'Flock.Providers.FlockShopProvider' does not contain a definition for 'GetWeaponsShopAsync'");

            Assert.AreEqual(1, missing.Count);
            Assert.AreEqual("FlockShopProvider", missing[0].Owner);
        }

        [Test]
        public void MissingGeneratedNamespace_IsRecognised()
        {
            List<FlockMissingSymbol> missing = Find(
                "Assets/A.cs(3,13): error CS0246: The type or namespace name 'Generated' does not exist in the namespace 'Flock' (are you missing an assembly reference?)");

            Assert.AreEqual(1, missing.Count);
            Assert.AreEqual("Flock", missing[0].Owner);
            Assert.AreEqual("Generated", missing[0].Name);
        }

        [Test]
        public void MissingGeneratedIdType_IsRecognised()
        {
            List<FlockMissingSymbol> missing = Find(
                "Assets/A.cs(9,9): error CS0246: The type or namespace name 'FlockAchievementId' could not be found (are you missing a using directive or an assembly reference?)");

            Assert.AreEqual(1, missing.Count);
            Assert.AreEqual("FlockAchievementId", missing[0].Name);
        }

        [Test]
        public void UnrelatedCompileErrors_AreIgnored()
        {
            // The hint must stay silent on ordinary project errors, or it becomes noise on every failed compile.
            List<FlockMissingSymbol> missing = Find(
                "Assets/A.cs(1,1): error CS1061: 'MyPlayerController' does not contain a definition for 'Jump'",
                "Assets/B.cs(2,2): error CS0246: The type or namespace name 'Newtonsoft' could not be found",
                "Assets/C.cs(3,3): error CS1002: ; expected",
                "Assets/D.cs(4,4): warning CS0168: The variable 'x' is declared but never used");

            Assert.IsEmpty(missing);
        }

        [Test]
        public void SameMemberAtManyCallSites_IsListedOnce()
        {
            List<FlockMissingSymbol> missing = Find(MissingAccessor, MissingAccessor, MissingAccessor);
            Assert.AreEqual(1, missing.Count);
        }

        [Test]
        public void NullAndEmptyInput_AreSafe()
        {
            Assert.IsEmpty(FlockCodegenHintClassifier.FindMissingGeneratedSymbols(null));
            Assert.IsEmpty(Find(null, ""));
        }

        [Test]
        public void NeverSynced_HintNamesTheDashboardAndSyncStep()
        {
            string hint = FlockCodegenHintClassifier.BuildHint(
                Find(MissingAccessor), FlockCodegenSyncState.NeverSynced, null);

            StringAssert.Contains("PlayerProvider.GetPlayerProgressAsync", hint);
            StringAssert.Contains("never run", hint);
            StringAssert.Contains("Flock dashboard", hint);
            StringAssert.Contains("Codegen > Sync Schemas", hint);
        }

        [Test]
        public void AlreadySynced_HintAsksForAReSync_AndNamesTheVersion()
        {
            string hint = FlockCodegenHintClassifier.BuildHint(
                Find(MissingAccessor), FlockCodegenSyncState.Synced, "gv-123");

            StringAssert.Contains("gv-123", hint);
            StringAssert.Contains("re-run", hint);
            // "never run" is the other branch's wording and must not leak into this one.
            StringAssert.DoesNotContain("never run", hint);
        }

        [Test]
        public void NoMatches_ProducesNoHint()
        {
            Assert.IsNull(FlockCodegenHintClassifier.BuildHint(
                new List<FlockMissingSymbol>(), FlockCodegenSyncState.NeverSynced, null));
            Assert.IsNull(FlockCodegenHintClassifier.BuildHint(null, FlockCodegenSyncState.Synced, "gv-1"));
        }

        // The reporter is what the compilation callback calls; these cover the wiring between it,
        // the classifier and the Console, which the pure tests above do not reach.

        [Test]
        public void Reporter_LogsOneWarning_ForAMissingAccessor()
        {
            LogAssert.Expect(LogType.Warning, new Regex("PlayerProvider\\.GetPlayerProgressAsync"));

            string logged = FlockCodegenCompileHint.ReportIfMissingGeneratedCode(new[] { MissingAccessor });

            Assert.IsNotNull(logged);
            StringAssert.Contains("Codegen > Sync Schemas", logged);
        }

        [Test]
        public void Reporter_StaysSilent_OnUnrelatedErrors()
        {
            // No LogAssert.Expect — an unexpected log here fails the test, which is the point.
            Assert.IsNull(FlockCodegenCompileHint.ReportIfMissingGeneratedCode(new[]
            {
                "Assets/A.cs(1,1): error CS1002: ; expected",
                "Assets/B.cs(2,2): error CS1061: 'MyPlayerController' does not contain a definition for 'Jump'"
            }));
        }

        // The one link the reporter tests can't reach: that [InitializeOnLoad] actually subscribed.
        // Unity firing its own event on recompile is the only part left unverified by this suite.
        [Test]
        public void Hook_IsSubscribedToTheCompilationPipeline()
        {
            FieldInfo field = typeof(CompilationPipeline).GetField(
                "assemblyCompilationFinished", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
                Assert.Inconclusive("Unity changed the backing field for assemblyCompilationFinished; subscription unverified.");

            Delegate handlers = field.GetValue(null) as Delegate;
            Assert.IsNotNull(handlers, "Nothing is subscribed to assemblyCompilationFinished.");

            bool subscribed = false;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                if (handler.Method.DeclaringType == typeof(FlockCodegenCompileHint))
                    subscribed = true;
            }
            Assert.IsTrue(subscribed, "FlockCodegenCompileHint did not subscribe to assemblyCompilationFinished.");
        }

        [Test]
        public void Reporter_DoesNotRepeatTheSameHint_WithinOnePass()
        {
            LogAssert.Expect(LogType.Warning, new Regex("GetPlayerProgressAsync"));

            Assert.IsNotNull(FlockCodegenCompileHint.ReportIfMissingGeneratedCode(new[] { MissingAccessor }));
            // Same pass, second assembly reporting the same break — must not log a duplicate.
            Assert.IsNull(FlockCodegenCompileHint.ReportIfMissingGeneratedCode(new[] { MissingAccessor }));
        }
    }
}
