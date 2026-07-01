using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Flock.Interfaces;
using Flock.Models;
using Newtonsoft.Json;
using UnityEngine;

namespace Flock.Editor.Codegen
{
    internal static class GameConfigEmitter
    {
        private const string AchievementTag = "achievement";

        // The list field + element type of the achievement config, captured so GetAchievementDetailsAsync
        // can return the real generated entry type without re-deriving its name.
        private struct AchievementDetailInfo
        {
            public string ConfigClass;
            public string ListProperty;
            public string EntryType;
        }

        // achievementsGenerated: only type the achievement config's name field as FlockAchievementId (and emit
        // the lookup) when the enum actually exists; otherwise the config falls back to a plain-string name.
        public static EmitResult Emit(IList<GameConfigSchema> configs, string outputDir, bool achievementsGenerated)
        {
            ResetDirectory(outputDir);

            HashSet<string> used = new HashSet<string>();
            Dictionary<string, string> classNamesById = new Dictionary<string, string>();
            int emitted = 0, skipped = 0;
            AchievementDetailInfo? achievementInfo = null;

            foreach (GameConfigSchema c in configs.OrderBy(x => x?.Id ?? "", StringComparer.Ordinal)
                                                    .ThenBy(x => x?.Name ?? "", StringComparer.Ordinal))
            {
                if (c.Schema == null || c.Schema.Count == 0)
                {
                    Debug.LogWarning($"[Flock Codegen] GameConfig '{c.Name}' (id={c.Id}) has no schema; skipping.");
                    skipped++;
                    continue;
                }

                bool isAchievement = achievementsGenerated && string.Equals(c.Tag, AchievementTag, StringComparison.OrdinalIgnoreCase);
                string className = CodeGenNamingHelpers.UnDuplicate(CodeGenNamingHelpers.ToPascalCase(c.Name) + "Config", used);
                string source = BuildClass(className, c, used, isAchievement, out AchievementDetailInfo? info);
                File.WriteAllText(Path.Combine(outputDir, className + ".g.cs"), source);
                if (!string.IsNullOrEmpty(c.Id))
                    classNamesById[c.Id] = className;
                if (info.HasValue)
                    achievementInfo = info;
                emitted++;
            }

            if (achievementInfo.HasValue)
                File.WriteAllText(Path.Combine(outputDir, "FlockAchievementDetails.g.cs"), BuildLookupSource(achievementInfo.Value));

            Debug.Log($"[Flock Codegen] GameConfigs: emitted {emitted}, skipped {skipped}.");
            return new EmitResult(emitted, classNamesById);
        }

        public readonly struct EmitResult
        {
            public readonly int Count;
            public readonly IReadOnlyDictionary<string, string> ClassNamesById;
            public EmitResult(int count, IReadOnlyDictionary<string, string> classNamesById)
            {
                Count = count;
                ClassNamesById = classNamesById;
            }
        }

        private static string BuildClass(string className, GameConfigSchema c, HashSet<string> usedClassNames,
            bool isAchievement, out AchievementDetailInfo? achievementInfo)
        {
            achievementInfo = null;
            string tagLiteral = ResolveSchemaTagLiteral(c.Tag);
            List<string> reservedProps = new List<string> { className, "SourceId", "SourceName", "SourceTag", "Schema" };

            // For the achievement config, type the entry's "name" field as the generated enum so details
            // are awarded/looked up by FlockAchievementId, not a raw string.
            SchemaPropertyEmitter.FieldTypeOverride typeOverride = isAchievement
                ? new SchemaPropertyEmitter.FieldTypeOverride
                {
                    FieldName = "name",
                    CsType = "FlockAchievementId",
                    Attribute = "[JsonConverter(typeof(FlockAchievementIdConverter))]"
                }
                : null;
            Dictionary<string, SchemaPropertyEmitter.EmittedProperty> captured =
                isAchievement ? new Dictionary<string, SchemaPropertyEmitter.EmittedProperty>() : null;

            StringBuilder schemaSource = new StringBuilder();
            schemaSource.AppendLine($"        public const string SourceId = \"{CodeGenNamingHelpers.EscapeStringLiteral(c.Id)}\";");
            schemaSource.AppendLine($"        public const string SourceName = \"{CodeGenNamingHelpers.EscapeStringLiteral(c.Name)}\";");
            if (tagLiteral != null)
                schemaSource.AppendLine($"        public const SchemaTag SourceTag = {tagLiteral};");

            List<string> nestedClasses = new List<string>();
            int emittedProps = SchemaPropertyEmitter.EmitProperties(
                c.Schema, className, "{ get; private set; }",
                schemaSource, nestedClasses, usedClassNames, reservedProps, className, typeOverride, captured);
            if (emittedProps == 0)
            {
                Debug.LogWarning($"[Flock Codegen] {className}: schema had {c.Schema.Count} field(s) but none were emittable.");
            }

            if (isAchievement)
                achievementInfo = ResolveAchievementInfo(className, c, captured);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   Generated by Flock Codegen. Do not edit by hand.");
            sb.AppendLine($"//   Source: GameConfig id={CodeGenNamingHelpers.SanitizeForLineComment(c.Id)} name={CodeGenNamingHelpers.SanitizeForLineComment(c.Name)} tag={CodeGenNamingHelpers.SanitizeForLineComment(c.Tag)}");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using Flock.Interfaces;");
            sb.AppendLine("using Flock.Models;");
            if (isAchievement)
                sb.AppendLine("using Flock.Generated.Achievements;");
            sb.AppendLine("using Newtonsoft.Json;");
            sb.AppendLine("using Newtonsoft.Json.Linq;");
            sb.AppendLine();
            sb.AppendLine("namespace Flock.Generated.Configs");
            sb.AppendLine("{");
            sb.AppendLine($"    public partial class {className}");
            sb.AppendLine("    {");
            sb.Append(schemaSource);
            sb.AppendLine();
            sb.AppendLine("        public static IReadOnlyList<TypedSchema> Schema { get; } = new List<TypedSchema>");
            sb.AppendLine("        {");
            for (int i = 0; i < c.Schema.Count; i++)
            {
                sb.Append("            ");
                sb.Append(SerializeTypedSchema(c.Schema[i], "            "));
                if (i < c.Schema.Count - 1) sb.AppendLine(",");
                else sb.AppendLine();
            }
            sb.AppendLine("        };");
            sb.AppendLine("    }");
            foreach (string nested in nestedClasses)
            {
                sb.AppendLine();
                sb.Append(nested);
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        // The achievements list is the top-level list field whose element carries a "name" field; pull its
        // generated property name + element type from what the property emitter actually wrote.
        private static AchievementDetailInfo? ResolveAchievementInfo(
            string className, GameConfigSchema c, IDictionary<string, SchemaPropertyEmitter.EmittedProperty> captured)
        {
            foreach (TypedSchema field in c.Schema)
            {
                if (field == null || string.IsNullOrEmpty(field.FieldName)) continue;
                string typeLower = (field.Type ?? "").Trim().ToLowerInvariant();
                if (typeLower != "list" && typeLower != "array") continue;

                TypedSchema element = field.SchemaAsSingle();
                List<TypedSchema> elementFields = element?.SchemaAsList();
                bool hasNameEntry = elementFields != null &&
                    elementFields.Any(f => f != null && string.Equals(f.FieldName, "name", StringComparison.OrdinalIgnoreCase));
                if (!hasNameEntry) continue;

                if (captured != null && captured.TryGetValue(field.FieldName, out SchemaPropertyEmitter.EmittedProperty prop)
                    && TryGetListElementType(prop.CsType, out string entryType))
                {
                    return new AchievementDetailInfo
                    {
                        ConfigClass = className,
                        ListProperty = prop.PropertyName,
                        EntryType = entryType
                    };
                }
            }

            Debug.LogWarning($"[Flock Codegen] Achievement config '{c.Name}': no list field with a 'name' entry found; GetAchievementDetailsAsync not generated.");
            return null;
        }

        private static bool TryGetListElementType(string csType, out string elementType)
        {
            elementType = null;
            if (string.IsNullOrEmpty(csType) || !csType.StartsWith("List<", StringComparison.Ordinal) || !csType.EndsWith(">", StringComparison.Ordinal))
                return false;
            elementType = csType.Substring("List<".Length, csType.Length - "List<".Length - 1);
            return true;
        }

        private static string BuildLookupSource(AchievementDetailInfo info)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   Generated by Flock Codegen. Do not edit by hand.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Threading;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using Flock.Generated.Achievements;");
            sb.AppendLine("using Flock.Providers;");
            sb.AppendLine();
            sb.AppendLine("namespace Flock.Generated.Configs");
            sb.AppendLine("{");
            sb.AppendLine("    public static class FlockAchievementDetailsExtensions");
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>Fetches the achievements config and returns the detail entry for the given achievement (null if absent).</summary>");
            sb.AppendLine($"        public static async Task<{info.EntryType}> GetAchievementDetailsAsync(this FlockConfigProvider provider, FlockAchievementId achievement, CancellationToken cancellationToken = default)");
            sb.AppendLine("        {");
            sb.AppendLine($"            {info.ConfigClass} config = await provider.GetByConfigIdAsync<{info.ConfigClass}>({info.ConfigClass}.SourceId, cancellationToken);");
            sb.AppendLine($"            return config?.{info.ListProperty}?.FirstOrDefault(entry => entry.Name == achievement);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string SerializeTypedSchema(TypedSchema field, string baseIndent)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("new TypedSchema { ");
            sb.Append($"Type = \"{CodeGenNamingHelpers.EscapeStringLiteral(field.Type ?? "")}\", ");
            sb.Append($"FieldName = \"{CodeGenNamingHelpers.EscapeStringLiteral(field.FieldName ?? "")}\", ");
            sb.Append($"TypeName = \"{CodeGenNamingHelpers.EscapeStringLiteral(field.TypeName ?? "")}\"");

            if (field.Schema is List<TypedSchema> list && list.Count > 0)
            {
                sb.Append(", Schema = new List<TypedSchema>");
                sb.AppendLine();
                sb.Append(baseIndent);
                sb.AppendLine("{");
                string innerIndent = baseIndent + "    ";
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append(innerIndent);
                    sb.Append(SerializeTypedSchema(list[i], innerIndent));
                    if (i < list.Count - 1) sb.AppendLine(",");
                    else sb.AppendLine();
                }
                sb.Append(baseIndent);
                sb.Append("}");
            }
            else if (field.Schema is TypedSchema single)
            {
                sb.Append(", Schema = ");
                sb.Append(SerializeTypedSchema(single, baseIndent));
            }

            sb.Append(" }");
            return sb.ToString();
        }

        private static string ResolveSchemaTagLiteral(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            if (Enum.TryParse<SchemaTag>(tag.Trim(), ignoreCase: true, out SchemaTag parsed)
                && parsed != SchemaTag.empty)
            {
                return $"SchemaTag.{parsed}";
            }
            Debug.LogWarning($"[Flock Codegen] Unknown SchemaTag '{tag}'; SourceTag omitted.");
            return null;
        }

        private static void ResetDirectory(string dir)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
    }
}
