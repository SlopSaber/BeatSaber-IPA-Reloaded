using IPA.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace IPA.JsonConverters
{
    internal class FeaturesFieldConverter : JsonConverter<Dictionary<string, List<JObject>>>
    {
        internal sealed class LegacyFeatureDictionary : Dictionary<string, List<JObject>> { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Assert([DoesNotReturnIf(false)] bool condition)
        {
            if (!condition)
                throw new InvalidOperationException();
        }

        public override Dictionary<string, List<JObject>> ReadJson(JsonReader reader, Type objectType, Dictionary<string, List<JObject>> existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.StartArray)
            {
                var result = new LegacyFeatureDictionary();
                foreach (var declaration in serializer.Deserialize<string[]>(reader) ?? Array.Empty<string>())
                {
                    var (name, arguments) = ParseLegacy(declaration);
                    var data = new JObject { ["arguments"] = new JArray(arguments) };
                    if (name == "define-feature" && arguments.Length == 2)
                    {
                        name = "IPA.DefineFeature";
                        data = new JObject { ["name"] = arguments[0], ["type"] = arguments[1] };
                    }
                    else if (name == "config-provider" && arguments.Length == 1)
                    {
                        name = "IPA.ConfigProvider";
                        data = new JObject { ["type"] = arguments[0] };
                    }
                    else if (name == "print") name = "IPA.Legacy.Print";
                    else if (name == "debug") name = "IPA.Legacy.Debug";
                    else if (name == "warn") name = "IPA.Legacy.Warn";
                    else if (name == "no-update") name = "IPA.Legacy.NoUpdate";
                    else if (name == "init-injector") name = "IPA.Legacy.InitInjector";
                    if (!result.TryGetValue(name, out var values)) result.Add(name, values = new List<JObject>());
                    values.Add(data);
                }
                return result;
            }

            var dict = new Dictionary<string, List<JObject>>();
            Assert(reader.TokenType == JsonToken.StartObject && reader.Read());

            while (reader.TokenType == JsonToken.PropertyName)
            {
                var name = (string)reader.Value;
                Assert(reader.Read());

                var list = reader.TokenType == JsonToken.StartObject
                    ? (new() { serializer.Deserialize<JObject>(reader) })
                    : serializer.Deserialize<List<JObject>>(reader);

                dict.Add(name, list);
                Assert(reader.Read());
            }

            return dict;
        }

        public override void WriteJson(JsonWriter writer, Dictionary<string, List<JObject>> value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, value);
        }

        private static (string Name, string[] Arguments) ParseLegacy(string declaration)
        {
            var text = new StringBuilder();
            var arguments = new List<string>();
            string name = null;
            bool escaped = false;
            int depth = 0;
            foreach (char character in declaration)
            {
                if (escaped) { text.Append(character); escaped = false; continue; }
                if (character == '\\') { escaped = true; continue; }
                if (character == '(' && depth++ == 0) { name = text.ToString().Trim(); text.Clear(); continue; }
                if (character == ')' && --depth == 0) { arguments.Add(text.ToString().Trim()); text.Clear(); continue; }
                if (character == ',' && depth == 1) { arguments.Add(text.ToString().Trim()); text.Clear(); continue; }
                text.Append(character);
            }
            if (depth != 0 || escaped) throw new JsonSerializationException($"Malformed legacy feature '{declaration}'.");
            return (name ?? text.ToString().Trim(), arguments.ToArray());
        }
    }
}
