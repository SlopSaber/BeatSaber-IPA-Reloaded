using IPA.Config;
using IPA.Loader.Composite;
using IPA.Utilities;
using IPA.Utilities.Async;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;
using UnityEngine.SceneManagement;
// ReSharper disable UnusedMember.Local

namespace IPA.Loader
{
    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
    internal class PluginComponent : MonoBehaviour
    {
        private CompositeBSPlugin bsPlugins;
        private CompositeIPAPlugin ipaPlugins;
        private bool quitting;
        public static PluginComponent Instance;
        private static bool initialized;

        internal static PluginComponent Create()
        {
            return Instance = new GameObject("IPA_PluginManager").AddComponent<PluginComponent>();
        }

        internal void Awake()
        {
            DontDestroyOnLoad(gameObject);

            if (!initialized)
            {
                UnityGame.SetMainThread();
                UnityGame.EnsureRuntimeGameVersion();
                LegacyRuntimeCompatibility.Install();

                Desktop.DesktopFramePacing.Install(gameObject);
                Desktop.DesktopFlight.Install(gameObject);
                StartupDiagnostics.Install();

                PluginManager.Load();
                if (Desktop.DesktopFlight.Instance != null) Desktop.DesktopCameraCompatibility.Install();

                bsPlugins = new CompositeBSPlugin(PluginManager.BSMetas);
#pragma warning disable 618
                ipaPlugins = new CompositeIPAPlugin(PluginManager.Plugins);
#pragma warning restore 618
                bsPlugins.OnEnable();
                ipaPlugins.OnApplicationStart();

                SceneManager.activeSceneChanged += OnActiveSceneChanged;
                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;

                var unitySched = UnityMainThreadTaskScheduler.Default as UnityMainThreadTaskScheduler;
                if (!unitySched.IsRunning)
                    StartCoroutine(unitySched.Coroutine());

                initialized = true;

#if DEBUG
                Config.Stores.GeneratedStoreImpl.DebugSaveAssembly($"{Config.Stores.GeneratedStoreImpl.GeneratedAssemblyName}.dll");
#endif
            }
        }

        internal void Update()
        {
            bsPlugins.OnUpdate();
            ipaPlugins.OnUpdate();
            IPA.Compat3.Runtime.UpdateModern();
            IPA.Compat3.Config.Config.Update();

            var unitySched = UnityMainThreadTaskScheduler.Default as UnityMainThreadTaskScheduler;
            if (!unitySched.IsRunning)
                StartCoroutine(unitySched.Coroutine());
        }

        internal void LateUpdate()
        {
            bsPlugins.OnLateUpdate();
            ipaPlugins.OnLateUpdate();
            IPA.Compat3.Runtime.LateUpdateModern();
        }

        internal void FixedUpdate()
        {
            bsPlugins.OnFixedUpdate();
            ipaPlugins.OnFixedUpdate();
            IPA.Compat3.Runtime.FixedUpdateModern();
        }

        internal void OnDestroy()
        {
            if (!quitting)
            {
                Create();
            }
        }

        internal void OnApplicationQuit()
        {
            if (quitting) return;
            quitting = true;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;

            IPA.Compat3.Runtime.QuitModern();
            bsPlugins?.OnApplicationQuit();
            ipaPlugins?.OnApplicationQuit();

            IPA.Compat3.Config.Config.Save();
            ConfigRuntime.ShutdownRuntime(); // this seems to be needed

            quitting = true;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode sceneMode)
        {
            ipaPlugins.OnLevelWasLoaded(scene.buildIndex);
            bsPlugins.OnSceneLoaded(scene, sceneMode);
            IPA.Compat3.Runtime.SceneLoaded(scene, sceneMode);
            StartCoroutine(NotifyLegacySceneInitialized(scene));
        }

        private IEnumerator NotifyLegacySceneInitialized(Scene scene)
        {
            yield return null;
            if (!quitting && scene.isLoaded) ipaPlugins.OnLevelWasInitialized(scene.buildIndex);
        }

        private void OnSceneUnloaded(Scene scene) {
            bsPlugins.OnSceneUnloaded(scene);
            IPA.Compat3.Runtime.SceneUnloaded(scene);
        }

        private void OnActiveSceneChanged(Scene prevScene, Scene nextScene) {
            bsPlugins.OnActiveSceneChanged(prevScene, nextScene);
            IPA.Compat3.Runtime.SceneChanged(prevScene, nextScene);
        }

    }
}
