using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using IPA.Utilities;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IPA.Loader
{
    internal static class MonoRuntimeRewriter
    {
        private static readonly Lazy<bool> Unity6Mono = new(() =>
        {
            string player = Path.Combine(UnityGame.InstallPath, "UnityPlayer.dll");
            return Type.GetType("Mono.Runtime") != null && File.Exists(player)
                && FileVersionInfo.GetVersionInfo(player).FileMajorPart >= 6000;
        });

        internal static void Preload()
        {
            if (!Unity6Mono.Value) return;
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => !assembly.IsDynamic && assembly.GetName().Name == "MonoMod.Utils");
            if (loaded != null)
            {
                if (LoaderCompatibility.OriginalPath(loaded.Location) == loaded.Location)
                    LibLoader.Log(IPA.Logging.Logger.Level.Warning, "MonoMod.Utils loaded before runtime compatibility could be applied.");
                return;
            }
            _ = LibLoader.LoadLibrary(new System.Reflection.AssemblyName("MonoMod.Utils, Version=25.0.13.0, Culture=neutral"));
        }

        internal static bool Rewrite(ModuleDefinition module)
        {
            if (module.Assembly.Name.Name != "MonoMod.Utils"
                || module.Assembly.Name.Version != new Version(25, 0, 13, 0) || !Unity6Mono.Value) return false;
            var generate = module.GetType("MonoMod.Utils.DynamicMethodDefinition")?.Methods
                .FirstOrDefault(method => method.Name == "Generate" && method.Parameters.Count == 1 && method.HasBody);
            if (generate == null) return false;
            var instructions = generate.Body.Instructions;
            bool IsGenerator(MethodReference method, string type) => method.Name == "Generate"
                && method.DeclaringType is GenericInstanceType generic && generic.GenericArguments.Count == 1
                && generic.GenericArguments[0].FullName == "MonoMod.Utils." + type;
            var cecil = instructions.Where(instruction => instruction.OpCode == OpCodes.Call)
                .Select(instruction => instruction.Operand as MethodReference)
                .FirstOrDefault(method => method != null && IsGenerator(method, "DMDCecilGenerator"));
            if (cecil == null) return false;

            // Retarget only the automatic fault/filter fallback, preserving explicit generator choices.
            for (int index = 0; index + 5 < instructions.Count; index++)
            {
                if (!(instructions[index].Operand is MethodReference any) || any.Name != "Any"
                    || any.DeclaringType.FullName != "System.Linq.Enumerable"
                    || instructions[index + 1].OpCode.FlowControl != FlowControl.Cond_Branch
                    || instructions[index + 2].OpCode != OpCodes.Ldarg_0 || instructions[index + 3].OpCode != OpCodes.Ldarg_1
                    || instructions[index + 4].OpCode != OpCodes.Call || instructions[index + 5].OpCode != OpCodes.Ret
                    || !(instructions[index + 4].Operand is MethodReference builder) || !IsGenerator(builder, "DMDEmitMethodBuilderGenerator")) continue;
                instructions[index + 4].Operand = cecil;
                return true;
            }
            return false;
        }
    }
}
