using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Hive.Versioning;
using IPA.Logging;
using Mono.Cecil;

namespace IPA.Loader
{
    internal static class LoaderCompatibility
    {
        private static readonly VersionRange LegacyVersions = new(">=3.0.0-0 <5.0.0-0");
        private static readonly Dictionary<string, string> PreparedFiles = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object PreparationLock = new();

        internal static bool MatchesDependency(PluginMetadata plugin, PluginMetadata dependency, VersionRange requirement)
        {
            if (requirement.Matches(dependency.HVersion)) return true;
            if (!dependency.IsSelf || (requirement & LegacyVersions) == VersionRange.Nothing) return false;

            Logger.Loader.Info($"'{plugin.Id}' uses BSIPA 3/4 API compatibility for '{requirement}' on BSIPA {dependency.HVersion}.");
            return true;
        }

        internal static string PrepareAssembly(string file)
        {
            file = Path.GetFullPath(file);
            string instance = IPA.Utilities.UnityGame.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!file.StartsWith(instance, StringComparison.OrdinalIgnoreCase)) return file;

            lock (PreparationLock)
            {
                if (PreparedFiles.TryGetValue(file, out string prepared)) return prepared;
                byte[] bytes = File.ReadAllBytes(file);
                using var input = new MemoryStream(bytes, false);
                using var resolver = new CecilLibLoader();
                resolver.AddSearchDirectory(Path.GetDirectoryName(file));
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                        resolver.AddSearchDirectory(Path.GetDirectoryName(assembly.Location));
                using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver });
                if (!IPA.Compat3.Runtime.Rewrite(module))
                {
                    PreparedFiles[file] = file;
                    return file;
                }

                using var hash = SHA256.Create();
                string digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
                string directory = Path.Combine(IPA.Utilities.UnityGame.PluginsPath, ".cache", "Compatibility",
                    typeof(LoaderCompatibility).Module.ModuleVersionId.ToString("N"), digest);
                Directory.CreateDirectory(directory);
                prepared = Path.Combine(directory, Path.GetFileName(file));
                if (!File.Exists(prepared))
                {
                    string temporary = prepared + ".tmp";
                    try { module.Write(temporary); File.Move(temporary, prepared); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                PreparedFiles[file] = prepared;
                Logger.Loader.Info($"Prepared legacy loader references in {Path.GetFileName(file)}; original unchanged.");
                return prepared;
            }
        }
    }
}
