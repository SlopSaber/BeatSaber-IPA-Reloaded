using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

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
        private readonly List<Type> queryTypes = new()
        {
            typeof(Camera), typeof(BaseInputModule), typeof(EventSystem)
        };
        private readonly List<Behaviour> results = new();
        private readonly HashSet<Behaviour> seen = new();

        internal void Clear()
        {
            results.Clear();
            seen.Clear();
        }

        internal List<Behaviour> Find()
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
                    if (type == null || !Names.Contains(type.Name) || !typeof(Behaviour).IsAssignableFrom(type)) continue;
                    if (typeof(Camera).IsAssignableFrom(type) || typeof(BaseInputModule).IsAssignableFrom(type)
                        || typeof(EventSystem).IsAssignableFrom(type)) continue;
                    queryTypes.Add(type);
                }
            }

            Clear();
            foreach (Type type in queryTypes)
            {
                foreach (UnityEngine.Object instance in Resources.FindObjectsOfTypeAll(type))
                {
                    if (instance is Behaviour behaviour && seen.Add(behaviour)) results.Add(behaviour);
                }
            }
            return results;
        }
    }
}
