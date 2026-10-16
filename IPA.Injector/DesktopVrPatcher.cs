using System;
using System.IO;
using System.Linq;
using IPA.Desktop;
using IPA.Injector.Backups;
using IPA.Logging;
using IPA.Utilities;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IPA.Injector
{
    internal static class DesktopVrPatcher
    {
        private const string DesktopMarker = "BSIPA_FPFC_NO_VR";

        internal static void Apply()
        {
            if (!DesktopMode.Requested) return;
            if (Environment.GetEnvironmentVariable(DesktopMarker) != "1")
            {
                string reason = Environment.GetEnvironmentVariable("BSIPA_FPFC_VR_ERROR") ?? "Install the matching winhttp.dll from the complete package.";
                Logger.Injector.Warn($"FPFC native VR suppression is unavailable. {reason} Managed FPFC alone cannot prevent early VR runtime startup.");
                return;
            }
            string managed = Path.Combine(GameVersionEarly.ResolveDataPath(UnityGame.InstallPath), "Managed");
            var backup = new BackupUnit(Path.Combine(UnityGame.InstallPath, "IPA", "Backups", "DesktopVR"),
                Utils.CurrentTime().ToString("yyyy-MM-dd_HH-mm-ss") + "-" + Guid.NewGuid().ToString("N"));
            foreach (string name in new[] { "Unity.XR.OpenXR.dll", "Unity.XR.Oculus.dll", "Unity.XR.OpenVR.dll" })
            {
                string path = Path.Combine(managed, name);
                if (!File.Exists(path)) continue;
                using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true });
                bool changed = false;
                foreach (var type in module.GetTypes().Where(type => type.FullName == "UnityEngine.XR.OpenXR.OpenXRLoader"
                    || type.FullName == "UnityEngine.XR.OpenXR.OpenXRLoaderBase" || type.FullName == "Unity.XR.Oculus.OculusLoader"
                    || type.FullName == "Unity.XR.OpenVR.OpenVRLoader"))
                foreach (var method in type.Methods.Where(method => method.Name == "Initialize" && !method.IsStatic && method.Parameters.Count == 0
                    && method.HasBody && method.ReturnType.FullName == "System.Boolean"))
                {
                    if (method.Body.Instructions.Count == 0 || method.Body.Instructions[0].Operand as string == DesktopMarker) continue;
                    var il = method.Body.GetILProcessor();
                    var first = method.Body.Instructions[0];
                    var proceed = il.Create(OpCodes.Nop);
                    foreach (var instruction in new[] {
                        il.Create(OpCodes.Ldstr, DesktopMarker),
                        il.Create(OpCodes.Call, module.ImportReference(typeof(Environment).GetMethod(nameof(Environment.GetEnvironmentVariable), new[] { typeof(string) }))),
                        il.Create(OpCodes.Ldstr, "1"),
                        il.Create(OpCodes.Call, module.ImportReference(typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) }))),
                        il.Create(OpCodes.Brfalse, proceed), il.Create(OpCodes.Ldc_I4_0), il.Create(OpCodes.Ret),
                        proceed }) il.InsertBefore(first, instruction);
                    changed = true;
                }
                if (!changed) continue;
                if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => !assembly.IsDynamic && assembly.GetName().Name == module.Assembly.Name.Name))
                    throw new InvalidOperationException($"Cannot guard loaded XR assembly {name}; fully restart the game.");
                string temporary = path + ".bsipa-vr-" + Guid.NewGuid().ToString("N") + ".tmp";
                try { module.Write(temporary); backup.Add(path); File.Replace(temporary, path, null); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            Logger.Injector.Info("FPFC non-VR startup selected: native VR device disabled, OpenXR pre-init suppressed, and managed XR loaders guarded for this process.");
        }
    }
}
