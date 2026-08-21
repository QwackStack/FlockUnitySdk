using System;

namespace Flock.Editor.Codegen
{
    internal static class TypeMap
    {
        // Maps a primitive type string from the flattened typed-schema to a C# type.
        // Composite types ("object", "list"/"array", "dict") are walked structurally by
        // SchemaPropertyEmitter and never pass through here.
        public static string MapPrimitiveTypeString(string typeString)
        {
            string normalized = (typeString ?? "").Trim().ToLowerInvariant();

            // An optional field arrives as "datetime?" / "integer?". Without stripping the marker the whole
            // field was written off as an unknown type and silently skipped.
            bool optional = normalized.EndsWith("?", StringComparison.Ordinal);
            if (optional)
                normalized = normalized.Substring(0, normalized.Length - 1).TrimEnd();

            string mapped = MapBase(normalized);
            if (mapped == null)
                return null;

            // Only value types need the C# nullable marker; string is already nullable.
            return optional && mapped != "string" ? mapped + "?" : mapped;
        }

        private static string MapBase(string normalized)
        {
            switch (normalized)
            {
                case "string":    return "string";
                case "integer":
                case "int":       return "int";
                case "long":
                case "int64":     return "long";
                case "float":     return "float";
                case "number":
                case "double":    return "double";
                case "boolean":
                case "bool":      return "bool";
                case "datetime":
                case "date":
                case "timestamp": return "DateTime";
                default:          return null;
            }
        }
    }
}
