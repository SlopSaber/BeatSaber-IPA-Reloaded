using System;
using Newtonsoft.Json.Linq;

#pragma warning disable CS1591 // Preserve the Unity PlayerPrefs call signatures in patched assemblies.
namespace IPA.Data
{
    public static class PortablePlayerPrefs
    {
        private static JToken Get(string key, string kind, Func<JToken> import)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            return SaveDataStore.Transaction("PlayerPrefs.json", data =>
            {
                var entry = data[key] as JObject;
                if (entry == null && data["$deletedAll"] == null && UnityEngine.PlayerPrefs.HasKey(key))
                {
                    entry = new JObject { ["kind"] = kind, ["value"] = import(), ["imported"] = true };
                    data[key] = entry;
                }
                return entry?["kind"]?.Value<string>() == kind ? entry["value"]?.DeepClone() : null;
            });
        }
        private static void Set(string key, string kind, JToken value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            SaveDataStore.Transaction("PlayerPrefs.json", data => { data[key] = new JObject { ["kind"] = kind, ["value"] = value }; return 0; });
        }
        public static int GetInt(string key) => GetInt(key, 0);
        public static int GetInt(string key, int defaultValue) => Get(key, "int", () => new JValue(UnityEngine.PlayerPrefs.GetInt(key, defaultValue)))?.Value<int>() ?? defaultValue;
        public static float GetFloat(string key) => GetFloat(key, 0f);
        public static float GetFloat(string key, float defaultValue) => Get(key, "float", () => new JValue(UnityEngine.PlayerPrefs.GetFloat(key, defaultValue)))?.Value<float>() ?? defaultValue;
        public static string GetString(string key) => GetString(key, "");
        public static string GetString(string key, string defaultValue) => Get(key, "string", () => new JValue(UnityEngine.PlayerPrefs.GetString(key, defaultValue)))?.Value<string>() ?? defaultValue;
        public static void SetInt(string key, int value) => Set(key, "int", new JValue(value));
        public static void SetFloat(string key, float value) => Set(key, "float", new JValue(value));
        public static void SetString(string key, string value) => Set(key, "string", new JValue(value ?? ""));
        public static bool HasKey(string key) => SaveDataStore.Transaction("PlayerPrefs.json", data => data[key] is JObject entry
            ? entry["kind"]?.Value<string>() != "deleted" : data["$deletedAll"] == null && UnityEngine.PlayerPrefs.HasKey(key));
        public static void DeleteKey(string key) => Set(key, "deleted", JValue.CreateNull());
        public static void DeleteAll() => SaveDataStore.Transaction("PlayerPrefs.json", data => { data.RemoveAll(); data["$deletedAll"] = true; return 0; });
        public static void Save() { /* Set/Delete operations already commit atomically under the shared lock. */ }
    }
}
