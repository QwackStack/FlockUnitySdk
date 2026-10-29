using System.Collections.Generic;
using Flock.Editor.Codegen;
using Flock.Models;
using NUnit.Framework;

namespace Flock.Tests
{
    // Covers SchemaHasher.ComputeContentHash — the fingerprint CI Verify compares to detect
    // same-version schema drift. The hash must be deterministic and change iff codegen output would.
    public class SchemaHasherTests
    {
        private static TypedSchema Field(string type, string name, string typeName = "")
            => new TypedSchema { Type = type, FieldName = name, TypeName = typeName };

        private static PlayerTemplateSchema Template(string id, string name, string tag, params TypedSchema[] fields)
            => new PlayerTemplateSchema { Id = id, Name = name, Tag = tag, Schema = new List<TypedSchema>(fields) };

        private static GameConfigSchema Config(string id, string name, string tag, params TypedSchema[] fields)
            => new GameConfigSchema { Id = id, Name = name, Tag = tag, Schema = new List<TypedSchema>(fields) };

        private static FlockSchemaSnapshot Snapshot(
            string gameVersionId,
            IEnumerable<PlayerTemplateSchema> templates = null,
            IEnumerable<GameConfigSchema> configs = null)
            => new FlockSchemaSnapshot
            {
                GameVersionId = gameVersionId,
                PlayerTemplates = new List<PlayerTemplateSchema>(templates ?? new PlayerTemplateSchema[0]),
                GameConfigs = new List<GameConfigSchema>(configs ?? new GameConfigSchema[0]),
            };

        [Test]
        public void IdenticalContent_ProducesSameHash()
        {
            FlockSchemaSnapshot a = Snapshot("v1", new[] { Template("t1", "Progress", null, Field("integer", "level"), Field("string", "name")) });
            FlockSchemaSnapshot b = Snapshot("v1", new[] { Template("t1", "Progress", null, Field("integer", "level"), Field("string", "name")) });
            Assert.AreEqual(SchemaHasher.ComputeContentHash(a), SchemaHasher.ComputeContentHash(b));
        }

        [Test]
        public void HashIgnoresGameVersionId()
        {
            // Content hash is schema-only; the GameVersionId is tracked and verified separately.
            FlockSchemaSnapshot a = Snapshot("v1", new[] { Template("t1", "Progress", null, Field("integer", "level")) });
            FlockSchemaSnapshot b = Snapshot("v2", new[] { Template("t1", "Progress", null, Field("integer", "level")) });
            Assert.AreEqual(SchemaHasher.ComputeContentHash(a), SchemaHasher.ComputeContentHash(b));
        }

        [Test]
        public void TemplateListOrder_DoesNotChangeHash()
        {
            PlayerTemplateSchema t1 = Template("t1", "A", null, Field("integer", "x"));
            PlayerTemplateSchema t2 = Template("t2", "B", null, Field("string", "y"));
            FlockSchemaSnapshot a = Snapshot("v1", new[] { t1, t2 });
            FlockSchemaSnapshot b = Snapshot("v1", new[] { t2, t1 });
            Assert.AreEqual(SchemaHasher.ComputeContentHash(a), SchemaHasher.ComputeContentHash(b));
        }

        [Test]
        public void FieldRename_ChangesHash()
        {
            FlockSchemaSnapshot a = Snapshot("v1", new[] { Template("t1", "P", null, Field("integer", "level")) });
            FlockSchemaSnapshot b = Snapshot("v1", new[] { Template("t1", "P", null, Field("integer", "lvl")) });
            Assert.AreNotEqual(SchemaHasher.ComputeContentHash(a), SchemaHasher.ComputeContentHash(b));
        }

        [Test]
        public void FieldTypeChange_ChangesHash()
        {
            FlockSchemaSnapshot a = Snapshot("v1", new[] { Template("t1", "P", null, Field("integer", "level")) });
            FlockSchemaSnapshot b = Snapshot("v1", new[] { Template("t1", "P", null, Field("string", "level")) });
            Assert.AreNotEqual(SchemaHasher.ComputeContentHash(a), SchemaHasher.ComputeContentHash(b));
        }

        [Test]
        public void FieldReorder_ChangesHash()
        {
            // The generated Schema list literal preserves field order, so a reorder is a real output change.
            FlockSchemaSnapshot a = Snapshot("v1", new[] { Template("t1", "P", null, Field("integer", "a"), Field("integer", "b")) });
            FlockSchemaSnapshot b = Snapshot("v1", new[] { Template("t1", "P", null, Field("integer", "b"), Field("integer", "a")) });
            Assert.AreNotEqual(SchemaHasher.ComputeContentHash(a), SchemaHasher.ComputeContentHash(b));
        }

        [Test]
        public void ConfigTagChange_ChangesHash()
        {
            FlockSchemaSnapshot a = Snapshot("v1", configs: new[] { Config("c1", "Gameplay", "playerData", Field("integer", "x")) });
            FlockSchemaSnapshot b = Snapshot("v1", configs: new[] { Config("c1", "Gameplay", "gameData", Field("integer", "x")) });
            Assert.AreNotEqual(SchemaHasher.ComputeContentHash(a), SchemaHasher.ComputeContentHash(b));
        }

        [Test]
        public void AddingField_ChangesHash()
        {
            FlockSchemaSnapshot a = Snapshot("v1", new[] { Template("t1", "P", null, Field("integer", "level")) });
            FlockSchemaSnapshot b = Snapshot("v1", new[] { Template("t1", "P", null, Field("integer", "level"), Field("integer", "xp")) });
            Assert.AreNotEqual(SchemaHasher.ComputeContentHash(a), SchemaHasher.ComputeContentHash(b));
        }

        [Test]
        public void NestedObjectSchemaDifference_ChangesHash()
        {
            TypedSchema objA = new TypedSchema { Type = "object", FieldName = "stats", Schema = new List<TypedSchema> { Field("integer", "hp") } };
            TypedSchema objB = new TypedSchema { Type = "object", FieldName = "stats", Schema = new List<TypedSchema> { Field("integer", "mp") } };
            FlockSchemaSnapshot a = Snapshot("v1", new[] { Template("t1", "P", null, objA) });
            FlockSchemaSnapshot b = Snapshot("v1", new[] { Template("t1", "P", null, objB) });
            Assert.AreNotEqual(SchemaHasher.ComputeContentHash(a), SchemaHasher.ComputeContentHash(b));
        }

        [Test]
        public void EmptySnapshot_ProducesStableNonEmptyHash()
        {
            string h1 = SchemaHasher.ComputeContentHash(Snapshot("v1"));
            string h2 = SchemaHasher.ComputeContentHash(Snapshot("v2"));
            Assert.IsNotNull(h1);
            Assert.IsNotEmpty(h1);
            Assert.AreEqual(h1, h2);
        }

        [Test]
        public void DistinctFieldSplit_DoesNotAlias()
        {
            // Length-prefixing guards against two different field-name splits hashing the same.
            FlockSchemaSnapshot a = Snapshot("v1", new[] { Template("t1", "P", null, Field("integer", "ab"), Field("integer", "c")) });
            FlockSchemaSnapshot b = Snapshot("v1", new[] { Template("t1", "P", null, Field("integer", "a"), Field("integer", "bc")) });
            Assert.AreNotEqual(SchemaHasher.ComputeContentHash(a), SchemaHasher.ComputeContentHash(b));
        }
    }
}
