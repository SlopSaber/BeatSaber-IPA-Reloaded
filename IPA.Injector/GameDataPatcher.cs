using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IPA.Data;
using IPA.Injector.Backups;
using IPA.Logging;
using IPA.Utilities;
using Mono.Cecil;

namespace IPA.Injector
{
    internal static class GameDataPatcher
    {
        internal static void Apply()
        {
            string managed = Path.Combine(GameVersionEarly.ResolveDataPath(UnityGame.InstallPath), "Managed");
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managed);
            var modules = new List<ModuleDefinition>();
            try
            {
                foreach (string path in Directory.GetFiles(managed, "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name == "mscorlib" || name == "netstandard" || name == "Microsoft.CSharp"
                        || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Unity", StringComparison.Ordinal)
                        || name.StartsWith("Mono.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                        || name.StartsWith("IPA.", StringComparison.Ordinal) || name.StartsWith("I18N", StringComparison.Ordinal)) continue;
                    try { modules.Add(ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true, AssemblyResolver = resolver })); }
                    catch (BadImageFormatException) { /* Native libraries have no managed persistence calls. */ }
                }
                var profile = SaveDataProfile.Read(modules, UnityGame.GameVersion.ToString());
                if (profile.Files.Count == 0) Logger.Injector.Warn("Unknown native save schema: save paths remain version-isolated; typed field conversion is unavailable.");
                string originalAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Hyperbolic Magnetism", "Beat Saber");
                SaveDataStore.Initialize(UnityGame.AppDataPath, UnityGame.UserDataPath, originalAppData, profile);
                var changes = modules.Where(SaveDataRewriter.Rewrite).ToList();
                foreach (var module in changes)
                    if (AppDomain.CurrentDomain.GetAssemblies().Any(a => !a.IsDynamic && a.GetName().Name == module.Assembly.Name.Name))
                        throw new InvalidOperationException($"Cannot redirect saves in loaded assembly {module.Name}. Fully restart with BSIPA enabled before launching the game.");
                string backupRoot = Path.Combine(UnityGame.InstallPath, "IPA", "Backups", "GameDataRouting");
                var backup = new BackupUnit(backupRoot, Utils.CurrentTime().ToString("yyyy-MM-dd_HH-mm-ss") + "-" + Guid.NewGuid().ToString("N"));
                foreach (var module in changes)
                {
                    string path = module.FileName;
                    string temporary = path + ".bsipa-data-" + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        module.Write(temporary); backup.Add(path); File.Replace(temporary, path, null);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                Logger.Injector.Info($"Installed game-data routing in {changes.Count} managed assemblies; originals backed up under IPA/Backups/GameDataRouting.");
            }
            finally { foreach (var module in modules) module.Dispose(); }
        }
    }
}
