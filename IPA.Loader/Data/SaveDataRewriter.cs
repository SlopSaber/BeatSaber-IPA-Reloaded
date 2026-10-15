using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IPA.Data
{
    internal static class SaveDataRewriter
    {
        private static readonly Dictionary<string, MethodInfo> FileMethods = Methods(typeof(NativeFileIO));
        private static readonly Dictionary<string, MethodInfo> PrefMethods = Methods(typeof(PortablePlayerPrefs));

        private static Dictionary<string, MethodInfo> Methods(Type type) => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName)) + ")", StringComparer.Ordinal);

        internal static bool Rewrite(ModuleDefinition module)
        {
            if (module.Assembly.Name.Name.StartsWith("IPA.", StringComparison.Ordinal)) return false;
            bool changed = false;
            foreach (var type in module.GetTypes())
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
            if ((type.Name == "BeatmapCharacteristicSO" || type.Name == "BeatmapCharacteristic") && method.Name == "get_serializedName")
            {
                var processor = method.Body.GetILProcessor();
                if (!method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == typeof(SaveDataStore).FullName))
                    foreach (var ret in method.Body.Instructions.Where(i => i.OpCode.Code == Code.Ret).ToArray())
                    {
                        // Keep the return instruction as the branch target and append a new return.
                        ret.OpCode = OpCodes.Call; ret.Operand = module.ImportReference(typeof(SaveDataStore).GetMethod(nameof(SaveDataStore.ObserveCharacteristic)));
                        processor.InsertAfter(ret, processor.Create(OpCodes.Ret)); changed = true;
                    }
            }
            foreach (var instruction in method.Body.Instructions)
            {
                if (!(instruction.Operand is MethodReference call) || instruction.OpCode.Code != Code.Call) continue;
                MethodInfo replacement = null;
                if (call.DeclaringType.FullName == "UnityEngine.Application" && call.Name == "get_persistentDataPath")
                    replacement = typeof(SaveDataStore).GetMethod(nameof(SaveDataStore.GetPersistentDataPath));
                else
                {
                    var candidates = call.DeclaringType.FullName == "UnityEngine.PlayerPrefs" ? PrefMethods
                        : call.DeclaringType.FullName == "System.IO.File" ? FileMethods : null;
                    candidates?.TryGetValue(call.Name + "(" + string.Join(",", call.Parameters.Select(p => p.ParameterType.FullName)) + ")", out replacement);
                }
                if (replacement == null) continue;
                instruction.Operand = module.ImportReference(replacement);
                changed = true;
            }
            }
            return changed;
        }
    }
}
