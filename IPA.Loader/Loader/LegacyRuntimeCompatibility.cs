using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Logger = IPA.Logging.Logger;

namespace IPA.Loader
{
    internal static class LegacyRuntimeCompatibility
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Harmony Patches = new("BSIPA.LegacyRuntimeCompatibility");

        internal static void Install()
        {
            if (!Version.TryParse(Utilities.UnityGame.GameVersion.ToString(), out var version) || version.Major != 0 || version.Minor != 11) return;
            Patch("MainSettingsModel", "Save", nameof(ApplyWindow), false);
            Patch("Zenject.ZenjectStateMachineBehaviourAutoInjecter", "Start", nameof(InjectClonedAnimator), true);
            Patch("HMUI.TableView", "ReloadData", nameof(RestoreTableSource), true);
        }

        private static void Patch(string name, string method, string hook, bool prefix)
        {
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(candidate => candidate != null)
                    ?? Type.GetType(name + ", " + (name.StartsWith("Zenject.", StringComparison.Ordinal) ? "Zenject" : "Assembly-CSharp"), false);
                var target = type?.GetMethod(method, Members, null, Type.EmptyTypes, null);
                if (target == null) { Logger.Loader.Debug($"Legacy compatibility target absent: {name}.{method}."); return; }
                var patch = new HarmonyMethod(typeof(LegacyRuntimeCompatibility), hook);
                Patches.Patch(target, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
            }
            catch (Exception error) { Logger.Loader.Warn($"Legacy compatibility hook unavailable for {name}.{method}: {error.Message}"); }
        }

        private static void ApplyWindow(object __instance)
        {
            var type = __instance.GetType();
            object mode = type.GetProperty("windowMode", Members)?.GetValue(__instance, null);
            object resolution = type.GetProperty("windowResolution", Members)?.GetValue(__instance, null);
            if (mode == null || resolution == null) return;
            var shape = resolution.GetType();
            int width = (int)shape.GetProperty("x").GetValue(resolution, null);
            int height = (int)shape.GetProperty("y").GetValue(resolution, null);
            bool fullscreen = string.Equals(mode.ToString(), "Fullscreen", StringComparison.OrdinalIgnoreCase);
            if (width > 0 && height > 0 && (Screen.width != width || Screen.height != height || Screen.fullScreen != fullscreen))
            {
                Screen.SetResolution(width, height, fullscreen);
                Logger.Loader.Debug($"Applied legacy window settings: {width}x{height}, fullscreen={fullscreen}.");
            }
        }

        private static void InjectClonedAnimator(Component __instance)
        {
            var field = __instance.GetType().GetField("_container", Members);
            if (field == null || field.GetValue(__instance) != null) return;
            // Object.Instantiate copies serialized fields, but not Zenject's container.
            var contexts = __instance.GetComponentsInParent<MonoBehaviour>(true)
                .Concat(Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(context => context != null
                    && context.gameObject.scene == __instance.gameObject.scene && context.GetType().FullName == "Zenject.SceneContext"));
            foreach (var context in contexts.Where(context => context != null))
            {
                var property = context.GetType().GetProperty("Container", Members);
                if (property?.PropertyType.FullName != "Zenject.DiContainer") continue;
                object container = property.GetValue(context, null);
                if (container == null) continue;
                var inject = container.GetType().GetMethod("Inject", Members, null, new[] { typeof(object) }, null);
                if (inject == null) continue;
                inject.Invoke(container, new object[] { __instance });
                if (field.GetValue(__instance) != null) Logger.Loader.Debug("Injected a cloned legacy animator from its scene container.");
                return;
            }
        }

        private static void RestoreTableSource(Component __instance)
        {
            var field = __instance.GetType().GetField("_dataSource", Members);
            if (field == null || field.GetValue(__instance) != null) return;
            foreach (var owner in __instance.GetComponentsInParent<MonoBehaviour>(true))
            {
                if (owner == null || !field.FieldType.IsInstanceOfType(owner)) continue;
                var table = owner.GetType().GetField("_tableView", Members);
                var levels = owner.GetType().GetField("_levels", Members);
                if (!ReferenceEquals(table?.GetValue(owner), __instance) || levels?.GetValue(owner) == null) continue;
                field.SetValue(__instance, owner);
                Logger.Loader.Debug("Restored a legacy level table's owning data source before refresh.");
                return;
            }
        }
    }
}
