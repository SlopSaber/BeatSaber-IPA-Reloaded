using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IPA.Loader
{
    internal static class LegacyAffinityRewriter
    {
        private const string Affinity = "SiraUtil.Affinity.";

        internal static bool Rewrite(ModuleDefinition module)
        {
            if (!module.AssemblyReferences.Any(reference => reference.Name == "SiraUtil")) return false;
            bool changed = false;
            foreach (var type in module.GetTypes().Where(type => !type.HasGenericParameters).ToArray())
            foreach (var callback in type.Methods.ToArray())
            {
                if (!callback.HasBody || callback.IsStatic || callback.HasGenericParameters
                    || callback.ReturnType.FullName != "System.Void") continue;
                var patches = callback.CustomAttributes.Where(attribute => attribute.AttributeType.FullName == Affinity + "AffinityPatchAttribute").ToArray();
                if (patches.Length != 1 || callback.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == Affinity + "AffinityTranspilerAttribute"
                    || attribute.AttributeType.FullName == Affinity + "AffinityFinalizerAttribute")) continue;
                try
                {
                    var patch = patches[0];
                    if (patch.ConstructorArguments.Count < 3 || patch.ConstructorArguments[0].Value is not TypeReference requestedType
                        || requestedType is TypeSpecification || patch.ConstructorArguments[1].Value is not string name
                        || Convert.ToInt32(patch.ConstructorArguments[2].Value) != 0) continue;
                    // Explicit overloads and argument variations require their own signature matching.
                    if (patch.ConstructorArguments.Skip(3).Any(argument => argument.Value != null
                        && (argument.Value is not CustomAttributeArgument[] values || values.Length != 0))) continue;
                    int argumentTypes = -1;
                    for (int i = 3; i < patch.Constructor.Parameters.Count; i++)
                        if (patch.Constructor.Parameters[i].ParameterType.FullName == "System.Type[]") argumentTypes = i;
                    if (argumentTypes < 0 || argumentTypes >= patch.ConstructorArguments.Count) continue;
                    var target = FindInheritedTarget(requestedType, name);
                    if (target == null) continue;
                    var instance = callback.Parameters.FirstOrDefault(parameter => parameter.Name == "__instance");
                    if (instance != null && instance.ParameterType.FullName != requestedType.FullName
                        && instance.ParameterType.FullName != target.DeclaringType.FullName && instance.ParameterType.FullName != "System.Object") continue;
                    Wrap(module, callback, patch, requestedType, target.DeclaringType, instance, argumentTypes);
                    changed = true;
                }
                catch (AssemblyResolutionException error)
                {
                    Logging.Logger.Loader.Debug($"Cannot prepare inherited Affinity target for {callback.FullName}: {error.Message}");
                }
            }
            return changed;
        }

        private static MethodDefinition FindInheritedTarget(TypeReference requested, string name)
        {
            var type = requested.Resolve();
            if (type == null || type.IsValueType || type.IsInterface || type.HasGenericParameters
                || type.Methods.Any(method => method.Name == name)) return null;
            for (var parent = type.BaseType; parent != null && parent is not TypeSpecification; parent = type.BaseType)
            {
                type = parent.Resolve();
                if (type == null || type.HasGenericParameters) return null;
                var candidates = type.Methods.Where(method => method.Name == name).ToArray();
                if (candidates.Length == 0) continue;
                // AccessTools.Method falls back to the parameterless overload on ambiguity.
                return candidates.SingleOrDefault(method => !method.IsStatic && !method.IsAbstract
                    && !method.HasGenericParameters && method.Parameters.Count == 0);
            }
            return null;
        }

        private static void Wrap(ModuleDefinition module, MethodDefinition callback, CustomAttribute patch,
            TypeReference requestedType, TypeReference declaredType, ParameterDefinition originalInstance, int argumentTypes)
        {
            var wrapper = new MethodDefinition("__BSIPA_InheritedAffinity_" + callback.MetadataToken.ToInt32().ToString("x8"),
                (callback.Attributes & MethodAttributes.MemberAccessMask) | MethodAttributes.HideBySig, module.TypeSystem.Void);
            var declared = module.ImportReference(declaredType);
            ParameterDefinition instance = null;
            foreach (var parameter in callback.Parameters)
            {
                var copy = new ParameterDefinition(parameter.Name, parameter.Attributes,
                    parameter == originalInstance ? declared : module.ImportReference(parameter.ParameterType));
                if (parameter.HasConstant) copy.Constant = parameter.Constant;
                wrapper.Parameters.Add(copy);
                if (parameter == originalInstance) instance = copy;
            }
            if (instance == null)
            {
                instance = new ParameterDefinition("__instance", ParameterAttributes.None, declared);
                wrapper.Parameters.Add(instance);
            }
            var il = wrapper.Body.GetILProcessor();
            var done = il.Create(OpCodes.Ret);
            // The base method also runs for sibling types; retain the annotation's original scope.
            il.Emit(OpCodes.Ldarg, instance);
            il.Emit(OpCodes.Isinst, module.ImportReference(requestedType));
            il.Emit(OpCodes.Brfalse, done);
            il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < callback.Parameters.Count; i++)
            {
                il.Emit(OpCodes.Ldarg, wrapper.Parameters[i]);
                if (callback.Parameters[i] == originalInstance)
                    il.Emit(OpCodes.Castclass, module.ImportReference(originalInstance.ParameterType));
            }
            il.Emit(callback.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, callback);
            il.Append(done);
            foreach (var attribute in callback.CustomAttributes.Where(attribute => attribute.AttributeType.Namespace == "SiraUtil.Affinity").ToArray())
            {
                callback.CustomAttributes.Remove(attribute);
                wrapper.CustomAttributes.Add(attribute);
            }
            patch.ConstructorArguments[0] = new CustomAttributeArgument(patch.ConstructorArguments[0].Type, declared);
            // DeclaredMethod does not use Method's parameterless fallback for ambiguous overloads.
            patch.ConstructorArguments[argumentTypes] = new CustomAttributeArgument(
                patch.ConstructorArguments[argumentTypes].Type, Array.Empty<CustomAttributeArgument>());
            callback.DeclaringType.Methods.Add(wrapper);
            Logging.Logger.Loader.Debug($"Prepared inherited Affinity target {requestedType.FullName}.{(string)patch.ConstructorArguments[1].Value} on {declared.FullName}, retaining its instance scope.");
        }
    }
}
