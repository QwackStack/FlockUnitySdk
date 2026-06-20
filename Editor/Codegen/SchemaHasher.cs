using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Flock.Models;

namespace Flock.Editor.Codegen
{
    // Stable SHA-256 fingerprint of the schema content that drives codegen output. Changes iff the
    // generated code would change (template/config id, name, tag, and the full field tree), so a
    // same-version backend edit is detectable even when the GameVersionId is unchanged.
    internal static class SchemaHasher
    {
        public static string ComputeContentHash(FlockSchemaSnapshot snapshot)
        {
            StringBuilder sb = new StringBuilder();

            // Templates and configs are sorted (matches the emitters' stable file ordering) so a
            // server-side list reordering doesn't read as drift.
            IEnumerable<PlayerTemplateSchema> templates =
                (snapshot.PlayerTemplates ?? new List<PlayerTemplateSchema>())
                    .Where(t => t != null)
                    .OrderBy(t => t.Id ?? "", StringComparer.Ordinal)
                    .ThenBy(t => t.Name ?? "", StringComparer.Ordinal);
            foreach (PlayerTemplateSchema t in templates)
            {
                sb.Append('T');
                AppendString(sb, t.Id);
                AppendString(sb, t.Name);
                AppendString(sb, t.Tag);
                AppendFields(sb, t.Schema);
            }

            IEnumerable<GameConfigSchema> configs =
                (snapshot.GameConfigs ?? new List<GameConfigSchema>())
                    .Where(c => c != null)
                    .OrderBy(c => c.Id ?? "", StringComparer.Ordinal)
                    .ThenBy(c => c.Name ?? "", StringComparer.Ordinal);
            foreach (GameConfigSchema c in configs)
            {
                sb.Append('C');
                AppendString(sb, c.Id);
                AppendString(sb, c.Name);
                AppendString(sb, c.Tag);
                AppendFields(sb, c.Schema);
            }

            return Sha256Hex(sb.ToString());
        }

        // Field order is preserved: the generated Schema list literal preserves it, so a reorder is
        // a real output change. Mirrors SchemaPropertyEmitter's list-vs-single child handling.
        private static void AppendFields(StringBuilder sb, IList<TypedSchema> fields)
        {
            sb.Append('[');
            if (fields != null)
            {
                foreach (TypedSchema f in fields)
                {
                    if (f == null) { sb.Append('~'); continue; }
                    sb.Append('F');
                    AppendString(sb, f.Type);
                    AppendString(sb, f.FieldName);
                    AppendString(sb, f.TypeName);
                    List<TypedSchema> childList = f.SchemaAsList();
                    if (childList != null)
                        AppendFields(sb, childList);
                    else
                    {
                        TypedSchema single = f.SchemaAsSingle();
                        if (single != null)
                            AppendFields(sb, new List<TypedSchema> { single });
                        else
                            sb.Append("[]");
                    }
                }
            }
            sb.Append(']');
        }

        // Length-prefixed so distinct field content can't alias to the same canonical string.
        private static void AppendString(StringBuilder sb, string value)
        {
            string v = value ?? "";
            sb.Append(v.Length).Append(':').Append(v);
        }

        private static string Sha256Hex(string input)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                StringBuilder hex = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes)
                    hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
