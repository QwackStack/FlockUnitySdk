using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Flock.Editor
{
    /// Watches compilation and, when an error looks like a schema accessor that was never generated,
    /// says so in the Console and names the Sync step. Editor-only; this is the compile-time half of
    /// the "unclear errors" feedback — a missing generated method is a compiler error, so no runtime
    /// FlockException can ever fire for it.
    ///
    /// Fires on recompiles inside a running Editor, which is when a developer actually writes the call.
    /// It cannot fire for errors that already exist when the Editor cold-starts: Unity compiles before
    /// [InitializeOnLoad] registers this callback. The Codegen tab's Status card covers that case.
    [InitializeOnLoad]
    internal static class FlockCodegenCompileHint
    {
        internal const string EnabledKey = "Flock.Codegen.CompileHintEnabled";

        // One hint per compilation pass — the same missing member usually breaks several assemblies.
        private static string _lastHint;

        static FlockCodegenCompileHint()
        {
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
        }

        /// Console hint toggle, surfaced in the Codegen tab. On by default.
        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set => EditorPrefs.SetBool(EnabledKey, value);
        }

        private static void OnCompilationStarted(object context) => ResetPass();

        /// Clears the once-per-pass guard. Called at the start of each compilation.
        internal static void ResetPass() => _lastHint = null;

        // Reported per-assembly and acted on immediately: batchmode can abort the run before a
        // compilationFinished callback would arrive.
        private static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || !Enabled)
                return;

            List<string> errors = new List<string>();
            foreach (CompilerMessage message in messages)
            {
                if (message.type == CompilerMessageType.Error)
                    errors.Add(message.message);
            }

            ReportIfMissingGeneratedCode(errors);
        }

        /// Logs the hint when these errors look like un-synced schemas. Internal so tests can drive it
        /// without Unity's compilation pipeline; returns the hint it logged, or null when it stayed silent.
        internal static string ReportIfMissingGeneratedCode(IEnumerable<string> compilerErrors)
        {
            List<FlockMissingSymbol> missing = FlockCodegenHintClassifier.FindMissingGeneratedSymbols(compilerErrors);
            if (missing.Count == 0)
                return null;

            string syncedGameVersionId = FlockCodeGenValidator.GetGeneratedGameVersionId();
            FlockCodegenSyncState state = syncedGameVersionId == null
                ? FlockCodegenSyncState.NeverSynced
                : FlockCodegenSyncState.Synced;

            string hint = FlockCodegenHintClassifier.BuildHint(missing, state, syncedGameVersionId);
            if (string.IsNullOrEmpty(hint) || hint == _lastHint)
                return null;

            _lastHint = hint;
            Debug.LogWarning(hint);
            return hint;
        }
    }
}
