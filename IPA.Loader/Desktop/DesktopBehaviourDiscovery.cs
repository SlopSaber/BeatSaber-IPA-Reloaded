using System;
using System.Collections.Generic;
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

        private readonly Dictionary<Type, bool> relevantTypes = new();
        private readonly List<GameObject> roots = new();
        private readonly List<Behaviour> components = new();
        private readonly List<Behaviour> results = new();
        private readonly HashSet<Scene> scenes = new();

        internal void Clear()
        {
            results.Clear();
            roots.Clear();
            components.Clear();
            scenes.Clear();
        }

        internal List<Behaviour> Find(Scene persistentScene)
        {
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
                root.GetComponentsInChildren(true, components);
                foreach (Behaviour behaviour in components)
                {
                    if (ReferenceEquals(behaviour, null)) continue;
                    Type type = behaviour.GetType();
                    if (!relevantTypes.TryGetValue(type, out bool relevant))
                    {
                        relevant = typeof(Camera).IsAssignableFrom(type) || typeof(BaseInputModule).IsAssignableFrom(type)
                            || typeof(EventSystem).IsAssignableFrom(type) || Names.Contains(type.Name);
                        relevantTypes.Add(type, relevant);
                    }
                    if (relevant) results.Add(behaviour);
                }
            }
            roots.Clear();
            components.Clear();
        }
    }
}
