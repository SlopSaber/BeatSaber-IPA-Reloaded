#define ENABLE_PROFILER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.Profiling;
using Logger = IPA.Logging.Logger;

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
        private readonly List<Type> targetTypes = new()
        {
            typeof(Camera), typeof(BaseInputModule), typeof(EventSystem)
        };
        private readonly Dictionary<Type, bool> relevantTypes = new();
        private readonly List<Behaviour> components = new();
        private readonly List<HierarchyNode> hierarchy = new();
        private readonly List<GameObject> roots = new();
        private readonly List<GameObject> sceneRoots = new();
        private List<Behaviour> results = new();
        private List<Behaviour> pendingResults = new();
        private readonly HashSet<Behaviour> seen = new();
        private readonly HashSet<Scene> scenes = new();
        private readonly List<Behaviour> changes = new();
        private readonly HashSet<Behaviour> changed = new();
        private DesktopObjectChanges objectChanges;
        private bool changesUnavailable;
        private bool changesRefresh;
        private int existingIndex;
        private int changeIndex;
        private bool hasSnapshot;
        private bool refreshing;
        private int rootIndex;
        private int lastAdvanceFrame = -1;
        private float nextRefresh;
        private static readonly long SliceTicks = Math.Max(1L, Stopwatch.Frequency / 1000);
        internal bool IsRefreshing => refreshing;

        private struct HierarchyNode
        {
            internal Transform Transform;
            internal int NextChild;
        }

        internal void Clear()
        {
            DisposeChanges();
            hasSnapshot = false;
            refreshing = false;
            lastAdvanceFrame = -1;
            results.Clear();
            pendingResults.Clear();
            roots.Clear();
            sceneRoots.Clear();
            hierarchy.Clear();
            components.Clear();
            seen.Clear();
            scenes.Clear();
            changes.Clear();
            changed.Clear();
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
            long start = Stopwatch.GetTimestamp();
            if (objectChanges != null)
            {
                Profiler.BeginSample("BSIPA.FPFC.ChangeEvents");
                try { objectChanges.Poll(); }
                catch (Exception error)
                {
                    DisableChanges(error);
                    BeginRefresh(persistentScene);
                }
                finally { Profiler.EndSample(); }
            }
            if (!refreshing)
            {
                if (Time.unscaledTime < nextRefresh) return false;
                BeginRefresh(persistentScene);
            }
            Profiler.BeginSample(changesRefresh ? "BSIPA.FPFC.Candidates" : "BSIPA.FPFC.SceneSweep");
            try
            {
                do { CollectNext(); }
                while (refreshing && Stopwatch.GetTimestamp() - start < SliceTicks);
            }
            finally { Profiler.EndSample(); }
            return !refreshing;
        }

        private void BeginRefresh(Scene persistentScene)
        {
            int previousTypeCount = targetTypes.Count;
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
                    targetTypes.Add(type);
                    relevantTypes.Clear();
                }
            }
            if (!changesUnavailable)
            {
                try
                {
                    if (objectChanges == null)
                    {
                        objectChanges = new DesktopObjectChanges(targetTypes, RecordChanges);
                        Logger.Default.Debug("FPFC discovery uses runtime object changes.");
                    }
                    else objectChanges.RegisterTypes();
                }
                catch (Exception error) { DisableChanges(error); }
            }
            changesRefresh = objectChanges != null && hasSnapshot && targetTypes.Count == previousTypeCount;
            pendingResults.Clear();
            roots.Clear();
            hierarchy.Clear();
            seen.Clear();
            scenes.Clear();
            rootIndex = 0;
            existingIndex = changeIndex = 0;
            refreshing = true;
            nextRefresh = Time.unscaledTime + 0.75f;
            // New target types need one full pass to seed already-existing instances.
            if (changesRefresh) return;
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
            if (changesRefresh)
            {
                if (existingIndex < results.Count) AddCandidate(results[existingIndex++]);
                else if (changeIndex < changes.Count) AddCandidate(changes[changeIndex++]);
                if (existingIndex < results.Count || changeIndex < changes.Count) return;
            }
            else if (hierarchy.Count > 0)
            {
                int index = hierarchy.Count - 1;
                HierarchyNode node = hierarchy[index];
                if (node.Transform == null || node.NextChild >= node.Transform.childCount)
                    hierarchy.RemoveAt(index);
                else
                {
                    Transform child = node.Transform.GetChild(node.NextChild++);
                    hierarchy[index] = node;
                    if (child != null) CollectObject(child);
                }
            }
            else if (rootIndex < roots.Count)
            {
                GameObject root = roots[rootIndex++];
                if (root != null) CollectObject(root.transform);
            }
            if (hierarchy.Count > 0 || rootIndex < roots.Count) return;
            if (changeIndex < changes.Count)
            {
                AddCandidate(changes[changeIndex++]);
                if (changeIndex < changes.Count) return;
            }
            var previous = results;
            results = pendingResults;
            pendingResults = previous;
            pendingResults.Clear();
            roots.Clear();
            seen.Clear();
            changes.Clear();
            changed.Clear();
            refreshing = false;
            hasSnapshot = true;
        }

        private void CollectObject(Transform transform)
        {
            // Yield between objects, including siblings under a wide hierarchy.
            hierarchy.Add(new HierarchyNode { Transform = transform });
            transform.gameObject.GetComponents(components);
            foreach (Behaviour component in components)
            {
                if (component != null && IsRelevant(component.GetType()) && seen.Add(component)) pendingResults.Add(component);
            }
            components.Clear();
        }

        private bool IsRelevant(Type type)
        {
            if (!relevantTypes.TryGetValue(type, out bool relevant))
            {
                foreach (Type target in targetTypes)
                    if (target.IsAssignableFrom(type)) { relevant = true; break; }
                relevantTypes.Add(type, relevant);
            }
            return relevant;
        }

        private void RecordChanges(UnityEngine.Object[] objects)
        {
            if (objects == null) return;
            foreach (UnityEngine.Object value in objects)
                if (value is Behaviour component && IsRelevant(component.GetType()) && changed.Add(component)) changes.Add(component);
        }

        private void AddCandidate(Behaviour component)
        {
            if (component == null || !component.gameObject.scene.IsValid() || !component.gameObject.scene.isLoaded) return;
            if (IsRelevant(component.GetType()) && seen.Add(component)) pendingResults.Add(component);
        }

        private void DisableChanges(Exception error)
        {
            changesUnavailable = true;
            DisposeChanges();
            Logger.Default.Debug($"FPFC runtime object changes unavailable; using scene discovery: {error.GetBaseException().Message}");
        }

        private void DisposeChanges()
        {
            DesktopObjectChanges previous = objectChanges;
            objectChanges = null;
            try { previous?.Dispose(); }
            catch (Exception error) { Logger.Default.Debug($"FPFC runtime object change cleanup: {error.Message}"); }
        }
    }
}
