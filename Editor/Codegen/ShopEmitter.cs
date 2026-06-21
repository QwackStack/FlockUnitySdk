using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Flock.Models;
using UnityEngine;

namespace Flock.Editor.Codegen
{
    // Emits typed shop accessors plus enum-keyed Purchase / AddGameFunds extensions. The enums list the
    // available shop-item ids and shop currency ids; the generated methods map each enum value to its
    // string id and call the raw provider methods. Because these live in the consumer's generated
    // assembly they don't exist until a sync runs. Currency NAMES are admin-only (OAuth2 /currency), so
    // FlockFundId members are id-based. AddGameFunds sends the currency id and resolves the player's wallet
    // (the row for the "currency"-tagged template) — that template id is baked here from the schema.
    internal static class ShopEmitter
    {
        public static int Emit(IList<Shop> shops, IList<PlayerTemplateSchema> playerTemplates, string outputDir)
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
            Directory.CreateDirectory(outputDir);

            List<Shop> ordered = (shops ?? new List<Shop>())
                .Where(s => s != null && !string.IsNullOrEmpty(s.Id) && !string.IsNullOrEmpty(s.Name))
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .ToList();

            // Shop accessor: stable PascalCase name -> shop.
            HashSet<string> shopNames = new HashSet<string>();
            List<KeyValuePair<string, Shop>> shopByName = new List<KeyValuePair<string, Shop>>();
            foreach (Shop shop in ordered)
                shopByName.Add(new KeyValuePair<string, Shop>(
                    CodeGenNamingHelpers.UnDuplicate(CodeGenNamingHelpers.ToPascalCase(shop.Name), shopNames), shop));

            // Item enum member -> item id (distinct items across all shops).
            HashSet<string> itemMembers = new HashSet<string>();
            HashSet<string> seenItemIds = new HashSet<string>(StringComparer.Ordinal);
            List<KeyValuePair<string, string>> itemByMember = new List<KeyValuePair<string, string>>();
            foreach (Shop shop in ordered)
            {
                if (shop.ShopItems == null) continue;
                foreach (ShopItem item in shop.ShopItems
                    .Where(i => i != null && !string.IsNullOrEmpty(i.Id) && !string.IsNullOrEmpty(i.Name))
                    .OrderBy(i => i.Id, StringComparer.Ordinal))
                {
                    if (!seenItemIds.Add(item.Id)) continue;
                    itemByMember.Add(new KeyValuePair<string, string>(
                        CodeGenNamingHelpers.UnDuplicate(CodeGenNamingHelpers.ToPascalCase(item.Name), itemMembers), item.Id));
                }
            }

            // Fund enum member -> currency id (distinct shop currencies). Member is id-based since the
            // currency name is admin-only and not reachable with the SDK's API key.
            HashSet<string> fundMembers = new HashSet<string>();
            SortedSet<string> currencyIds = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Shop shop in ordered)
                if (shop.ShopItems != null)
                    foreach (ShopItem item in shop.ShopItems)
                        if (item != null && !string.IsNullOrEmpty(item.Currency))
                            currencyIds.Add(item.Currency);

            List<KeyValuePair<string, string>> fundByMember = new List<KeyValuePair<string, string>>();
            foreach (string currencyId in currencyIds)
                fundByMember.Add(new KeyValuePair<string, string>(
                    CodeGenNamingHelpers.UnDuplicate(SanitizeIdentifier(currencyId), fundMembers), currencyId));

            // The wallet is the player template tagged "currency"; bake its id so the generated AddGameFunds
            // resolves the row directly by id (skips the runtime fetch-all-templates tag scan). Empty -> resolve by tag.
            string currencyTemplateId = "";
            foreach (PlayerTemplateSchema t in playerTemplates ?? new List<PlayerTemplateSchema>())
                if (t != null && !string.IsNullOrEmpty(t.Id) && string.Equals(t.Tag, "currency", StringComparison.OrdinalIgnoreCase))
                {
                    currencyTemplateId = t.Id;
                    break;
                }

            if (fundByMember.Count > 0 && string.IsNullOrEmpty(currencyTemplateId))
                Debug.LogWarning("[Flock Codegen] Shop currencies exist but no player template is tagged \"currency\" — generated AddGameFunds(FlockFundId) will throw at runtime. Tag your wallet/currency template \"currency\".");

            File.WriteAllText(Path.Combine(outputDir, "FlockShopCatalog.g.cs"), BuildSource(shopByName, itemByMember, fundByMember, currencyTemplateId));
            Debug.Log($"[Flock Codegen] Shops: emitted {shopByName.Count} accessor(s), {itemByMember.Count} item id(s), {fundByMember.Count} fund(s).");
            return shopByName.Count;
        }

        // Turns an arbitrary id into a valid C# identifier: non-alphanumeric -> underscore, and a leading
        // underscore only when the id starts with a digit (identifiers can't start with one).
        private static string SanitizeIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            StringBuilder sb = new StringBuilder(s.Length + 1);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        private static string BuildSource(
            List<KeyValuePair<string, Shop>> shops,
            List<KeyValuePair<string, string>> items,
            List<KeyValuePair<string, string>> funds,
            string currencyTemplateId)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   Generated by Flock Codegen. Do not edit by hand.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Threading;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using Flock.Models;");
            sb.AppendLine("using Flock.Providers;");
            sb.AppendLine();
            sb.AppendLine("namespace Flock.Generated.Shops");
            sb.AppendLine("{");

            if (items.Count > 0)
            {
                sb.AppendLine("    public enum FlockShopItemId");
                sb.AppendLine("    {");
                foreach (KeyValuePair<string, string> it in items)
                    sb.AppendLine($"        {it.Key},");
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            if (funds.Count > 0)
            {
                sb.AppendLine("    public enum FlockFundId");
                sb.AppendLine("    {");
                foreach (KeyValuePair<string, string> f in funds)
                    sb.AppendLine($"        {f.Key},");
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            sb.AppendLine("    public static class FlockShopExtensions");
            sb.AppendLine("    {");

            foreach (KeyValuePair<string, Shop> kv in shops)
            {
                sb.AppendLine($"        public static Task<Shop> Get{kv.Key}ShopAsync(this FlockShopProvider provider, CancellationToken cancellationToken = default)");
                sb.AppendLine($"            => provider.GetByIdAsync(\"{CodeGenNamingHelpers.EscapeStringLiteral(kv.Value.Id)}\", cancellationToken);");
                sb.AppendLine();
            }

            if (items.Count > 0)
            {
                sb.AppendLine("        public static Task<PlayerInventory> PurchaseAsync(this FlockShopProvider provider, FlockShopItemId item, CancellationToken cancellationToken = default)");
                sb.AppendLine("            => provider.PurchaseAsync(ShopItemUuid(item), cancellationToken: cancellationToken);");
                sb.AppendLine();
                sb.AppendLine("        private static string ShopItemUuid(FlockShopItemId item)");
                sb.AppendLine("        {");
                sb.AppendLine("            switch (item)");
                sb.AppendLine("            {");
                foreach (KeyValuePair<string, string> it in items)
                    sb.AppendLine($"                case FlockShopItemId.{it.Key}: return \"{CodeGenNamingHelpers.EscapeStringLiteral(it.Value)}\";");
                sb.AppendLine("                default: throw new ArgumentOutOfRangeException(nameof(item));");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (funds.Count > 0)
            {
                sb.AppendLine("#if !FLOCK_NO_PLAYER");
                if (!string.IsNullOrEmpty(currencyTemplateId))
                {
                    // Fast path: the currency template id is known, so resolve the wallet directly by id.
                    sb.AppendLine($"        private const string CurrencyTemplateId = \"{CodeGenNamingHelpers.EscapeStringLiteral(currencyTemplateId)}\";");
                    sb.AppendLine();
                    sb.AppendLine("        public static Task<PlayerData> AddGameFundsAsync(this FlockCommandProvider commands, FlockFundId fund, int amount, CancellationToken cancellationToken = default)");
                    sb.AppendLine("            => commands.AddGameFundsAsync(FundCurrency(fund), amount, CurrencyTemplateId, cancellationToken);");
                }
                else
                {
                    // No "currency"-tagged template found at sync; fall back to the runtime tag-resolving overload.
                    sb.AppendLine("        public static Task<PlayerData> AddGameFundsAsync(this FlockCommandProvider commands, FlockFundId fund, int amount, CancellationToken cancellationToken = default)");
                    sb.AppendLine("            => commands.AddGameFundsAsync(FundCurrency(fund), amount, cancellationToken);");
                }
                sb.AppendLine();
                sb.AppendLine("        private static string FundCurrency(FlockFundId fund)");
                sb.AppendLine("        {");
                sb.AppendLine("            switch (fund)");
                sb.AppendLine("            {");
                foreach (KeyValuePair<string, string> f in funds)
                    sb.AppendLine($"                case FlockFundId.{f.Key}: return \"{CodeGenNamingHelpers.EscapeStringLiteral(f.Value)}\";");
                sb.AppendLine("                default: throw new ArgumentOutOfRangeException(nameof(fund));");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine("#endif");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
