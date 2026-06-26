using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Flock.Models;
using UnityEngine;

namespace Flock.Editor.Codegen
{
    // Emits typed shop accessors plus enum-keyed Purchase / AddGameFunds / GetMyInventory extensions.
    // FlockShopItemId maps each item to its string id; FlockFundId maps each currency name to itself
    // (name-based, not id-based — ShopItem.Currency is the name string). AddGameFunds resolves the
    // player's wallet by the currency-tagged template id baked at sync time.
    internal static class ShopEmitter
    {
        private sealed class ShopItemEntry
        {
            public string Member;
            public string Id;
            public int Price;
            public string Currency;
            public string ShopName;
        }

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

            // Item enum member -> item (distinct items across all shops).
            HashSet<string> itemMembers = new HashSet<string>();
            HashSet<string> seenItemIds = new HashSet<string>(StringComparer.Ordinal);
            List<ShopItemEntry> itemEntries = new List<ShopItemEntry>();
            foreach (Shop shop in ordered)
            {
                if (shop.ShopItems == null) continue;
                foreach (ShopItem item in shop.ShopItems
                    .Where(i => i != null && !string.IsNullOrEmpty(i.Id) && !string.IsNullOrEmpty(i.Name))
                    .OrderBy(i => i.Id, StringComparer.Ordinal))
                {
                    if (!seenItemIds.Add(item.Id)) continue;
                    itemEntries.Add(new ShopItemEntry
                    {
                        Member = CodeGenNamingHelpers.UnDuplicate(CodeGenNamingHelpers.ToPascalCase(item.Name), itemMembers),
                        Id = item.Id,
                        Price = item.Price,
                        Currency = item.Currency ?? "",
                        ShopName = shop.Name ?? ""
                    });
                }
            }

            // Fund enum member -> currency name (distinct shop currency names).
            HashSet<string> fundMembers = new HashSet<string>();
            SortedSet<string> currencyNames = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Shop shop in ordered)
                if (shop.ShopItems != null)
                    foreach (ShopItem item in shop.ShopItems)
                        if (item != null && !string.IsNullOrEmpty(item.Currency))
                            currencyNames.Add(item.Currency);

            List<KeyValuePair<string, string>> fundByMember = new List<KeyValuePair<string, string>>();
            foreach (string currencyName in currencyNames)
                fundByMember.Add(new KeyValuePair<string, string>(
                    CodeGenNamingHelpers.UnDuplicate(SanitizeIdentifier(currencyName), fundMembers), currencyName));

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

            File.WriteAllText(Path.Combine(outputDir, "FlockShopCatalog.g.cs"), BuildSource(shopByName, itemEntries, fundByMember, currencyTemplateId));
            Debug.Log($"[Flock Codegen] Shops: emitted {shopByName.Count} accessor(s), {itemEntries.Count} item id(s), {fundByMember.Count} fund(s).");
            return shopByName.Count;
        }

        // Turns an arbitrary string into a valid C# identifier: non-alphanumeric -> underscore, leading
        // underscore inserted when the string starts with a digit.
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
            List<ShopItemEntry> items,
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
                foreach (ShopItemEntry entry in items)
                {
                    string priceLabel = entry.Price > 0 ? $"{entry.Price} {entry.Currency}" : "free";
                    sb.AppendLine($"        /// <summary>{priceLabel} — {entry.ShopName}</summary>");
                    sb.AppendLine($"        {entry.Member},");
                }
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
                foreach (ShopItemEntry entry in items)
                    sb.AppendLine($"                case FlockShopItemId.{entry.Member}: return \"{CodeGenNamingHelpers.EscapeStringLiteral(entry.Id)}\";");
                sb.AppendLine("                default: throw new ArgumentOutOfRangeException(nameof(item));");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("        public static Task<PaginatedResponse<PlayerInventory>> GetMyInventoryAsync(this FlockShopProvider provider, int page = 1, int limit = 100, CancellationToken cancellationToken = default)");
            sb.AppendLine("            => provider.GetPlayerInventoryAsync(page: page, limit: limit, cancellationToken: cancellationToken);");
            sb.AppendLine();

            if (funds.Count > 0)
            {
                sb.AppendLine("#if !FLOCK_NO_PLAYER");
                if (!string.IsNullOrEmpty(currencyTemplateId))
                {
                    sb.AppendLine($"        private const string CurrencyTemplateId = \"{CodeGenNamingHelpers.EscapeStringLiteral(currencyTemplateId)}\";");
                    sb.AppendLine();
                    sb.AppendLine("        public static Task<PlayerData> AddGameFundsAsync(this FlockCommandProvider commands, FlockFundId fund, int amount, CancellationToken cancellationToken = default)");
                    sb.AppendLine("            => commands.AddGameFundsAsync(FundCurrency(fund), amount, CurrencyTemplateId, cancellationToken);");
                }
                else
                {
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
