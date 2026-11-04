using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace IPA.Desktop
{
    internal sealed class DesktopBehaviourDiscovery
    {
        private static readonly HashSet<string> Names = new()
        {
            "VRGraphicRaycaster", "FirstPersonFlyingController", "VRController", "VRPointer",
            "TrackedPoseDriver", "SmoothCamera", "PlayerController", "PauseController",
            "GamePauseManager", "TutorialPauseManager", "SimpleCameraController", "CamerasViewport"
        };

        private readonly HashSet<Assembly> inspectedAssemblies = new();
        private readonly List<SceneQuery> queries = new()
        {
            new SceneQuery<Camera>(), new SceneQuery<BaseInputModule>(), new SceneQuery<EventSystem>()
        };
        private readonly List<GameObject> roots = new();
        private readonly List<Behaviour> results = new();
        private readonly HashSet<Behaviour> seen = new();
        private readonly HashSet<Scene> scenes = new();

        private abstract class SceneQuery
        {
            internal abstract void Collect(GameObject root, List<Behaviour> results, HashSet<Behaviour> seen);
        }

        private sealed class SceneQuery<T> : SceneQuery where T : Behaviour
        {
            private readonly List<T> components = new();

            internal override void Collect(GameObject root, List<Behaviour> results, HashSet<Behaviour> seen)
            {
                root.GetComponentsInChildren(true, components);
                foreach (T component in components)
                    if (!ReferenceEquals(component, null) && seen.Add(component)) results.Add(component);
                components.Clear();
            }
        }

        internal void Clear()
        {
            results.Clear();
            roots.Clear();
            seen.Clear();
            scenes.Clear();
        }

        internal List<Behaviour> Find(Scene persistentScene)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!inspectedAssemblies.Add(assembly)) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error) { types = error.Types; }
                catch (NotSupportedException) { continue; }
                foreach (Type type in types)
                {
                    if (type == null || !typeof(Behaviour).IsAssignableFrom(type) || !Names.Contains(type.Name)) continue;
                    if (typeof(Camera).IsAssignableFrom(type) || typeof(BaseInputModule).IsAssignableFrom(type)
                        || typeof(EventSystem).IsAssignableFrom(type)) continue;
                    queries.Add((SceneQuery)Activator.CreateInstance(typeof(SceneQuery<>).MakeGenericType(type), true));
                }
            }
            Clear();
            for (int index = 0; index < SceneManager.sceneCount; index++) Collect(SceneManager.GetSceneAt(index));
            // DontDestroyOnLoad is not included in SceneManager.sceneCount.
            Collect(persistentScene);
            return results;
        }

        private void Collect(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || !scenes.Add(scene)) return;
            if (roots.Capacity <= scene.rootCount) roots.Capacity = scene.rootCount + 1;
            scene.GetRootGameObjects(roots);
            foreach (GameObject root in roots)
            {
                if (root == null) continue;
                foreach (SceneQuery query in queries) query.Collect(root, results, seen);
            }
            roots.Clear();
        }
    }
}
