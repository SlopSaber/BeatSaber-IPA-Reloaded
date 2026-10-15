using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Logger = IPA.Logging.Logger;

#pragma warning disable CS1591 // Public bridge entry points are referenced by rewritten assemblies.
namespace IPA.Data
{
    public static class SaveDataStore
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, JObject> Baselines = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, JToken> ScoreBaselines = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Characteristics = new(StringComparer.Ordinal) { "Standard", "OneSaber", "NoArrows", "90Degree", "360Degree" };
        private static SaveDataProfile profile;
        private static string root;
        private static string native;
        private static FileStream lease;
        private static readonly string[] KnownFiles = { "PlayerData.dat", "GameDynamicData.dat", "settings.cfg", "settings.ini", "MainSettings.json", "GraphicsSettings.json", "ControllerProfiles.dat", "AvatarData.dat", "LocalLeaderboards.dat", "LocalDailyLeaderboards.dat" };

        public static string GetPersistentDataPath() => native ?? throw new InvalidOperationException("BSIPA save routing was not initialized; refusing to use global AppData.");
        public static string ObserveCharacteristic(string name) { if (name != null) lock (Gate) Characteristics.Add(name); return name; }
        internal static bool SupportsCharacteristic(string name) { lock (Gate) return Characteristics.Contains(name); }

        internal static void Initialize(string userData, string original, SaveDataProfile current)
        {
            lock (Gate)
            {
                if (profile != null) return;
                root = Path.Combine(userData, "Hyperbolic Magnetism", "Beat Saber");
                Directory.CreateDirectory(Path.Combine(root, "Shared"));
                Directory.CreateDirectory(Path.Combine(root, "Backups"));
                string versionRoot = Path.Combine(root, "Versions", current.Version);
                native = Path.Combine(versionRoot, "Native");
                Directory.CreateDirectory(native);
                try { lease = new FileStream(Path.Combine(native, ".bsipa-owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    // Two installs of the same version must not write the same native files.
                    string session = Path.Combine(versionRoot, "Sessions", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(session);
                    foreach (string file in Directory.GetFiles(native))
                        if (!Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal)) File.Copy(file, Path.Combine(session, Path.GetFileName(file)));
                    native = session;
                    lease = new FileStream(Path.Combine(native, ".bsipa-owner"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                }
                profile = current;
                Transaction("EraIndex.json", index => { var era = index[current.Era] as JObject ?? new JObject(); era[current.Version] = true; index[current.Era] = era; return 0; });
                // Import copies once. Content hashes retain every distinct source/backup without touching it.
                if (Directory.Exists(original))
                {
                    foreach (string file in Directory.GetFiles(original).OrderByDescending(File.GetLastWriteTimeUtc))
                    {
                        string name = Path.GetFileName(file);
                        if (!KnownFiles.Any(k => name.Equals(k, StringComparison.OrdinalIgnoreCase) || name.StartsWith(k + ".", StringComparison.OrdinalIgnoreCase))) continue;
                        byte[] bytes = File.ReadAllBytes(file);
                        Archive(name, bytes);
                        if (KnownFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) ImportSettings(name, Encoding.UTF8.GetString(bytes));
                    }
                }
                foreach (string name in KnownFiles)
                {
                    string path = Path.Combine(native, name), source = Path.Combine(original, name);
                    bool supported = current.Files.ContainsKey(name) || (name == "settings.ini" && current.Ini)
                        || name == "LocalLeaderboards.dat" || name == "LocalDailyLeaderboards.dat" || name == "ControllerProfiles.dat" || name == "AvatarData.dat";
                    if (!supported) continue;
                    Logger.Default.Debug($"Reconciling native save {name}.");
                    string eraPath = EraPath(name);
                    if (File.Exists(path)) RecordWrite(path); // Reconcile a save left by a failed master-store write.
                    else if (File.Exists(eraPath)) File.Copy(eraPath, path);
                    else if (File.Exists(source))
                    {
                        string text = File.ReadAllText(source);
                        if (current.Files.TryGetValue(name, out var shape) && Parse(text) is JObject obj)
                        {
                            string expected = (shape.Fields.TryGetValue("version", out var v) ? v : shape.Fields.TryGetValue("_version", out v) ? v : null)?.DefaultString;
                            string supplied = (string)(obj["version"] ?? obj["_version"]);
                            if (!string.IsNullOrEmpty(expected) && expected != supplied) ClearProgress(obj);
                            text = obj.ToString(Formatting.None);
                        }
                        else if (name.EndsWith("Leaderboards.dat", StringComparison.OrdinalIgnoreCase)) continue; // Global scores have no known scoring era.
                        File.WriteAllText(path, text, new UTF8Encoding(false));
                    }
                    else if (name == "settings.ini") File.WriteAllText(path, "# BeatSaber settings\n");
                    else if (current.Files.TryGetValue(name, out var configShape) && name == "settings.cfg")
                        File.WriteAllText(path, configShape.Project(new JObject(), (_, __) => { }).ToString(Formatting.None));
                    else if (current.Files.ContainsKey(name))
                    {
                        // Shared UserData can arrive on a PC without the original global saves.
                        string scores = Path.Combine(root, "Shared", "Scores");
                        using var owned = Acquire(Path.Combine(root, "Shared", ".write-lock"));
                        string template = Directory.Exists(scores) ? Directory.EnumerateFiles(scores, name + ".json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
                        if (template != null && Parse(File.ReadAllText(template)) is JObject prior)
                        {
                            ClearProgress(prior);
                            File.WriteAllText(path, prior.ToString(Formatting.None));
                        }
                    }
                    if (File.Exists(path))
                    {
                        RecordWrite(path);
                        File.WriteAllText(path, PrepareRead(path, File.ReadAllText(path)), new UTF8Encoding(false));
                    }
                }
                AppDomain.CurrentDomain.ProcessExit += (_, __) => { CustomDifficultyLabels.Flush(); lease?.Dispose(); };
                Logger.Default.Info($"Game saves redirected to UserData/Hyperbolic Magnetism/Beat Saber/Versions/{current.Version}; scoring era {current.Era}. Original AppData retained.");
            }
        }

        internal static T Transaction<T>(string relative, Func<JObject, T> operation)
        {
            if (root == null) throw new InvalidOperationException("The BSIPA shared data store is not initialized.");
            string shared = Path.Combine(root, "Shared");
            string path = Path.GetFullPath(Path.Combine(shared, relative));
            if (!path.StartsWith(Path.GetFullPath(shared) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid shared data path.", nameof(relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var owned = Acquire(Path.Combine(shared, ".write-lock"));
            JObject data = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            string before = data.ToString(Formatting.None);
            T result = operation(data);
            if (before != data.ToString(Formatting.None)) Atomic(path, data.ToString(Formatting.Indented));
            return result;
        }

        private static FileStream Acquire(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (attempt < 100) { Thread.Sleep(20); }
            }
        }

        private static void Atomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(text); writer.Flush(); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static bool IsNative(string path) => native != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(native) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static string EraPath(string name) => Path.Combine(root, "Shared", "Scores", profile.Era, name + ".json");
        private static JToken Parse(string text)
        {
            try { return JToken.Parse(text.TrimStart('\uFEFF')); }
            catch (JsonReaderException) { return null; }
        }

        private static void ImportSettings(string name, string text)
        {
            JToken data = name == "settings.ini" ? SharedSettings.ParseIni(text) : Parse(text);
            if (data == null) return;
            JObject values = SharedSettings.Extract(name, data, profile);
            Transaction("Settings.json", state => { foreach (var p in values.Properties()) if (state[p.Name] == null) state[p.Name] = p.Value.DeepClone(); return 0; });
        }

        internal static string PrepareRead(string path, string text)
        {
            if (!IsNative(path)) return CustomDifficultyLabels.ObserveMap(path, text);
            string name = Path.GetFileName(path);
            if (!KnownFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) return text;
            lock (Gate)
            {
                JToken raw = name == "settings.ini" ? SharedSettings.ParseIni(text) : Parse(text);
                if (raw == null) return text;
                if (name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                    raw = Transaction(Path.Combine("Scores", profile.Era, name + ".json"), saved => Merge(saved, raw, true));
                JToken result = Transaction("Settings.json", settings => SharedSettings.Apply(name, raw, settings, profile));
                var preserved = new JObject();
                if (name == "settings.ini" && profile.IniFields.Count > 0 && result is JObject ini)
                    foreach (var property in ini.Properties().ToArray())
                    {
                        bool supported = profile.IniFields.TryGetValue(property.Name, out var member);
                        JToken value = property.Value;
                        bool valid = supported && (member.Kind == "bool" ? value.Type == JTokenType.Boolean
                            : member.Kind == "integer" ? value.Type == JTokenType.Integer
                            : member.Kind == "number" ? value.Type == JTokenType.Integer || value.Type == JTokenType.Float
                            : member.Kind == "string" ? value.Type == JTokenType.String
                            : member.Kind == "enum" ? value.Type == JTokenType.String && member.EnumValues.ContainsKey((string)value)
                                || value.Type == JTokenType.Integer && member.EnumValues.ContainsValue((long)value)
                            : true);
                        if (!valid) { preserved["/ini/" + property.Name] = value.DeepClone(); property.Remove(); }
                        else if (member.Kind == "enum" && value.Type == JTokenType.Integer)
                            property.Value = member.EnumValues.First(e => e.Value == (long)value).Key;
                    }
                if (profile.Files.TryGetValue(name, out var shape))
                    result = shape.Project(result, (key, value) => preserved[key] = value.DeepClone());
                Preserve(name, preserved);
                Baselines[name] = SharedSettings.Extract(name, result, profile);
                ScoreBaselines[name] = result.DeepClone();
                return name == "settings.ini" ? SharedSettings.WriteIni((JObject)result) : result.ToString(Formatting.None);
            }
        }

        internal static void RecordWrite(string path)
        {
            if (!IsNative(path)) return;
            string name = Path.GetFileName(path);
            if (!KnownFiles.Contains(name, StringComparer.OrdinalIgnoreCase) || !File.Exists(path)) return;
            lock (Gate)
            {
                string text = File.ReadAllText(path);
                JToken raw = name == "settings.ini" ? SharedSettings.ParseIni(text) : Parse(text);
                if (raw == null) { Logger.Default.Warn($"Invalid native save '{name}' retained in the version view; shared data was not replaced."); return; }
                Archive(name, Encoding.UTF8.GetBytes(text));
                JObject values = SharedSettings.Extract(name, raw, profile);
                Baselines.TryGetValue(name, out var baseline);
                Transaction("Settings.json", settings =>
                {
                    foreach (var property in values.Properties())
                        if (baseline == null ? settings[property.Name] == null : !JToken.DeepEquals(baseline[property.Name], property.Value))
                            settings[property.Name] = MergeDelta(settings[property.Name], baseline?[property.Name], property.Value);
                    return 0;
                });
                Baselines[name] = values;
                if (name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                {
                    ScoreBaselines.TryGetValue(name, out var scoreBaseline);
                    Transaction(Path.Combine("Scores", profile.Era, name + ".json"), saved =>
                    {
                        var merged = Merge(saved, raw, true, baseline: scoreBaseline) as JObject;
                        if (merged != null) { saved.RemoveAll(); foreach (var p in merged.Properties().ToArray()) saved[p.Name] = p.Value.DeepClone(); }
                        return 0;
                    });
                    ScoreBaselines[name] = raw.DeepClone();
                }
                var extras = new JObject();
                if (profile.Files.TryGetValue(name, out var shape)) shape.Project(raw, (key, value) => extras[key] = value.DeepClone());
                Preserve(name, extras);
            }
        }

        private static JToken MergeDelta(JToken master, JToken baseline, JToken incoming)
        {
            if (incoming is JObject obj)
            {
                var result = master is JObject current ? (JObject)current.DeepClone() : new JObject();
                foreach (var property in obj.Properties())
                    if (!JToken.DeepEquals((baseline as JObject)?[property.Name], property.Value))
                        result[property.Name] = MergeDelta(result[property.Name], (baseline as JObject)?[property.Name], property.Value);
                return result;
            }
            return incoming.DeepClone();
        }

        private static void Preserve(string name, JObject extras)
        {
            if (!extras.HasValues) return;
            Transaction("CustomDifficultyLabels.json", data =>
            {
                var nativeData = data["preservedNativeData"] as JObject ?? new JObject();
                var byVersion = nativeData[profile.Version] as JObject ?? new JObject();
                byVersion[name] = Merge(byVersion[name], extras, false); nativeData[profile.Version] = byVersion; data["preservedNativeData"] = nativeData;
                return 0;
            });
        }

        private static void Archive(string name, byte[] bytes)
        {
            using var hash = SHA256.Create();
            string digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            string directory = Path.Combine(root, "Backups", name);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, digest);
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                file.Write(bytes, 0, bytes.Length); file.Flush(true);
            }
            catch (IOException) when (File.Exists(path))
            {
                byte[] existing = File.ReadAllBytes(path);
                if (!hash.ComputeHash(existing).SequenceEqual(hash.ComputeHash(bytes))) throw new IOException("A save backup did not match its content hash.");
            }
        }

        private static void ClearProgress(JToken token)
        {
            if (token is JObject obj)
                foreach (var property in obj.Properties())
                    if (property.Value is JArray && (property.Name.IndexOf("StatsData", StringComparison.OrdinalIgnoreCase) >= 0 || property.Name.IndexOf("leaderboards", StringComparison.OrdinalIgnoreCase) >= 0)) property.Value = new JArray();
                    else ClearProgress(property.Value);
            else if (token is JArray array) foreach (var item in array) ClearProgress(item);
        }

        private static string Identity(JToken token)
        {
            if (!(token is JObject obj)) return token.ToString(Formatting.None);
            JToken id = obj["levelId"] ?? obj["_levelID"] ?? obj["levelID"] ?? obj["missionId"] ?? obj["missionID"] ?? obj["_leaderboardId"] ?? obj["leaderboardId"] ?? obj["playerId"] ?? obj["colorSchemeId"];
            return id == null ? null : id + "|" + (obj["difficulty"] ?? obj["_difficulty"]) + "|" + (obj["beatmapCharacteristicName"] ?? obj["_gameplayMode"]);
        }

        private static void IndexPosition(Dictionary<string, SortedSet<int>> positions, string identity, int index)
        {
            if (identity == null) return;
            if (!positions.TryGetValue(identity, out var matches)) positions[identity] = matches = new SortedSet<int>();
            matches.Add(index);
        }

        private static JToken Merge(JToken old, JToken incoming, bool scores, string key = "", JToken baseline = null)
        {
            if (old is JObject previous && incoming is JObject next)
            {
                var result = (JObject)previous.DeepClone();
                foreach (var property in next.Properties()) result[property.Name] = Merge(result[property.Name], property.Value, scores, property.Name, (baseline as JObject)?[property.Name]);
                return result;
            }
            if (scores && old is JArray oldArray && incoming is JArray newArray)
            {
                var result = (JArray)oldArray.DeepClone();
                var positions = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
                var identities = new List<string>(result.Count);
                for (int index = 0; index < result.Count; index++)
                {
                    string identity = Identity(result[index]);
                    identities.Add(identity);
                    IndexPosition(positions, identity, index);
                }
                var baselineArray = baseline as JArray;
                var priorItems = new Dictionary<string, JToken>(StringComparer.Ordinal);
                foreach (var value in baselineArray ?? new JArray())
                {
                    string identity = Identity(value);
                    if (identity != null && !priorItems.ContainsKey(identity)) priorItems.Add(identity, value);
                }
                for (int index = 0; index < newArray.Count; index++)
                {
                    JToken value = newArray[index]; string identity = Identity(value);
                    int found = identity == null ? index < result.Count ? index : -1
                        : positions.TryGetValue(identity, out var matches) ? matches.Min : -1;
                    JToken priorItem = identity == null
                        ? baselineArray != null && index < baselineArray.Count ? baselineArray[index] : null
                        : priorItems.TryGetValue(identity, out var prior) ? prior : null;
                    if (found >= 0)
                    {
                        string previousIdentity = identities[found];
                        result[found] = Merge(result[found], value, true, baseline: priorItem);
                        string updatedIdentity = Identity(result[found]);
                        if (!string.Equals(previousIdentity, updatedIdentity, StringComparison.Ordinal))
                        {
                            // Preserve first-match behavior for duplicate IDs, including
                            // positional replacements that change a record's identity.
                            if (previousIdentity != null)
                            {
                                var previousMatches = positions[previousIdentity];
                                previousMatches.Remove(found);
                                if (previousMatches.Count == 0) positions.Remove(previousIdentity);
                            }
                            IndexPosition(positions, updatedIdentity, found);
                            identities[found] = updatedIdentity;
                        }
                    }
                    else
                    {
                        result.Add(value.DeepClone());
                        identities.Add(identity);
                        IndexPosition(positions, identity, result.Count - 1);
                    }
                }
                return result;
            }
            string name = key.TrimStart('_');
            if (scores && name == "playCount" && old?.Type == JTokenType.Integer && incoming?.Type == JTokenType.Integer && baseline?.Type == JTokenType.Integer)
                return new JValue((long)old + Math.Max(0, (long)incoming - (long)baseline));
            if (scores && old != null && incoming != null && (name.IndexOf("highScore", StringComparison.OrdinalIgnoreCase) >= 0 || name == "maxCombo" || name == "maxRank" || name == "playCount"))
                if (old.Type == JTokenType.Integer && incoming.Type == JTokenType.Integer) return new JValue(Math.Max((long)old, (long)incoming));
            if (scores && old?.Type == JTokenType.Boolean && incoming?.Type == JTokenType.Boolean && (name == "fullCombo" || name == "validScore")) return new JValue((bool)old || (bool)incoming);
            return incoming?.DeepClone() ?? old?.DeepClone() ?? JValue.CreateNull();
        }
    }
}
