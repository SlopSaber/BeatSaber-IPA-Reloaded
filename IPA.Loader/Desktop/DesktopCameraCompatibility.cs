using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Logger = IPA.Logging.Logger;

namespace IPA.Desktop
{
    internal static class DesktopCameraCompatibility
    {
        private static readonly Harmony Patches = new("BSIPA.DesktopCameraCompatibility");
        private static Type camera2Type;
        private static Type siraControllerType;
        private static PropertyInfo siraAllowInput;
        internal static bool PreferBSIPA { get; private set; }
        private static bool OwnsCamera => PreferBSIPA && DesktopFlight.Instance != null && DesktopFlight.Instance.OwnsCamera;

        internal static void Install()
        {
            PreferBSIPA = Config.SelfConfig.Instance.PreferBSIPAFPFC;
            var sira = Loader.PluginManager.GetPluginFromId("SiraUtil")?.Assembly;
            siraControllerType = sira?.GetType("SiraUtil.Tools.FPFC.SimpleCameraController", false);
            if (siraControllerType != null && !typeof(Behaviour).IsAssignableFrom(siraControllerType)) siraControllerType = null;
            siraAllowInput = siraControllerType?.GetProperty("AllowInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Patch(sira, "SiraUtil.Tools.FPFC.SiraFullFPFCInstaller", "InstallBindings", nameof(SiraArguments), transpiler: true);
            Patch(sira, "SiraUtil.Tools.FPFC.FPFCSettingsController", "get_Enabled", nameof(SiraEnabled));
            Patch(sira, "SiraUtil.Tools.FPFC.FPFCToggle", "EnableFPFC", nameof(SiraActivating));
            Patch(sira, "SiraUtil.Tools.FPFC.SimpleCameraController", "Awake", nameof(ExternalControllerStarting));
            Patch(sira, "SiraUtil.Tools.FPFC.SimpleCameraController", "OnEnable", nameof(ExternalControllerStarting));
            Patch(sira, "SiraUtil.Tools.FPFC.SimpleCameraController", "Update", nameof(ExternalControllerUpdating));
            var camera2 = Loader.PluginManager.GetPluginFromId("Camera2")?.Assembly;
            camera2Type = camera2?.GetType("Camera2.Behaviours.Cam2", false);
            Patch(camera2, "Camera2.Behaviours.Cam2", "LateUpdate", nameof(AllowExternalCamera));
            Patch(camera2, "Camera2.Behaviours.Cam2", "PrepareMiddlewaredRender", nameof(AllowExternalCamera));
            Patch(camera2, "Camera2.Behaviours.CamerasViewport", "Update", nameof(AllowExternalCamera));
            Patch(camera2, "Camera2.Behaviours.CamerasViewport", "Awake", nameof(ViewportCreated), postfix: true);
            Logger.Default.Info($"FPFC camera preference: preferBSIPAFPFC={PreferBSIPA.ToString().ToLowerInvariant()}; {(PreferBSIPA ? "BSIPA owns camera and pointer controls" : "active SiraUtil/Camera2 FPFC controllers take priority")}.");
        }

        private static void Patch(Assembly assembly, string type, string method, string callback, bool transpiler = false, bool postfix = false)
        {
            try
            {
                var target = assembly?.GetType(type, false)?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .SingleOrDefault(candidate => candidate.Name == method);
                if (target == null) return;
                var hook = new HarmonyMethod(typeof(DesktopCameraCompatibility), callback);
                Patches.Patch(target, prefix: !transpiler && !postfix ? hook : null,
                    postfix: postfix ? hook : null, transpiler: transpiler ? hook : null);
                Logger.Default.Debug($"FPFC standdown hook installed: {type}.{method}.");
            }
            catch (Exception error) { Logger.Default.Warn($"FPFC standdown hook unavailable for {type}.{method}: {error.Message}"); }
        }

        private static IEnumerable<CodeInstruction> SiraArguments(IEnumerable<CodeInstruction> instructions)
        {
            var original = typeof(Environment).GetMethod(nameof(Environment.GetCommandLineArgs));
            var replacement = typeof(DesktopCameraCompatibility).GetMethod(nameof(SiraCommandLine), BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(original)) instruction.operand = replacement;
                yield return instruction;
            }
        }

        private static string[] SiraCommandLine()
        {
            var arguments = Environment.GetCommandLineArgs();
            return OwnsCamera ? arguments.Concat(new[] { "--no-sirautil-fpfc" }).ToArray() : arguments;
        }

        private static bool AllowExternalCamera() => !OwnsCamera;

        private static void ExternalControllerStarting(Behaviour __instance)
        {
            if (!PreferBSIPA && IsExternalActive(__instance)) DesktopFlight.Instance?.YieldToExternal(__instance);
        }

        private static void SiraActivating(Behaviour ____simpleCameraController)
        {
            if (!PreferBSIPA && ____simpleCameraController != null && ____simpleCameraController.isActiveAndEnabled)
                DesktopFlight.Instance?.YieldToExternal(____simpleCameraController);
        }

        private static bool ExternalControllerUpdating(Behaviour __instance)
        {
            ExternalControllerStarting(__instance);
            return AllowExternalCamera();
        }

        internal static bool IsExternalActive(Behaviour controller)
            => controller != null && controller.isActiveAndEnabled && controller.gameObject.scene.IsValid()
                && controller.gameObject.scene.isLoaded && (siraAllowInput?.PropertyType != typeof(bool)
                    || (bool)siraAllowInput.GetValue(controller, null));

        internal static Behaviour FindExternalController()
            => PreferBSIPA || siraControllerType == null ? null
                : Resources.FindObjectsOfTypeAll(siraControllerType).OfType<Behaviour>().FirstOrDefault(IsExternalActive);

        private static bool SiraEnabled(ref bool __result)
        {
            if (!OwnsCamera) return true;
            __result = false;
            return false;
        }

        private static void ViewportCreated(Behaviour __instance)
        {
            if (OwnsCamera) Suppress(__instance);
        }

        internal static bool IsCamera2Camera(Camera camera)
            => camera != null && camera2Type != null && camera.GetComponentInParent(camera2Type) != null;

        internal static void Suppress(Behaviour behaviour)
        {
            if (!OwnsCamera) return;
            string type = behaviour.GetType().FullName;
            if (type == "SiraUtil.Tools.FPFC.SimpleCameraController") DesktopFlight.Instance.Disable(behaviour);
            if (type == "Camera2.Behaviours.CamerasViewport")
            {
                var canvas = behaviour.GetComponent<Canvas>();
                if (canvas != null) DesktopFlight.Instance.Disable(canvas);
            }
            if (behaviour is Camera camera && IsCamera2Camera(camera)) DesktopFlight.Instance.Disable(camera);
        }
    }
}
