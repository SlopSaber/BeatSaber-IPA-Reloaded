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
        private static Dictionary<string, PreparedEntry> preparedIndex;

        // Preparation output depends only on the file contents, its path, the selected profile and this loader
        // build, so a file whose size and write time are unchanged can reuse the earlier result without being
        // read through Cecil again.
        private sealed class PreparedEntry
        {
            public long Length;
            public long WriteTicks;
            public string Profile;
            public string Prepared;
            public string Registered;
        }

        private static string PreparedIndexPath => Path.Combine(IPA.Utilities.UnityGame.CompatibilityCachePath,
            typeof(LoaderCompatibility).Module.ModuleVersionId.ToString("N"), "prepared-index.txt");

        private static Dictionary<string, PreparedEntry> PreparedIndex
        {
            get
            {
                if (preparedIndex != null) return preparedIndex;
                preparedIndex = new Dictionary<string, PreparedEntry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(PreparedIndexPath))
                        foreach (string line in File.ReadAllLines(PreparedIndexPath))
                        {
                            string[] fields = line.Split('\t');
                            if (fields.Length != 6 || !long.TryParse(fields[1], out long length) || !long.TryParse(fields[2], out long ticks))
                                continue;
                            preparedIndex[fields[0]] = new PreparedEntry
                                { Length = length, WriteTicks = ticks, Profile = fields[3], Prepared = fields[4], Registered = fields[5] };
                        }
                }
                catch (Exception e)
                {
                    Logger.Loader.Debug($"Ignoring the prepared assembly index: {e.Message}");
                }
                return preparedIndex;
            }
        }

        private static bool TryReusePrepared(string file, string indexKey, FileInfo info, out string result)
        {
            result = null;
            if (!PreparedIndex.TryGetValue(indexKey, out var entry) || entry.Length != info.Length || entry.WriteTicks != info.LastWriteTimeUtc.Ticks)
                return false;
            if (entry.Registered.Length != 0 && !ProfilesByPath.ContainsKey(file))
            {
                var registered = FindProfile(entry.Registered);
                if (registered == null) return false;
                ProfilesByPath[file] = registered;
            }
            string key = file + "|" + entry.Profile;
            if (entry.Prepared.Length == 0)
            {
                PreparedFiles[key] = file;
                result = file;
                return true;
            }
            if (!File.Exists(entry.Prepared)) return false;
            PreparedFiles[key] = entry.Prepared;
            RegisterOriginalPath(entry.Prepared, file);
            Logger.Loader.Debug($"Reused prepared loader references for {Path.GetFileName(file)}.");
            result = entry.Prepared;
            return true;
        }

        private static string RegisteredProfile(string file, bool wasRegistered)
            => !wasRegistered && ProfilesByPath.TryGetValue(file, out var registered) ? registered.Version.ToString() : "";

        private static Compatibility.CompatibilityProfile FindProfile(string version)
            => Compatibility.CompatibilityProfiles.Current.Version.ToString() == version
                ? Compatibility.CompatibilityProfiles.Current
                : Compatibility.CompatibilityProfiles.Releases.FirstOrDefault(profile => profile.Version.ToString() == version);

        private static void RecordPrepared(string indexKey, FileInfo info, string profile, string prepared, string registered)
        {
            var entry = new PreparedEntry
                { Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks, Profile = profile, Prepared = prepared, Registered = registered };
            PreparedIndex[indexKey] = entry;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PreparedIndexPath));
                File.AppendAllText(PreparedIndexPath,
                    $"{indexKey}\t{entry.Length}\t{entry.WriteTicks}\t{profile}\t{prepared}\t{registered}\n");
            }
            catch (Exception e)
            {
                Logger.Loader.Debug($"Could not record prepared assembly {Path.GetFileName(prepared)}: {e.Message}");
            }
        }

        internal static string OriginalPath(string path) => OriginalPaths.TryGetValue(path, out var original) ? original : path;
        internal static void RegisterOriginalPath(string prepared, string original) => OriginalPaths[prepared] = original;

        internal static bool MatchesDependency(PluginMetadata plugin, PluginMetadata dependency, VersionRange requirement)
        {
            if (!dependency.IsSelf) return requirement.Matches(dependency.HVersion)
                || dependency.RuntimeVersions.Any(requirement.Matches);

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

        internal static Compatibility.CompatibilityProfile LegacyGameAssemblyProfile()
        {
            Compatibility.CompatibilityProfile selected = null;
            foreach (string file in ModFiles.PluginFiles)
            {
                try
                {
                    using var module = ModuleDefinition.ReadModule(file);
                    var target = GetTargetVersion(module);
                    Compatibility.CompatibilityProfile profile = null;
                    if (target != null)
                    {
                        ValidateTarget(target);
                        profile = ResolveAssemblyProfile(file, module, null);
                    }
                    if (profile == null && module.GetTypes().Any(type => !type.IsAbstract && !type.IsInterface
                        && type.Interfaces.Any(implemented => implemented.InterfaceType.FullName == "IPA.Old.IPlugin"
                            || implemented.InterfaceType.FullName == "IPA.Old.IEnhancedPlugin"
                            || implemented.InterfaceType.FullName == "IllusionPlugin.IPlugin"
                            || implemented.InterfaceType.FullName == "IllusionPlugin.IEnhancedPlugin"
                            || implemented.InterfaceType.FullName == "IPA.IBeatSaberPlugin"
                            || implemented.InterfaceType.FullName == "IPA.IEnhancedBeatSaberPlugin")))
                        profile = Compatibility.CompatibilityProfiles.Releases.First(candidate => candidate.Version.Major == 3);
                    if (profile?.RequiresGameAssemblyPatches == true
                        && (selected == null || profile.Version.CompareTo(selected.Version) > 0)) selected = profile;
                }
                catch (BadImageFormatException) { }
                catch (Exception error)
                {
                    // Normal metadata loading reports invalid plugins; one probe must not abort bootstrap.
                    Logger.Loader.Debug($"Cannot infer legacy game patches from '{Path.GetFileName(file)}': {error.Message}");
                }
            }
            return selected;
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
                string indexKey = key;
                var info = new FileInfo(file);
                if (TryReusePrepared(file, indexKey, info, out prepared)) return prepared;
                bool wasRegistered = ProfilesByPath.ContainsKey(file);
                byte[] bytes = File.ReadAllBytes(file);
                using var input = new MemoryStream(bytes, false);
                using var resolver = new CecilLibLoader();
                resolver.AddSearchDirectory(Path.GetDirectoryName(file));
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                        resolver.AddSearchDirectory(Path.GetDirectoryName(assembly.Location));
                using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver });
                if (module.AssemblyReferences.Any(reference => reference.Name == "SiraUtil"))
                    foreach (string modDirectory in ModFiles.Directories(ModFiles.Roots))
                        resolver.AddSearchDirectory(modDirectory);
                ValidateTarget(GetTargetVersion(module));
                if (GetTargetVersion(module) != null)
                    profile = ResolveAssemblyProfile(file, module, profile);
                else profile = null;
                key = file + "|" + profile?.Version;
                if (PreparedFiles.TryGetValue(key, out prepared))
                {
                    RecordPrepared(indexKey, info, profile?.Version.ToString() ?? "", prepared == file ? "" : prepared, RegisteredProfile(file, wasRegistered));
                    return prepared;
                }
                using var hash = SHA256.Create();
                string digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
                // A single full-length hash keeps source/content isolation without deep cache paths.
                string cacheKey = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(file.ToUpperInvariant() + "\0" + digest))).Replace("-", "");
                string directory = Path.Combine(IPA.Utilities.UnityGame.CompatibilityCachePath,
                    typeof(LoaderCompatibility).Module.ModuleVersionId.ToString("N"), profile?.Version.ToString() ?? "binary", cacheKey);
                prepared = Path.Combine(directory, Path.GetFileName(file));
                if (!File.Exists(prepared))
                {
                    bool compatibilityChanged = LegacyPluginRewriter.Rewrite(module);
                    compatibilityChanged |= LegacyAffinityRewriter.Rewrite(module);
                    compatibilityChanged |= IPA.Compat3.Runtime.Rewrite(module, profile);
                    compatibilityChanged |= MonoRuntimeRewriter.Rewrite(module);
                    bool dataRoutingChanged = IPA.Data.SaveDataRewriter.Rewrite(module);
                    if (!compatibilityChanged && !dataRoutingChanged)
                    {
                        PreparedFiles[key] = file;
                        RecordPrepared(indexKey, info, profile?.Version.ToString() ?? "", "", RegisteredProfile(file, wasRegistered));
                        return file;
                    }

                    Directory.CreateDirectory(directory);
                    string temporary = prepared + ".tmp";
                    try { module.Write(temporary); File.Move(temporary, prepared); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                PreparedFiles[key] = prepared;
                RegisterOriginalPath(prepared, file);
                RecordPrepared(indexKey, info, profile?.Version.ToString() ?? "", prepared, RegisteredProfile(file, wasRegistered));
                Logger.Loader.Info($"Prepared loader references in {Path.GetFileName(file)} with BSIPA profile {profile?.Version.ToString() ?? "binary signatures"}; original unchanged.");
                return prepared;
            }
        }
    }
}
