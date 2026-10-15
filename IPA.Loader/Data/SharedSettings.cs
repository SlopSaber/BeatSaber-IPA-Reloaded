using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace IPA.Data
{
    internal static class SharedSettings
    {
        // Canonical keys follow the modern INI names; legacy aliases map into the same values.
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
        {
            ["windowResolutionWidth"] = "window.resolution.x", ["windowResolutionHeight"] = "window.resolution.y",
            ["windowMode"] = "window.fullscreen", ["vrResolutionScale"] = "quality.vr_resolution_scale",
            ["menuVRResolutionScaleMultiplier"] = "quality.menu_vr_resolution_scale_multiplier",
            ["antiAliasingLevel"] = "quality.anti_aliasing_level", ["mirrorGraphicsSettings"] = "quality.mirror",
            ["mainEffectGraphicsSettings"] = "quality.main_fx", ["bloomGraphicsSettings"] = "quality.bloom",
            ["smokeGraphicsSettings"] = "quality.smoke_graphics", ["burnMarkTrailsEnabled"] = "quality.burn_mark_trails",
            ["screenDisplacementEffectsEnabled"] = "quality.screen_displacement_fx",
            ["roomCenterX"] = "room.center.x", ["roomCenterY"] = "room.center.y", ["roomCenterZ"] = "room.center.z",
            ["roomRotation"] = "room.rotation", ["controllerPositionX"] = "controller.position.x",
            ["controllerPositionY"] = "controller.position.y", ["controllerPositionZ"] = "controller.position.z",
            ["controllerRotationX"] = "controller.rotation.x", ["controllerRotationY"] = "controller.rotation.y",
            ["controllerRotationZ"] = "controller.rotation.z", ["smoothCameraEnabled"] = "smooth_camera.enabled",
            ["smoothCameraFieldOfView"] = "smooth_camera.fov", ["smoothCameraPositionSmooth"] = "smooth_camera.smooth_position",
            ["smoothCameraRotationSmooth"] = "smooth_camera.smooth_rotation", ["volume"] = "audio.volume",
            ["ambientVolumeScale"] = "audio.ambient_volume_scale", ["audioLatency"] = "audio.latency",
            ["overrideAudioLatency"] = "audio.override_latency", ["language"] = "misc.language",
            ["roomCenter.x"] = "room.center.x", ["roomCenter.y"] = "room.center.y", ["roomCenter.z"] = "room.center.z",
            ["windowResolution.x"] = "window.resolution.x", ["windowResolution.y"] = "window.resolution.y",
            ["audioSettings.volume"] = "audio.volume", ["audioSettings.ambientVolumeScale"] = "audio.ambient_volume_scale",
            ["audioSettings.audioLatency"] = "audio.latency", ["audioSettings.overrideAudioLatency"] = "audio.override_latency",
            ["smoothCameraSettings.enabled"] = "smooth_camera.enabled", ["smoothCameraSettings.fieldOfView"] = "smooth_camera.fov",
            ["customPreset.vrResolutionScale"] = "quality.vr_resolution_scale", ["customPreset.antiAliasingLevel"] = "quality.anti_aliasing_level",
            ["customPreset.mirrorGraphicsSettings"] = "quality.mirror", ["customPreset.mainEffectGraphicsSettings"] = "quality.main_fx",
            ["customPreset.bloomGraphicsSettings"] = "quality.bloom", ["customPreset.smokeGraphicsSettings"] = "quality.smoke_graphics"
        };
        private static readonly string[] PlayerGroups = { "playerSpecificSettings", "gameplayModifiers", "practiceSettings", "colorSchemesSettings", "overrideEnvironmentSettings", "favoritesLevelIds" };

        internal static JObject Extract(string file, JToken data, SaveDataProfile profile)
        {
            var result = new JObject();
            if (file == "settings.ini")
            {
                foreach (var property in ((JObject)data).Properties()) result[property.Name] = property.Value.DeepClone();
                return result;
            }
            if (file == "PlayerData.dat")
            {
                if (data["localPlayers"] is JArray players && players.FirstOrDefault() is JObject player)
                    foreach (string group in PlayerGroups) if (player[group] != null) result["player." + group] = player[group].DeepClone();
                return result;
            }
            if (file == "GameDynamicData.dat")
            {
                JToken options = data["_playerDynamicData"]?.FirstOrDefault()?["_gameplayOptions"];
                if (options?["staticLights"] != null) result["player.playerSpecificSettings"] = new JObject { ["staticLights"] = options["staticLights"].DeepClone() };
                if (options?["mirror"] != null)
                {
                    var settings = result["player.playerSpecificSettings"] as JObject ?? new JObject();
                    settings["leftHanded"] = options["mirror"].DeepClone(); result["player.playerSpecificSettings"] = settings;
                }
                return result;
            }
            if (file != "settings.cfg" && file != "MainSettings.json" && file != "GraphicsSettings.json") return result;
            profile.Files.TryGetValue(file, out var shape);
            foreach (var alias in Aliases)
            {
                JToken token = data.SelectToken(alias.Key, false);
                if (token == null || token.Type == JTokenType.Null) continue;
                DataShape member = Member(shape, alias.Key);
                if (member?.Kind == "enum" && token.Type == JTokenType.Integer)
                {
                    string name = member.EnumValues.FirstOrDefault(e => e.Value == (long)token).Key;
                    if (name != null) token = new JValue(name);
                }
                if (alias.Value == "window.fullscreen")
                {
                    if (token.Type == JTokenType.Integer && (long)token >= 0 && (long)token <= 1) token = new JValue((long)token == 1);
                    if (token.Type == JTokenType.String) token = new JValue(((string)token).IndexOf("Full", StringComparison.OrdinalIgnoreCase) >= 0);
                    else if (token.Type != JTokenType.Boolean) continue; // An untyped numeric enum has no portable meaning.
                }
                if (alias.Value == "quality.smoke_graphics" && token.Type == JTokenType.Integer) token = new JValue((long)token == 1);
                // Preset indices refer to version-specific ScriptableObject arrays, not INI enums.
                string canonical = IsPreset(alias.Value) && token.Type == JTokenType.Integer ? "preset." + profile.Version + "." + alias.Value : alias.Value;
                result[canonical] = token.DeepClone();
            }
            return result;
        }

        internal static JToken Apply(string file, JToken data, JObject values, SaveDataProfile profile)
        {
            JToken result = data.DeepClone();
            if (file == "settings.ini")
            {
                var ini = (JObject)result;
                foreach (var entry in values.Properties().Where(p => !p.Name.StartsWith("player.", StringComparison.Ordinal) && !p.Name.StartsWith("preset.", StringComparison.Ordinal)))
                    if (ini[entry.Name] != null || Aliases.Values.Contains(entry.Name)) ini[entry.Name] = entry.Value.DeepClone();
            }
            else if (file == "PlayerData.dat")
            {
                if (result["localPlayers"]?.FirstOrDefault() is JObject player)
                    foreach (string group in PlayerGroups)
                        if (values["player." + group] != null) player[group] = values["player." + group].DeepClone();
            }
            else if (file == "GameDynamicData.dat")
            {
                if (result["_playerDynamicData"]?.FirstOrDefault()?["_gameplayOptions"] is JObject options && values["player.playerSpecificSettings"] is JObject player)
                {
                    if (player["staticLights"] != null) options["staticLights"] = player["staticLights"].DeepClone();
                    if (player["leftHanded"] != null) options["mirror"] = player["leftHanded"].DeepClone();
                }
            }
            else if (file == "settings.cfg" || file == "MainSettings.json" || file == "GraphicsSettings.json")
            {
                profile.Files.TryGetValue(file, out var shape);
                foreach (var alias in Aliases)
                {
                    var value = IsPreset(alias.Value) ? values["preset." + profile.Version + "." + alias.Value] : values[alias.Value];
                    if (value == null || (result.SelectToken(alias.Key, false) == null && Member(shape, alias.Key) == null)) continue;
                    if (alias.Value == "window.fullscreen" && value.Type == JTokenType.Boolean) value = new JValue((bool)value ? 1 : 0);
                    if (alias.Value == "quality.smoke_graphics" && value.Type == JTokenType.Boolean) value = new JValue((bool)value ? 1 : 0);
                    Set((JObject)result, alias.Key, value);
                }
            }
            return result;
        }

        private static bool IsPreset(string key) => key == "quality.mirror" || key == "quality.main_fx" || key == "quality.bloom";

        internal static JObject ParseIni(string text)
        {
            var result = new JObject();
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string clean = line.Trim();
                if (clean.StartsWith("#", StringComparison.Ordinal) || clean.StartsWith(";", StringComparison.Ordinal)) continue;
                int equals = clean.IndexOf('='); if (equals < 1) continue;
                string key = clean.Substring(0, equals).Trim(), value = clean.Substring(equals + 1).Trim();
                if (bool.TryParse(value, out bool boolean)) result[key] = boolean;
                else if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) result[key] = integer;
                else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && !double.IsInfinity(number) && !double.IsNaN(number)) result[key] = number;
                else result[key] = value;
            }
            return result;
        }

        internal static string WriteIni(JObject data) => "# BeatSaber settings managed by BSIPA\n" + string.Join("\n", data.Properties().Select(p => p.Name + " = "
            + (p.Value.Type == JTokenType.String ? (string)p.Value : p.Value.ToString(Newtonsoft.Json.Formatting.None)))) + "\n";

        private static DataShape Member(DataShape root, string key)
        {
            foreach (string part in key.Split('.')) { if (root == null || !root.Fields.TryGetValue(part, out root)) return null; }
            return root;
        }
        private static void Set(JObject target, string path, JToken value)
        {
            string[] parts = path.Split('.');
            for (int i = 0; i < parts.Length - 1; i++) { if (!(target[parts[i]] is JObject nested)) { nested = new JObject(); target[parts[i]] = nested; } target = nested; }
            target[parts[parts.Length - 1]] = value.DeepClone();
        }
    }
}
