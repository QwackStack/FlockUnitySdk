using System.Collections.Generic;
using System.Text;

namespace Flock.Editor.Codegen
{
    internal static class Naming
    {
        public static string ToPascalCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return "Unnamed";
            var sb = new StringBuilder();
            bool nextUpper = true;
            foreach (char c in input)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(nextUpper ? char.ToUpperInvariant(c) : c);
                    nextUpper = false;
                }
                else
                {
                    nextUpper = true;
                }
            }
            string result = sb.ToString();
            if (result.Length == 0) return "Unnamed";
            if (char.IsDigit(result[0])) result = "_" + result;
            return result;
        }

        public static string Disambiguate(string baseName, HashSet<string> used)
        {
            if (used.Add(baseName)) return baseName;
            int i = 2;
            while (!used.Add($"{baseName}_{i}")) i++;
            return $"{baseName}_{i}";
        }

        public static string EscapeStringLiteral(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    case '\a': sb.Append("\\a"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\v': sb.Append("\\v"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string SanitizeForLineComment(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\r' || c == '\n') sb.Append(' ');
                else if (c < 0x20 || c == 0x7f) sb.Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
