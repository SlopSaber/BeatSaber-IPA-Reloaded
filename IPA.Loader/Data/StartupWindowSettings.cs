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
                if (!SharedSettings.IsValid("window.resolution.x", settings["window.resolution.x"])
                    || !SharedSettings.IsValid("window.resolution.y", settings["window.resolution.y"]))
                {
                    Logging.Logger.Default.Warn("Shared startup window dimensions are invalid or incomplete; retaining native display settings.");
                    return;
                }
                int width = (int)settings["window.resolution.x"];
                int height = (int)settings["window.resolution.y"];
                bool fullscreen = (bool)mode;
                Screen.SetResolution(width, height, fullscreen);
                Logging.Logger.Default.Info($"Applied shared startup window settings: {width}x{height}, {(fullscreen ? "fullscreen" : "windowed")}.");
            }
            catch (Exception error)
            {
                Logging.Logger.Default.Warn($"Shared startup window settings unavailable: {error.Message}");
            }
        }
    }
}
