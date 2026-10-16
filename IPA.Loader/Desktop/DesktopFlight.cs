using IPA.Config;
using Logger = IPA.Logging.Logger;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace IPA.Desktop
{
    [DefaultExecutionOrder(-10000)]
    internal sealed class DesktopFlight : MonoBehaviour
    {
        internal static DesktopFlight Instance { get; private set; }
        internal DesktopInput Input { get; private set; }
        internal Camera Camera { get; private set; }
        internal bool Ready => !stopping && Camera != null && Camera.isActiveAndEnabled;
        internal bool Captured => Ready && Application.isFocused && Cursor.lockState == CursorLockMode.Locked;
        internal bool CanInteract { get; private set; }
        internal Vector2 MouseMotion { get; private set; }
        internal Vector2 PointerPosition => Camera.pixelRect.center;
        private readonly Dictionary<Behaviour, bool> disabled = new();
        private readonly List<DesktopPointerInput> pointers = new();
        private readonly List<KeyValuePair<Renderer, bool>> hidden = new();
        private readonly List<Action> restoreMembers = new();
        private Vector3 position;
        private Quaternion rotation;
        private Vector3 originalPosition;
        private Quaternion originalRotation;
        private StereoTargetEyeMask originalStereo;
        private float yaw;
        private float pitch;
        private float nextScan;
        private bool dirty = true;
        private CursorLockMode originalLock;
        private bool originalCursor;
        private bool stopping;
        private bool applicationQuitting;
        private bool focusedLastFrame;
        private MethodInfo autoTracking;
        private Behaviour pauseTarget;
        private MethodInfo pauseMethod;
        private PropertyInfo pauseProperty;
        private FieldInfo pausedField;

        internal static void Install(GameObject host)
        {
            if (!DesktopMode.Requested || Instance != null) return;
            try { host.AddComponent<DesktopFlight>(); }
            catch (Exception error) { Logger.Default.Error($"Could not initialize IPA FPFC: {error}"); }
        }

        private void Awake()
        {
            try { Input = new DesktopInput(); }
            catch (Exception error)
            {
                Logger.Default.Error($"Could not initialize desktop input: {error}");
                enabled = false;
                Destroy(this);
                return;
            }
            Instance = this;
            originalLock = Cursor.lockState;
            originalCursor = Cursor.visible;
            SceneManager.sceneLoaded += SceneLoaded;
            DesktopGameHooks.Install();
            Logger.Default.Info("IPA FPFC enabled: WASD fly, Q/E down/up at double speed, Shift faster, Ctrl slower, Escape pause. Mouse stays captured while focused.");
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode) { dirty = true; }

        internal void Disable(Behaviour behaviour)
        {
            if (!disabled.ContainsKey(behaviour)) disabled.Add(behaviour, behaviour.enabled);
            if (behaviour.enabled) behaviour.enabled = false;
        }

        private void Scan()
        {
            dirty = false;
            nextScan = Time.unscaledTime + 0.75f;
            Camera candidate = UnityEngine.Camera.main;
            if (candidate != Camera)
            {
                RestoreCamera();
                Camera = candidate;
                if (Camera != null)
                {
                    originalPosition = Camera.transform.localPosition;
                    originalRotation = Camera.transform.localRotation;
                    originalStereo = Camera.stereoTargetEye;
                    position = Camera.transform.position;
                    // Legacy room offsets can put an untracked head below world zero.
                    // Supply standing height in tracking space, retaining room translation/rotation.
                    if (Mathf.Abs(originalPosition.y) < 0.1f && position.y < 1f)
                    {
                        Vector3 standing = originalPosition;
                        standing.y = 1.6f;
                        position = Camera.transform.parent != null ? Camera.transform.parent.TransformPoint(standing) : standing;
                    }
                    else if (position.y < 0.1f) position.y = 1.6f;
                    Vector3 angles = Camera.transform.eulerAngles;
                    yaw = angles.y;
                    pitch = Mathf.DeltaAngle(0f, angles.x);
                    rotation = Quaternion.Euler(pitch, yaw, 0f);
                    Camera.stereoTargetEye = StereoTargetEyeMask.None;
                    Camera.ResetProjectionMatrix();
                    autoTracking = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(assembly => assembly.GetType("UnityEngine.XR.XRDevice", false)).FirstOrDefault(type => type != null)
                        ?.GetMethod("DisableAutoXRCameraTracking", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Camera), typeof(bool) }, null);
                    SetAutoTracking(true);
                }
            }
            if (!Ready) return;
            pauseTarget = null;
            pauseMethod = null;
            pauseProperty = null;
            pausedField = null;
            Behaviour visualSource = null;
            foreach (Behaviour behaviour in Resources.FindObjectsOfTypeAll<Behaviour>())
            {
                if (behaviour == null || !behaviour.gameObject.scene.IsValid() || !behaviour.gameObject.scene.isLoaded) continue;
                Type type = behaviour.GetType();
                string name = type.Name;
                if (name == "FirstPersonFlyingController" || name == "VRController" || name == "VRPointer"
                    || (name == "SimpleCameraController" && type.Namespace == "SiraUtil.Tools.FPFC")
                    || (name == "TrackedPoseDriver" && (Camera.transform.IsChildOf(behaviour.transform) || behaviour.transform.IsChildOf(Camera.transform)))
                    || (name == "SmoothCamera" && behaviour.gameObject == Camera.gameObject)) Disable(behaviour);
                if (behaviour is BaseInputModule module && !(module is DesktopPointerInput)) Disable(module);
                if (behaviour is EventSystem events && events.gameObject.activeInHierarchy)
                {
                    var pointer = events.GetComponent<DesktopPointerInput>();
                    if (pointer == null) { pointer = events.gameObject.AddComponent<DesktopPointerInput>(); pointers.Add(pointer); }
                    pointer.Flight = this;
                    pointer.enabled = true;
                }
                if (name == "VRController")
                    foreach (Renderer renderer in behaviour.GetComponentsInChildren<Renderer>(true)) Hide(renderer);
                if (name == "VRPointer")
                {
                    if (visualSource == null || (!visualSource.gameObject.activeInHierarchy && behaviour.gameObject.activeInHierarchy)) visualSource = behaviour;
                    foreach (string member in PointerVisuals)
                    {
                        if (type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(behaviour) is Component visual && visual)
                            foreach (Renderer renderer in visual.GetComponentsInChildren<Renderer>(true)) Hide(renderer);
                    }
                }
                if (name == "PlayerController")
                {
                    SetBoolean(behaviour, "desktopMode", true);
                    SetBoolean(behaviour, "disableSabers", true);
                }
                if (behaviour.isActiveAndEnabled && (name == "PauseController" || name == "GamePauseManager" || name == "TutorialPauseManager"))
                {
                    pauseTarget = behaviour;
                    pauseMethod = type.GetMethod("Pause", Type.EmptyTypes);
                    pauseProperty = type.GetProperty("pause");
                    pausedField = type.GetField("_paused", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }
            foreach (var dead in disabled.Keys.Where(behaviour => behaviour == null).ToArray()) disabled.Remove(dead);
            pointers.RemoveAll(pointer => pointer == null);
            if (visualSource != null) foreach (var pointer in pointers) pointer.BindVisuals(visualSource);
        }

        private void SetBoolean(Behaviour owner, string name, bool value)
        {
            var property = owner.GetType().GetProperty(name);
            if (property?.PropertyType != typeof(bool) || !property.CanRead || !property.CanWrite) return;
            bool previous = (bool)property.GetValue(owner, null);
            if (previous == value) return;
            property.SetValue(owner, value, null);
            restoreMembers.Add(() => { if (owner != null) property.SetValue(owner, previous, null); });
        }

        private void Hide(Renderer renderer)
        {
            if (hidden.Any(saved => saved.Key == renderer)) return;
            hidden.Add(new KeyValuePair<Renderer, bool>(renderer, renderer.enabled));
            renderer.enabled = false;
        }

        private static bool EditingText()
        {
            GameObject selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            if (selected == null) return false;
            foreach (var component in selected.GetComponents<Component>())
            {
                if (component == null) continue;
                var focused = component.GetType().GetProperty("isFocused");
                if (focused?.PropertyType == typeof(bool) && (bool)focused.GetValue(component, null)) return true;
            }
            return false;
        }

        private void Update()
        {
            if (Input == null) return;
            if (dirty || !Ready || Time.unscaledTime >= nextScan) Scan();
            // Ignore the focus/capture transition click and any cursor-warp look delta.
            CanInteract = focusedLastFrame && Captured;
            ApplyCapture();
            MouseMotion = Vector2.zero;
            bool editing = Ready && EditingText();
            if (Ready && Application.isFocused)
            {
                if (Input.Key("escape", true))
                {
                    if (editing) EventSystem.current.SetSelectedGameObject(null);
                    else Pause();
                }
                if (Captured && !editing)
                {
                    if (CanInteract) MouseMotion = Input.Look;
                    float sensitivity = Positive(SelfConfig.Instance.FPFCLookSensitivity, 0.2f);
                    yaw += MouseMotion.x * sensitivity;
                    pitch = Mathf.Clamp(pitch - MouseMotion.y * sensitivity, -89f, 89f);
                    rotation = Quaternion.Euler(pitch, yaw, 0f);
                    Vector3 movement = new((Input.Key("d") ? 1 : 0) - (Input.Key("a") ? 1 : 0),
                        (Input.Key("e") ? 1 : 0) - (Input.Key("q") ? 1 : 0),
                        (Input.Key("w") ? 1 : 0) - (Input.Key("s") ? 1 : 0));
                    float speed = Positive(SelfConfig.Instance.FPFCMoveSpeed, 1.5f);
                    if (Input.Key("left shift") || Input.Key("right shift")) speed *= 3f;
                    if (Input.Key("left ctrl") || Input.Key("right ctrl")) speed *= 0.25f;
                    movement = Vector3.ClampMagnitude(movement, 1f);
                    position += (rotation * new Vector3(movement.x, 0f, movement.z) + Vector3.up * (movement.y * 2f)) * speed * Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                }
            }
            ApplyPose();
            focusedLastFrame = Ready && Application.isFocused;
        }

        private static float Positive(float value, float fallback) => value > 0f && !float.IsInfinity(value) ? value : fallback;
        private void LateUpdate() { ApplyPose(); if (Input != null && !stopping) ApplyCapture(); }
        private void ApplyCapture()
        {
            bool capture = Ready && Application.isFocused;
            Cursor.lockState = capture ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !capture;
        }
        private void ApplyPose()
        {
            if (!Ready) return;
            Camera.stereoTargetEye = StereoTargetEyeMask.None;
            Camera.transform.SetPositionAndRotation(position, rotation);
        }
        private void OnApplicationFocus(bool focused)
        {
            focusedLastFrame = false;
            CanInteract = false;
            if (focused) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            foreach (var pointer in pointers) if (pointer != null) pointer.CancelPointer();
        }

        private void Pause()
        {
            if (pauseTarget == null) { dirty = true; return; }
            bool paused = pausedField?.GetValue(pauseTarget) is bool flag ? flag
                : pauseProperty?.CanRead == true && pauseProperty.GetValue(pauseTarget, null) is bool state && state;
            if (!paused)
            {
                if (pauseMethod != null) pauseMethod.Invoke(pauseTarget, null);
                else if (pauseProperty?.CanWrite == true && pauseProperty.PropertyType == typeof(bool)) pauseProperty.SetValue(pauseTarget, true, null);
            }
        }

        private void RestoreCamera()
        {
            if (Camera == null) return;
            Camera.transform.localPosition = originalPosition;
            Camera.transform.localRotation = originalRotation;
            Camera.stereoTargetEye = originalStereo;
            Camera.ResetProjectionMatrix();
            SetAutoTracking(false);
        }

        private static readonly string[] PointerVisuals = { "_cursorTransform", "_leftCursorTransform", "_rightCursorTransform", "_laserPointerTransform", "_laserPointer", "_leftLaserPointer", "_rightLaserPointer" };

        private void SetAutoTracking(bool disable)
        {
            if (autoTracking == null) return;
            try { autoTracking.Invoke(null, new object[] { Camera, disable }); }
            catch (Exception error) { Logger.Default.Debug($"FPFC automatic XR tracking control unavailable: {error.Message}"); autoTracking = null; }
        }

        private void OnApplicationQuit() { applicationQuitting = true; }
        private void OnDisable() { Shutdown(); }
        private void OnDestroy() { Shutdown(); }
        private void Shutdown()
        {
            if (stopping || Input == null) return;
            stopping = true;
            SceneManager.sceneLoaded -= SceneLoaded;
            // At quit, native pointer/controller dependencies may already be destroyed.
            // Restore gameplay ownership only when disabling FPFC during a live session.
            if (!applicationQuitting)
            {
                RestoreCamera();
                foreach (var pointer in pointers) if (pointer != null) { pointer.enabled = false; Destroy(pointer); }
                foreach (var saved in disabled) if (saved.Key != null) saved.Key.enabled = saved.Value;
                foreach (var saved in hidden) if (saved.Key != null) saved.Key.enabled = saved.Value;
                foreach (var restore in restoreMembers) restore();
            }
            Cursor.lockState = originalLock;
            Cursor.visible = originalCursor;
            if (Instance == this) Instance = null;
        }
    }
}
