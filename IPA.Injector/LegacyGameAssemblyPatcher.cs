using IPA.Config;
using IPA.Injector.Backups;
using IPA.Loader.Compatibility;
using IPA.Logging;
using IPA.Utilities;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IPA.Injector
{
    internal static class LegacyGameAssemblyPatcher
    {
        private static readonly string[] FallbackAssemblies = { "Assembly-CSharp.dll" };

        internal static void Apply(CompatibilityProfile profile)
        {
            string managedPath = Path.Combine(GameVersionEarly.ResolveDataPath(UnityGame.InstallPath), "Managed");
            string backupRoot = Path.Combine(UnityGame.InstallPath, "IPA", "Backups", "LegacyCompatibility");
            var backup = new BackupUnit(backupRoot, Utils.CurrentTime().ToString("yyyy-MM-dd_HH-mm-ss") + "-" + Guid.NewGuid().ToString("N"));
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managedPath);
            foreach (string name in SelfConfig.GameAssemblies_.Concat(FallbackAssemblies).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name
                    || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                string path = Path.Combine(managedPath, name);
                if (!File.Exists(path)) continue;

                try
                {
                    using var module = ModuleDefinition.ReadModule(path, new ReaderParameters
                    {
                        InMemory = true,
                        AssemblyResolver = resolver
                    });
                    using var original = FindOriginal(path, backupRoot, module);
                    bool changed = false;
                    foreach (var type in module.GetTypes()) changed |= PatchType(type, profile, module, original);
                    if (!changed) continue;
                    if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => !assembly.IsDynamic
                        && assembly.GetName().Name == module.Assembly.Name.Name))
                    {
                        Logger.Injector.Error($"Cannot restore legacy access in {name}: the game assembly is already loaded. Fully restart the game with the updated loader.");
                        continue;
                    }

                    // Keep a unique original backup and replace the file only after writing succeeds.
                    string temporary = path + ".bsipa-legacy-" + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        module.Write(temporary);
                        backup.Add(path);
                        File.Replace(temporary, path, null);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                    Logger.Injector.Info($"Restored BSIPA {profile.Version} game-member access and virtual methods in {name}; original backed up under {backupRoot}/{backup.Name}.");
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                    || error is BadImageFormatException || error is AssemblyResolutionException)
                {
                    Logger.Injector.Error($"Could not restore legacy game-member access in {name}: {error}");
                }
            }
        }

        private static ModuleDefinition FindOriginal(string path, string backupRoot, ModuleDefinition current)
        {
            if (!Directory.Exists(backupRoot)) return null;
            string relative = Utils.GetRelativePath(path, UnityGame.InstallPath);
            foreach (string directory in Directory.EnumerateDirectories(backupRoot).OrderBy(value => value, StringComparer.Ordinal))
            {
                string candidate = Path.Combine(directory, relative);
                if (!File.Exists(candidate)) continue;
                try
                {
                    var original = ModuleDefinition.ReadModule(candidate, new ReaderParameters { InMemory = true });
                    if (original.Mvid == current.Mvid && original.Assembly.Name.FullName == current.Assembly.Name.FullName) return original;
                    original.Dispose();
                }
                catch (Exception error) when (error is IOException || error is BadImageFormatException)
                {
                    Logger.Injector.Debug($"Cannot read virtualization baseline '{candidate}': {error.Message}");
                }
            }
            return null;
        }

        private static TypeReference AddInModifier(TypeReference type, TypeReference original, ModuleDefinition module)
        {
            while (type is IModifierType existing) type = existing.ElementType;
            var optional = new List<TypeReference>();
            var required = new List<TypeReference>();
            while (original is IModifierType modifier)
            {
                var imported = module.ImportReference(modifier.ModifierType);
                if (original.IsOptionalModifier) optional.Add(imported);
                else required.Add(imported);
                original = modifier.ElementType;
            }
            if (!required.Any(modifier => modifier.FullName == typeof(System.Runtime.InteropServices.InAttribute).FullName))
                required.Add(module.ImportReference(typeof(System.Runtime.InteropServices.InAttribute)));
            // Match the historical virtualizer's signature encoding: required
            // modifiers inside optional modifiers, retaining each group's order.
            foreach (var modifier in required) type = new RequiredModifierType(modifier, type);
            foreach (var modifier in optional) type = new OptionalModifierType(modifier, type);
            return type;
        }

        private static bool PatchType(TypeDefinition type, CompatibilityProfile profile, ModuleDefinition module, ModuleDefinition original)
        {
            // Static classes, interfaces and value types cannot acquire instance virtual slots.
            if (type.IsInterface || type.IsValueType || type.Name == "<Module>"
                || type.Name == "SceneControl" || type.Name == "ConfigUI") return false;
            bool changed = false;
            if (type.IsSealed && !type.IsAbstract) { type.IsSealed = false; changed = true; }
            if (type.IsNestedPrivate && profile.RequiresInParameterModifiers)
            {
                type.IsNestedPublic = true;
                changed = true;
            }
            if (type.IsAbstract) return changed;
            foreach (var method in type.Methods)
            {
                if (!method.IsManaged || !method.IsIL || method.IsStatic || method.IsAbstract || method.IsAddOn
                    || method.IsConstructor || method.IsSpecialName || method.IsGenericInstance || method.HasOverrides) continue;
                // Earlier passes have already cleared final/private flags. The
                // matching original identifies which methods need an upgraded recipe.
                var baseline = original?.LookupToken(method.MetadataToken) as MethodDefinition;
                if (baseline == null || baseline.Name != method.Name || baseline.DeclaringType.FullName != type.FullName
                    || baseline.Parameters.Count != method.Parameters.Count || baseline.GenericParameters.Count != method.GenericParameters.Count)
                    baseline = method;
                if (!baseline.IsManaged || !baseline.IsIL || baseline.IsStatic
                    || (baseline.IsVirtual && (!baseline.IsFinal || !profile.RequiresInParameterModifiers))
                    || baseline.IsAbstract || baseline.IsAddOn || baseline.IsConstructor || baseline.IsSpecialName
                    || baseline.IsGenericInstance || baseline.HasOverrides) continue;
                if (profile.RequiresInParameterModifiers)
                {
                    foreach (var parameter in method.Parameters.Where(parameter => parameter.IsIn))
                    {
                        var modified = AddInModifier(parameter.ParameterType, baseline.Parameters[parameter.Index].ParameterType, module);
                        if (modified.FullName == parameter.ParameterType.FullName) continue;
                        parameter.ParameterType = modified;
                        changed = true;
                    }
                }
                var attributes = method.Attributes;
                method.IsPublic = true;
                method.IsNewSlot = true;
                method.IsVirtual = true;
                method.IsFinal = false;
                method.IsHideBySig = true;
                changed |= attributes != method.Attributes;
            }
            foreach (var field in type.Fields.Where(field => field.IsPrivate))
            {
                field.IsFamily = true;
                changed = true;
            }
            return changed;
        }
    }
}
