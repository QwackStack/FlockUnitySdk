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
