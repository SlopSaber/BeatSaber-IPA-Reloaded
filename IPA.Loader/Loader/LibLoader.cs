#nullable enable
using IPA.AntiMalware;
using IPA.Config;
using IPA.Logging;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace IPA.Loader
{
    internal class CecilLibLoader : BaseAssemblyResolver
    {
        private static readonly string CurrentAssemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        private static readonly string CurrentAssemblyPath = Assembly.GetExecutingAssembly().Location;

        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            LibLoader.SetupAssemblyFilenames();

            if (name.Name == CurrentAssemblyName)
                return AssemblyDefinition.ReadAssembly(CurrentAssemblyPath, parameters);

            string? library = LibLoader.FindLibraryPath(new AssemblyName(name.FullName));
            if (library != null) return AssemblyDefinition.ReadAssembly(library, parameters);


            return base.Resolve(name, parameters);
        }
    }

    internal static class LibLoader
    {
        internal static string LibraryPath => IPA.Utilities.UnityGame.LibraryPath;
        internal static string NativeLibraryPath => IPA.Utilities.UnityGame.NativeLibraryPath;
        internal static Dictionary<string, (string Path, Version Version)> FilenameLocations = null!;
        private sealed class LibraryCandidate
        {
            internal string Path = "";
            internal AssemblyName Identity = null!;
        }
        private static LibraryCandidate[] candidates = Array.Empty<LibraryCandidate>();

        internal static void Configure()
        {
            SetupAssemblyFilenames(true);
            AppDomain.CurrentDomain.AssemblyResolve -= AssemblyLibLoader;
            AppDomain.CurrentDomain.AssemblyResolve += AssemblyLibLoader;
        }

        private static readonly object lockObj = new();

        [ThreadStatic]
        private static bool thisThreadIsInitializing;

        internal static void SetupAssemblyFilenames(bool force = false)
        {
            if (FilenameLocations == null || force)
            {
                lock (lockObj)
                {
                    if (thisThreadIsInitializing)
                    {
                        Log(Logger.Level.Warning, "Library loader initialization recursion detected");
                        return;
                    }
                    if (FilenameLocations is not null && !force) return;

                    thisThreadIsInitializing = true;

                    try
                    {
                        var newFilenameLocations = new Dictionary<string, (string, Version Version)>(StringComparer.OrdinalIgnoreCase);
                        var discovered = new List<LibraryCandidate>();
                        var nativeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        string installPath = IPA.Utilities.UnityGame.InstallPath;
                        var roots = new[] { Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                            Path.Combine(installPath, "Libs"), Path.Combine(installPath, "IPA", "Libs") }.Concat(ModFiles.Roots);
                        var files = ModFiles.Files(roots, "*.dll", true);

                        foreach (var file in files)
                        {
                            var fileInfo = new FileInfo(file);
                            AssemblyName identity;
                            try { identity = AssemblyName.GetAssemblyName(fileInfo.FullName); }
                            catch (BadImageFormatException) { nativeDirectories.Add(fileInfo.DirectoryName!); continue; }
                            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                            { Log(Logger.Level.Warning, $"Cannot inspect library '{fileInfo.FullName}': {error.Message}"); continue; }
                            discovered.Add(new LibraryCandidate { Path = fileInfo.FullName, Identity = identity });
                            foreach (string alias in new[] { fileInfo.Name, identity.Name + ".dll" }.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (!newFilenameLocations.TryGetValue(alias, out var existing) || identity.Version > existing.Version)
                                    newFilenameLocations[alias] = (fileInfo.FullName, identity.Version);
                            }
                        }

                        static void AddDirectoryToPath(string path)
                        {
                            string current = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
                            if (!current.Split(Path.PathSeparator).Any(entry => string.Equals(entry.Trim('"').TrimEnd(Path.DirectorySeparatorChar),
                                path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                                Environment.SetEnvironmentVariable("Path", path + Path.PathSeparator + current);
                        }

                        var nativeRoots = new[] { NativeLibraryPath, Path.Combine(installPath, "Libs", "Native"),
                            Path.Combine(installPath, "IPA", "Libs", "Native") };
                        foreach (string nativeRoot in nativeRoots.Concat(nativeDirectories).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            if (!Directory.Exists(nativeRoot)) continue;
                            AddDirectoryToPath(nativeRoot);
                            foreach (string directory in ModFiles.Directories(new[] { nativeRoot }, true)) AddDirectoryToPath(directory);
                        }

                        FilenameLocations = newFilenameLocations;
                        candidates = discovered.ToArray();

                        _ = LoadLibrary(new AssemblyName("Newtonsoft.Json, Version=12.0.0.0, Culture=neutral"));
                        _ = LoadLibrary(new AssemblyName("netstandard, Version=2.0.0.0, Culture=neutral"));

                    }
                    finally { thisThreadIsInitializing = false; }
                }
            }
        }

        public static Assembly? AssemblyLibLoader(object source, ResolveEventArgs e)
        {
            var asmName = new AssemblyName(e.Name);
            string? requester = e.RequestingAssembly == null || e.RequestingAssembly.IsDynamic ? null : e.RequestingAssembly.Location;
            bool optionalSerializerProbe = asmName.Name.EndsWith(".XmlSerializers", StringComparison.Ordinal)
                && (e.RequestingAssembly == null || !e.RequestingAssembly.GetReferencedAssemblies().Any(reference => reference.Name == asmName.Name));
            return LoadLibrary(asmName, requester, optionalSerializerProbe);
        }

        internal static string? FindLibraryPath(AssemblyName requested, string? requester = null)
        {
            SetupAssemblyFilenames();
            string? directory = string.IsNullOrEmpty(requester) ? null : Path.GetDirectoryName(LoaderCompatibility.OriginalPath(requester!));
            byte[] token = requested.GetPublicKeyToken() ?? Array.Empty<byte>();
            bool NameMatches(LibraryCandidate candidate) => string.Equals(candidate.Identity.Name, requested.Name, StringComparison.OrdinalIgnoreCase)
                || (requested.Name == "0Harmony.Legacy1" && candidate.Identity.Name == "0Harmony" && candidate.Identity.Version.Major < 2);
            bool Nearby(LibraryCandidate candidate) => directory != null
                && candidate.Path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return candidates.Where(candidate => NameMatches(candidate)
                    && (token.Length == 0 || token.SequenceEqual(candidate.Identity.GetPublicKeyToken() ?? Array.Empty<byte>()))
                    && string.Equals(requested.CultureName ?? "", candidate.Identity.CultureName ?? "", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => requested.Version != null && candidate.Identity.Version == requested.Version)
                .ThenByDescending(Nearby).ThenByDescending(candidate => candidate.Identity.Version)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).Select(candidate => candidate.Path).FirstOrDefault();
        }

        internal static Assembly? LoadLibrary(AssemblyName asmName, string? requester = null, bool optionalSerializerProbe = false)
        {
            Log(Logger.Level.Debug, $"Resolving library {asmName}");

            SetupAssemblyFilenames();
            if (asmName.Name == typeof(LibLoader).Assembly.GetName().Name)
            {
                LoaderCompatibility.ValidateTarget(asmName.Version);
                return typeof(LibLoader).Assembly;
            }

            if (asmName.Name == "0Harmony" && asmName.Version != null && asmName.Version.Major < 2)
            {
                asmName = new AssemblyName(asmName.FullName) { Name = "0Harmony.Legacy1" };
            }
            Log(Logger.Level.Debug, $"Looking for file {asmName.Name}.dll");
            string? library = FindLibraryPath(asmName, requester);
            if (library != null)
            {
                Log(Logger.Level.Debug, $"Found library {asmName.Name} as {library}");
                return LoadSafe(library, requester);
            }

            if (optionalSerializerProbe) Log(Logger.Level.Debug, $"Optional XML serializer assembly {asmName} was not found.");
            else Log(Logger.Level.Critical, $"No library {asmName} found");

            return null;
        }

        private static Assembly? LoadSafe(string path, string? requester)
        {
            if (!File.Exists(path))
            {
                Log(Logger.Level.Critical, $"{path} no longer exists!");
                return null;
            }

            if (AntiMalwareEngine.IsInitialized)
            {
                var result = AntiMalwareEngine.Engine.ScanFile(new FileInfo(path));
                if (result is ScanResult.Detected)
                {
                    Log(Logger.Level.Error, $"Scan of '{path}' found malware; not loading");
                    return null;
                }
                if (!SelfConfig.AntiMalware_.RunPartialThreatCode_ && result is not ScanResult.KnownSafe and not ScanResult.NotDetected)
                {
                    Log(Logger.Level.Error, $"Scan of '{path}' found partial threat; not loading. To load this, enable AntiMalware.RunPartialThreatCode in the config.");
                    return null;
                }
            }

            return Assembly.LoadFrom(LoaderCompatibility.PrepareAssembly(path, LoaderCompatibility.ProfileForPath(requester)));
        }

        internal static void Log(Logger.Level lvl, string message)
        { // multiple proxy methods to delay loading of assemblies until it's done
            if (Logger.LogCreated)
            {
                AssemblyLibLoaderCallLogger(lvl, message);
            }
            else
            {
                if (((byte)lvl & (byte)StandardLogger.PrintFilter) != 0)
                    Console.WriteLine($"[{lvl}] {message}");
            }
        }
        internal static void Log(Logger.Level lvl, Exception message)
        { // multiple proxy methods to delay loading of assemblies until it's done
            if (Logger.LogCreated)
            {
                AssemblyLibLoaderCallLogger(lvl, message);
            }
            else
            {
                if (((byte)lvl & (byte)StandardLogger.PrintFilter) != 0)
                    Console.WriteLine($"[{lvl}] {message}");
            }
        }

        private static void AssemblyLibLoaderCallLogger(Logger.Level lvl, string message) => Logger.LibLoader.Log(lvl, message);
        private static void AssemblyLibLoaderCallLogger(Logger.Level lvl, Exception message) => Logger.LibLoader.Log(lvl, message);

    }
}
