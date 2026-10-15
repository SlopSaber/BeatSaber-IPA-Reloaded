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
            if (!module.AssemblyReferences.Any(reference => reference.Name == "IllusionPlugin" || reference.Name == "IllusionInjector")) return false;
            bool changed = false;
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
    }
}
