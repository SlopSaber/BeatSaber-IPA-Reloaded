using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            "GamePauseManager", "TutorialPauseManager", "SimpleCameraController", "CamerasViewport", "VRCenterAdjust"
        };

        private readonly HashSet<Assembly> inspectedAssemblies = new();
        private readonly List<SceneQuery> queries = new()
        {
            new SceneQuery<Camera>(), new SceneQuery<BaseInputModule>(), new SceneQuery<EventSystem>()
        };
        private readonly List<GameObject> roots = new();
        private readonly List<GameObject> sceneRoots = new();
        private List<Behaviour> results = new();
        private List<Behaviour> pendingResults = new();
        private readonly HashSet<Behaviour> seen = new();
        private readonly HashSet<Scene> scenes = new();
        private bool hasSnapshot;
        private bool refreshing;
        private int rootIndex;
        private int queryIndex;
        private int lastAdvanceFrame = -1;
        private float nextRefresh;
        private static readonly long SliceTicks = Math.Max(1L, Stopwatch.Frequency / 1000);
        internal bool IsRefreshing => refreshing;

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
            hasSnapshot = false;
            refreshing = false;
            lastAdvanceFrame = -1;
            results.Clear();
            pendingResults.Clear();
            roots.Clear();
            sceneRoots.Clear();
            seen.Clear();
            scenes.Clear();
        }

        internal List<Behaviour> Find(Scene persistentScene)
        {
            if (!hasSnapshot)
            {
                BeginRefresh(persistentScene);
                // Initial ownership must be established before applying flight input.
                while (refreshing) CollectNext();
            }
            return results;
        }

        internal bool Advance(Scene persistentScene)
        {
            if (!hasSnapshot || lastAdvanceFrame == Time.frameCount) return false;
            lastAdvanceFrame = Time.frameCount;
            if (!refreshing)
            {
                if (Time.unscaledTime < nextRefresh) return false;
                BeginRefresh(persistentScene);
            }
            long start = Stopwatch.GetTimestamp();
            do { CollectNext(); }
            while (refreshing && Stopwatch.GetTimestamp() - start < SliceTicks);
            return !refreshing;
        }

        private void BeginRefresh(Scene persistentScene)
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
            pendingResults.Clear();
            roots.Clear();
            seen.Clear();
            scenes.Clear();
            rootIndex = queryIndex = 0;
            refreshing = true;
            nextRefresh = Time.unscaledTime + 0.75f;
            for (int index = 0; index < SceneManager.sceneCount; index++) CollectRoots(SceneManager.GetSceneAt(index));
            // DontDestroyOnLoad is not included in SceneManager.sceneCount.
            CollectRoots(persistentScene);
        }

        private void CollectRoots(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || !scenes.Add(scene)) return;
            if (sceneRoots.Capacity <= scene.rootCount) sceneRoots.Capacity = scene.rootCount + 1;
            scene.GetRootGameObjects(sceneRoots);
            roots.AddRange(sceneRoots);
            sceneRoots.Clear();
        }

        private void CollectNext()
        {
            if (rootIndex < roots.Count)
            {
                GameObject root = roots[rootIndex];
                if (root != null) queries[queryIndex].Collect(root, pendingResults, seen);
                if (root == null || ++queryIndex == queries.Count)
                {
                    queryIndex = 0;
                    rootIndex++;
                }
            }
            if (rootIndex < roots.Count) return;
            var previous = results;
            results = pendingResults;
            pendingResults = previous;
            pendingResults.Clear();
            roots.Clear();
            seen.Clear();
            refreshing = false;
            hasSnapshot = true;
        }
    }
}
