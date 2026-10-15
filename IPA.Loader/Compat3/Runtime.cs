using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using IPA.Logging;
using Mono.Cecil;
using Newtonsoft.Json.Linq;
using UnityEngine.SceneManagement;

// These exported facades intentionally retain the BSIPA 3 binary contracts.
#pragma warning disable CS0618

namespace IPA.Compat3
{
    // Separate facades keep the BSIPA 3 return types; changing an assembly
    // reference alone cannot make PluginInfo-returning calls bind to v4 APIs.
    /// <summary>Preserves BSIPA 3 plugin metadata type identities.</summary>
    public static class PluginLoader
    {
        /// <summary>Describes a plugin through the BSIPA 3 metadata contract.</summary>
        public sealed class PluginMetadata
        {
            /// <summary>Gets the loaded plugin assembly.</summary>
            public Assembly Assembly { get; internal set; }
            /// <summary>Gets the entry-point type discovered in the original DLL.</summary>
            public TypeDefinition PluginType { get; internal set; }
            /// <summary>Gets the display name.</summary>
            public string Name { get; internal set; }
            /// <summary>Gets the manifest identifier.</summary>
            public string Id { get; internal set; }
            /// <summary>Gets the legacy semantic version wrapper.</summary>
            public SemVer.Version Version { get; internal set; }
            /// <summary>Gets the original plugin file.</summary>
            public FileInfo File { get; internal set; }
            /// <summary>Gets whether metadata has no executable entry point.</summary>
            public bool IsBare { get; internal set; }
            /// <summary>Gets the features enabled for the plugin.</summary>
            public IReadOnlyList<IPA.Loader.Features.Feature> Features { get; internal set; } = Array.Empty<IPA.Loader.Features.Feature>();
        }
        /// <summary>Exposes plugin state through the BSIPA 3 manager contract.</summary>
        public sealed class PluginInfo
        {
            internal IBeatSaberPlugin Plugin { get; set; }
            /// <summary>Gets the legacy metadata view.</summary>
            public PluginMetadata Metadata { get; internal set; }
            internal bool Active = true;
            internal bool HostedByModern;
            internal bool StartupDelivered;
            internal Func<IBeatSaberPlugin> Factory;
            internal void Initialize() { if (Plugin == null) { Plugin = Factory(); PluginInitInjector.Initialize(Plugin, Metadata); } }
        }
    }

    /// <summary>Routes legacy plugin queries and state changes to the v5 loader.</summary>
    public static class PluginManager
    {
        static PluginManager()
        {
            IPA.Loader.PluginManager.PluginEnabled += (metadata, restart) => PluginEnabled?.Invoke(GetPluginFromId(metadata.Id) ?? new PluginLoader.PluginInfo { Metadata = Convert(metadata) }, restart);
            IPA.Loader.PluginManager.PluginDisabled += (metadata, restart) => PluginDisabled?.Invoke(Convert(metadata), restart);
        }
        internal static readonly List<PluginLoader.PluginInfo> Legacy = new List<PluginLoader.PluginInfo>();
        private static IEnumerable<PluginLoader.PluginInfo> All => Legacy.Concat(IPA.Loader.PluginManager.EnabledPlugins.Select(meta => new PluginLoader.PluginInfo
        {
            Metadata = new PluginLoader.PluginMetadata { Assembly = meta.Assembly, PluginType = meta.PluginType,
                Name = meta.Name, Id = meta.Id, Version = meta.Version, File = meta.File, IsBare = meta.IsBare, Features = meta.Features }
        })).GroupBy(p => p.Metadata.Id + "|" + p.Metadata.Name).Select(group => group.First());
        /// <summary>Finds a plugin by display name.</summary>
        public static PluginLoader.PluginInfo GetPlugin(string name) => All.FirstOrDefault(x => x.Metadata.Name == name);
        /// <summary>Finds a plugin by manifest identifier.</summary>
        public static PluginLoader.PluginInfo GetPluginFromId(string id) => All.FirstOrDefault(x => x.Metadata.Id == id);
        /// <summary>Preserves the original ModSaber identifier lookup.</summary>
        public static PluginLoader.PluginInfo GetPluginFromModSaberName(string id) => GetPluginFromId(id);
        /// <summary>Gets the plugins exposed through the legacy manager.</summary>
        public static IEnumerable<PluginLoader.PluginInfo> AllPlugins => All;
        /// <summary>Gets metadata for disabled legacy and modern plugins.</summary>
        public static IEnumerable<PluginLoader.PluginMetadata> DisabledPlugins => Legacy.Where(p => !p.Active).Select(p => p.Metadata)
            .Concat(IPA.Loader.PluginManager.DisabledPlugins.Select(Convert));
        private static PluginLoader.PluginMetadata Convert(IPA.Loader.PluginMetadata meta) => new PluginLoader.PluginMetadata
            { Assembly = meta.Assembly, PluginType = meta.PluginType, Name = meta.Name, Id = meta.Id, Version = meta.Version, File = meta.File, IsBare = meta.IsBare, Features = meta.Features };
        /// <summary>Finds a disabled plugin by display name.</summary>
        public static PluginLoader.PluginMetadata GetDisabledPlugin(string name) => DisabledPlugins.FirstOrDefault(p => p.Name == name);
        /// <summary>Finds a disabled plugin by manifest identifier.</summary>
        public static PluginLoader.PluginMetadata GetDisabledPluginFromId(string id) => DisabledPlugins.FirstOrDefault(p => p.Id == id);
        /// <summary>Finds the plugin state associated with legacy metadata.</summary>
        public static PluginLoader.PluginInfo InfoFromMetadata(PluginLoader.PluginMetadata meta) => All.FirstOrDefault(p => p.Metadata.Id == meta.Id && p.Metadata.Name == meta.Name);
        /// <summary>Gets whether the plugin is disabled.</summary>
        public static bool IsDisabled(PluginLoader.PluginMetadata meta) => DisabledPlugins.Any(p => p.Id == meta.Id && p.Name == meta.Name);
        /// <summary>Gets whether the plugin is enabled.</summary>
        public static bool IsEnabled(PluginLoader.PluginMetadata meta) => !IsDisabled(meta) && InfoFromMetadata(meta) != null;
        /// <summary>Receives an enabled plugin and whether the change requires restart.</summary>
        public delegate void PluginEnableDelegate(PluginLoader.PluginInfo plugin, bool needsRestart);
        /// <summary>Receives disabled plugin metadata and whether the change requires restart.</summary>
        public delegate void PluginDisableDelegate(PluginLoader.PluginMetadata plugin, bool needsRestart);
        /// <summary>Occurs when a plugin is enabled.</summary>
        public static event PluginEnableDelegate PluginEnabled;
        /// <summary>Occurs when a plugin is disabled.</summary>
        public static event PluginDisableDelegate PluginDisabled;
        /// <summary>Disables a plugin selected by identifier or display name.</summary>
        public static bool DisablePlugin(string id) => DisablePlugin(GetPluginFromId(id) ?? GetPlugin(id));
        /// <summary>Enables a disabled plugin selected by identifier or display name.</summary>
        public static bool EnablePlugin(string id) => EnablePlugin(GetDisabledPluginFromId(id) ?? GetDisabledPlugin(id));
        /// <summary>Disables an identified legacy or modern plugin.</summary>
        public static bool DisablePlugin(PluginLoader.PluginInfo info)
        {
            if (info != null && (info.HostedByModern || info.Plugin == null)) return SetModernState(info.Metadata, false);
            if (info == null || !(info.Plugin is IDisablablePlugin plugin) || !info.Active) return false;
            plugin.OnDisable(); info.Active = false;
            IPA.Loader.DisabledConfig.Instance.DisabledModIds.Add(info.Metadata.Id ?? info.Metadata.Name);
            PluginDisabled?.Invoke(info.Metadata, false); return true;
        }
        /// <summary>Enables an identified disabled plugin.</summary>
        public static bool EnablePlugin(PluginLoader.PluginMetadata meta)
        {
            if (meta == null) return false;
            var info = InfoFromMetadata(meta);
            if (info == null || info.HostedByModern || info.Plugin == null) return SetModernState(meta, true);
            if (info.Active) return false;
            info.Initialize();
            if (!(info.Plugin is IDisablablePlugin plugin)) return false;
            plugin.OnEnable(); info.Active = true;
            IPA.Loader.DisabledConfig.Instance.DisabledModIds.Remove(meta.Id ?? meta.Name);
            PluginEnabled?.Invoke(info, false); return true;
        }
        private static bool SetModernState(PluginLoader.PluginMetadata metadata, bool enable)
        {
            var core = IPA.Loader.PluginManager.EnabledPlugins.Concat(IPA.Loader.PluginManager.DisabledPlugins)
                .FirstOrDefault(m => m.Id == metadata.Id && m.Name == metadata.Name);
            if (core == null) return false;
            using var transaction = IPA.Loader.PluginManager.PluginStateTransaction();
            bool accepted = enable ? transaction.Enable(core) : transaction.Disable(core);
            if (!accepted) return false;
            var task = transaction.Commit();
            _ = task.ContinueWith(t => Logger.Loader.Error(t.Exception), System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted, System.Threading.Tasks.TaskScheduler.Default);
            return true;
        }
        /// <summary>Gets plugins using the original IPA interface.</summary>
        public static IEnumerable<IPA.Old.IPlugin> Plugins => IPA.Loader.PluginManager.Plugins;
    }

    /// <summary>Supplies legacy constructor-style initialization parameters.</summary>
    public static class PluginInitInjector
    {
        /// <summary>Resolves one initialization argument from the preceding value and plugin metadata.</summary>
        public delegate object InjectParameter(object previous, ParameterInfo parameter, PluginLoader.PluginMetadata metadata);
        private static readonly List<Tuple<Type, InjectParameter>> Injectors = new List<Tuple<Type, InjectParameter>>();
        /// <summary>Registers an initializer for parameters of the supplied type.</summary>
        public static void AddInjector(Type type, InjectParameter injector) => Injectors.Add(Tuple.Create(type, injector));
        internal static void Initialize(IBeatSaberPlugin plugin, PluginLoader.PluginMetadata meta)
        {
            MethodInfo init = plugin.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(m => m.Name == "Init");
            if (init == null) return;
            object[] values = init.GetParameters().Select(parameter =>
            {
                object value = null;
                if (parameter.ParameterType == typeof(Logger)) value = new StandardLogger(meta.Name);
                else if (parameter.ParameterType == typeof(PluginLoader.PluginMetadata)) value = meta;
                else if (parameter.ParameterType == typeof(IPA.Config.IModPrefs))
                    value = new IPA.Config.ModPrefs(new IPA.Loader.PluginMetadata { Manifest = new IPA.Loader.PluginManifest
                        { Name = meta.Name, Id = meta.Id, Version = meta.Version.UnderlyingVersion } });
                else if (parameter.ParameterType == typeof(Config.IConfigProvider))
                {
                    var config = Config.Config.GetProviderFor(meta.Name, parameter); config.Load(); value = config;
                }
                foreach (var pair in Injectors.Where(x => parameter.ParameterType.IsAssignableFrom(x.Item1)))
                    value = pair.Item2(value, parameter, meta);
                if (value == null && parameter.ParameterType.IsValueType) value = Activator.CreateInstance(parameter.ParameterType);
                return value;
            }).ToArray();
            init.Invoke(plugin, values);
        }
    }

    internal static class Runtime
    {
        internal static bool IsEntryType(TypeDefinition type) => !type.IsAbstract && !type.IsInterface
            && type.Interfaces.Any(i => i.InterfaceType.FullName == "IPA.IBeatSaberPlugin" || i.InterfaceType.FullName == "IPA.IEnhancedBeatSaberPlugin");
        internal static string PrepareAssembly(string file)
            => IPA.Loader.LoaderCompatibility.PrepareAssembly(file);
        internal static object CreateModern(IPA.Loader.PluginMetadata meta)
        {
            Type type = meta.Assembly.GetType(meta.PluginType.FullName.Replace('/', '+'), true);
            var info = new PluginLoader.PluginInfo { HostedByModern = true, Metadata = new PluginLoader.PluginMetadata
                { Assembly = meta.Assembly, PluginType = meta.PluginType, Name = meta.Name, Id = meta.Id, Version = meta.Version,
                  File = meta.File, Features = meta.Features }, Factory = () => (IBeatSaberPlugin)Activator.CreateInstance(type) };
            info.Initialize(); PluginManager.Legacy.Add(info); return info;
        }
        internal static System.Threading.Tasks.Task EnableModern(object instance)
        {
            var info = (PluginLoader.PluginInfo)instance;
            info.Active = true;
            if (!info.StartupDelivered) { info.Plugin.OnApplicationStart(); info.StartupDelivered = true; }
            if (info.Plugin is IDisablablePlugin plugin) plugin.OnEnable();
            return System.Threading.Tasks.Task.CompletedTask;
        }
        internal static System.Threading.Tasks.Task DisableModern(object instance)
        {
            var info = (PluginLoader.PluginInfo)instance;
            if (info.Active && info.Plugin is IDisablablePlugin plugin) plugin.OnDisable();
            info.Active = false; return System.Threading.Tasks.Task.CompletedTask;
        }
        internal static void UpdateModern() { foreach (var info in PluginManager.Legacy.Where(p => p.HostedByModern)) Invoke(info, p => p.OnUpdate()); }
        internal static void FixedUpdateModern() { foreach (var info in PluginManager.Legacy.Where(p => p.HostedByModern)) Invoke(info, p => p.OnFixedUpdate()); }
        internal static void LateUpdateModern() { foreach (var info in PluginManager.Legacy.Where(p => p.HostedByModern)) Invoke(info, p => { if (p is IGenericEnhancedPlugin enhanced) enhanced.OnLateUpdate(); }); }
        internal static void QuitModern() { foreach (var info in PluginManager.Legacy.Where(p => p.HostedByModern)) Invoke(info, p => p.OnApplicationQuit()); }
        internal static bool IsInterfacePlugin(ModuleDefinition module) => Types(module.Types)
            .Any(t => t.Interfaces.Any(i => i.InterfaceType.FullName == "IPA.IBeatSaberPlugin" || i.InterfaceType.FullName == "IPA.IEnhancedBeatSaberPlugin"));
        private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
        {
            foreach (var type in types) { yield return type; foreach (var child in Types(type.NestedTypes)) yield return child; }
        }
        private static bool UsesLegacyContracts(ModuleDefinition module) => IsInterfacePlugin(module)
            || module.GetTypeReferences().Any(type => type.FullName == "IPA.Loader.PluginLoader/PluginInfo"
                || type.FullName == "IPA.Loader.PluginLoader/PluginMetadata"
                || type.FullName == "IPA.Config.IConfigProvider" || type.Namespace == "IPA.Config.ConfigProviders"
                || type.FullName == "IPA.Utilities.BeatSaber");
        internal static bool Rewrite(ModuleDefinition module)
        {
            bool legacy = UsesLegacyContracts(module);
            bool changed = false;
            if (module.Assembly.Name.Name == "0Harmony" && module.Assembly.Name.Version.Major < 2)
            {
                module.Assembly.Name.Name = "0Harmony.Legacy1";
                changed = true;
            }
            foreach (var reference in module.AssemblyReferences)
            {
                if (reference.Name == "0Harmony" && reference.Version.Major < 2) { reference.Name = "0Harmony.Legacy1"; changed = true; }
                if (reference.Name == "IPA.Loader" && reference.Version != typeof(Runtime).Assembly.GetName().Version)
                { reference.Version = typeof(Runtime).Assembly.GetName().Version; changed = true; }
            }
            if (!legacy) return changed;
            // Snapshot the original names before renaming declaring types also
            // changes the full names of their nested metadata/delegate types.
            var references = module.GetTypeReferences().Select(type => (Type: type, Name: type.FullName, Namespace: type.Namespace)).ToArray();
            foreach (var entry in references)
            {
                var reference = entry.Type;
                if (entry.Name == "IPA.Loader.PluginLoader" || entry.Name == "IPA.Loader.PluginManager"
                    || entry.Name == "IPA.Loader.PluginInitInjector") { reference.Namespace = "IPA.Compat3"; changed = true; }
                if (entry.Name == "IPA.Config.Config" || entry.Name == "IPA.Config.IConfigProvider"
                    || entry.Namespace == "IPA.Config.ConfigProviders")
                { reference.Namespace = "IPA.Compat3.Config" + (entry.Namespace.EndsWith("ConfigProviders", StringComparison.Ordinal) ? ".ConfigProviders" : ""); changed = true; }
                if (entry.Name == "IPA.Utilities.BeatSaber") { reference.Name = "UnityGame"; changed = true; }
            }
            return changed;
        }
        internal static IPA.Old.IPlugin Create(Type type, string file)
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IBeatSaberPlugin).IsAssignableFrom(type)) return null;
            JObject manifest = null;
            foreach (string resource in type.Assembly.GetManifestResourceNames().Where(n => n.EndsWith("manifest.json", StringComparison.Ordinal)))
                using (var reader = new StreamReader(type.Assembly.GetManifestResourceStream(resource))) { manifest = JObject.Parse(reader.ReadToEnd()); break; }
            var meta = new PluginLoader.PluginMetadata { Assembly = type.Assembly, File = new FileInfo(file),
                Name = (string)manifest?["name"] ?? type.Name, Id = (string)manifest?["id"],
                Version = new SemVer.Version((string)manifest?["version"] ?? "0.0.0") };
            var info = new PluginLoader.PluginInfo { Metadata = meta,
                Factory = () => (IBeatSaberPlugin)Activator.CreateInstance(type),
                Active = !IPA.Loader.DisabledConfig.Instance.DisabledModIds.Contains(meta.Id ?? meta.Name) };
            if (info.Active) info.Initialize();
            PluginManager.Legacy.Add(info);
            return new Adapter(meta, info);
        }
        internal static void SceneLoaded(Scene scene, LoadSceneMode mode)
        { foreach (var info in PluginManager.Legacy) Invoke(info, p => p.OnSceneLoaded(scene, mode)); }
        internal static void SceneUnloaded(Scene scene)
        { foreach (var info in PluginManager.Legacy) Invoke(info, p => p.OnSceneUnloaded(scene)); }
        internal static void SceneChanged(Scene oldScene, Scene newScene)
        { foreach (var info in PluginManager.Legacy) Invoke(info, p => p.OnActiveSceneChanged(oldScene, newScene)); }
        private static void Invoke(PluginLoader.PluginInfo info, Action<IBeatSaberPlugin> callback)
        { if (!info.Active) return; try { callback(info.Plugin); } catch (Exception error) { Logger.Loader.Error($"BSIPA 3 {info.Metadata.Name}: {error}"); } }
        private sealed class Adapter : IPA.Old.IEnhancedPlugin
        {
            private IBeatSaberPlugin plugin => info.Plugin;
            private readonly PluginLoader.PluginMetadata metadata;
            private readonly PluginLoader.PluginInfo info;
            internal Adapter(PluginLoader.PluginMetadata metadata, PluginLoader.PluginInfo info) { this.metadata = metadata; this.info = info; }
            public string Name => metadata.Name;
            public string Version => metadata.Version.ToString();
            public string[] Filter => new[] { "Beat Saber" };
            public void OnApplicationStart() { if (!info.Active) return; plugin.OnApplicationStart(); if (plugin is IDisablablePlugin dynamicPlugin) dynamicPlugin.OnEnable(); }
            public void OnApplicationQuit() { if (plugin == null) return; try { plugin.OnApplicationQuit(); } finally { if (info.Active && plugin is IDisablablePlugin dynamicPlugin) dynamicPlugin.OnDisable(); } }
            public void OnUpdate() { if (info.Active) plugin.OnUpdate(); }
            public void OnFixedUpdate() { if (info.Active) plugin.OnFixedUpdate(); }
            public void OnLateUpdate() { if (info.Active && plugin is IGenericEnhancedPlugin enhanced) enhanced.OnLateUpdate(); }
            public void OnLevelWasLoaded(int level) { }
            public void OnLevelWasInitialized(int level) { }
        }
    }
}
