using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IPA.Data
{
    internal static class StartupWindowSettings
    {
        internal static void Apply()
        {
            // Explicit Unity display arguments take priority over saved preferences.
            string[] displayArguments = { "-screen-fullscreen", "-screen-width", "-screen-height", "-window-mode", "-popupwindow" };
            if (Environment.GetCommandLineArgs().Any(argument => displayArguments.Contains(argument, StringComparer.OrdinalIgnoreCase))) return;
            try
            {
                var settings = SaveDataStore.Transaction("Settings.json", data => (JObject)data.DeepClone(), readOnly: true);
                var mode = settings["window.fullscreen"];
                if (mode?.Type != JTokenType.Boolean) return;
                int width = Dimension(settings["window.resolution.x"], Screen.width);
                int height = Dimension(settings["window.resolution.y"], Screen.height);
                bool fullscreen = (bool)mode;
                Screen.SetResolution(width, height, fullscreen);
                Logging.Logger.Default.Info($"Applied shared startup window settings: {width}x{height}, {(fullscreen ? "fullscreen" : "windowed")}.");
            }
            catch (Exception error)
            {
                Logging.Logger.Default.Warn($"Shared startup window settings unavailable: {error.Message}");
            }
        }

        private static int Dimension(JToken value, int fallback)
            => value?.Type == JTokenType.Integer && (long)value > 0 && (long)value <= int.MaxValue ? (int)value : fallback;
    }
}
