using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
    public class ShopData
    {
        [JsonProperty("stats")]
        public Dictionary<string, object> Stats { get; set; }

        [JsonProperty("web_shop_url")]
        public string WebShopUrl { get; set; }

        [JsonProperty("pwa_shop_url")]
        public string PwaShopUrl { get; set; }
    }

    public class Shop
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("game_version_id")]
        public string GameVersionId { get; set; }

        [JsonProperty("data")]
        public ShopData Data { get; set; }

        [JsonProperty("shop_items")]
        public List<ShopItem> ShopItems { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }
    }

    public class ShopItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("shop_id")]
        public string ShopId { get; set; }

        [JsonProperty("patch_id")]
        public string PatchId { get; set; }

        [JsonProperty("price")]
        public int Price { get; set; }

        [JsonProperty("currency")]
        public string Currency { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }
    }
}
