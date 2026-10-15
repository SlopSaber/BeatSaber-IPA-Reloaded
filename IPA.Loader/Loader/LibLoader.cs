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

            if (LibLoader.FilenameLocations.TryGetValue($"{name.Name}.dll", out var assemblyInfo))
            {
                if (File.Exists(assemblyInfo.Path))
                    return AssemblyDefinition.ReadAssembly(assemblyInfo.Path, parameters);
            }
            else if (LibLoader.FilenameLocations.TryGetValue($"{name.Name}.{name.Version}.dll", out assemblyInfo))
            {
                if (File.Exists(assemblyInfo.Path))
                    return AssemblyDefinition.ReadAssembly(assemblyInfo.Path, parameters);
            }


            return base.Resolve(name, parameters);
        }
    }

    internal static class LibLoader
    {
        internal static string LibraryPath => IPA.Utilities.UnityGame.LibraryPath;
        internal static string NativeLibraryPath => IPA.Utilities.UnityGame.NativeLibraryPath;
        internal static Dictionary<string, (string Path, Version Version)> FilenameLocations = null!;

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

                    var newFilenameLocations = new Dictionary<string, (string, Version Version)>();

                    string installPath = IPA.Utilities.UnityGame.InstallPath;
                    bool IncludeLibraryDirectory(string path) => !string.Equals(Path.GetFileName(path), ".cache", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Path.GetFileName(path), "Native", StringComparison.OrdinalIgnoreCase);
                    var roots = new[] { Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                        Path.Combine(installPath, "Libs"), Path.Combine(installPath, "IPA", "Libs"), IPA.Utilities.UnityGame.PluginsPath };
                    var files = roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists)
                        .SelectMany(root => TraverseTree(root, IncludeLibraryDirectory));

                    foreach (var fileInfo in files)
                    {
                        if (!fileInfo.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var versionInfo = FileVersionInfo.GetVersionInfo(fileInfo.FullName).FileVersion;
                        if (versionInfo == null || !Version.TryParse(versionInfo, out var fileVersion))
                        {
                            fileVersion = new Version(0, 0, 0, 0);
                        }

                        if (!newFilenameLocations.TryGetValue(fileInfo.Name, out var assemblyInfo) || fileVersion > assemblyInfo.Version)
                        {
                            newFilenameLocations[fileInfo.Name] = (fileInfo.FullName, fileVersion);
                        }
                        else
                        {
                            Log(Logger.Level.Notice, $"Multiple instances of {fileInfo.Name} exist! Ignoring {fileInfo.FullName}");
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
                    foreach (string nativeRoot in nativeRoots.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!Directory.Exists(nativeRoot)) continue;
                        AddDirectoryToPath(nativeRoot);
                        _ = TraverseTree(nativeRoot, dir =>
                        { // this is a terrible hack for iterating directories
                            AddDirectoryToPath(dir); return true;
                        }).All(f => true); // force it to iterate all
                    }

                    FilenameLocations = newFilenameLocations;

                    _ = LoadLibrary(new AssemblyName("Newtonsoft.Json, Version=12.0.0.0, Culture=neutral"));
                    _ = LoadLibrary(new AssemblyName("netstandard, Version=2.0.0.0, Culture=neutral"));

                    thisThreadIsInitializing = false;
                }
            }
        }

        public static Assembly? AssemblyLibLoader(object source, ResolveEventArgs e)
        {
            var asmName = new AssemblyName(e.Name);
            return LoadLibrary(asmName);
        }

        internal static Assembly? LoadLibrary(AssemblyName asmName)
        {
            Log(Logger.Level.Debug, $"Resolving library {asmName}");

            SetupAssemblyFilenames();

            if (asmName.Name == "0Harmony" && asmName.Version != null && asmName.Version.Major < 2)
            {
                asmName = new AssemblyName(asmName.FullName) { Name = "0Harmony.Legacy1" };
            }
            var testFile = $"{asmName.Name}.dll";
            Log(Logger.Level.Debug, $"Looking for file {asmName.Name}.dll");

            if (FilenameLocations.TryGetValue(testFile, out var assemblyInfo))
            {
                Log(Logger.Level.Debug, $"Found file {testFile} as {assemblyInfo.Path}");
                return LoadSafe(assemblyInfo.Path);
            }
            else if (FilenameLocations.TryGetValue(testFile = $"{asmName.Name}.{asmName.Version}.dll", out assemblyInfo))
            {
                Log(Logger.Level.Debug, $"Found file {testFile} as {assemblyInfo.Path}");
                Log(Logger.Level.Warning, $"File {testFile} should be renamed to just {asmName.Name}.dll");
                return LoadSafe(assemblyInfo.Path);
            }

            Log(Logger.Level.Critical, $"No library {asmName} found");

            return null;
        }

        private static Assembly? LoadSafe(string path)
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

            return Assembly.LoadFrom(LoaderCompatibility.PrepareAssembly(path));
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

        // https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/file-system/how-to-iterate-through-a-directory-tree
        private static IEnumerable<FileInfo> TraverseTree(string root, Func<string, bool>? dirValidator = null)
        {
            if (dirValidator == null) dirValidator = s => true;

            var dirs = new Stack<string>(32);

            if (!Directory.Exists(root))
                throw new ArgumentException("Directory does not exist", nameof(root));
            dirs.Push(root);

            while (dirs.Count > 0)
            {
                string currentDir = dirs.Pop();
                string[] subDirs;
                try
                {
                    subDirs = Directory.GetDirectories(currentDir);
                }
                catch (UnauthorizedAccessException)
                { continue; }
                catch (DirectoryNotFoundException)
                { continue; }

                string[] files;
                try
                {
                    files = Directory.GetFiles(currentDir);
                }
                catch (UnauthorizedAccessException)
                { continue; }
                catch (DirectoryNotFoundException)
                { continue; }

                foreach (string str in subDirs)
                    if (dirValidator(str)) dirs.Push(str);

                foreach (string file in files)
                {
                    FileInfo nextValue;
                    try
                    {
                        nextValue = new FileInfo(file);
                    }
                    catch (FileNotFoundException)
                    { continue; }

                    yield return nextValue;
                }
            }
        }
    }
}
