using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IPA.Data
{
    /// <summary>Stores custom map labels independently of the game's difficulty enum.</summary>
    public static class CustomDifficultyLabels
    {
        private static readonly object PendingLock = new();
        private static JObject pending = new();
        private static readonly Timer FlushTimer = new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        internal static void Flush()
        {
            lock (PendingLock)
            {
                if (!pending.HasValues) return;
                try
                {
                    SaveDataStore.Transaction("CustomDifficultyLabels.json", data =>
                    {
                        var levels = data["levels"] as JObject ?? new JObject();
                        levels.Merge(pending, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Union });
                        data["levels"] = levels; return 0;
                    });
                    pending = new JObject();
                }
                catch (Exception error) { IPA.Logging.Logger.Default.Warn($"Custom difficulty label flush deferred: {error.Message}"); }
            }
        }

        /// <summary>Saves a label using a level ID, characteristic and arbitrary difficulty identifier.</summary>
        public static void Set(string levelId, string characteristic, string difficulty, string label)
        {
            if (string.IsNullOrEmpty(levelId)) throw new ArgumentException("A level ID is required.", nameof(levelId));
            SaveDataStore.Transaction("CustomDifficultyLabels.json", data =>
            {
                var levels = data["levels"] as JObject ?? new JObject();
                var level = levels[levelId] as JObject ?? new JObject();
                var mode = level[characteristic ?? "Standard"] as JObject ?? new JObject();
                mode[difficulty ?? ""] = label ?? ""; level[characteristic ?? "Standard"] = mode;
                levels[levelId] = level; data["levels"] = levels; return 0;
            });
        }

        /// <summary>Reads a label without asking the game to parse an unsupported enum.</summary>
        public static string Get(string levelId, string characteristic, string difficulty)
            => SaveDataStore.Transaction("CustomDifficultyLabels.json", data => (string)data["levels"]?[levelId]?[characteristic ?? "Standard"]?[difficulty ?? ""]);

        internal static string ObserveMap(string path, string text)
        {
            string name = Path.GetFileName(path);
            if (!name.Equals("Info.dat", StringComparison.OrdinalIgnoreCase) && !name.Equals("Info.json", StringComparison.OrdinalIgnoreCase)) return text;
            JObject info;
            try { info = JObject.Parse(text); } catch (JsonReaderException) { return text; }
            var sets = info["_difficultyBeatmapSets"] as JArray ?? info["difficultyBeatmapSets"] as JArray;
            if (sets == null && info["difficultyBeatmaps"] is JArray flat)
                sets = new JArray(flat.OfType<JObject>().GroupBy(m => (string)(m["characteristic"] ?? m["beatmapCharacteristicName"]) ?? "Standard")
                    .Select(group => new JObject { ["beatmapCharacteristicName"] = group.Key, ["difficultyBeatmaps"] = new JArray(group) }));
            if (sets == null) return text;
            // Metadata identity is independent of installation paths/symlink spelling. It is also
            // retained separately from any game-specific level ID or score representation.
            using var hash = SHA256.Create();
            var fingerprint = (JObject)info.DeepClone();
            StripLabels(fingerprint);
            string identity = "metadata-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(fingerprint.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
            foreach (JObject set in sets.OfType<JObject>())
            {
                string characteristic = (string)(set["_beatmapCharacteristicName"] ?? set["beatmapCharacteristicName"]) ?? "Standard";
                var maps = set["_difficultyBeatmaps"] as JArray ?? set["difficultyBeatmaps"] as JArray;
                if (maps == null) continue;
                foreach (JObject map in maps.OfType<JObject>())
                {
                    string label = (string)(map["_customData"]?["_difficultyLabel"] ?? map["customData"]?["difficultyLabel"]);
                    if (label == null) continue;
                    string difficulty = (string)(map["_difficulty"] ?? map["difficulty"]);
                    lock (PendingLock)
                    {
                        var level = pending[identity] as JObject ?? new JObject();
                        var mode = level[characteristic] as JObject ?? new JObject();
                        mode[difficulty ?? ""] = label; level[characteristic] = mode; pending[identity] = level;
                        FlushTimer.Change(250, Timeout.Infinite);
                    }
                }
            }
            return text;
        }

        private static void StripLabels(JToken token)
        {
            if (token is JObject obj)
                foreach (var property in obj.Properties().ToArray())
                {
                    if (property.Name == "_difficultyLabel" || property.Name == "difficultyLabel") property.Remove();
                    else
                    {
                        StripLabels(property.Value);
                        if ((property.Name == "_customData" || property.Name == "customData") && property.Value is JObject empty && !empty.HasValues) property.Remove();
                    }
                }
            else if (token is JArray array) foreach (var value in array) StripLabels(value);
        }
    }
}
