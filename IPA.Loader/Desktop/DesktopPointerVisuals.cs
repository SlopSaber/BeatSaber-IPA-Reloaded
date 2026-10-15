using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using Logger = IPA.Logging.Logger;
using Object = UnityEngine.Object;

namespace IPA.Desktop
{
    // Use the game's own materials and meshes, without running a second input/controller writer.
    internal sealed class DesktopPointerVisuals : IDisposable
    {
        private readonly GameObject root;
        private readonly Transform laser;
        private readonly Transform cursor;
        private readonly float width;
        private readonly float defaultLength;
        private readonly Action<Vector3> setLaserPosition;
        private readonly Action<Vector3> setLaserScale;
        private readonly Action<float> setFadeDistance;

        internal bool IsAlive => root != null;

        internal static DesktopPointerVisuals Create(Behaviour source)
        {
            try
            {
                var laserPrefab = Field(source, "_laserPointerPrefab") as Component;
                var cursorPrefab = Field(source, "_cursorPrefab") as Component;
                if (laserPrefab == null && cursorPrefab == null) return null;
                return new DesktopPointerVisuals(source, laserPrefab, cursorPrefab);
            }
            catch (Exception error)
            {
                Logger.Default.Debug($"Native FPFC pointer assets unavailable: {error.Message}");
                return null;
            }
        }

        private DesktopPointerVisuals(Behaviour source, Component laserPrefab, Component cursorPrefab)
        {
            root = new GameObject("IPA FPFC Menu Pointer");
            // Keep world-unit dimensions even when the EventSystem is below a scaled canvas.
            // The owning input module disposes this root on disable or scene unload.
            Object.DontDestroyOnLoad(root);
            width = Field(source, "_laserPointerWidth") is float w && w > 0f ? w : 0.01f;
            defaultLength = Field(source, "_defaultLaserPointerLength") is float length && length > 0f ? length : 10f;
            try
            {
                if (laserPrefab != null)
                {
                    laser = Object.Instantiate(laserPrefab.transform, root.transform, false);
                    laser.name = "Native menu laser";
                    // Older versions expose a Transform; newer versions adjust their renderer child.
                    var component = laser.GetComponent(laserPrefab.GetType());
                    setLaserPosition = Bind<Vector3>(component, "SetLocalPosition");
                    setLaserScale = Bind<Vector3>(component, "SetLocalScale");
                    setFadeDistance = Bind<float>(component, "SetFadeDistance");
                }
                if (cursorPrefab != null)
                {
                    cursor = Object.Instantiate(cursorPrefab.transform, root.transform, false);
                    cursor.name = "Native menu dot";
                }
                Hide();
            }
            catch { Object.Destroy(root); throw; }
        }

        private static object Field(Behaviour source, string name)
            => source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);

        private static Action<T> Bind<T>(Component component, string name)
        {
            var method = component?.GetType().GetMethod(name, new[] { typeof(T) });
            return method == null ? null : (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), component, method);
        }

        internal void Draw(Camera camera, Ray ray, RaycastResult hit)
        {
            if (root == null) return;
            bool hasHit = hit.gameObject != null && Finite(hit.worldPosition);
            Vector3 target = hasHit ? hit.worldPosition : ray.GetPoint(defaultLength);
            // Share the camera's exact world-space origin with UI hit detection.
            Vector3 origin = ray.origin;
            Vector3 direction = target - origin;
            float length = direction.magnitude;
            if (!Finite(target) || length < 0.001f) { Hide(); return; }
            root.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(direction, camera.transform.up));
            root.SetActive(true);
            if (laser != null)
            {
                laser.gameObject.SetActive(true);
                Vector3 position = new(0f, 0f, length * 0.5f);
                Vector3 scale = new(width, width, length);
                if (setLaserPosition != null) setLaserPosition(position); else laser.localPosition = position;
                if (setLaserScale != null) setLaserScale(scale); else laser.localScale = scale;
                setFadeDistance?.Invoke(hasHit ? Mathf.Epsilon : 1f);
            }
            if (cursor != null)
            {
                cursor.gameObject.SetActive(hasHit);
                if (hasHit) cursor.position = target;
            }
        }

        private static bool Finite(Vector3 value)
            => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        internal void Hide() { if (root != null) root.SetActive(false); }
        public void Dispose() { if (root != null) Object.Destroy(root); }
    }
}
