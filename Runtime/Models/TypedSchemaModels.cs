using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Flock.Models
{
    public class TypedSchema
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("field_name")]
        public string FieldName { get; set; }

        [JsonProperty("type_name")]
        public string TypeName { get; set; }

        [JsonProperty("schema")]
        [JsonConverter(typeof(TypedSchemaChildConverter))]
        public object Schema { get; set; }

        public TypedSchema SchemaAsSingle() => Schema as TypedSchema;
        public List<TypedSchema> SchemaAsList() => Schema as List<TypedSchema>;
    }

    public class DataField
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("field_name")]
        public string FieldName { get; set; }

        [JsonProperty("type_name")]
        public string TypeName { get; set; }

        [JsonProperty("value")]
        [JsonConverter(typeof(DataFieldValueConverter))]
        public object Value { get; set; }

        public List<DataField> ValueAsList() => Value as List<DataField>;
        public Dictionary<string, DataField> ValueAsDict() => Value as Dictionary<string, DataField>;

        /// <summary>
        /// Returns <see cref="Value"/> converted to <typeparamref name="T"/>. JSON numbers
        /// deserialize boxed as <c>long</c> (integers) or <c>double</c> (decimals), so a
        /// direct cast like <c>(int)Value</c> throws <see cref="InvalidCastException"/> —
        /// use this instead. Returns <c>default</c> when <see cref="Value"/> is null;
        /// throws when no conversion exists (e.g. <c>GetValue&lt;int&gt;()</c> on text).
        /// </summary>
        public T GetValue<T>()
        {
            object raw = Value;

            if (raw == null)
                return default;

            if (raw is T typed)
                return typed;

            if (raw is JToken token)
                return token.ToObject<T>();

            Type target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T)Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
        }
    }

    public static class DataFieldExtensions
    {
        public static JObject ToFlatObject(this IList<DataField> fields)
        {
            JObject obj = new JObject();
            if (fields == null) return obj;
            foreach (DataField f in fields)
            {
                if (f == null || string.IsNullOrEmpty(f.FieldName)) continue;
                obj[f.FieldName] = ToJToken(f);
            }
            return obj;
        }

        private static JToken ToJToken(DataField field)
        {
            if (field == null || field.Value == null) return JValue.CreateNull();
            string type = (field.Type ?? "").Trim().ToLowerInvariant();

            if (type == "object" && field.Value is IList<DataField> objFields)
                return ToFlatObject(objFields);

            if ((type == "list" || type == "array") && field.Value is IList<DataField> listItems)
            {
                JArray arr = new JArray();
                foreach (DataField item in listItems) arr.Add(ToJToken(item));
                return arr;
            }

            if (type == "dict" && field.Value is IDictionary<string, DataField> dict)
            {
                JObject dictObj = new JObject();
                foreach (KeyValuePair<string, DataField> kv in dict) dictObj[kv.Key] = ToJToken(kv.Value);
                return dictObj;
            }

            return JToken.FromObject(field.Value);
        }
    }

    public static class TypedSchemaExtensions
    {
        public static List<DataField> ToDataFieldList(this IReadOnlyList<TypedSchema> schema, object poco)
        {
            if (schema == null || poco == null) return new List<DataField>();
            return BuildFields(JObject.FromObject(poco), schema);
        }

        private static List<DataField> BuildFields(JObject obj, IReadOnlyList<TypedSchema> schema)
        {
            List<DataField> result = new List<DataField>();
            foreach (TypedSchema field in schema)
            {
                if (field == null || string.IsNullOrEmpty(field.FieldName)) continue;
                JToken token = obj[field.FieldName];
                result.Add(new DataField
                {
                    Type = field.Type,
                    FieldName = field.FieldName,
                    TypeName = field.TypeName,
                    Value = ConvertJToken(token, field)
                });
            }
            return result;
        }

        private static object ConvertJToken(JToken token, TypedSchema schema)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            string type = (schema.Type ?? "").Trim().ToLowerInvariant();

            if (type == "object")
            {
                List<TypedSchema> children = schema.SchemaAsList();
                if (children == null || !(token is JObject jo)) return null;
                return BuildFields(jo, children);
            }

            if (type == "list" || type == "array")
            {
                TypedSchema element = schema.SchemaAsSingle();
                if (element == null || !(token is JArray arr)) return null;
                List<DataField> list = new List<DataField>();
                foreach (JToken el in arr)
                {
                    list.Add(new DataField
                    {
                        Type = element.Type,
                        FieldName = element.FieldName ?? "",
                        TypeName = element.TypeName ?? "",
                        Value = ConvertJToken(el, element)
                    });
                }
                return list;
            }

            if (type == "dict")
            {
                TypedSchema valueSchema = schema.SchemaAsSingle();
                if (valueSchema == null || !(token is JObject dictObj)) return null;
                Dictionary<string, DataField> dict = new Dictionary<string, DataField>();
                foreach (JProperty prop in dictObj.Properties())
                {
                    dict[prop.Name] = new DataField
                    {
                        Type = valueSchema.Type,
                        FieldName = valueSchema.FieldName ?? "",
                        TypeName = valueSchema.TypeName ?? "",
                        Value = ConvertJToken(prop.Value, valueSchema)
                    };
                }
                return dict;
            }

            return ((JValue)token).Value;
        }
    }

    internal class TypedSchemaChildConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(object);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            switch (token.Type)
            {
                case JTokenType.Null:   return null;
                case JTokenType.Array:  return token.ToObject<List<TypedSchema>>(serializer);
                case JTokenType.Object: return token.ToObject<TypedSchema>(serializer);
                default:                return null;
            }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            => serializer.Serialize(writer, value);
    }

    internal class DataFieldValueConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(object);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            switch (token.Type)
            {
                case JTokenType.Null:   return null;
                case JTokenType.Array:  return token.ToObject<List<DataField>>(serializer);
                case JTokenType.Object: return token.ToObject<Dictionary<string, DataField>>(serializer);
                default:                return ((JValue)token).Value;
            }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            => serializer.Serialize(writer, value);
    }
}
