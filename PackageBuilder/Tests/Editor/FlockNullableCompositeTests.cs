using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Flock.Editor.Codegen;
using Flock.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    /// <summary>A nullable list, dict or object ("list?") is still a list, dict or object: generated, read and saved like one.</summary>
    public class FlockNullableCompositeTests
    {
        public class PetSave
        {
            [JsonProperty("name")] public string Name { get; set; }
        }

        public class HeroSave
        {
            [JsonProperty("scores")] public List<int> Scores { get; set; }
            [JsonProperty("stats")] public Dictionary<string, string> Stats { get; set; }
            [JsonProperty("pet")] public PetSave Pet { get; set; }
            [JsonProperty("title")] public string Title { get; set; }
        }

        // The backend writes the marker itself (a legacy "list<integer>?" becomes "list?"); letter case and spaces as a dashboard might send them.
        private static List<TypedSchema> NullableSchema() => new List<TypedSchema>
        {
            new TypedSchema { Type = "list?", FieldName = "scores", TypeName = "hero", Schema = new TypedSchema { Type = "integer", FieldName = "item", TypeName = "scores" } },
            new TypedSchema { Type = " Dict? ", FieldName = "stats", TypeName = "hero", Schema = new TypedSchema { Type = "string", FieldName = "value", TypeName = "stats" } },
            new TypedSchema { Type = "OBJECT?", FieldName = "pet", TypeName = "hero", Schema = new List<TypedSchema> { new TypedSchema { Type = "string", FieldName = "name", TypeName = "pet" } } },
            new TypedSchema { Type = "string?", FieldName = "title", TypeName = "hero" },
        };

        [Test]
        public void NullableListDictAndObjectFieldsAreGeneratedWithTheirTypes()
        {
            StringBuilder body = new StringBuilder();
            List<string> nested = new List<string>();
            int emitted = SchemaPropertyEmitter.EmitProperties(NullableSchema(), "Hero", "{ get; set; }", body, nested,
                new HashSet<string>(), null, "test");

            string source = body.ToString();
            Assert.AreEqual(4, emitted, source);
            StringAssert.DoesNotContain("Skipped", source);
            StringAssert.Contains("public List<int> Scores { get; set; }", source);
            StringAssert.Contains("public Dictionary<string, string> Stats { get; set; }", source);
            StringAssert.Contains("public HeroPet Pet { get; set; }", source);
            Assert.IsTrue(nested.Any(c => c.Contains("public partial class HeroPet") && c.Contains("public string Name { get; set; }")), string.Join("\n", nested));
        }

        [Test]
        public void ATypedSaveSendsTheValuesOfNullableListDictAndObjectFields()
        {
            HeroSave save = new HeroSave
            {
                Scores = new List<int> { 3, 5 },
                Stats = new Dictionary<string, string> { { "hp", "10" } },
                Pet = new PetSave { Name = "Rex" },
                Title = "Sir"
            };

            JObject sent = NullableSchema().ToDataFieldList(save).ToFlatObject();

            Assert.AreEqual(JToken.Parse("[3,5]").ToString(), sent["scores"].ToString(), sent.ToString());
            Assert.AreEqual("10", (string)sent["stats"]["hp"], sent.ToString());
            Assert.AreEqual("Rex", (string)sent["pet"]["name"], sent.ToString());
            Assert.AreEqual("Sir", (string)sent["title"]);
        }

        [Test]
        public void ANullableFieldLeftEmptyIsSentAsNull()
        {
            JObject sent = NullableSchema().ToDataFieldList(new HeroSave()).ToFlatObject();

            foreach (string field in new[] { "scores", "stats", "pet", "title" })
                Assert.AreEqual(JTokenType.Null, sent[field].Type, field);
        }

        [Test]
        public void NullableListDictAndObjectReadFromTheServerComeBackAsPlainValues()
        {
            // A row as the server sends it: each value wrapped in its own field description.
            string wire = "[" +
                "{\"type\":\"list?\",\"field_name\":\"scores\",\"type_name\":\"hero\",\"value\":[" +
                    "{\"type\":\"integer\",\"field_name\":\"item\",\"type_name\":\"scores\",\"value\":3}," +
                    "{\"type\":\"integer\",\"field_name\":\"item\",\"type_name\":\"scores\",\"value\":5}]}," +
                "{\"type\":\"dict?\",\"field_name\":\"stats\",\"type_name\":\"hero\",\"value\":{" +
                    "\"hp\":{\"type\":\"string\",\"field_name\":\"value\",\"type_name\":\"stats\",\"value\":\"10\"}}}," +
                "{\"type\":\"object?\",\"field_name\":\"pet\",\"type_name\":\"hero\",\"value\":[" +
                    "{\"type\":\"string\",\"field_name\":\"name\",\"type_name\":\"pet\",\"value\":\"Rex\"}]}," +
                "{\"type\":\"string?\",\"field_name\":\"title\",\"type_name\":\"hero\",\"value\":null}]";
            List<DataField> row = JsonConvert.DeserializeObject<List<DataField>>(wire);

            HeroSave read = row.ToFlatObject().ToObject<HeroSave>();

            CollectionAssert.AreEqual(new[] { 3, 5 }, read.Scores);
            Assert.AreEqual("10", read.Stats["hp"]);
            Assert.AreEqual("Rex", read.Pet.Name);
            Assert.IsNull(read.Title);
        }

        [Test]
        public void TheAchievementListIsFoundWhenItIsNullable()
        {
            string folder = Path.Combine(Path.GetTempPath(), "flock_nullable_" + Guid.NewGuid().ToString("N"));
            try
            {
                GameConfigSchema config = new GameConfigSchema
                {
                    Id = "cfg-1",
                    Name = "Achievements",
                    Tag = "achievement",
                    Schema = new List<TypedSchema>
                    {
                        new TypedSchema
                        {
                            Type = "list?", FieldName = "achievements", TypeName = "achievements",
                            Schema = new TypedSchema
                            {
                                Type = "object", FieldName = "item", TypeName = "achievement",
                                Schema = new List<TypedSchema> { new TypedSchema { Type = "string", FieldName = "name", TypeName = "achievement" } }
                            }
                        }
                    }
                };

                GameConfigEmitter.Emit(new List<GameConfigSchema> { config }, folder, true);

                Assert.IsTrue(File.Exists(Path.Combine(folder, "FlockAchievementDetails.g.cs")), "The achievement lookup is generated");
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        [Test]
        public void TheMarkerIsReadOffWhateverTheSpacingAndCase()
        {
            Assert.AreEqual("list", TypedSchema.BaseTypeOf(" List? "));
            Assert.AreEqual("integer", TypedSchema.BaseTypeOf("integer ?"));
            Assert.AreEqual("", TypedSchema.BaseTypeOf(null));
            Assert.IsTrue(TypedSchema.IsNullableType(" dict? "));
            Assert.IsFalse(TypedSchema.IsNullableType("dict"));
        }
    }
}
