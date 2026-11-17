using System;
using System.IO;
using System.Linq;
using IPA.Injector.Backups;
using IPA.Logging;
using IPA.Utilities;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IPA.Injector
{
    /// <summary>Initializes plugins before synchronous native scene installation.</summary>
    public static class SceneContextBootstrap
    {
        /// <summary>Entry point called by the native scene context.</summary>
        public static void BeforeAwake() => Injector.InitializeBeforeSceneContext();

        internal static void Apply()
        {
            string managed = Path.Combine(GameVersionEarly.ResolveDataPath(UnityGame.InstallPath), "Managed");
            string mainPath = Path.Combine(managed, "Main.dll");
            if (!File.Exists(mainPath)) return;
            using (var main = ModuleDefinition.ReadModule(mainPath))
            {
                // Newer asynchronous initialization does not need this boundary.
                if (main.GetType("AppInit")?.BaseType?.FullName != "Zenject.MonoInstaller") return;
            }

            string path = Path.Combine(managed, "Zenject.dll");
            using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true });
            var awake = module.GetType("Zenject.SceneContext")?.Methods.SingleOrDefault(method =>
                method.Name == "Awake" && !method.IsStatic && method.Parameters.Count == 0);
            if (awake?.HasBody != true || awake.Body.Instructions.Count == 0)
                throw new NotSupportedException("The native scene context has no supported Awake boundary.");
            var entry = module.ImportReference(((Action)BeforeAwake).Method);
            if (awake.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Call
                && instruction.Operand is MethodReference method && method.FullName == entry.FullName)) return;
            if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => !assembly.IsDynamic
                && assembly.GetName().Name == module.Assembly.Name.Name))
                throw new InvalidOperationException("The native scene context was loaded before its bootstrap boundary could be installed.");

            var il = awake.Body.GetILProcessor();
            il.InsertBefore(awake.Body.Instructions[0], il.Create(OpCodes.Call, entry));
            string temporary = path + ".bootstrap-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                module.Write(temporary);
                var backup = new BackupUnit(Path.Combine(UnityGame.InstallPath, "IPA", "Backups", "SceneContextBootstrap"),
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                backup.Add(path);
                File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Logger.Injector.Info("Installed synchronous scene bootstrap boundary; native installers will wait for plugin initialization.");
        }
    }
}
