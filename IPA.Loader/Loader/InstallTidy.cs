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
            if (!SelfConfig.Instance.TidyUp) return;
            Directory.CreateDirectory(UnityGame.ModsPath);
            Directory.CreateDirectory(Path.Combine(UnityGame.ModsPath, "Core"));
            Directory.CreateDirectory(UnityGame.ModLibrariesPath);
            Directory.CreateDirectory(UnityGame.CoreLibraryPath);
            var coreNames = new HashSet<string>(typeof(InstallTidy).Assembly.GetReferencedAssemblies().Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            string index = Path.Combine(UnityGame.CoreLibraryPath, "CoreLibraryIndex.json");
            JObject catalog = File.Exists(index) ? JObject.Parse(File.ReadAllText(index)) : new JObject();
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
            var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var descriptors = new List<Descriptor>();
            var coreIds = new HashSet<string>(SelfConfig.Instance.CoreMods, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string extension = Path.GetExtension(file);
                bool oldLibrary = oldLibraries.Any(directory => Inside(file, directory));
                bool plugin = Inside(file, UnityGame.PluginsPath);
                if (extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                if (extension.Equals(".manifest", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var data = JObject.Parse(File.ReadAllText(file));
                        if (data["id"] == null || data["version"] == null || !(data["files"] is JArray references)) continue;
                        var descriptor = new Descriptor { Source = file, Manifest = data };
                        foreach (var entry in references.Where(p => p.Type == JTokenType.String))
                        {
                            string reference = ((string)entry).Replace('/', Path.DirectorySeparatorChar);
                            descriptor.References[(string)entry] = ResolveOriginal(file, reference);
                        }
                        descriptors.Add(descriptor);
                        if (!Inside(file, UnityGame.ModLibrariesPath)) moves[file] = Path.Combine(UnityGame.ModLibrariesPath, Segment((string)data["id"]), Segment((string)data["version"]), Path.GetFileName(file));
                    }
                    catch (JsonException error) { Logger.Loader.Warn($"Cannot tidy invalid library manifest '{file}': {error.Message}"); }
                    continue;
                }
                if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var identity = AssemblyName.GetAssemblyName(file);
                        string id = EmbeddedId(file);
                        if (id != null && coreIds.Contains(id))
                        {
                            if (!Inside(file, Path.Combine(UnityGame.ModsPath, "Core"))) moves[file] = Path.Combine(UnityGame.ModsPath, "Core", Path.GetFileName(file));
                        }
                        else if (oldLibrary)
                        {
                            string libraryRoot = coreNames.Contains(identity.Name) ? UnityGame.CoreLibraryPath : UnityGame.ModLibrariesPath;
                            moves[file] = Path.Combine(libraryRoot, "Versions", Segment(identity.Name), identity.Version.ToString(), Digest(file).Substring(0, 12), Path.GetFileName(file));
                        }
                        else if (plugin) moves[file] = Path.Combine(UnityGame.ModsPath, Relative(file, UnityGame.PluginsPath));
                    }
                    catch (BadImageFormatException)
                    {
                        if (oldLibrary) moves[file] = Path.Combine(UnityGame.ModLibrariesPath, "Native", Relative(file, oldLibraries.First(directory => Inside(file, directory))));
                        else if (plugin) moves[file] = Path.Combine(UnityGame.ModsPath, Relative(file, UnityGame.PluginsPath));
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    { Logger.Loader.Warn($"Cannot inspect '{file}' for TidyUp: {error.Message}"); }
                }
                else if (plugin) moves[file] = Path.Combine(UnityGame.ModsPath, Relative(file, UnityGame.PluginsPath));
                else if (oldLibrary) moves[file] = Path.Combine(UnityGame.ModLibrariesPath, "LegacyAssets", Relative(file, oldLibraries.First(directory => Inside(file, directory))));
            }
            // Resolve all final paths before updating file lists. Never overwrite a different binary.
            foreach (string source in moves.Keys.ToArray())
            {
                string target = moves[source];
                if (File.Exists(target) && Digest(source) != Digest(target))
                    target = Path.Combine(Path.GetDirectoryName(target), "Versions", Digest(source).Substring(0, 12), Path.GetFileName(target));
                moves[source] = target;
            }
            int moved = 0;
            foreach (var move in moves.ToArray())
            {
                try { MoveVerified(move.Key, move.Value); moved++; }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { moves.Remove(move.Key); Logger.Loader.Warn($"TidyUp will retry '{move.Key}' next launch: {error.Message}"); }
            }
            foreach (var descriptor in descriptors)
            {
                string path = moves.TryGetValue(descriptor.Source, out string movedPath) && File.Exists(movedPath) ? movedPath : descriptor.Source;
                if (!File.Exists(path)) continue;
                var references = new JArray();
                foreach (var reference in descriptor.References)
                {
                    if (Path.GetExtension(reference.Key).Equals(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                    string destination = moves.TryGetValue(reference.Value, out string target) && File.Exists(target) ? target : reference.Value;
                    references.Add(Inside(destination, Install) ? Relative(destination, Install).Replace('\\', '/') : reference.Key);
                }
                descriptor.Manifest["files"] = references;
                string text = descriptor.Manifest.ToString(Formatting.Indented);
                if (JToken.DeepEquals(JObject.Parse(File.ReadAllText(path)), descriptor.Manifest)) continue;
                Archive(path, "Manifests");
                string temporary = path + ".tidy-" + Guid.NewGuid().ToString("N");
                try { File.WriteAllText(temporary, text); File.Replace(temporary, path, null); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            foreach (string source in files.Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                string destination = moves.TryGetValue(source, out string target) && File.Exists(target) ? target : source;
                if (!File.Exists(destination)) continue;
                try { UpdateEmbeddedManifest(source, destination, moves); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is BadImageFormatException)
                { Logger.Loader.Warn($"Cannot update embedded file paths in '{destination}': {error.Message}"); }
            }
            foreach (string file in files.Concat(Walk(UnityGame.CoreLibraryPath)).Where(p => Path.GetExtension(p).Equals(".pdb", StringComparison.OrdinalIgnoreCase)))
            {
                try { Archive(file, "Symbols"); Journal(file, null, Digest(file)); File.Delete(file); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { Logger.Loader.Warn($"Cannot remove debug symbols '{file}': {error.Message}"); }
            }
            foreach (string directory in oldLibraries.Concat(new[] { UnityGame.PluginsPath })) RemoveEmpty(directory);
            LibLoader.SetupAssemblyFilenames(true);
            Logger.Loader.Info($"TidyUp organized {moved} files; manifests retained, debug symbols archived under IPA/Backups/TidyUp.");
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
            return File.Exists(nearby) ? nearby : rooted;
        }
        private static void UpdateEmbeddedManifest(string original, string path, Dictionary<string, string> moves)
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
                    if (Path.GetExtension(relative).Equals(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                    string source = ResolveOriginal(original, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!moves.ContainsKey(source))
                    {
                        string local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(original), relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (moves.ContainsKey(local)) source = local;
                    }
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
