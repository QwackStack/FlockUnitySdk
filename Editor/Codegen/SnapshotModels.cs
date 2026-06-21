using System;
using System.Collections.Generic;
using Flock.Models;

namespace Flock.Editor.Codegen
{
    internal class FlockSchemaSnapshot
    {
        //TODO add command snapshot
        public string GameVersionId { get; set; }
        public DateTime FetchedAt { get; set; }
        public List<PlayerTemplateSchema> PlayerTemplates { get; set; } = new List<PlayerTemplateSchema>();
        public List<GameConfigSchema> GameConfigs { get; set; } = new List<GameConfigSchema>();
        public List<Shop> Shops { get; set; } = new List<Shop>();
    }
}
