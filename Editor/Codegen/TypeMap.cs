namespace Flock.Editor.Codegen
{
    internal static class TypeMap
    {
        // Returns null for unknown type strings; emitter logs and skips the field.
        // "object" maps to JObject (consumers call .ToObject<T>() for typed views).
        // "array" / "list" map to List<object>. "list<T>" maps to List<T> where T
        // is recursively resolved (so "list<list<int>>" works). The backend sends
        // bare "array" with no element type info — typed arrays aren't supported.
        public static string MapTypeString(string typeString)
        {
            string normalized = (typeString ?? "").Trim().ToLowerInvariant();

            if (TryParseListGeneric(normalized, out string elementTypeString))
            {
                string elementCsType = MapTypeString(elementTypeString);
                if (elementCsType == null) return null;
                return $"global::System.Collections.Generic.List<{elementCsType}>";
            }

            //TODO fix array support once backend is added
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
                case "timestamp": return "global::System.DateTime";
                case "array":
                case "list":      return "global::System.Collections.Generic.List<object>";
                case "object":    return "global::Newtonsoft.Json.Linq.JObject";
                default:          return null;
            }
        }

        private static bool TryParseListGeneric(string normalized, out string elementType)
        {
            elementType = null;
            if (!normalized.StartsWith("list<")) return false;
            if (normalized[normalized.Length - 1] != '>') return false;

            const int openIdx = 4;
            elementType = normalized.Substring(openIdx + 1, normalized.Length - openIdx - 2).Trim();
            return elementType.Length > 0;
        }
    }
}
