#nullable enable
using IPA.AntiMalware;
using IPA.Config;
using IPA.Injector.Backups;
using IPA.Loader;
using IPA.Logging;
using IPA.Utilities;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using static IPA.Logging.Logger;

namespace IPA.Injector
{
    /// <summary>
    /// The entry point type for BSIPA's Doorstop injector.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    internal static class Injector
    {
        private static Task? pluginAsyncLoadTask;
        private static Task? permissionFixTask;
        //private static string otherNewtonsoftJson = null;

        // ReSharper disable once UnusedParameter.Global
        internal static void Main(string[] args)
        { // entry point for doorstop
          // At this point, literally nothing but mscorlib is loaded,
          // and since this class doesn't have any static fields that
          // aren't defined in mscorlib, we can control exactly what
          // gets loaded.
            _ = args;
            try
            {
                args = Environment.GetCommandLineArgs();
                CommandLineParser.ParseArguments(args);

                SetupLibraryLoading();

                // this is weird, but it prevents Mono from having issues loading the type.
                // IMPORTANT: NO CALLS TO ANY LOGGER CAN HAPPEN BEFORE THIS
                var unused = StandardLogger.PrintFilter;
                #region // Above hack explanation
                /*
                 * Due to an unknown bug in the version of Mono that Unity uses, if the first access to StandardLogger
                 * is a call to a constructor, then Mono fails to load the type correctly. However, if the first access is to
                 * the above static property (or maybe any, but I don't really know) it behaves as expected and works fine.
                 */
                #endregion

                Default.Debug("Initializing logger");

                if (AntiPiracy.IsInvalid(UnityGame.InstallPath, out var invalidFile))
                {
                    Default.Error($"Unsupported installation file '{Utils.GetRelativePath(invalidFile, UnityGame.InstallPath)}'; BSIPA startup stopped.");

                    return;
                }

                WriteDebugLogs(args);

                EnsureDirectories();
                PendingMods.Apply();
                Loader.LibLoader.SetupAssemblyFilenames(true);

                SelfConfig.Load();
                InstallTidy.Apply();
                DisabledConfig.Load();

                Logging.Logger.Injector.Debug("Prepping bootstrapper");

                // make sure to load the game version and check boundaries before installing the bootstrap, because that uses the game assemblies property
                GameVersionEarly.Load();
                SelfConfig.Instance.CheckVersionBoundary();

                PluginLoader.BeginMetadataScan();
                DesktopVrPatcher.Apply();
                GameDataPatcher.Apply();

                var legacyGameProfile = LoaderCompatibility.LegacyGameAssemblyProfile();
                if (legacyGameProfile != null) LegacyGameAssemblyPatcher.Apply(legacyGameProfile);

                // updates backup
                InstallBootstrapPatch();

                AntiMalwareEngine.Initialize();

                Loader.LibLoader.SetupAssemblyFilenames(true);

                pluginAsyncLoadTask = PluginLoader.LoadTask();
                permissionFixTask = PermissionFix.FixPermissions(new DirectoryInfo(UnityGame.InstallPath));
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private static void WriteDebugLogs(string[] args)
        {
            var runtime = OpenXRHelper.GetOpenXRRuntime();
            Default.Debug($"Command-line: \"{string.Join(" ", args)}\"");
            Default.Debug($"Current time: {DateTimeOffset.Now}");
            Default.Debug($"Current culture: {CultureInfo.CurrentCulture.Name}");
            Default.Debug($"Operating system: {Environment.OSVersion.VersionString}; CLR {Environment.Version}; {IntPtr.Size * 8}-bit process.");
            Default.Debug($"Current OpenXR runtime: {runtime ?? "null"}, exists: {File.Exists(runtime)}");
        }

        private static void EnsureDirectories()
        {
            Directory.CreateDirectory(Path.Combine(UnityGame.ModsPath, "Core"));
            string path;
            if (!Directory.Exists(path = Path.Combine(UnityGame.InstallPath, "UserData")))
                _ = Directory.CreateDirectory(path);
        }

        private static void SetupLibraryLoading()
        {
            if (loadingDone) return;
            loadingDone = true;
            Loader.LibLoader.Configure();
        }

        private static void InstallBootstrapPatch()
        {
            var sw = Stopwatch.StartNew();

            var cAsmName = Assembly.GetExecutingAssembly().GetName();
            var dataPath = GameVersionEarly.ResolveDataPath(UnityGame.InstallPath);
            var managedPath = Path.Combine(dataPath, "Managed");

            var dataDir = Path.GetFileName(dataPath);
            var gameName = dataDir.Substring(0, dataDir.Length - 5);

            Logging.Logger.Injector.Debug("Finding backup");
            var backupPath = Path.Combine(UnityGame.InstallPath, "IPA", "Backups", gameName);
            var bkp = BackupManager.FindLatestBackup(backupPath);
            if (bkp == null)
                Logging.Logger.Injector.Debug("No existing bootstrap backup; a backup will be created before modifying the engine assembly.");

            var readerParameters = new ReaderParameters
            {
                ReadWrite = false,
                InMemory = true,
                ReadingMode = ReadingMode.Immediate
            };

            var unityPath = Path.Combine(managedPath, "UnityEngine.CoreModule.dll");
            if (!File.Exists(unityPath)) unityPath = Path.Combine(managedPath, "UnityEngine.dll");
            if (!File.Exists(unityPath))
                throw new NotSupportedException("No managed Unity engine assembly was found. BSIPA requires a Mono player.");

            Logging.Logger.Injector.Debug($"Ensuring bootstrap patch in {Path.GetFileName(unityPath)} exists");

            using var unityAsmDef = AssemblyDefinition.ReadAssembly(unityPath, readerParameters);
            var unityModDef = unityAsmDef.MainModule;

            bool modified = false;
            foreach (var asmref in unityModDef.AssemblyReferences)
            {
                if (asmref.Name == cAsmName.Name)
                {
                    if (asmref.Version != cAsmName.Version)
                    {
                        asmref.Version = cAsmName.Version;
                        modified = true;
                    }
                }
            }

            var application = unityModDef.GetType("UnityEngine", "Camera");

            if (application == null)
            {
                throw new NotSupportedException($"{Path.GetFileName(unityPath)} has no UnityEngine.Camera definition to bootstrap.");
            }

            MethodDefinition? cctor = null;
            foreach (var m in application.Methods)
                if (m.IsRuntimeSpecialName && m.Name == ".cctor")
                    cctor = m;

            var cbs = unityModDef.ImportReference(((Action)CreateBootstrapper).Method);

            if (cctor == null)
            {
                cctor = new MethodDefinition(".cctor",
                    MethodAttributes.RTSpecialName | MethodAttributes.Static | MethodAttributes.SpecialName,
                    unityModDef.TypeSystem.Void);
                application.Methods.Add(cctor);
                modified = true;

                var ilp = cctor.Body.GetILProcessor();
                ilp.Emit(OpCodes.Call, cbs);
                ilp.Emit(OpCodes.Ret);
            }
            else
            {
                if (!cctor.HasBody)
                    throw new NotSupportedException("UnityEngine.Camera has an initializer without managed IL.");
                var ilp = cctor.Body.GetILProcessor();
                var bootstrapCalls = cctor.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Call
                    && instruction.Operand is MethodReference method && method.FullName == cbs.FullName).ToArray();
                if (bootstrapCalls.Length == 0)
                {
                    if (cctor.Body.Instructions.Count == 0) ilp.Emit(OpCodes.Ret);
                    ilp.InsertBefore(cctor.Body.Instructions[0], ilp.Create(OpCodes.Call, cbs));
                    modified = true;
                }
            }

            if (modified)
            {
                string tempFilePath = Path.GetTempFileName();
                (bkp ?? new BackupUnit(backupPath)).Add(unityPath);
                unityAsmDef.Write(tempFilePath);
                File.Delete(unityPath);
                File.Move(tempFilePath, unityPath);
            }
            sw.Stop();
            Logging.Logger.Injector.Info($"Installing bootstrapper took {sw.Elapsed}");
        }

        private static bool bootstrapped;

        private static void CreateBootstrapper()
        {
            if (bootstrapped) return;
            bootstrapped = true;

            Application.logMessageReceivedThreaded += delegate (string condition, string stackTrace, LogType type)
            {
                var level = UnityLogRedirector.LogTypeToLevel(type);
                UnityLogProvider.UnityLogger.Log(level, $"{condition}");
                UnityLogProvider.UnityLogger.Log(level, $"{stackTrace}");
            };

            StdoutInterceptor.EnsureHarmonyLogging();

            // need to reinit streams singe Unity seems to redirect stdout
            StdoutInterceptor.RedirectConsole();

            var bootstrapper = new GameObject("NonDestructiveBootstrapper").AddComponent<Bootstrapper>();
            bootstrapper.Destroyed += Bootstrapper_Destroyed;
        }

        private static bool loadingDone;

        private static void Bootstrapper_Destroyed()
        {
            // wait for plugins to finish loading
            pluginAsyncLoadTask?.Wait();
            permissionFixTask?.Wait();

            Default.Debug("Plugins loaded");
            Default.Debug(string.Join(", ", PluginLoader.PluginsMetadata.ToString()));
            _ = PluginComponent.Create();
        }
    }
}
