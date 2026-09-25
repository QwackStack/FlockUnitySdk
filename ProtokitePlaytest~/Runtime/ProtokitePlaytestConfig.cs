using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Protokite.Playtest
{
    /// <summary>The feature switches a playtest's config can carry. The server may add more, so compare against these names.</summary>
    public static class ProtokitePlaytestFeatures
    {
        public const string VideoRecording = "video_recording";
        public const string ExceptionCapturing = "exception_capturing";
        public const string HeavyAnalytics = "heavy_analytics";
    }

    /// <summary>The kinds of question a feedback form can ask. A server newer than this package may send a kind not listed here.</summary>
    public static class ProtokitePlaytestFormFieldTypes
    {
        public const string Text = "text";
        public const string TextArea = "textarea";
        public const string Rating = "rating";
        public const string Select = "select";
        public const string Checkbox = "checkbox";
    }

    /// <summary>One question on a playtest feedback form.</summary>
    public sealed class ProtokitePlaytestFormField
    {
        /// <summary>The key the answer is sent under.</summary>
        public string Id { get; internal set; }

        /// <summary>One of <see cref="ProtokitePlaytestFormFieldTypes"/>, or a kind this package does not know, kept as sent.</summary>
        public string Type { get; internal set; }

        public string Label { get; internal set; }

        /// <summary>Whether an answer is needed. True when the server does not say, as the server itself assumes.</summary>
        public bool Required { get; internal set; } = true;

        /// <summary>Extra guidance shown with the question; empty when there is none.</summary>
        public string HelpText { get; internal set; } = "";

        /// <summary>The choices for a select question; empty for other kinds.</summary>
        public IReadOnlyList<string> Options { get; internal set; } = Array.Empty<string>();
    }

    /// <summary>A playtest's published feedback form.</summary>
    public sealed class ProtokitePlaytestForm
    {
        public string Id { get; internal set; }
        public string TestId { get; internal set; }
        public string GameId { get; internal set; }
        public string Title { get; internal set; }

        /// <summary>Shown under the title; empty when there is none.</summary>
        public string Description { get; internal set; } = "";

        public bool IsPublished { get; internal set; } = true;

        /// <summary>The questions, in the order the studio arranged them.</summary>
        public IReadOnlyList<ProtokitePlaytestFormField> Fields { get; internal set; } = Array.Empty<ProtokitePlaytestFormField>();

        /// <summary>When the form was created, in ISO 8601.</summary>
        public string CreatedAt { get; internal set; }

        /// <summary>When the form was last changed, in ISO 8601.</summary>
        public string UpdatedAt { get; internal set; }
    }

    /// <summary>Everything a playtest build needs from Protokite before a session starts.</summary>
    public sealed class ProtokitePlaytestConfig
    {
        private static readonly IReadOnlyDictionary<string, bool> NoFeatures = new Dictionary<string, bool>();

        /// <summary>The Protokite playtest this build is linked to. Never empty.</summary>
        public string TestId { get; internal set; }

        /// <summary>The event name the server records by itself when a session starts; the SDK never sends it.</summary>
        public string SessionStartedEvent { get; internal set; }

        /// <summary>The Flock Game Version ID the playtest is linked to; empty when the server does not say.</summary>
        public string FlockGameVersionId { get; internal set; } = "";

        /// <summary>The feature switches exactly as the server sent them. Read them through <see cref="IsFeatureEnabled"/>.</summary>
        public IReadOnlyDictionary<string, bool> Features { get; internal set; } = NoFeatures;

        /// <summary>The published feedback form, or null when the playtest has none, which means show no form at all.</summary>
        public ProtokitePlaytestForm Form { get; internal set; }

        /// <summary>True only when the server sent the feature as true. A feature the config does not mention is off.</summary>
        public bool IsFeatureEnabled(string featureName)
        {
            return featureName != null && Features.TryGetValue(featureName, out bool enabled) && enabled;
        }

        /// <summary>Reads the config from the server's JSON; null when it has no test_id, since nothing works without one.</summary>
        internal static ProtokitePlaytestConfig FromJson(JObject json)
        {
            string testId = ReadString(json, "test_id");
            if (string.IsNullOrEmpty(testId))
                return null;

            return new ProtokitePlaytestConfig
            {
                TestId = testId,
                SessionStartedEvent = ReadString(json, "session_started_event"),
                FlockGameVersionId = ReadString(json, "flock_game_version_id"),
                Features = ReadFeatures(json["features"] as JObject),
                Form = ReadForm(json["form"] as JObject)
            };
        }

        // Only a JSON boolean counts: text such as "true" or a number switches nothing on.
        private static IReadOnlyDictionary<string, bool> ReadFeatures(JObject json)
        {
            Dictionary<string, bool> features = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (json == null)
                return features;
            foreach (JProperty property in json.Properties())
            {
                if (property.Value.Type == JTokenType.Boolean)
                    features[property.Name] = (bool)property.Value;
            }
            return features;
        }

        // A null form means none is published, and a form without an id cannot take answers, so it counts as none too.
        private static ProtokitePlaytestForm ReadForm(JObject json)
        {
            string id = json == null ? "" : ReadString(json, "id");
            if (string.IsNullOrEmpty(id))
                return null;

            return new ProtokitePlaytestForm
            {
                Id = id,
                TestId = ReadString(json, "test_id"),
                GameId = ReadString(json, "game_id"),
                Title = ReadString(json, "title"),
                Description = ReadString(json, "description"),
                IsPublished = ReadBool(json, "is_published", true),
                Fields = ReadFields(json["fields"] as JArray),
                CreatedAt = ReadString(json, "created_at"),
                UpdatedAt = ReadString(json, "updated_at")
            };
        }

        private static IReadOnlyList<ProtokitePlaytestFormField> ReadFields(JArray json)
        {
            List<ProtokitePlaytestFormField> fields = new List<ProtokitePlaytestFormField>();
            if (json == null)
                return fields;
            foreach (JToken token in json)
            {
                if (!(token is JObject field))
                    continue;
                // Answers are sent by id, so a question without one could never be answered.
                string id = ReadString(field, "id");
                if (string.IsNullOrEmpty(id))
                    continue;
                fields.Add(new ProtokitePlaytestFormField
                {
                    Id = id,
                    Type = ReadString(field, "type"),
                    Label = ReadString(field, "label"),
                    Required = ReadBool(field, "required", true),
                    HelpText = ReadString(field, "help_text"),
                    Options = ReadStrings(field["options"] as JArray)
                });
            }
            return fields;
        }

        private static IReadOnlyList<string> ReadStrings(JArray json)
        {
            List<string> strings = new List<string>();
            if (json == null)
                return strings;
            foreach (JToken token in json)
            {
                if (token.Type == JTokenType.String)
                    strings.Add((string)token);
            }
            return strings;
        }

        // A date arrives already read as a DateTime by the HTTP client, and is written back out in round-trip form.
        private static string ReadString(JObject json, string name)
        {
            JToken token = json[name];
            if (token == null || token.Type == JTokenType.Null)
                return "";
            if (token.Type == JTokenType.String)
                return (string)token;
            if (token.Type == JTokenType.Date && token is JValue value && value.Value is DateTime date)
                return date.ToString("o");
            return "";
        }

        private static bool ReadBool(JObject json, string name, bool fallback)
        {
            JToken token = json[name];
            return token != null && token.Type == JTokenType.Boolean ? (bool)token : fallback;
        }
    }
}
