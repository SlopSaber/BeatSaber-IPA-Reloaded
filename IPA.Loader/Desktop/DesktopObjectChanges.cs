using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace IPA.Desktop
{
    internal sealed class DesktopObjectChanges : IDisposable
    {
        private readonly object dispatcher;
        private readonly Delegate callback;
        private readonly MethodInfo enable;
        private readonly object sceneObjects;
        private readonly Action<Type> dispatch;
        private readonly List<Type> types;
        private int registered;
        private int cursor;
        private int lastPollFrame;

        internal DesktopObjectChanges(List<Type> types, Action<UnityEngine.Object[]> changed)
        {
            Type owner = typeof(UnityEngine.Object).Assembly.GetType("UnityEngine.ObjectDispatcher", true);
            Type flags = owner.GetNestedType("TypeTrackingFlags", BindingFlags.Public | BindingFlags.NonPublic);
            Type data = owner.Assembly.GetType("UnityEngine.TypeDispatchData", true);
            Type callbackType = typeof(Action<>).MakeGenericType(data);
            FieldInfo objects = data.GetField("changed");
            if (flags == null || !data.IsValueType || objects?.FieldType != typeof(UnityEngine.Object[]))
                throw new NotSupportedException("Unsupported runtime object change data.");
            enable = owner.GetMethod("EnableTypeTracking", new[] { flags, typeof(Type[]) });
            MethodInfo collect = owner.GetMethod("DispatchTypeChangesAndClear",
                new[] { typeof(Type), callbackType, typeof(bool), typeof(bool) });
            PropertyInfo history = owner.GetProperty("maxDispatchHistoryFramesCount");
            if (enable == null || collect == null || history?.PropertyType != typeof(int)
                || !history.CanRead || !history.CanWrite || !typeof(IDisposable).IsAssignableFrom(owner))
                throw new NotSupportedException("Unsupported runtime object change API.");
            sceneObjects = Enum.Parse(flags, "SceneObjects");
            this.types = types;

            // Only copy managed references; the callback's native ID buffers are borrowed.
            var receiver = new DynamicMethod("FPFCReceiveChanges", typeof(void),
                new[] { typeof(Action<UnityEngine.Object[]>), data }, typeof(DesktopObjectChanges), true);
            ILGenerator il = receiver.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarga_S, (byte)1);
            il.Emit(OpCodes.Ldfld, objects);
            il.Emit(OpCodes.Callvirt, typeof(Action<UnityEngine.Object[]>).GetMethod("Invoke"));
            il.Emit(OpCodes.Ret);
            callback = receiver.CreateDelegate(callbackType, changed);

            // Bind once so polling does not allocate reflection argument arrays or boxed booleans.
            var poll = new DynamicMethod("FPFCCollectChanges", typeof(void),
                new[] { typeof(DesktopObjectChanges), typeof(Type) }, typeof(DesktopObjectChanges), true);
            il = poll.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, typeof(DesktopObjectChanges).GetField(nameof(dispatcher), BindingFlags.Instance | BindingFlags.NonPublic));
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, typeof(DesktopObjectChanges).GetField(nameof(callback), BindingFlags.Instance | BindingFlags.NonPublic));
            il.Emit(OpCodes.Castclass, callbackType);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, collect);
            il.Emit(OpCodes.Ret);
            dispatch = (Action<Type>)poll.CreateDelegate(typeof(Action<Type>), this);

            dispatcher = Activator.CreateInstance(owner, true);
            try
            {
                if ((int)history.GetValue(dispatcher, null) < 64) history.SetValue(dispatcher, 64, null);
                RegisterTypes();
                lastPollFrame = Time.frameCount;
            }
            catch { Dispose(); throw; }
        }

        internal void RegisterTypes()
        {
            while (registered < types.Count)
            {
                enable.Invoke(dispatcher, new[] { sceneObjects, new Type[] { types[registered] } });
                registered++;
            }
        }

        internal void Poll()
        {
            if (Time.frameCount - lastPollFrame > 32)
                throw new InvalidOperationException("Runtime object change history may have expired.");
            lastPollFrame = Time.frameCount;
            // Drain every registered type within sixteen frames, below native history expiry.
            int count = Math.Max(1, (registered + 15) / 16);
            for (int index = 0; index < count; index++)
            {
                if (cursor >= registered) cursor = 0;
                dispatch(types[cursor++]);
            }
        }

        public void Dispose() => ((IDisposable)dispatcher).Dispose();
    }
}
