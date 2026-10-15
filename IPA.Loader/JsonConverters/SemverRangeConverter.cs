#nullable enable
using System;
using System.Runtime.Remoting.Messaging;
using Hive.Versioning;
using Newtonsoft.Json;

namespace IPA.JsonConverters
{
    internal class SemverRangeConverter : JsonConverter<VersionRange?>
    {
        public override VersionRange? ReadJson(JsonReader reader, Type objectType, VersionRange? existingValue, bool hasExistingValue, JsonSerializer serializer)
            => reader.Value is not string s ? existingValue : SemVer.LegacyVersionParser.ParseRange(s, true);

        public override void WriteJson(JsonWriter writer, VersionRange? value, JsonSerializer serializer)
        {
            if (value is null) writer.WriteNull();
            else writer.WriteValue(value.ToString());
        }
    }
}
