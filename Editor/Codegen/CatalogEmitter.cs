using System;
using System.Collections.Generic;
using System.Globalization;
using Flock.Editor.Catalog;
using Flock.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Flock.Editor.Codegen
{
    // Writes the designer-facing FlockContentCatalog.asset from the fetched snapshot. Editor-only and
    // skipped in batch mode (asset creation needs the generated folder already imported by AssetDatabase).
    internal static class CatalogEmitter
    {
        internal const string Subdir = "Catalog";
        internal const string AssetFileName = "FlockContentCatalog.asset";
        private const int MaxValueLength = 400;

        /// <summary>Project-relative path of the catalog asset for a given generated root.</summary>
        internal static string AssetPath(string generatedRoot) => $"{generatedRoot}/{Subdir}/{AssetFileName}";

        public static int Emit(FlockSchemaSnapshot snapshot, string gameVersionName, string generatedRoot)
        {
            string folder = generatedRoot + "/" + Subdir;
            string assetPath = AssetPath(generatedRoot);

            // Reset our own subfolder so a re-sync never leaves a stale catalog behind.
            if (AssetDatabase.IsValidFolder(folder))
                AssetDatabase.DeleteAsset(folder);
            AssetDatabase.CreateFolder(generatedRoot, Subdir);

            FlockContentCatalog catalog = ScriptableObject.CreateInstance<FlockContentCatalog>();
            catalog.gameVersion = gameVersionName ?? "";
            catalog.gameVersionId = snapshot.GameVersionId ?? "";
            catalog.generatedAtUtc = snapshot.FetchedAt.ToString("o", CultureInfo.InvariantCulture);

            foreach (Shop shop in snapshot.Shops ?? new List<Shop>())
            {
                if (shop == null) continue;
                CatalogShop entry = new CatalogShop { name = shop.Name ?? "", status = shop.Status ?? "" };
                foreach (ShopItem item in shop.ShopItems ?? new List<ShopItem>())
                {
                    if (item == null) continue;
                    entry.items.Add(new CatalogShopItem
                    {
                        name = item.Name ?? "",
                        price = item.Price,
                        currency = item.Currency ?? "",
                        status = item.Status ?? ""
                    });
                }
                catalog.shops.Add(entry);
            }

            foreach (GameConfigSchema cfg in snapshot.GameConfigs ?? new List<GameConfigSchema>())
            {
                if (cfg == null) continue;
                catalog.configs.Add(new CatalogSchema
                {
                    name = cfg.Name ?? "",
                    tag = cfg.Tag ?? "",
                    fields = BuildFields(cfg.Data, cfg.Schema)
                });
            }

            foreach (PlayerTemplateSchema tpl in snapshot.PlayerTemplates ?? new List<PlayerTemplateSchema>())
            {
                if (tpl == null) continue;
                // The achievement template is surfaced in its own section below — don't list it twice.
                if (string.Equals(tpl.Tag, "achievement", StringComparison.OrdinalIgnoreCase)) continue;
                catalog.templates.Add(new CatalogSchema
                {
                    name = tpl.Name ?? "",
                    tag = tpl.Tag ?? "",
                    fields = BuildFields(tpl.Data, tpl.Schema)
                });
            }

            // Achievements: surface the fields of the single "achievement"-tagged template as their own
            // list so designers see the available FlockAchievementId members without digging through templates.
            foreach (PlayerTemplateSchema tpl in snapshot.PlayerTemplates ?? new List<PlayerTemplateSchema>())
            {
                if (tpl == null || !string.Equals(tpl.Tag, "achievement", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (TypedSchema field in tpl.Schema ?? new List<TypedSchema>())
                {
                    if (field == null || string.IsNullOrEmpty(field.FieldName)) continue;
                    catalog.achievements.Add(new CatalogAchievement
                    {
                        name = field.FieldName,
                        type = string.IsNullOrEmpty(field.TypeName) ? (field.Type ?? "") : field.TypeName
                    });
                }
                break;
            }

            AssetDatabase.CreateAsset(catalog, assetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Flock Codegen] Catalog: {catalog.shops.Count} shop(s), {catalog.configs.Count} config(s), {catalog.templates.Count} template(s), {catalog.achievements.Count} achievement(s) → {assetPath}");
            return catalog.shops.Count + catalog.configs.Count + catalog.templates.Count + catalog.achievements.Count;
        }

        // Prefers actual values (Data); falls back to the schema's field names/types when there is no data.
        private static List<CatalogField> BuildFields(List<DataField> data, List<TypedSchema> schema)
        {
            List<CatalogField> fields = new List<CatalogField>();

            if (data != null && data.Count > 0)
            {
                JObject flat = data.ToFlatObject();
                foreach (DataField field in data)
                {
                    if (field == null || string.IsNullOrEmpty(field.FieldName)) continue;
                    JToken token = flat[field.FieldName];
                    fields.Add(new CatalogField
                    {
                        name = field.FieldName,
                        type = string.IsNullOrEmpty(field.TypeName) ? (field.Type ?? "") : field.TypeName,
                        value = Cap(token == null || token.Type == JTokenType.Null ? "" : token.ToString(Formatting.None))
                    });
                }
                return fields;
            }

            foreach (TypedSchema field in schema ?? new List<TypedSchema>())
            {
                if (field == null || string.IsNullOrEmpty(field.FieldName)) continue;
                fields.Add(new CatalogField
                {
                    name = field.FieldName,
                    type = string.IsNullOrEmpty(field.TypeName) ? (field.Type ?? "") : field.TypeName,
                    value = ""
                });
            }
            return fields;
        }

        private static string Cap(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length > MaxValueLength ? value.Substring(0, MaxValueLength) + "…" : value;
        }
    }
}
