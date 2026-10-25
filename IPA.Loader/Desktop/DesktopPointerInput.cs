using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using Logger = IPA.Logging.Logger;

namespace IPA.Desktop
{
    internal sealed class DesktopPointerInput : PointerInputModule
    {
        internal DesktopFlight Flight;
        private PointerEventData pointer;
        private Vector2 dragMotion;
        private GameObject lastClick;
        private float lastClickTime;
        private DesktopPointerVisuals visuals;
        private float nextVisualsAttempt;
        private Type raycastComparerOwner;
        private Comparison<RaycastResult> raycastComparer;
        private static DesktopPointerInput currentPointer;
        private static PointerEventData raycastPointer;

        internal static bool IsDesktopRaycast(PointerEventData data) => ReferenceEquals(raycastPointer, data);

        internal static bool TryGetWorldPosition(out Vector3 position)
        {
            var active = currentPointer;
            if (active != null && active.Flight != null && active.Flight.Captured && active.Flight.CanInteract)
            {
                position = active.pointer != null && active.pointer.pointerCurrentRaycast.gameObject != null
                    ? active.pointer.pointerCurrentRaycast.worldPosition
                    : active.Flight.Camera.transform.position + active.Flight.Camera.transform.forward * 10f;
                return true;
            }
            position = default; return false;
        }

        internal void BindVisuals(Behaviour source)
        {
            DesktopGameHooks.BindPointer(source);
            EnsureVisuals();
        }

        private void EnsureVisuals()
        {
            if (visuals != null && visuals.IsAlive) return;
            if (Time.unscaledTime < nextVisualsAttempt) return;
            nextVisualsAttempt = Time.unscaledTime + 1f;
            visuals = DesktopPointerVisuals.Create();
        }

        public override bool IsModuleSupported() => Flight != null && Flight.Ready;
        public override bool ShouldActivateModule() => IsModuleSupported() && Flight.Captured;

        public override void UpdateModule()
        {
            if (Flight == null || !Flight.Ready) return;
            foreach (var module in GetComponents<BaseInputModule>())
            {
                if (module == this) continue;
                BindRaycastComparer(module);
                Flight.Disable(module);
            }
        }

        private void BindRaycastComparer(BaseInputModule module)
        {
            Type type = module.GetType();
            if (type.Name != "VRInputModule" || type == raycastComparerOwner) return;
            raycastComparerOwner = type;
            raycastComparer = null;
            try
            {
                MethodInfo comparer = type.GetMethod("RaycastComparer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(RaycastResult), typeof(RaycastResult) }, null);
                // Earlier game versions use Unity's ordering without a comparer.
                if (comparer == null) return;
                if (comparer.ReturnType != typeof(int))
                    throw new MissingMethodException(type.FullName, "RaycastComparer(RaycastResult, RaycastResult)");
                raycastComparer = (Comparison<RaycastResult>)Delegate.CreateDelegate(typeof(Comparison<RaycastResult>), comparer);
            }
            catch (Exception error) { Logger.Default.Warn($"FPFC native UI hit ordering unavailable: {error.Message}"); }
        }

        public override void Process()
        {
            if (!ShouldActivateModule() || !Flight.CanInteract) { CancelPointer(); return; }
            ExecuteEvents.Execute(eventSystem.currentSelectedGameObject, GetBaseEventData(), ExecuteEvents.updateSelectedHandler);
            if (pointer == null) GetPointerData(-1, out pointer, true);
            Vector2 previousPosition = pointer.position;
            Vector2 screen = Flight.PointerPosition;
            Vector2 mouseMotion = Flight.MouseMotion;
            pointer.position = screen;
            pointer.scrollDelta = Flight.Input.Scroll;
            pointer.button = PointerEventData.InputButton.Left;

            // The game's VR raycasters read this seed as a world ray, including curved canvases.
            Ray ray = new(Flight.Camera.transform.position, Flight.Camera.transform.forward);
            pointer.pointerCurrentRaycast = new RaycastResult { worldPosition = ray.origin, worldNormal = ray.direction };
            var previousRaycastPointer = raycastPointer;
            raycastPointer = pointer;
            try { eventSystem.RaycastAll(pointer, m_RaycastResultCache); }
            finally { raycastPointer = previousRaycastPointer; }
            // Native world-space UI ranks distance before overlapping graphic depth.
            if (raycastComparer != null) m_RaycastResultCache.Sort(raycastComparer);
            pointer.pointerCurrentRaycast = FindFirstRaycast(m_RaycastResultCache);
            currentPointer = this;
            m_RaycastResultCache.Clear();
            // VR UI uses the hit's canvas projection for sliders and curved surfaces.
            if (pointer.pointerCurrentRaycast.module != null && pointer.pointerCurrentRaycast.module.GetType().Name == "VRGraphicRaycaster")
                pointer.position = pointer.pointerCurrentRaycast.screenPosition;
            pointer.delta = pointer.position - previousPosition;
            GameObject current = pointer.pointerCurrentRaycast.gameObject;
            HandlePointerExitAndEnter(pointer, current);
            if (Flight.Input.Mouse(0, 1)) Press(current);
            if (pointer.pointerDrag != null && Flight.Input.Mouse(0))
            {
                dragMotion += mouseMotion;
                if (!pointer.dragging && (!pointer.useDragThreshold || dragMotion.sqrMagnitude >= eventSystem.pixelDragThreshold * eventSystem.pixelDragThreshold))
                {
                    ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.beginDragHandler);
                    pointer.dragging = true;
                    if (pointer.pointerPress != pointer.pointerDrag)
                    {
                        ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerUpHandler);
                        pointer.pointerPress = null;
                    }
                    pointer.eligibleForClick = false;
                }
                if (pointer.dragging) ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.dragHandler);
            }
            if (Flight.Input.Mouse(0, -1)) Release(current, true);
            if (pointer.scrollDelta.sqrMagnitude > 0) ExecuteEvents.ExecuteHierarchy(current, pointer, ExecuteEvents.scrollHandler);
        }

        private void Press(GameObject current)
        {
            var selected = ExecuteEvents.GetEventHandler<ISelectHandler>(current);
            if (eventSystem.currentSelectedGameObject != selected) eventSystem.SetSelectedGameObject(null, pointer);
            pointer.eligibleForClick = true;
            pointer.dragging = false;
            pointer.useDragThreshold = true;
            pointer.pressPosition = pointer.position;
            pointer.pointerPressRaycast = pointer.pointerCurrentRaycast;
            pointer.pointerPress = ExecuteEvents.ExecuteHierarchy(current, pointer, ExecuteEvents.pointerDownHandler)
                ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(current);
            pointer.rawPointerPress = current;
            pointer.clickCount = pointer.pointerPress == lastClick && Time.unscaledTime - lastClickTime < 0.3f ? pointer.clickCount + 1 : 1;
            pointer.clickTime = lastClickTime = Time.unscaledTime;
            lastClick = pointer.pointerPress;
            pointer.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(current);
            if (pointer.pointerDrag != null) ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.initializePotentialDrag);
            dragMotion = Vector2.zero;
        }

        private void Release(GameObject current, bool click)
        {
            if (pointer == null) return;
            ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerUpHandler);
            if (click && pointer.eligibleForClick && pointer.pointerPress == ExecuteEvents.GetEventHandler<IPointerClickHandler>(current))
                ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerClickHandler);
            else if (click && pointer.dragging) ExecuteEvents.ExecuteHierarchy(current, pointer, ExecuteEvents.dropHandler);
            if (pointer.dragging) ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.endDragHandler);
            pointer.eligibleForClick = false;
            pointer.pointerPress = pointer.rawPointerPress = pointer.pointerDrag = null;
            pointer.dragging = false;
        }

        internal void CancelPointer()
        {
            Release(null, false);
            if (pointer != null) HandlePointerExitAndEnter(pointer, null);
            visuals?.Hide();
        }

        public override void DeactivateModule() { CancelPointer(); base.DeactivateModule(); }
        private void LateUpdate()
        {
            if (EventSystem.current != eventSystem || Flight == null || !Flight.Ready) { visuals?.Hide(); return; }
            EnsureVisuals();
            visuals?.Draw(Flight.Camera);
        }
        protected override void OnDisable()
        {
            if (currentPointer == this) currentPointer = null;
            CancelPointer();
            visuals?.Dispose();
            visuals = null;
            base.OnDisable();
        }
    }
}
