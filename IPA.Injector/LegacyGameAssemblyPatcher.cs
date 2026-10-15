using IPA.Config;
using IPA.Injector.Backups;
using IPA.Loader.Compatibility;
using IPA.Logging;
using IPA.Utilities;
using Mono.Cecil;
using System;
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
                    bool changed = false;
                    foreach (var type in module.GetTypes()) changed |= PatchType(type, profile, module);
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

        private static bool PatchType(TypeDefinition type, CompatibilityProfile profile, ModuleDefinition module)
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
                if (!method.IsManaged || !method.IsIL || method.IsStatic
                    || (method.IsVirtual && (!method.IsFinal || !profile.RequiresInParameterModifiers))
                    || method.IsAbstract || method.IsAddOn || method.IsConstructor || method.IsSpecialName
                    || method.IsGenericInstance || method.HasOverrides) continue;
                if (profile.RequiresInParameterModifiers)
                {
                    foreach (var parameter in method.Parameters.Where(parameter => parameter.IsIn))
                    {
                        TypeReference element = parameter.ParameterType;
                        bool hasModifier = false;
                        while (element is IModifierType modifier)
                        {
                            if (element.IsRequiredModifier && modifier.ModifierType.FullName == typeof(System.Runtime.InteropServices.InAttribute).FullName)
                                hasModifier = true;
                            element = modifier.ElementType;
                        }
                        if (!hasModifier)
                            parameter.ParameterType = new RequiredModifierType(module.ImportReference(typeof(System.Runtime.InteropServices.InAttribute)), parameter.ParameterType);
                    }
                }
                method.IsPublic = true;
                if (!method.IsVirtual) method.IsNewSlot = true;
                method.IsVirtual = true;
                method.IsFinal = false;
                method.IsHideBySig = true;
                changed = true;
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
