using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
        private static readonly Hive.Versioning.Version[] Legacy3Profiles = ("3.13.7 3.13.5 3.13.2 3.13.1 3.13.0 "
            + "3.12.24 3.12.23 3.12.22 3.12.21 3.12.20 3.12.19 3.12.18 3.12.17 3.12.16 3.12.15 3.12.14 3.12.13 3.12.12 3.12.11 3.12.10 3.12.9 3.12.8 3.12.7 3.12.6 3.12.5 3.12.4 3.12.3 3.12.2 3.12.1 3.12.0 "
            + "3.11.6 3.11.5 3.11.4 3.11.3 3.11.0 3.10.5 3.10.4 3.10.3 3.10.2 3.10.0 3.9.1 3.9.0 3.8.9 3.8.8 3.8.7 3.8.6 3.8.5 3.8.2 3.8.1 3.8.0 3.7.2 3.7.1 3.7.0 3.6.3 3.6.2 3.6.1 3.6.0 3.5.2 3.5.0 3.4.0")
            .Split(' ').Select(version => new Hive.Versioning.Version(version)).ToArray();
        private static readonly Hive.Versioning.Version[] Legacy4Profiles = ("4.3.7 4.3.6 4.3.5 4.3.4 4.3.3 4.3.2 4.3.1 4.3.0 4.2.1 4.2.0 4.1.6 4.1.4 4.1.3 4.1.2 4.1.1 4.1.0 4.0.5 4.0.4 4.0.3 4.0.2")
            .Split(' ').Select(version => new Hive.Versioning.Version(version)).ToArray();
        private static readonly Dictionary<string, string> PreparedFiles = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object PreparationLock = new();
        private static readonly ConcurrentDictionary<string, string> OriginalPaths = new(StringComparer.OrdinalIgnoreCase);

        internal static string OriginalPath(string path) => OriginalPaths.TryGetValue(path, out var original) ? original : path;
        internal static void RegisterOriginalPath(string prepared, string original) => OriginalPaths[prepared] = original;

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
                var candidates = legacy ? Legacy3Profiles
                    : requirement == null && plugin.Manifest.Features is IPA.JsonConverters.FeaturesFieldConverter.LegacyFeatureDictionary
                        ? Legacy4Profiles
                    : binary != null ? new[] { CurrentVersion }.Concat(Legacy4Profiles)
                    : new[] { CurrentVersion }.Concat(Legacy4Profiles).Concat(Legacy3Profiles);
                plugin.TargetIpaVersion = candidates.FirstOrDefault(version => requirement == null || requirement.Matches(version));
                if (plugin.TargetIpaVersion == null)
                    throw new NotSupportedException($"No BSIPA API profile satisfies '{requirement}' for binary contract {binary}. Declare misc.ipa-version for an older exact API target.");
            }
            if (requirement != null && !requirement.Matches(plugin.TargetIpaVersion))
                throw new NotSupportedException($"BSIPA API profile {plugin.TargetIpaVersion} does not satisfy '{requirement}'.");
        }

        internal static System.Version GetTargetVersion(ModuleDefinition module)
            => module.AssemblyReferences.FirstOrDefault(reference => reference.Name == "IPA.Loader")?.Version;

        internal static System.Version GetTargetVersion(TypeReference type)
        {
            while (type.DeclaringType != null) type = type.DeclaringType;
            return type.Scope is AssemblyNameReference assembly && assembly.Name == "IPA.Loader" ? assembly.Version : null;
        }

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
                string source = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(file.ToUpperInvariant()))).Replace("-", "");
                string directory = Path.Combine(IPA.Utilities.UnityGame.PluginsPath, ".cache", "Compatibility",
                    typeof(LoaderCompatibility).Module.ModuleVersionId.ToString("N"), source, digest);
                Directory.CreateDirectory(directory);
                prepared = Path.Combine(directory, Path.GetFileName(file));
                if (!File.Exists(prepared))
                {
                    string temporary = prepared + ".tmp";
                    try { module.Write(temporary); File.Move(temporary, prepared); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                PreparedFiles[file] = prepared;
                RegisterOriginalPath(prepared, file);
                Logger.Loader.Info($"Prepared legacy loader references in {Path.GetFileName(file)}; original unchanged.");
                return prepared;
            }
        }
    }
}
