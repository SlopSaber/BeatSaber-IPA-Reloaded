using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IPA.Loader
{
    internal static class LegacyPluginRewriter
    {
        internal static bool Rewrite(ModuleDefinition module)
        {
            var oldReferences = module.AssemblyReferences.Where(reference => reference.Name == "IllusionPlugin" || reference.Name == "IllusionInjector").ToArray();
            if (oldReferences.Length == 0) return false;
            // Dependency resolution can load a DLL before plugin discovery. Both paths
            // must prepare the same type identities before the CLR caches the assembly.
            foreach (var type in module.GetTypeReferences().Where(type => type.Scope is AssemblyNameReference scope && oldReferences.Contains(scope)))
            {
                string mapped = type.FullName switch
                {
                    "IllusionPlugin.IPlugin" or "IllusionPlugin.IEnhancedPlugin" => "IPA.Old",
                    "IllusionPlugin.IniFile" or "IllusionPlugin.IModPrefs" or "IllusionPlugin.ModPrefs" => "IPA.Config",
                    "IllusionPlugin.Utils.ReflectionUtil" => "IPA.Utilities",
                    "IllusionPlugin.Logging.Logger" or "IllusionPlugin.Logging.LogPrinter" => "IPA.Logging",
                    "IllusionInjector.Logging.UnityLogInterceptor" or "IllusionInjector.Logging.StandardLogger" => "IPA.Logging",
                    "IllusionInjector.PluginManager" or "IllusionInjector.PluginComponent" => "IPA.Loader",
                    "IllusionInjector.CompositeBSPlugin" or "IllusionInjector.CompositeIPAPlugin" => "IPA.Loader.Composite",
                    "IllusionInjector.Updating.SelfPlugin" => "IPA.Updating",
                    "IllusionInjector.Updating.Backup.BackupUnit" => "IPA.Updating.Backup",
                    _ when type.Namespace == "IllusionInjector.Utilities" => "IPA.Utilities",
                    _ when type.Namespace == "IllusionInjector.Logging.Printers" => "IPA.Logging.Printers",
                    _ => null
                };
                if (mapped != null) type.Namespace = mapped;
            }
            foreach (var reference in oldReferences) reference.Name = "IPA.Loader";
            bool changed = true;
            InitializeCustomColors(module);
            foreach (var method in module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
            {
                var instructions = method.Body.Instructions;
                for (int i = 0; i + 1 < instructions.Count; i++)
                {
                    // Some old mods create an empty config and immediately read it,
                    // discarding the exclusive FileStream without closing its handle.
                    if (instructions[i].OpCode != OpCodes.Call || instructions[i].Operand is not MethodReference call
                        || call.DeclaringType.FullName != "System.IO.File" || call.Name != "Create"
                        || call.ReturnType.FullName != "System.IO.FileStream" || instructions[i + 1].OpCode != OpCodes.Pop) continue;
                    var discard = instructions[i + 1];
                    if (instructions.Any(instruction => ReferenceEquals(instruction.Operand, discard)
                        || instruction.Operand is Instruction[] targets && targets.Contains(discard))) continue;
                    if (method.Body.ExceptionHandlers.Any(handler => handler.TryStart == discard || handler.TryEnd == discard
                        || handler.HandlerStart == discard || handler.HandlerEnd == discard || handler.FilterStart == discard)) continue;
                    discard.OpCode = OpCodes.Callvirt;
                    discard.Operand = module.ImportReference(typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose)));
                    changed = true;
                }
            }
            return changed;
        }

        private static void InitializeCustomColors(ModuleDefinition module)
        {
            var type = module.Types.FirstOrDefault(type => type.FullName == "CustomColors.Plugin");
            var update = type?.Methods.FirstOrDefault(method => method.Name == "OnUpdate" && method.HasBody && !method.IsStatic && method.Parameters.Count == 0);
            var initialize = type?.Methods.FirstOrDefault(method => method.Name == "GetObjects" && method.HasBody && !method.IsStatic && method.Parameters.Count == 0 && method.ReturnType.FullName == "System.Void");
            var colors = type?.Fields.FirstOrDefault(field => field.Name == "_scriptableColors" && field.FieldType is ArrayType && !field.IsStatic);
            var lights = type?.Fields.FirstOrDefault(field => field.Name == "_prePassLights" && field.FieldType is ArrayType && !field.IsStatic);
            if (update == null || initialize == null || colors == null || lights == null || update.Body.Instructions.Count == 0) return;
            var il = update.Body.GetILProcessor();
            var start = update.Body.Instructions[0];
            var load = il.Create(OpCodes.Ldarg_0);
            // The original mod discovers resources only on activeSceneChanged;
            // its first Update can precede that event after subscribing at startup.
            foreach (var instruction in new[] {
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, colors), il.Create(OpCodes.Brfalse, load),
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, lights), il.Create(OpCodes.Brtrue, start),
                load, il.Create(OpCodes.Call, initialize) }) il.InsertBefore(start, instruction);
        }
    }
}
