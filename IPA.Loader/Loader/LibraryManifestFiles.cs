using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Newtonsoft.Json.Linq;

namespace IPA.Loader
{
    internal static class LibraryManifestFiles
    {
        private static string Normalize(string value) => string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToUpperInvariant();

        internal static string[] Find(JObject manifest, IEnumerable<string> files)
        {
            var names = new HashSet<string>(new[] { Normalize((string)manifest["id"]), Normalize((string)manifest["name"]) });
            names.Remove("");
            var matches = new List<(string Path, Version Version, int Rank)>();
            foreach (string file in files.Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var module = ModuleDefinition.ReadModule(file);
                    int rank = names.Contains(Normalize(module.Assembly.Name.Name)) ? 3 : 0;
                    if (rank == 0 && module.Assembly.CustomAttributes.Any(a => (a.AttributeType.Name == "AssemblyTitleAttribute" || a.AttributeType.Name == "AssemblyProductAttribute")
                        && a.ConstructorArguments.Count > 0 && names.Contains(Normalize(a.ConstructorArguments[0].Value as string)))) rank = 2;
                    if (rank == 0 && module.GetTypes().Any(t => names.Contains(Normalize((t.Namespace ?? "").Split('.')[0])))) rank = 1;
                    if (rank > 0) matches.Add((file, module.Assembly.Name.Version, rank));
                }
                catch (Exception error) when (error is BadImageFormatException || error is IOException || error is UnauthorizedAccessException) { }
            }
            if (matches.Count == 0) return Array.Empty<string>();
            int highest = matches.Max(m => m.Rank);
            var best = matches.Where(m => m.Rank == highest).ToArray();
            string requested = ((string)manifest["version"] ?? "").Split('-', '+')[0];
            if (Version.TryParse(requested, out var version))
            {
                var exact = best.Where(m => m.Version.Major == version.Major && m.Version.Minor == version.Minor && m.Version.Build == version.Build).ToArray();
                if (exact.Length > 0) best = exact;
            }
            // A bare manifest describes one library; do not invent associations for unrelated IDs.
            return best.OrderByDescending(m => m.Version).ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase).Take(1).Select(m => m.Path).ToArray();
        }

        internal static bool HasLegacyEntry(string path)
        {
            using var module = ModuleDefinition.ReadModule(path);
            return module.GetTypes().Any(t => t.Interfaces.Any(i => i.InterfaceType.FullName == "IPA.Old.IPlugin" || i.InterfaceType.FullName == "IPA.Old.IEnhancedPlugin"
                || i.InterfaceType.FullName == "IllusionPlugin.IPlugin" || i.InterfaceType.FullName == "IllusionPlugin.IEnhancedPlugin"));
        }
    }
}
