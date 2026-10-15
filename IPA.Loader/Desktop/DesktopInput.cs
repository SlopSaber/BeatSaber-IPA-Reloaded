using Logger = IPA.Logging.Logger;
using IPA.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace IPA.Desktop
{
    // Input moved between Unity modules and newer players can disable the legacy API.
    // Resolve it at runtime so one loader binary works with both input backends.
    internal sealed class DesktopInput
    {
        private readonly Type mouseType;
        private readonly Type keyboardType;
        private readonly Func<string, bool> key;
        private readonly Func<string, bool> keyDown;
        private readonly Func<int, bool> mouse;
        private readonly Func<int, bool> mouseDown;
        private readonly Func<int, bool> mouseUp;
        private readonly Func<string, float> axis;
        private readonly Func<Vector2> scroll;
        private readonly Dictionary<string, PropertyInfo> properties = new();
        private readonly Dictionary<Type, MethodInfo> readers = new();
        private readonly Dictionary<string, string> keyboardNames = new()
        {
            ["left shift"] = "leftShiftKey", ["right shift"] = "rightShiftKey",
            ["left ctrl"] = "leftCtrlKey", ["right ctrl"] = "rightCtrlKey", ["escape"] = "escapeKey"
        };

        internal DesktopInput()
        {
            mouseType = FindType("UnityEngine.InputSystem.Mouse");
            keyboardType = FindType("UnityEngine.InputSystem.Keyboard");
            Type legacy = FindType("UnityEngine.Input");
            if (legacy == null)
            {
                foreach (string data in Directory.GetDirectories(UnityGame.InstallPath, "*_Data"))
                {
                    foreach (string module in new[] { "UnityEngine.InputLegacyModule.dll", "UnityEngine.InputModule.dll" })
                    {
                        string file = Path.Combine(data, "Managed", module);
                        if (!File.Exists(file)) continue;
                        legacy = Assembly.LoadFrom(file).GetType("UnityEngine.Input");
                        if (legacy != null) break;
                    }
                    if (legacy != null) break;
                }
            }
            if (legacy != null)
            {
                key = Bind<string, bool>(legacy, "GetKey");
                keyDown = Bind<string, bool>(legacy, "GetKeyDown");
                mouse = Bind<int, bool>(legacy, "GetMouseButton");
                mouseDown = Bind<int, bool>(legacy, "GetMouseButtonDown");
                mouseUp = Bind<int, bool>(legacy, "GetMouseButtonUp");
                axis = Bind<string, float>(legacy, "GetAxisRaw");
                scroll = (Func<Vector2>)Delegate.CreateDelegate(typeof(Func<Vector2>), legacy.GetProperty("mouseScrollDelta").GetGetMethod());
            }
            if (legacy == null && mouseType == null) throw new NotSupportedException("No desktop input backend was found.");
            Logger.Default.Debug("FPFC desktop input backends resolved.");
        }

        private static Func<T, TResult> Bind<T, TResult>(Type type, string name)
            => (Func<T, TResult>)Delegate.CreateDelegate(typeof(Func<T, TResult>), type.GetMethod(name, new[] { typeof(T) }));

        private static Type FindType(string name)
        {
            Type found = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);
            if (found != null || !name.StartsWith("UnityEngine.InputSystem.", StringComparison.Ordinal)) return found;
            foreach (string data in Directory.GetDirectories(UnityGame.InstallPath, "*_Data"))
            {
                string file = Path.Combine(data, "Managed", "Unity.InputSystem.dll");
                if (File.Exists(file)) return Assembly.LoadFrom(file).GetType(name, false);
            }
            return null;
        }

        private object Property(object instance, string name)
        {
            string id = instance.GetType().AssemblyQualifiedName + ":" + name;
            if (!properties.TryGetValue(id, out var property)) properties[id] = property = instance.GetType().GetProperty(name);
            return property?.GetValue(instance, null);
        }

        private static object Current(Type type) => type?.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
        private bool Button(object control, string state) => control != null && Property(control, state) is bool value && value;
        private Vector2 Vector(object control)
        {
            if (control == null) return Vector2.zero;
            if (!readers.TryGetValue(control.GetType(), out var reader))
                readers[control.GetType()] = reader = control.GetType().GetMethod("ReadValue", Type.EmptyTypes);
            return reader?.Invoke(control, null) is Vector2 value ? value : Vector2.zero;
        }

        internal bool Key(string name, bool down = false)
        {
            object keyboard = Current(keyboardType);
            if (keyboard != null)
            {
                string property = keyboardNames.TryGetValue(name, out var mapped) ? mapped : name + "Key";
                return Button(Property(keyboard, property), down ? "wasPressedThisFrame" : "isPressed");
            }
            return (down ? keyDown : key)?.Invoke(name) ?? false;
        }

        internal bool Mouse(int button, int edge = 0)
        {
            object device = Current(mouseType);
            if (device != null)
                return Button(Property(device, button == 0 ? "leftButton" : "rightButton"),
                    edge == 1 ? "wasPressedThisFrame" : edge == -1 ? "wasReleasedThisFrame" : "isPressed");
            return (edge == 1 ? mouseDown : edge == -1 ? mouseUp : mouse)?.Invoke(button) ?? false;
        }

        internal Vector2 Scroll => Current(mouseType) is object device ? Vector(Property(device, "scroll")) / 120f : scroll?.Invoke() ?? Vector2.zero;
        internal Vector2 Look => Current(mouseType) is object device ? Vector(Property(device, "delta"))
            : new Vector2(axis?.Invoke("MouseX") ?? 0f, axis?.Invoke("MouseY") ?? 0f);
    }
}
