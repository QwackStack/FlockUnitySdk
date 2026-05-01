using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Flock.Editor.Codegen
{
    internal static class SchemaPropertyEmitter
    {
        private static readonly string[] SystemObjectMembers =
        {
            "Equals", "GetHashCode", "GetType", "ToString", "Finalize", "MemberwiseClone"
        };

        public static int EmitProperties(
            IDictionary<string, object> schema,
            string parentClassName,
            string propertyAccessor,
            StringBuilder body,
            List<string> nestedClasses,
            HashSet<string> usedClassNames,
            IEnumerable<string> reservedPropertyNames,
            string contextLogName)
        {
            var usedPropertyNames = new HashSet<string>(SystemObjectMembers);
            if (reservedPropertyNames != null)
            {
                foreach (string n in reservedPropertyNames) usedPropertyNames.Add(n);
            }

            int emitted = 0;
            foreach (var field in schema.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                string rawProp = Naming.ToPascalCase(field.Key);
                string propName = Naming.Disambiguate(rawProp, usedPropertyNames);
                string jsonName = Naming.EscapeStringLiteral(field.Key);
                string keyForComment = Naming.SanitizeForLineComment(field.Key);

                if (field.Value is string typeStr)
                {
                    string csType = TypeMap.MapTypeString(typeStr);
                    if (csType == null)
                    {
                        body.AppendLine($"        // Skipped '{keyForComment}': unknown type '{Naming.SanitizeForLineComment(typeStr)}'.");
                        body.AppendLine();
                        Debug.LogWarning($"[Flock Codegen] {contextLogName}: unknown type '{typeStr}' for field '{field.Key}'.");
                        continue;
                    }
                    body.AppendLine($"        [JsonProperty(\"{jsonName}\")]");
                    body.AppendLine($"        public {csType} {propName} {propertyAccessor}");
                    body.AppendLine();
                    emitted++;
                    continue;
                }

                IDictionary<string, object> nestedDict = TryAsDict(field.Value);
                if (nestedDict != null)
                {
                    string nestedClassName = Naming.Disambiguate(parentClassName + propName, usedClassNames);
                    string nestedSource = BuildNestedClass(nestedClassName, nestedDict, propertyAccessor, nestedClasses, usedClassNames);
                    nestedClasses.Add(nestedSource);
                    body.AppendLine($"        [JsonProperty(\"{jsonName}\")]");
                    body.AppendLine($"        public {nestedClassName} {propName} {propertyAccessor}");
                    body.AppendLine();
                    emitted++;
                    continue;
                }

                if (field.Value is JArray jarray)
                {
                    string elementCsType = ResolveArrayElementCsType(
                        jarray, parentClassName, propName, propertyAccessor,
                        nestedClasses, usedClassNames, contextLogName, field.Key);
                    if (elementCsType == null)
                    {
                        body.AppendLine($"        // Skipped '{keyForComment}': unsupported typed array element.");
                        body.AppendLine();
                        continue;
                    }
                    body.AppendLine($"        [JsonProperty(\"{jsonName}\")]");
                    body.AppendLine($"        public global::System.Collections.Generic.List<{elementCsType}> {propName} {propertyAccessor}");
                    body.AppendLine();
                    emitted++;
                    continue;
                }

                string shape = field.Value == null ? "null" : field.Value.GetType().Name;
                body.AppendLine($"        // Skipped '{keyForComment}': unsupported shape '{shape}'.");
                body.AppendLine();
                Debug.LogWarning($"[Flock Codegen] {contextLogName}: unsupported shape '{shape}' for field '{field.Key}'.");
            }

            return emitted;
        }

        private static string BuildNestedClass(
            string className,
            IDictionary<string, object> schema,
            string propertyAccessor,
            List<string> nestedClasses,
            HashSet<string> usedClassNames)
        {
            var inner = new StringBuilder();
            EmitProperties(schema, className, propertyAccessor, inner, nestedClasses, usedClassNames, new[] { className }, className);

            var sb = new StringBuilder();
            sb.AppendLine($"    public partial class {className}");
            sb.AppendLine("    {");
            sb.Append(inner);
            sb.AppendLine("    }");
            sb.AppendLine();
            return sb.ToString();
        }

        private static string ResolveArrayElementCsType(
            JArray jarray,
            string parentClassName,
            string propName,
            string propertyAccessor,
            List<string> nestedClasses,
            HashSet<string> usedClassNames,
            string contextLogName,
            string fieldKey)
        {
            if (jarray.Count == 0)
            {
                Debug.LogWarning($"[Flock Codegen] {contextLogName}: empty typed array for '{fieldKey}'; use 'array' for List<object>.");
                return null;
            }

            JToken first = jarray[0];

            IDictionary<string, object> elementDict = TryAsDict(first);
            if (elementDict != null)
            {
                string itemClassName = Naming.Disambiguate(parentClassName + propName + "Item", usedClassNames);
                nestedClasses.Add(BuildNestedClass(itemClassName, elementDict, propertyAccessor, nestedClasses, usedClassNames));
                return itemClassName;
            }

            if (first.Type == JTokenType.String)
            {
                string typeStr = first.Value<string>();
                string csType = TypeMap.MapTypeString(typeStr);
                if (csType == null)
                    Debug.LogWarning($"[Flock Codegen] {contextLogName}: unknown array element type '{typeStr}' for '{fieldKey}'.");
                return csType;
            }

            Debug.LogWarning($"[Flock Codegen] {contextLogName}: unsupported array element shape '{first.Type}' for '{fieldKey}'.");
            return null;
        }

        private static IDictionary<string, object> TryAsDict(object value)
        {
            if (value is IDictionary<string, object> dict) return dict;
            if (value is JObject jobj) return jobj.ToObject<Dictionary<string, object>>();
            return null;
        }
    }
}
