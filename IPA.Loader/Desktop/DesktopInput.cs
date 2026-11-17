using Logger = IPA.Logging.Logger;
using IPA.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace IPA.Desktop
{
    // Input moved between Unity modules and newer players can disable the legacy API.
    // Resolve it at runtime so one loader binary works with both input backends.
    internal sealed class DesktopInput
    {
        private readonly Func<object> currentMouse;
        private readonly Func<object> currentKeyboard;
        private readonly Func<string, bool> key;
        private readonly Func<string, bool> keyDown;
        private readonly Func<int, bool> mouse;
        private readonly Func<int, bool> mouseDown;
        private readonly Func<int, bool> mouseUp;
        private readonly Func<string, float> axis;
        private readonly Func<Vector2> scroll;
        private readonly Dictionary<(Type, string), Func<object, object>> controls = new();
        private readonly Dictionary<(Type, string), Func<object, bool>> buttons = new();
        private readonly Dictionary<Type, Func<object, Vector2>> readers = new();
        private readonly Dictionary<string, string> keyboardNames = new()
        {
            ["left shift"] = "leftShiftKey", ["right shift"] = "rightShiftKey",
            ["left ctrl"] = "leftCtrlKey", ["right ctrl"] = "rightCtrlKey", ["escape"] = "escapeKey"
        };

        internal DesktopInput()
        {
            Type mouseType = FindType("UnityEngine.InputSystem.Mouse");
            Type keyboardType = FindType("UnityEngine.InputSystem.Keyboard");
            currentMouse = BindCurrentDevice(mouseType);
            currentKeyboard = BindCurrentDevice(keyboardType);
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

        private static Func<object> BindCurrentDevice(Type type)
        {
            var getter = type?.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            return getter == null ? null : (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), getter);
        }

        // Input System types are optional at runtime. Bind their getters once
        // while keeping button and vector reads unboxed.
        private static Func<object, TResult> BindReader<TResult>(MethodInfo method)
        {
            if (method == null || !typeof(TResult).IsAssignableFrom(method.ReturnType)) return null;
            var instance = Expression.Parameter(typeof(object));
            var call = Expression.Call(Expression.Convert(instance, method.DeclaringType), method);
            return Expression.Lambda<Func<object, TResult>>(call, instance).Compile();
        }

        private static TResult ReadProperty<TResult>(Dictionary<(Type, string), Func<object, TResult>> cache,
            object instance, string name)
        {
            var key = (instance.GetType(), name);
            if (!cache.TryGetValue(key, out var read))
                cache[key] = read = BindReader<TResult>(key.Item1.GetProperty(name)?.GetGetMethod());
            return read == null ? default : read(instance);
        }

        private object Control(object device, string name) => ReadProperty(controls, device, name);
        private bool Button(object control, string state) => control != null && ReadProperty(buttons, control, state);

        private Vector2 Vector(object control)
        {
            if (control == null) return Vector2.zero;
            Type type = control.GetType();
            if (!readers.TryGetValue(type, out var read))
                readers[type] = read = BindReader<Vector2>(type.GetMethod("ReadValue", Type.EmptyTypes));
            return read == null ? Vector2.zero : read(control);
        }

        internal bool Key(string name, bool down = false)
        {
            object keyboard = currentKeyboard?.Invoke();
            if (keyboard != null)
            {
                if (!keyboardNames.TryGetValue(name, out var property))
                    keyboardNames[name] = property = name + "Key";
                return Button(Control(keyboard, property), down ? "wasPressedThisFrame" : "isPressed");
            }
            return (down ? keyDown : key)?.Invoke(name) ?? false;
        }

        internal bool Mouse(int button, int edge = 0)
        {
            object device = currentMouse?.Invoke();
            if (device != null)
                return Button(Control(device, button == 0 ? "leftButton" : "rightButton"),
                    edge == 1 ? "wasPressedThisFrame" : edge == -1 ? "wasReleasedThisFrame" : "isPressed");
            return (edge == 1 ? mouseDown : edge == -1 ? mouseUp : mouse)?.Invoke(button) ?? false;
        }

        internal Vector2 Scroll => currentMouse?.Invoke() is object device ? Vector(Control(device, "scroll")) / 120f : scroll?.Invoke() ?? Vector2.zero;
        internal Vector2 Look => currentMouse?.Invoke() is object device ? Vector(Control(device, "delta"))
            : new Vector2(axis?.Invoke("MouseX") ?? 0f, axis?.Invoke("MouseY") ?? 0f);
    }
}
