using UnityEngine;
using UnityEngine.EventSystems;

namespace IPA.Desktop
{
    internal sealed class DesktopPointerInput : PointerInputModule
    {
        internal DesktopFlight Flight;
        private PointerEventData pointer;
        private Vector2 dragMotion;
        private GameObject lastClick;
        private float lastClickTime;
        private Vector2 lastScreenPosition;
        internal bool Hovering { get; private set; }

        public override bool IsModuleSupported() => Flight != null && Flight.Ready;
        public override bool ShouldActivateModule() => IsModuleSupported() && Application.isFocused;

        public override void UpdateModule()
        {
            if (Flight == null) return;
            foreach (var module in GetComponents<BaseInputModule>()) if (module != this) Flight.Disable(module);
        }

        public override void Process()
        {
            if (!ShouldActivateModule()) { CancelPointer(); return; }
            ExecuteEvents.Execute(eventSystem.currentSelectedGameObject, GetBaseEventData(), ExecuteEvents.updateSelectedHandler);
            if (pointer == null) GetPointerData(-1, out pointer, true);
            Vector2 previousPosition = pointer.position;
            Vector2 screen = Flight.PointerPosition;
            Vector2 mouseMotion = Flight.Captured ? Flight.MouseMotion : screen - lastScreenPosition;
            lastScreenPosition = screen;
            pointer.position = screen;
            pointer.scrollDelta = Flight.Input.Scroll;
            pointer.button = PointerEventData.InputButton.Left;

            // The game's VR raycasters read this seed as a world ray, including curved canvases.
            Ray ray = Flight.Camera.ScreenPointToRay(screen);
            pointer.pointerCurrentRaycast = new RaycastResult { worldPosition = ray.origin, worldNormal = ray.direction };
            eventSystem.RaycastAll(pointer, m_RaycastResultCache);
            pointer.pointerCurrentRaycast = FindFirstRaycast(m_RaycastResultCache);
            m_RaycastResultCache.Clear();
            // VR UI uses the hit's canvas projection for sliders and curved surfaces.
            if (pointer.pointerCurrentRaycast.module != null && pointer.pointerCurrentRaycast.module.GetType().Name == "VRGraphicRaycaster")
                pointer.position = pointer.pointerCurrentRaycast.screenPosition;
            pointer.delta = pointer.position - previousPosition;
            GameObject current = pointer.pointerCurrentRaycast.gameObject;
            Hovering = ExecuteEvents.GetEventHandler<IPointerClickHandler>(current) != null
                || ExecuteEvents.GetEventHandler<IDragHandler>(current) != null;
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
            Hovering = false;
        }

        public override void DeactivateModule() { CancelPointer(); base.DeactivateModule(); }
        protected override void OnDisable() { CancelPointer(); base.OnDisable(); }
    }
}
