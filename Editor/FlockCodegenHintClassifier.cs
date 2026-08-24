using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Flock.Editor
{
    /// Whether codegen has ever produced a manifest in this project.
    public enum FlockCodegenSyncState
    {
        NeverSynced,
        Synced
    }

    /// One compile error the classifier recognised as missing generated code.
    public readonly struct FlockMissingSymbol
    {
        /// The provider or generated type the member was looked up on; null when the error was about a type itself.
        public readonly string Owner;

        /// The member or type name the compiler could not resolve.
        public readonly string Name;

        public FlockMissingSymbol(string owner, string name)
        {
            Owner = owner;
            Name = name;
        }

        public override string ToString() => string.IsNullOrEmpty(Owner) ? Name : $"{Owner}.{Name}";
    }

    /// Recognises the compile errors a consumer hits when dashboard schemas were never synced, and
    /// writes the hint that names the missing step. Pure (no Unity calls) so it is unit-testable;
    /// FlockCodegenCompileHint feeds it real compiler output.
    public static class FlockCodegenHintClassifier
    {
        // Codegen emits extension methods onto exactly these four providers, so an unresolved member
        // on one of them is almost always a schema that was never generated.
        private static readonly string[] ExtendedProviders =
        {
            "PlayerProvider",
            "FlockConfigProvider",
            "FlockCommandProvider",
            "FlockShopProvider"
        };

        // Generated types with a fixed name — per-template classes are named from the schema, so they can't be listed.
        private static readonly string[] GeneratedTypes =
        {
            "FlockAchievementId",
            "FlockShopItemId",
            "FlockFundId",
            "SchemasManifest"
        };

        // 'PlayerProvider' does not contain a definition for 'GetPlayerProgressAsync' and no accessible extension method...
        private static readonly Regex NoDefinition = new Regex(
            @"error CS(?:1061|0117):\s*'([^']+)' does not contain a definition for '([^']+)'", RegexOptions.Compiled);

        // The type or namespace name 'Generated' does not exist in the namespace 'Flock'
        private static readonly Regex MissingNamespace = new Regex(
            @"error CS0246:.*?name '([^']+)' does not exist in the namespace '(Flock[^']*)'", RegexOptions.Compiled);

        // The type or namespace name 'FlockAchievementId' could not be found
        private static readonly Regex MissingType = new Regex(
            @"error CS0246:\s*The type or namespace name '([^']+)' could not be found", RegexOptions.Compiled);

        /// Picks the compile errors that look like missing generated code. Empty when none match — callers stay silent then.
        public static List<FlockMissingSymbol> FindMissingGeneratedSymbols(IEnumerable<string> compilerMessages)
        {
            List<FlockMissingSymbol> found = new List<FlockMissingSymbol>();
            if (compilerMessages == null)
                return found;

            foreach (string message in compilerMessages)
            {
                if (string.IsNullOrEmpty(message))
                    continue;

                Match noDefinition = NoDefinition.Match(message);
                if (noDefinition.Success)
                {
                    string owner = LastSegment(noDefinition.Groups[1].Value);
                    if (Contains(ExtendedProviders, owner) || Contains(GeneratedTypes, owner))
                        AddOnce(found, new FlockMissingSymbol(owner, noDefinition.Groups[2].Value));
                    continue;
                }

                Match missingNamespace = MissingNamespace.Match(message);
                if (missingNamespace.Success)
                {
                    AddOnce(found, new FlockMissingSymbol(missingNamespace.Groups[2].Value, missingNamespace.Groups[1].Value));
                    continue;
                }

                Match missingType = MissingType.Match(message);
                if (missingType.Success && Contains(GeneratedTypes, missingType.Groups[1].Value))
                    AddOnce(found, new FlockMissingSymbol(null, missingType.Groups[1].Value));
            }

            return found;
        }

        /// The console hint for these symbols, or null when there is nothing worth saying.
        public static string BuildHint(
            IReadOnlyList<FlockMissingSymbol> missing,
            FlockCodegenSyncState state,
            string syncedGameVersionId)
        {
            if (missing == null || missing.Count == 0)
                return null;

            StringBuilder text = new StringBuilder();
            text.Append("[Flock] ");
            text.Append(state == FlockCodegenSyncState.NeverSynced
                ? "Codegen has never run in this project, and these compile errors look like generated code that doesn't exist yet:"
                : "These compile errors look like generated code that doesn't exist yet:");

            foreach (FlockMissingSymbol symbol in missing)
                text.Append("\n  - ").Append(symbol.ToString());

            text.Append("\n\nTyped accessors like these are generated from the schemas you author in the Flock dashboard — ");
            text.Append("they do not exist in the SDK until codegen runs.");

            if (state == FlockCodegenSyncState.NeverSynced)
            {
                text.Append("\nFix: author the schema in the Flock dashboard, then run Flock > Settings > Codegen > Sync Schemas.");
            }
            else
            {
                text.Append("\nCodegen last synced");
                if (!string.IsNullOrEmpty(syncedGameVersionId))
                    text.Append($" for game version ID '{syncedGameVersionId}'");
                text.Append(". Fix: if you added or renamed this in the dashboard since, re-run Flock > Settings > Codegen > Sync Schemas.");
            }

            return text.ToString();
        }

        // Roslyn may print a namespace-qualified owner; the short name is what the emitters use.
        private static string LastSegment(string typeName)
        {
            int dot = typeName.LastIndexOf('.');
            return dot >= 0 ? typeName.Substring(dot + 1) : typeName;
        }

        private static bool Contains(string[] names, string candidate)
        {
            foreach (string name in names)
            {
                if (string.Equals(name, candidate))
                    return true;
            }
            return false;
        }

        // The same missing member is reported once per call site — list it once.
        private static void AddOnce(List<FlockMissingSymbol> found, FlockMissingSymbol symbol)
        {
            foreach (FlockMissingSymbol existing in found)
            {
                if (existing.Owner == symbol.Owner && existing.Name == symbol.Name)
                    return;
            }
            found.Add(symbol);
        }
    }
}
