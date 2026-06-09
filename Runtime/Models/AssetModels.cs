using System;
using Newtonsoft.Json;

namespace Flock.Models
{
    public class AssetSchema
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("extension_type")]
        public string ExtensionType { get; set; }

        [JsonProperty("size_bytes")]
        public long? SizeBytes { get; set; }

        [JsonProperty("s3_download_url")]
        public string S3DownloadUrl { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}
