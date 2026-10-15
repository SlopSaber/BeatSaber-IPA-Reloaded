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
        private static readonly ConcurrentDictionary<string, Compatibility.CompatibilityProfile> ProfilesByPath = new(StringComparer.OrdinalIgnoreCase);
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

        internal static void SelectProfile(PluginMetadata plugin, ModuleDefinition module = null, bool log = true)
        {
            var binary = plugin.TargetLoaderVersion;
            ValidateTarget(binary);
            bool legacy = binary?.Major == 1 || plugin.IsInterface3;
            plugin.Manifest.Dependencies.TryGetValue("BSIPA", out var requirement);
            var requested = plugin.Manifest.Misc?.IpaVersion;
            var minimum = Compatibility.CompatibilityProfiles.MinimumBinaryVersion(module, plugin.PluginType);
            if (!legacy && plugin.Manifest.GameVersion == null
                && (minimum == null || minimum.CompareTo(new Hive.Versioning.Version("4.2.1")) < 0))
                minimum = new Hive.Versioning.Version("4.2.1");
            bool oldFeatures = module != null && IPA.Compat3.Runtime.UsesLegacyFeatureContract(module);
            var candidates = Compatibility.CompatibilityProfiles.Releases.Where(profile =>
                (legacy ? profile.Version.Major == 3 : binary != null ? profile.Version.Major >= 4 : true)
                && (requirement == null || requirement.Matches(profile.Version))
                && (minimum == null || profile.Version.CompareTo(minimum) >= 0)
                && (!oldFeatures || profile.UsesLegacyFeatures)).ToArray();

            Compatibility.CompatibilityProfile selected;
            if (plugin.IsSelf)
            {
                selected = Compatibility.CompatibilityProfiles.Current;
                plugin.CompatibilitySelection = "installed loader";
            }
            else if (requested != null)
            {
                selected = candidates.FirstOrDefault(profile => profile.Version.CompareTo(requested) == 0)
                    ?? throw new NotSupportedException($"Recorded BSIPA profile {requested} is incompatible with binary contract {binary}, its API signatures, or dependency '{requirement}'.");
                plugin.CompatibilitySelection = "explicit misc.ipa-version";
            }
            else
            {
                if (candidates.Length == 0)
                    throw new NotSupportedException($"No recorded BSIPA profile satisfies '{requirement}' and binary contract {binary} (minimum API {minimum}, legacy features {oldFeatures}).");
                string game = plugin.Manifest.GameVersion?.ToString() ?? IPA.Utilities.UnityGame.GameVersion?.ToString();
                var anchor = Compatibility.CompatibilityProfiles.GameBaseline(game);
                if (anchor == null)
                {
                    anchor = requirement != null ? candidates.OrderBy(profile => profile.Version).First().Version
                        : legacy || plugin.Manifest.Features is IPA.JsonConverters.FeaturesFieldConverter.LegacyFeatureDictionary
                            ? candidates.Where(profile => profile.UsesLegacyFeatures).Select(profile => profile.Version).FirstOrDefault()
                            : Compatibility.CompatibilityProfiles.Current.Version;
                }
                selected = candidates.Where(profile => anchor != null && profile.Version.CompareTo(anchor) <= 0)
                    .OrderByDescending(profile => profile.Version).FirstOrDefault()
                    ?? candidates.OrderBy(profile => profile.Version).First();
                plugin.CompatibilitySelection = $"game {game ?? "unspecified"}, release baseline {anchor?.ToString() ?? "unspecified"}, minimum binary API {minimum?.ToString() ?? "unspecified"}";
            }

            if (selected.RequiresGameVersion && plugin.Manifest.GameVersion == null)
                throw new Newtonsoft.Json.JsonSerializationException($"BSIPA {selected.Version} requires a gameVersion in the manifest.");
            if (plugin.Manifest.Id == null)
            {
                if (!selected.AllowsNullId)
                    throw new Newtonsoft.Json.JsonSerializationException($"BSIPA {selected.Version} requires a non-null plugin id.");
                if (log) Logger.Loader.Warn($"Plugin '{plugin.Manifest.Name}' has a null legacy ID; using its name under BSIPA {selected.Version}.");
                plugin.Manifest.Id = plugin.Manifest.Name;
            }
            if (!selected.UsesLegacyFeatures && plugin.Manifest.Features is IPA.JsonConverters.FeaturesFieldConverter.LegacyFeatureDictionary old)
            {
                if (log && old.Count != 0)
                    Logger.Features.Warn($"'{plugin.Manifest.Id}' uses BSIPA {selected.Version}, whose feature system ignores legacy feature arrays.");
                old.Clear();
            }
            plugin.CompatibilityProfile = selected;
            plugin.TargetIpaVersion = selected.Version;
            if (plugin.File != null) ProfilesByPath[plugin.File.FullName] = selected;
            if (log) Logger.Loader.Debug($"Selected BSIPA {selected.Version} for '{plugin.Id}': {plugin.CompatibilitySelection}.");
        }

        internal static Compatibility.CompatibilityProfile ProfileForPath(string path)
            => path != null && ProfilesByPath.TryGetValue(OriginalPath(path), out var profile) ? profile : null;

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

        // Keep JSON/profile discovery outside the bootstrap path that loads Newtonsoft.Json itself.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Compatibility.CompatibilityProfile ResolveAssemblyProfile(string file, ModuleDefinition module,
            Compatibility.CompatibilityProfile profile)
        {
            if (ProfileForPath(file) == null)
            {
                var resource = module.Resources.OfType<EmbeddedResource>().FirstOrDefault(entry => entry.Name.EndsWith(".manifest.json", StringComparison.Ordinal));
                if (resource != null)
                {
                    using var reader = new StreamReader(resource.GetResourceStream());
                    var metadata = new PluginMetadata { File = new FileInfo(file), TargetLoaderVersion = GetTargetVersion(module),
                        IsInterface3 = IPA.Compat3.Runtime.IsInterfacePlugin(module) };
                    metadata.Manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginManifest>(reader.ReadToEnd())
                        ?? throw new Newtonsoft.Json.JsonSerializationException($"Null manifest in {file}.");
                    SelectProfile(metadata, module, false);
                    profile = metadata.CompatibilityProfile;
                }
            }
            var minimum = Compatibility.CompatibilityProfiles.MinimumBinaryVersion(module);
            if (profile != null && minimum != null && profile.Version.CompareTo(minimum) < 0)
                profile = Compatibility.CompatibilityProfiles.Releases.Where(candidate => candidate.Version.CompareTo(minimum) >= 0)
                    .OrderBy(candidate => candidate.Version).First();
            if (profile != null && !profile.UsesLegacyFeatures && IPA.Compat3.Runtime.UsesLegacyFeatureContract(module))
                profile = Compatibility.CompatibilityProfiles.Releases.First(candidate => candidate.Version.Major == 4 && candidate.UsesLegacyFeatures);
            return profile;
        }

        internal static string PrepareAssembly(string file, Compatibility.CompatibilityProfile inheritedProfile = null)
        {
            file = Path.GetFullPath(file);
            string instance = IPA.Utilities.UnityGame.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!file.StartsWith(instance, StringComparison.OrdinalIgnoreCase)) return file;

            lock (PreparationLock)
            {
                var profile = ProfileForPath(file) ?? inheritedProfile;
                string key = file + "|" + profile?.Version;
                if (PreparedFiles.TryGetValue(key, out string prepared)) return prepared;
                byte[] bytes = File.ReadAllBytes(file);
                using var input = new MemoryStream(bytes, false);
                using var resolver = new CecilLibLoader();
                resolver.AddSearchDirectory(Path.GetDirectoryName(file));
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                        resolver.AddSearchDirectory(Path.GetDirectoryName(assembly.Location));
                using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver });
                ValidateTarget(GetTargetVersion(module));
                if (GetTargetVersion(module) != null)
                    profile = ResolveAssemblyProfile(file, module, profile);
                else profile = null;
                key = file + "|" + profile?.Version;
                if (PreparedFiles.TryGetValue(key, out prepared)) return prepared;
                if (!IPA.Compat3.Runtime.Rewrite(module, profile))
                {
                    PreparedFiles[key] = file;
                    return file;
                }

                using var hash = SHA256.Create();
                string digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
                string source = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(file.ToUpperInvariant()))).Replace("-", "");
                string directory = Path.Combine(IPA.Utilities.UnityGame.PluginsPath, ".cache", "Compatibility",
                    typeof(LoaderCompatibility).Module.ModuleVersionId.ToString("N"), profile?.Version.ToString() ?? "binary", source, digest);
                Directory.CreateDirectory(directory);
                prepared = Path.Combine(directory, Path.GetFileName(file));
                if (!File.Exists(prepared))
                {
                    string temporary = prepared + ".tmp";
                    try { module.Write(temporary); File.Move(temporary, prepared); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                PreparedFiles[key] = prepared;
                RegisterOriginalPath(prepared, file);
                Logger.Loader.Info($"Prepared loader references in {Path.GetFileName(file)} with BSIPA profile {profile?.Version.ToString() ?? "binary signatures"}; original unchanged.");
                return prepared;
            }
        }
    }
}
