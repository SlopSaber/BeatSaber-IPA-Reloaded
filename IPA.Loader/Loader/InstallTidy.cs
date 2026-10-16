using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using IPA.Config;
using IPA.Logging;
using IPA.Utilities;
using Mono.Cecil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IPA.Loader
{
    internal static class InstallTidy
    {
        private sealed class Descriptor
        {
            internal string Source;
            internal JObject Manifest;
            internal readonly Dictionary<string, string> References = new(StringComparer.Ordinal);
        }
        private static string Install => UnityGame.InstallPath;
        private static string Backup => Path.Combine(Install, "IPA", "Backups", "TidyUp");

        internal static void Apply()
        {
            bool tidy = SelfConfig.Instance.TidyUp;
            JObject libraryIndex = ModLibraryIndex.Read();
            Directory.CreateDirectory(UnityGame.ModsPath);
            if (tidy) Directory.CreateDirectory(Path.Combine(UnityGame.ModsPath, "Core"));
            Directory.CreateDirectory(UnityGame.ModLibrariesPath);
            Directory.CreateDirectory(UnityGame.CoreLibraryPath);
            if (!tidy) RestoreSidecars(libraryIndex);
            string index = Path.Combine(UnityGame.CoreLibraryPath, "CoreLibraryIndex.json");
            JObject catalog = File.Exists(index) ? JObject.Parse(File.ReadAllText(index)) : new JObject();
            bool releaseLayoutComplete = ((int?)catalog["format"] ?? 1) >= 2
                && (catalog["providers"] as JArray ?? new JArray()).OfType<JObject>().All(provider => File.Exists(Path.Combine(UnityGame.CoreLibraryPath, (string)provider["path"])));
            foreach (string layout in tidy && releaseLayoutComplete ? new[] { "NuGet", "Upstream" } : Array.Empty<string>())
            {
                string oldLayout = Path.Combine(UnityGame.CoreLibraryPath, "Versions", layout);
                foreach (string file in Walk(oldLayout))
                {
                    try { MoveVerified(file, Path.Combine(Backup, "CoreLibraries", Relative(file, UnityGame.CoreLibraryPath))); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { Logger.Loader.Warn($"Old core-library layout cleanup deferred: {error.Message}"); }
                }
                RemoveEmpty(oldLayout);
            }
            if (tidy && releaseLayoutComplete && ((int?)catalog["format"] ?? 1) >= 3)
            {
                var canonicalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var verifiedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JObject provider in (catalog["providers"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    string file = Path.GetFullPath(Path.Combine(UnityGame.CoreLibraryPath, (string)provider["path"]));
                    if (!Inside(file, UnityGame.CoreLibraryPath)) continue;
                    canonicalFiles.Add(file);
                    try
                    {
                        string hash = Digest(file);
                        if (string.Equals(hash, (string)provider["sha256"], StringComparison.OrdinalIgnoreCase)) verifiedHashes.Add(hash);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    { Logger.Loader.Warn($"Core-library duplicate cleanup deferred: {error.Message}"); }
                }
                string versions = Path.Combine(UnityGame.CoreLibraryPath, "Versions");
                foreach (string file in Walk(versions).Where(file => Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase) && !canonicalFiles.Contains(file)))
                {
                    try
                    {
                        if (verifiedHashes.Contains(Digest(file)))
                            MoveVerified(file, Path.Combine(Backup, "CoreLibraries", Relative(file, UnityGame.CoreLibraryPath)));
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    { Logger.Loader.Warn($"Core-library duplicate cleanup deferred: {error.Message}"); }
                }
                RemoveEmpty(versions);
            }
            var coreNames = new HashSet<string>(typeof(InstallTidy).Assembly.GetReferencedAssemblies().Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            foreach (string name in (JArray)catalog["coreAssemblyNames"] ?? new JArray()) coreNames.Add(name);
            foreach (JObject provider in (catalog["providers"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string file = Path.GetFullPath(Path.Combine(UnityGame.CoreLibraryPath, (string)provider["path"]));
                if (!Inside(file, UnityGame.CoreLibraryPath) || !File.Exists(file)) continue;
                try { coreNames.Add(AssemblyName.GetAssemblyName(file).Name); }
                catch (BadImageFormatException) { }
            }
            var oldLibraries = new[] { Path.Combine(Install, "Libs"), Path.Combine(Install, "IPA", "Libs") };
            var roots = new[] { UnityGame.PluginsPath, UnityGame.ModsPath }.Concat(oldLibraries).ToArray();
            var files = roots.SelectMany(Walk).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var describedLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Plan manifests first, so file-less legacy descriptors can identify their DLLs by metadata.
            files = files.OrderBy(file => file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
            var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var descriptors = new List<Descriptor>();
            foreach (JObject entry in (JArray)libraryIndex["libraries"])
            {
                var descriptor = new Descriptor { Source = ModLibraryIndex.ManifestPath((string)entry["name"]), Manifest = (JObject)entry["manifest"].DeepClone() };
                foreach (string reference in (JArray)descriptor.Manifest["files"] ?? new JArray())
                {
                    string resolved = ResolveOriginal(descriptor.Source, reference.Replace('/', Path.DirectorySeparatorChar));
                    descriptor.References[reference] = resolved;
                    describedLibraries.Add(resolved);
                }
                descriptors.Add(descriptor);
            }
            var coreIds = new HashSet<string>(SelfConfig.Instance.CoreMods, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string extension = Path.GetExtension(file);
                bool oldLibrary = oldLibraries.Any(directory => Inside(file, directory));
                bool plugin = Inside(file, UnityGame.PluginsPath);
                bool modLibrary = Inside(file, UnityGame.ModLibrariesPath) && !Inside(file, Path.Combine(UnityGame.ModLibrariesPath, "Native"));
                string nativeRoot = oldLibraries.Select(directory => Path.Combine(directory, "Native")).FirstOrDefault(directory => Inside(file, directory));
                if (nativeRoot != null) { moves[file] = Path.Combine(UnityGame.ModLibrariesPath, "Native", Relative(file, nativeRoot)); continue; }
                if (tidy && extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) && !modLibrary && !oldLibrary) continue;
                if (IsManifest(file) || extension.Equals(".json", StringComparison.OrdinalIgnoreCase) && !Inside(file, Path.Combine(UnityGame.ModLibrariesPath, "Native")))
                {
                    try
                    {
                        var data = JObject.Parse(File.ReadAllText(file));
                        if (data["id"] == null || data["version"] == null || !IsManifest(file) && (data["name"] == null || data["author"] == null || data["description"] == null))
                        {
                            if (modLibrary || oldLibrary) moves[file] = Path.Combine(UnityGame.ModLibrariesPath, Path.GetFileName(file));
                            else if (tidy && plugin) moves[file] = Path.Combine(UnityGame.ModsPath, Relative(file, UnityGame.PluginsPath));
                            continue;
                        }
                        var references = data["files"] as JArray ?? new JArray();
                        if (references.Count == 0)
                            foreach (string library in LibraryManifestFiles.Find(data, files)) references.Add(Relative(library, Install).Replace('\\', '/'));
                        data["files"] = references;
                        var descriptor = new Descriptor { Source = file, Manifest = data };
                        foreach (var entry in references.Where(p => p.Type == JTokenType.String))
                        {
                            string reference = ((string)entry).Replace('/', Path.DirectorySeparatorChar);
                            descriptor.References[(string)entry] = ResolveOriginal(file, reference);
                            describedLibraries.Add(descriptor.References[(string)entry]);
                        }
                        descriptors.RemoveAll(existing => (string)existing.Manifest["id"] == (string)data["id"] && (string)existing.Manifest["version"] == (string)data["version"]);
                        descriptors.Add(descriptor);
                        moves[file] = Path.Combine(UnityGame.ModLibrariesPath, Path.GetFileName(file));
                    }
                    catch (JsonException error)
                    {
                        if (IsManifest(file)) Logger.Loader.Warn($"Cannot tidy invalid library manifest '{file}': {error.Message}");
                        else if (modLibrary || oldLibrary) moves[file] = Path.Combine(UnityGame.ModLibrariesPath, Path.GetFileName(file));
                        else if (tidy && plugin) moves[file] = Path.Combine(UnityGame.ModsPath, Relative(file, UnityGame.PluginsPath));
                    }
                    continue;
                }
                if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var identity = AssemblyName.GetAssemblyName(file);
                        string id = EmbeddedId(file);
                        if (tidy && id != null && coreIds.Contains(id))
                        {
                            if (!Inside(file, Path.Combine(UnityGame.ModsPath, "Core"))) moves[file] = Path.Combine(UnityGame.ModsPath, "Core", Path.GetFileName(file));
                        }
                        else if (modLibrary || oldLibrary || describedLibraries.Contains(file) && id == null && !LibraryManifestFiles.HasLegacyEntry(file))
                        {
                            string libraryRoot = coreNames.Contains(identity.Name) ? UnityGame.CoreLibraryPath : UnityGame.ModLibrariesPath;
                            moves[file] = libraryRoot == UnityGame.ModLibrariesPath
                                ? Path.Combine(libraryRoot, Path.GetFileName(file))
                                : Path.Combine(libraryRoot, "Versions", Segment(identity.Name), identity.Version.ToString(), Digest(file).Substring(0, 12), Path.GetFileName(file));
                        }
                        else if (tidy && plugin) moves[file] = Path.Combine(UnityGame.ModsPath, Relative(file, UnityGame.PluginsPath));
                    }
                    catch (BadImageFormatException)
                    {
                        if (oldLibrary) moves[file] = Path.Combine(UnityGame.ModLibrariesPath, "Native", Relative(file, oldLibraries.First(directory => Inside(file, directory))));
                        else if (tidy && plugin) moves[file] = Path.Combine(UnityGame.ModsPath, Relative(file, UnityGame.PluginsPath));
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    { Logger.Loader.Warn($"Cannot inspect '{file}' for TidyUp: {error.Message}"); }
                }
                else if (modLibrary || oldLibrary) moves[file] = Path.Combine(UnityGame.ModLibrariesPath, Path.GetFileName(file));
                else if (tidy && plugin) moves[file] = Path.Combine(UnityGame.ModsPath, Relative(file, UnityGame.PluginsPath));
            }
            // Resolve all final paths before updating file lists. Never overwrite a different binary.
            var reservedTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in moves.Keys.ToArray())
            {
                string target = moves[source];
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) { moves.Remove(source); continue; }
                string hash = Digest(source);
                if (File.Exists(target) && hash != Digest(target) || reservedTargets.TryGetValue(target, out string reserved) && reserved != hash)
                    target = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + "." + hash + Path.GetExtension(target));
                reservedTargets[target] = hash;
                moves[source] = target;
            }
            int moved = 0;
            foreach (var move in moves.ToArray())
            {
                try { MoveVerified(move.Key, move.Value); moved++; }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { moves.Remove(move.Key); Logger.Loader.Warn($"TidyUp will retry '{move.Key}' next launch: {error.Message}"); }
            }
            var indexedLibraries = new JArray();
            var removals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var indexedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var manifestPaths = new HashSet<string>(descriptors.Select(descriptor => moves.TryGetValue(descriptor.Source, out string path) && File.Exists(path) ? path : descriptor.Source), StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in descriptors)
            {
                string path = moves.TryGetValue(descriptor.Source, out string movedPath) && File.Exists(movedPath) ? movedPath : descriptor.Source;
                var references = new JArray();
                foreach (var reference in descriptor.References)
                {
                    string destination = moves.TryGetValue(reference.Value, out string target) && File.Exists(target) ? target : reference.Value;
                    if (tidy && Inside(destination, UnityGame.ModLibrariesPath) && !Inside(destination, Path.Combine(UnityGame.ModLibrariesPath, "Native")) && (IsRemovableSidecar(destination) || manifestPaths.Contains(destination))) continue;
                    references.Add(Inside(destination, Install) ? Relative(destination, Install).Replace('\\', '/') : reference.Key);
                }
                descriptor.Manifest["files"] = references;
                string text = descriptor.Manifest.ToString(Formatting.Indented);
                string name = Path.GetFileName(path);
                string identity = (string)descriptor.Manifest["id"] + "\0" + (string)descriptor.Manifest["version"];
                if (indexedNames.TryGetValue(name, out string existingIdentity) && existingIdentity != identity)
                    name = ManifestCollisionName(name, text);
                if (!tidy)
                {
                    string flat = ModLibraryIndex.ManifestPath(name);
                    if (File.Exists(flat) && !JToken.DeepEquals(JObject.Parse(File.ReadAllText(flat)), descriptor.Manifest)
                        && ((string)JObject.Parse(File.ReadAllText(flat))["id"] != (string)descriptor.Manifest["id"]
                            || (string)JObject.Parse(File.ReadAllText(flat))["version"] != (string)descriptor.Manifest["version"]))
                    {
                        name = ManifestCollisionName(name, text);
                        flat = ModLibraryIndex.ManifestPath(name);
                    }
                    if (!File.Exists(flat) || !JToken.DeepEquals(JObject.Parse(File.ReadAllText(flat)), descriptor.Manifest))
                    {
                        if (File.Exists(flat)) Archive(flat, "Manifests");
                        WriteJson(flat, descriptor.Manifest);
                    }
                }
                else if (File.Exists(path))
                {
                    Archive(path, "Manifests");
                    removals[path] = Digest(path);
                }
                indexedNames[name] = identity;
                indexedLibraries.Add(new JObject { ["name"] = name, ["manifest"] = descriptor.Manifest });
            }
            libraryIndex["libraries"] = indexedLibraries;
            if (tidy)
                foreach (string file in Walk(UnityGame.ModLibrariesPath).Where(file => !Inside(file, Path.Combine(UnityGame.ModLibrariesPath, "Native")) && !IsManifest(file) && IsRemovableSidecar(file)))
                {
                    Archive(file, "Sidecars");
                    string hash = Digest(file);
                    var sidecars = (JArray)libraryIndex["sidecars"];
                    if (!sidecars.OfType<JObject>().Any(entry => (string)entry["name"] == Path.GetFileName(file) && (string)entry["sha256"] == hash))
                        sidecars.Add(new JObject { ["name"] = Path.GetFileName(file), ["sha256"] = hash, ["backup"] = Relative(Path.Combine(Backup, "Sidecars", hash), Install).Replace('\\', '/') });
                    removals[file] = hash;
                }
            if (File.Exists(ModLibraryIndex.FilePath)) Archive(ModLibraryIndex.FilePath, "LibraryIndexes");
            ModLibraryIndex.Write(libraryIndex);
            foreach (var removal in removals)
            {
                try
                {
                    if (!File.Exists(removal.Key) || Digest(removal.Key) != removal.Value) continue;
                    Journal(removal.Key, null, removal.Value);
                    File.Delete(removal.Key);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { Logger.Loader.Warn($"Sidecar cleanup deferred for '{removal.Key}': {error.Message}"); }
            }
            foreach (string source in files.Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                string destination = moves.TryGetValue(source, out string target) && File.Exists(target) ? target : source;
                if (!File.Exists(destination)) continue;
                try { UpdateEmbeddedManifest(source, destination, moves, removals); }
                catch (BadImageFormatException) { /* Native DLLs have no managed manifest. */ }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { Logger.Loader.Warn($"Cannot update embedded file paths in '{destination}': {error.Message}"); }
            }
            foreach (string file in (tidy ? files.Concat(Walk(UnityGame.CoreLibraryPath)) : Array.Empty<string>()).Where(p => File.Exists(p) && Path.GetExtension(p).Equals(".pdb", StringComparison.OrdinalIgnoreCase)))
            {
                try { Archive(file, "Symbols"); Journal(file, null, Digest(file)); File.Delete(file); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { Logger.Loader.Warn($"Cannot remove debug symbols '{file}': {error.Message}"); }
            }
            foreach (string directory in oldLibraries.Concat(new[] { UnityGame.PluginsPath })) RemoveEmpty(directory);
            RemoveEmpty(UnityGame.ModLibrariesPath);
            LibLoader.SetupAssemblyFilenames(true);
            Logger.Loader.Info(tidy ? $"TidyUp organized {moved} files; library metadata indexed, sidecars backed up under IPA/Backups/TidyUp."
                : $"Library layout flattened {moved} files; manifests and documentation retained.");
        }

        private static bool IsRemovableSidecar(string path)
        {
            string extension = Path.GetExtension(path);
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(path)) return false;
                try
                {
                    using var reader = System.Xml.XmlReader.Create(path, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
                    return reader.MoveToContent() == System.Xml.XmlNodeType.Element && reader.LocalName == "doc";
                }
                catch (System.Xml.XmlException) { return false; }
            }
            bool text = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".md", StringComparison.OrdinalIgnoreCase);
            bool documentation = text && new[] { "LICENSE", "NOTICE", "COPYING", "README", "CHANGELOG", "AUTHORS" }.Any(prefix => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) || documentation
                || path.EndsWith(".dll.mdb", StringComparison.OrdinalIgnoreCase) || IsManifest(path);
        }

        private static bool IsManifest(string path) => path.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase);

        private static string ManifestCollisionName(string name, string text)
        {
            using var hash = SHA256.Create();
            return Path.GetFileNameWithoutExtension(name) + "." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant() + Path.GetExtension(name);
        }

        private static void WriteJson(string path, JObject data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tidy-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, data.ToString(Formatting.Indented));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void RestoreSidecars(JObject index)
        {
            foreach (JObject entry in (JArray)index["sidecars"])
            {
                string backup = Path.GetFullPath(Path.Combine(Install, (string)entry["backup"]));
                if (!Inside(backup, Backup) || !File.Exists(backup) || Digest(backup) != (string)entry["sha256"]) continue;
                string path = ModLibraryIndex.ManifestPath((string)entry["name"]);
                if (File.Exists(path) && Digest(path) != (string)entry["sha256"])
                    path = Path.Combine(UnityGame.ModLibrariesPath, Path.GetFileNameWithoutExtension(path) + "." + (string)entry["sha256"] + Path.GetExtension(path));
                if (!File.Exists(path)) File.Copy(backup, path);
                if (Digest(path) != (string)entry["sha256"]) throw new IOException("Restored library sidecar failed its content hash check.");
            }
        }

        private static IEnumerable<string> Walk(string directory)
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) yield break;
            foreach (string file in Directory.GetFiles(directory)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (string child in Directory.GetDirectories(directory))
            {
                string name = Path.GetFileName(child);
                if (name == ".compatibility" || name == ".cache" || name == "Backups") continue;
                foreach (string file in Walk(child)) yield return file;
            }
        }
        private static string ResolveOriginal(string owner, string reference)
        {
            string rooted = Path.GetFullPath(Path.Combine(Install, reference));
            if (File.Exists(rooted) || Path.IsPathRooted(reference)) return rooted;
            string nearby = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(owner), reference));
            if (File.Exists(nearby)) return nearby;
            if (Path.GetExtension(reference).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                string library = LibLoader.FindLibraryPath(new AssemblyName(Path.GetFileNameWithoutExtension(reference)), owner);
                if (library != null) return library;
            }
            return rooted;
        }
        private static void UpdateEmbeddedManifest(string original, string path, Dictionary<string, string> moves, Dictionary<string, string> removals)
        {
            using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true });
            bool changed = false;
            foreach (var resource in module.Resources.OfType<EmbeddedResource>().Where(r => r.Name.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                using var reader = new StreamReader(resource.GetResourceStream());
                JObject manifest;
                try { manifest = JObject.Parse(reader.ReadToEnd()); } catch (JsonException) { continue; }
                if (!(manifest["files"] is JArray entries)) continue;
                var files = new JArray();
                foreach (var entry in entries)
                {
                    if (entry.Type != JTokenType.String) { files.Add(entry.DeepClone()); continue; }
                    string relative = (string)entry;
                    string source = ResolveOriginal(original, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!moves.ContainsKey(source))
                    {
                        string local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(original), relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (moves.ContainsKey(local)) source = local;
                    }
                    if (removals.ContainsKey(source) || moves.TryGetValue(source, out string removedTarget) && removals.ContainsKey(removedTarget)) continue;
                    files.Add(moves.TryGetValue(source, out string target) && File.Exists(target) ? Relative(target, Install).Replace('\\', '/') : relative);
                }
                if (JToken.DeepEquals(entries, files)) continue;
                manifest["files"] = files;
                int index = module.Resources.IndexOf(resource);
                module.Resources[index] = new EmbeddedResource(resource.Name, resource.Attributes, Encoding.UTF8.GetBytes(manifest.ToString(Formatting.Indented)));
                changed = true;
            }
            if (!changed) return;
            Archive(path, "Manifests");
            string temporary = path + ".tidy-" + Guid.NewGuid().ToString("N");
            try { module.Write(temporary); File.Replace(temporary, path, null); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static string EmbeddedId(string file)
        {
            using var module = ModuleDefinition.ReadModule(file);
            foreach (var resource in module.Resources.OfType<EmbeddedResource>().Where(r => r.Name.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)))
            {
                using var reader = new StreamReader(resource.GetResourceStream());
                try { return (string)JObject.Parse(reader.ReadToEnd())["id"]; } catch (JsonException) { }
            }
            return null;
        }
        private static bool Inside(string file, string directory) => Path.GetFullPath(file).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static string Relative(string file, string directory) => Path.GetFullPath(file).Substring(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).Length + 1);
        private static string Segment(string value) => string.Concat((value ?? "Unknown").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        private static string Digest(string file) { using var hash = SHA256.Create(); using var stream = File.OpenRead(file); return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static void MoveVerified(string source, string target)
        {
            if (!Inside(source, Install) || !Inside(target, Install)) throw new IOException("TidyUp only moves files inside the installation.");
            for (string directory = Path.GetDirectoryName(target); Inside(directory, Install); directory = Path.GetDirectoryName(directory))
                if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("TidyUp leaves linked destination directories untouched.");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (!File.Exists(target)) File.Copy(source, target);
            string digest = Digest(source);
            if (digest != Digest(target)) throw new IOException("A moved file failed its content hash check.");
            Journal(source, target, digest);
            File.Delete(source); // A locked assembly remains as a retryable duplicate until next launch.
        }
        private static void Archive(string path, string category)
        {
            string directory = Path.Combine(Backup, category); Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, Digest(path));
            if (!File.Exists(target)) File.Copy(path, target);
            if (Digest(path) != Digest(target)) throw new IOException("A tidy backup failed its content hash check.");
        }
        private static void Journal(string source, string target, string digest)
        {
            Directory.CreateDirectory(Backup);
            File.AppendAllText(Path.Combine(Backup, "operations.jsonl"), new JObject { ["source"] = Relative(source, Install), ["target"] = target == null ? null : Relative(target, Install), ["sha256"] = digest }.ToString(Formatting.None) + Environment.NewLine);
        }
        private static void RemoveEmpty(string directory)
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string child in Directory.GetDirectories(directory)) RemoveEmpty(child);
            if (Inside(directory, Install) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
