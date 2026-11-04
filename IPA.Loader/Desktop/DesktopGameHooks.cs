using HarmonyLib;
using Logger = IPA.Logging.Logger;
using System;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace IPA.Desktop
{
    internal static class DesktopGameHooks
    {
        private static readonly Harmony Patches = new("BSIPA.DesktopFlight");
        private static readonly HashSet<MethodInfo> PointerGetters = new();
        private static readonly HashSet<MethodInfo> Raycasters = new();
        private static readonly Dictionary<Type, MethodInfo> PointerGetterMethods = new();
        private static readonly Dictionary<Type, MethodInfo> RaycastMethods = new();

        private struct BlockingState
        {
            internal bool Changed;
            internal LayerMask Mask;
        }

        internal static void BindRaycaster(Behaviour raycaster)
        {
            var type = raycaster.GetType();
            if (!RaycastMethods.TryGetValue(type, out var method))
            {
                method = type.GetField("_blockingMask", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType == typeof(LayerMask)
                    ? type.GetMethod("Raycast", new[] { typeof(PointerEventData), typeof(List<RaycastResult>) }) : null;
                RaycastMethods.Add(type, method);
            }
            if (method == null || Raycasters.Contains(method)) return;
            try
            {
                Patches.Patch(method, prefix: new HarmonyMethod(typeof(DesktopGameHooks), nameof(DesktopRaycastBeginning)),
                    finalizer: new HarmonyMethod(typeof(DesktopGameHooks), nameof(DesktopRaycastEnded)));
                Raycasters.Add(method);
                Logger.Default.Info("BSIPA pointer uses native UI raycasts without world-physics blockers; other pointer events retain their blocking masks.");
            }
            catch (Exception error) { Logger.Default.Warn($"FPFC UI raycast hook unavailable: {error.Message}"); }
        }

        private static void DesktopRaycastBeginning(PointerEventData __0, ref LayerMask ____blockingMask, out BlockingState __state)
        {
            __state = default;
            if (!DesktopPointerInput.IsDesktopRaycast(__0)) return;
            __state = new BlockingState { Changed = true, Mask = ____blockingMask };
            ____blockingMask = 0;
        }

        private static void DesktopRaycastEnded(ref LayerMask ____blockingMask, BlockingState __state)
        {
            if (__state.Changed) ____blockingMask = __state.Mask;
        }

        internal static void BindPointer(Behaviour pointer)
        {
            var type = pointer.GetType();
            if (!PointerGetterMethods.TryGetValue(type, out var getter))
            {
                getter = type.GetProperty("cursorPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);
                PointerGetterMethods.Add(type, getter);
            }
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

        /// <summary>
        /// The game blocks UI input during view transitions by disabling its own input module. FPFC keeps that
        /// module disabled permanently, so the requested state is mirrored here for the desktop pointer.
        /// </summary>
        internal static bool NativeInteractionEnabled { get; private set; } = true;
        private static bool interactionHookInstalled;

        internal static void Install()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!interactionHookInstalled)
                {
                    var setter = assembly.GetType("HMUI.FlowCoordinator", false)?.GetMethod("SetGlobalUserInteraction",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                    if (setter != null)
                    {
                        try
                        {
                            Patches.Patch(setter, prefix: new HarmonyMethod(typeof(DesktopGameHooks), nameof(NativeInteractionChanged)));
                            interactionHookInstalled = true;
                        }
                        catch (Exception error) { Logger.Default.Warn($"FPFC interaction lock hook unavailable: {error.Message}"); }
                    }
                }
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

        private static void NativeInteractionChanged(bool __0) => NativeInteractionEnabled = __0;

        private static bool DesktopFocus(ref bool __result)
        {
            if (DesktopFlight.Instance == null || !DesktopFlight.Instance.Ready) return true;
            __result = Application.isFocused;
            return false;
        }
    }
}
