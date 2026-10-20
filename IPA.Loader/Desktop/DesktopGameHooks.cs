using HarmonyLib;
using Logger = IPA.Logging.Logger;
using System;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;

namespace IPA.Desktop
{
    internal static class DesktopGameHooks
    {
        private static readonly Harmony Patches = new("BSIPA.DesktopFlight");
        private static readonly HashSet<MethodInfo> PointerGetters = new();

        internal static void BindPointer(Behaviour pointer)
        {
            var getter = pointer.GetType().GetProperty("cursorPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);
            if (getter == null || getter.ReturnType != typeof(Vector3) || PointerGetters.Contains(getter)) return;
            try
            {
                Patches.Patch(getter, prefix: new HarmonyMethod(typeof(DesktopGameHooks), nameof(DesktopCursorPosition)));
                PointerGetters.Add(getter);
            }
            catch (Exception error) { Logger.Default.Warn($"FPFC cursor hook unavailable: {error.Message}"); }
        }

        private static bool DesktopCursorPosition(ref Vector3 __result)
        {
            if (!DesktopPointerInput.TryGetWorldPosition(out var position)) return true;
            __result = position; return false;
        }

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
