using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using IPA.Utilities;

namespace IPA.Loader
{
    internal static class ModLibraryIndex
    {
        internal static string FilePath => Path.Combine(UnityGame.InstallPath, "IPA", "ModLibraryIndex.json");

        internal static string ManifestPath(string name)
        {
            if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name == "." || name == "..")
                throw new InvalidDataException("Invalid library sidecar name in ModLibraryIndex.json.");
            return Path.Combine(UnityGame.ModLibrariesPath, name);
        }

        internal static JObject Read()
        {
            if (!File.Exists(FilePath)) return new JObject { ["format"] = 1, ["libraries"] = new JArray(), ["sidecars"] = new JArray() };
            var data = JObject.Parse(File.ReadAllText(FilePath));
            if ((int?)data["format"] != 1 || !(data["libraries"] is JArray) || !(data["sidecars"] is JArray))
                throw new InvalidDataException("Unsupported or incomplete ModLibraryIndex.json; library cleanup stopped.");
            foreach (JToken item in (JArray)data["libraries"])
            {
                var entry = item as JObject ?? throw new InvalidDataException("Invalid indexed library entry.");
                ManifestPath((string)entry["name"]);
                if (!(entry["manifest"] is JObject)) throw new InvalidDataException("Missing indexed library manifest.");
            }
            foreach (JToken item in (JArray)data["sidecars"])
            {
                var entry = item as JObject ?? throw new InvalidDataException("Invalid indexed sidecar entry.");
                ManifestPath((string)entry["name"]);
                if (!System.Text.RegularExpressions.Regex.IsMatch((string)entry["sha256"] ?? "", "^[0-9a-fA-F]{64}$") || entry["backup"]?.Type != JTokenType.String)
                    throw new InvalidDataException("Invalid indexed sidecar backup.");
            }
            return data;
        }

        internal static void Write(JObject data)
        {
            if (File.Exists(FilePath) && JToken.DeepEquals(JObject.Parse(File.ReadAllText(FilePath)), data)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temporary = FilePath + ".tidy-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, data.ToString(Formatting.Indented));
                if (!JToken.DeepEquals(JObject.Parse(File.ReadAllText(temporary)), data)) throw new IOException("Library index verification failed.");
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
