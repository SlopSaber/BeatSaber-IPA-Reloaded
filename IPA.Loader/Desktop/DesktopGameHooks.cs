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
        private static Type siraControllerType;

        internal static void BindSiraControls()
        {
            try
            {
                var assembly = Loader.PluginManager.GetPluginFromId("SiraUtil")?.Assembly;
                siraControllerType = assembly?.GetType("SiraUtil.Tools.FPFC.SimpleCameraController", false);
                if (siraControllerType == null || !typeof(Behaviour).IsAssignableFrom(siraControllerType))
                {
                    siraControllerType = null;
                    if (assembly != null) Logger.Default.Debug("SiraUtil has no FPFC controller; IPA desktop controls remain available.");
                    return;
                }
                var onEnable = siraControllerType.GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (onEnable == null) return;
                Patches.Patch(onEnable, prefix: new HarmonyMethod(typeof(DesktopGameHooks), nameof(SiraControllerEnabling)));
            }
            catch (Exception error) { Logger.Default.Warn($"SiraUtil FPFC handoff hook unavailable: {error.Message}"); }
        }

        private static void SiraControllerEnabling(Behaviour __instance)
        {
            try { DesktopFlight.Instance?.YieldToSira(__instance); }
            catch (Exception error) { Logger.Default.Warn($"Could not fully restore IPA controls during SiraUtil FPFC handoff: {error}"); }
        }

        internal static Behaviour ActiveSiraController(Camera camera)
        {
            if (camera == null || siraControllerType == null) return null;
            return camera.GetComponent(siraControllerType) is Behaviour controller && controller.isActiveAndEnabled ? controller : null;
        }

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
