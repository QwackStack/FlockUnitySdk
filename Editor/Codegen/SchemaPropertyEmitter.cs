using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Flock.Models;
using UnityEngine;

namespace Flock.Editor.Codegen
{
    // Walks a flattened typed-schema list and emits C# properties for each field.
    // Same walker is used for PlayerTemplate and GameConfig because both share the
    // TypedSchema shape (OpenAPI PlayerTemplateTypedSchema / GameConfigTypedSchema
    // are structurally identical).
    internal static class SchemaPropertyEmitter
    {
        private static readonly string[] SystemObjectMembers =
        {
            "Equals", "GetHashCode", "GetType", "ToString", "Finalize", "MemberwiseClone"
        };

        // Forces a specific C# type (and an optional extra attribute line) for any field whose name matches,
        // at any depth. Used to type the achievement config's "name" entry as FlockAchievementId.
        internal sealed class FieldTypeOverride
        {
            public string FieldName;
            public string CsType;
            public string Attribute;

            public bool Matches(TypedSchema field)
                => field != null && string.Equals(field.FieldName, FieldName, StringComparison.OrdinalIgnoreCase);
        }

        // What a top-level field emitted as: PascalCase property name + resolved C# type. Lets a caller find,
        // e.g., the list field and its element type without re-deriving the (UnDuplicate'd) generated names.
        internal readonly struct EmittedProperty
        {
            public readonly string PropertyName;
            public readonly string CsType;
            public EmittedProperty(string propertyName, string csType)
            {
                PropertyName = propertyName;
                CsType = csType;
            }
        }

        public static int EmitProperties(
            IList<TypedSchema> fields,
            string parentClassName,
            string propertyAccessor,
            StringBuilder body,
            List<string> nestedClasses,
            HashSet<string> usedClassNames,
            IEnumerable<string> reservedPropertyNames,
            string contextLogName,
            FieldTypeOverride typeOverride = null,
            IDictionary<string, EmittedProperty> topLevelOut = null)
        {
            HashSet<string> usedPropertyNames = new HashSet<string>(SystemObjectMembers);
            if (reservedPropertyNames != null)
            {
                foreach (string n in reservedPropertyNames) usedPropertyNames.Add(n);
            }

            int emitted = 0;
            foreach (TypedSchema field in fields.OrderBy(f => f?.FieldName ?? "", StringComparer.Ordinal))
            {
                if (field == null || string.IsNullOrEmpty(field.FieldName))
                    continue;

                string rawProp = CodeGenNamingHelpers.ToPascalCase(field.FieldName);
                string propName = CodeGenNamingHelpers.UnDuplicate(rawProp, usedPropertyNames);
                string jsonName = CodeGenNamingHelpers.EscapeStringLiteral(field.FieldName);
                string keyForComment = CodeGenNamingHelpers.SanitizeForLineComment(field.FieldName);

                string csType;
                string extraAttribute = null;
                if (typeOverride != null && typeOverride.Matches(field))
                {
                    csType = typeOverride.CsType;
                    extraAttribute = typeOverride.Attribute;
                }
                else
                {
                    csType = ResolveType(field, parentClassName, propName, propertyAccessor,
                                         nestedClasses, usedClassNames, contextLogName, typeOverride);
                }

                if (csType == null)
                {
                    body.AppendLine($"        // Skipped '{keyForComment}': unable to resolve type '{CodeGenNamingHelpers.SanitizeForLineComment(field.Type)}'.");
                    body.AppendLine();
                    continue;
                }

                if (!string.IsNullOrEmpty(extraAttribute))
                    body.AppendLine($"        {extraAttribute}");
                body.AppendLine($"        [JsonProperty(\"{jsonName}\")]");
                body.AppendLine($"        public {csType} {propName} {propertyAccessor}");
                body.AppendLine();
                emitted++;

                if (topLevelOut != null)
                    topLevelOut[field.FieldName] = new EmittedProperty(propName, csType);
            }

            return emitted;
        }

        private static string ResolveType(
            TypedSchema field,
            string parentClassName,
            string propName,
            string propertyAccessor,
            List<string> nestedClasses,
            HashSet<string> usedClassNames,
            string contextLogName,
            FieldTypeOverride typeOverride)
        {
            string typeLower = (field.Type ?? "").Trim().ToLowerInvariant();

            if (typeLower == "object")
            {
                List<TypedSchema> children = field.SchemaAsList();
                if (children == null || children.Count == 0)
                {
                    Debug.LogWarning($"[Flock Codegen] {contextLogName}: object field '{field.FieldName}' missing nested schema list; emitting JObject.");
                    return "JObject";
                }
                string nestedClassName = CodeGenNamingHelpers.UnDuplicate(parentClassName + propName, usedClassNames);
                nestedClasses.Add(BuildNestedClass(nestedClassName, children, propertyAccessor, nestedClasses, usedClassNames, typeOverride));
                return nestedClassName;
            }

            if (typeLower == "list" || typeLower == "array")
            {
                TypedSchema element = field.SchemaAsSingle();
                if (element == null)
                {
                    Debug.LogWarning($"[Flock Codegen] {contextLogName}: list field '{field.FieldName}' missing element schema; emitting List<object>.");
                    return "List<object>";
                }
                string elementType = ResolveType(element, parentClassName, propName + "Item", propertyAccessor,
                                                  nestedClasses, usedClassNames, contextLogName, typeOverride);
                if (elementType == null) return null;
                return $"List<{elementType}>";
            }

            if (typeLower == "dict")
            {
                TypedSchema valueSchema = field.SchemaAsSingle();
                if (valueSchema == null)
                {
                    Debug.LogWarning($"[Flock Codegen] {contextLogName}: dict field '{field.FieldName}' missing value schema; emitting Dictionary<string, object>.");
                    return "Dictionary<string, object>";
                }
                string valueType = ResolveType(valueSchema, parentClassName, propName + "Value", propertyAccessor,
                                                nestedClasses, usedClassNames, contextLogName, typeOverride);
                if (valueType == null) return null;
                return $"Dictionary<string, {valueType}>";
            }

            string mapped = TypeMap.MapPrimitiveTypeString(typeLower);
            if (mapped == null)
            {
                Debug.LogWarning($"[Flock Codegen] {contextLogName}: unknown type '{field.Type}' for field '{field.FieldName}'.");
            }
            return mapped;
        }

        private static string BuildNestedClass(
            string className,
            IList<TypedSchema> fields,
            string propertyAccessor,
            List<string> nestedClasses,
            HashSet<string> usedClassNames,
            FieldTypeOverride typeOverride)
        {
            StringBuilder inner = new StringBuilder();
            EmitProperties(fields, className, propertyAccessor, inner, nestedClasses, usedClassNames, new[] { className }, className, typeOverride);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"    public partial class {className}");
            sb.AppendLine("    {");
            sb.Append(inner);
            sb.AppendLine("    }");
            sb.AppendLine();
            return sb.ToString();
        }
    }
}
