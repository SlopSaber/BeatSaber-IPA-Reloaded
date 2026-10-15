using HarmonyLib;
using Logger = IPA.Logging.Logger;
using System;
using System.Reflection;
using UnityEngine;

namespace IPA.Desktop
{
    internal static class DesktopGameHooks
    {
        private static readonly Harmony Patches = new("BSIPA.DesktopFlight");

        internal static void Install()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (string name in new[] { "VRPlatformHelper", "UnityXRHelper", "OpenVRHelper", "OculusVRHelper" })
                {
                    var type = assembly.GetType(name, false);
                    var getter = type?.GetProperty("hasInputFocus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);
                    if (getter == null || getter.IsAbstract || getter.ReturnType != typeof(bool)) continue;
                    try { Patches.Patch(getter, prefix: new HarmonyMethod(typeof(DesktopGameHooks), nameof(DesktopFocus))); }
                    catch (Exception error) { Logger.Default.Debug($"FPFC focus hook unavailable for {name}: {error.Message}"); }
                }
            }
        }

        private static bool DesktopFocus(ref bool __result)
        {
            if (DesktopFlight.Instance == null || !DesktopFlight.Instance.Ready) return true;
            __result = Application.isFocused;
            return false;
        }
    }
}
