using System;
using System.Collections.Generic;
using UnityEngine;

namespace Flock.Editor.Catalog
{
    /// Read-only snapshot of shops, game configs and player templates, written by Flock Codegen so
    /// designers can browse backend content in the Inspector without reading generated C#. Flock-owned
    /// and overwritten on every Sync — do not edit by hand. Editor-only; never referenced at runtime.
    public class FlockContentCatalog : ScriptableObject
    {
        public string gameVersion;
        public string gameVersionId;
        public string generatedAtUtc;

        public List<CatalogShop> shops = new List<CatalogShop>();
        public List<CatalogSchema> configs = new List<CatalogSchema>();
        public List<CatalogSchema> templates = new List<CatalogSchema>();
    }

    [Serializable]
    public class CatalogShop
    {
        public string name;
        public string status;
        public List<CatalogShopItem> items = new List<CatalogShopItem>();
    }

    [Serializable]
    public class CatalogShopItem
    {
        public string name;
        public int price;
        public string currency;
        public string status;
    }

    // Used for both game configs and player templates.
    [Serializable]
    public class CatalogSchema
    {
        public string name;
        public string tag;
        public List<CatalogField> fields = new List<CatalogField>();
    }

    [Serializable]
    public class CatalogField
    {
        public string name;
        public string type;
        public string value;
    }
}
