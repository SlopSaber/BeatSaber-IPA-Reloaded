using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Hive.Versioning;
using IPA.Logging;
using Mono.Cecil;

namespace IPA.Loader
{
    internal static class LoaderCompatibility
    {
        private static readonly Hive.Versioning.Version Legacy3Version = new("3.13.7");
        private static readonly Hive.Versioning.Version Legacy4Version = new("4.3.7");
        private static readonly Hive.Versioning.Version CurrentVersion = new(new System.Version(IPA.Config.SelfConfig.IPAVersion).ToString(3));
        private static readonly Dictionary<string, string> PreparedFiles = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object PreparationLock = new();

        internal static bool MatchesDependency(PluginMetadata plugin, PluginMetadata dependency, VersionRange requirement)
        {
            if (!dependency.IsSelf) return requirement.Matches(dependency.HVersion);

            var compatibleVersion = plugin.TargetIpaVersion ?? dependency.HVersion;
            if (!requirement.Matches(compatibleVersion)) return false;

            if (!requirement.Matches(dependency.HVersion))
                Logger.Loader.Info($"'{plugin.Id}' uses BSIPA {compatibleVersion} API compatibility for '{requirement}' on BSIPA {dependency.HVersion}.");
            return true;
        }

        internal static void SelectProfile(PluginMetadata plugin)
        {
            var binary = plugin.TargetLoaderVersion;
            ValidateTarget(binary);
            bool legacy = binary?.Major == 1 || plugin.IsInterface3;
            plugin.Manifest.Dependencies.TryGetValue("BSIPA", out var requirement);
            var requested = plugin.Manifest.Misc?.IpaVersion;
            if (requested != null)
            {
                var maximum = requested.Major == 3 ? Legacy3Version : requested.Major == 4 ? Legacy4Version : CurrentVersion;
                if (requested.Major < 3 || requested.Major > 5 || requested.CompareTo(maximum) > 0
                    || (legacy && requested.Major != 3)
                    || (!legacy && binary != null && requested.Major == 3))
                    throw new NotSupportedException($"BSIPA API profile {requested} is incompatible with binary contract {binary}.");
                plugin.TargetIpaVersion = requested;
            }
            else
            {
                var candidates = legacy ? new[] { Legacy3Version }
                    : binary != null ? new[] { CurrentVersion, Legacy4Version }
                    : new[] { CurrentVersion, Legacy4Version, Legacy3Version };
                plugin.TargetIpaVersion = candidates.FirstOrDefault(version => requirement == null || requirement.Matches(version));
                if (plugin.TargetIpaVersion == null)
                    throw new NotSupportedException($"No BSIPA API profile satisfies '{requirement}' for binary contract {binary}. Declare misc.ipa-version for an older exact API target.");
            }
            if (requirement != null && !requirement.Matches(plugin.TargetIpaVersion))
                throw new NotSupportedException($"BSIPA API profile {plugin.TargetIpaVersion} does not satisfy '{requirement}'.");
        }

        internal static System.Version GetTargetVersion(ModuleDefinition module)
            => module.AssemblyReferences.FirstOrDefault(reference => reference.Name == "IPA.Loader")?.Version;

        internal static void ValidateTarget(System.Version target)
        {
            var current = typeof(LoaderCompatibility).Assembly.GetName().Version;
            if (target != null && target > current)
                throw new NotSupportedException($"Plugin targets IPA.Loader {target}, newer than the installed {current}.");
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
                ValidateTarget(GetTargetVersion(module));
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
