using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace SparvagnRush.Gameplay
{
    public sealed class TramFollowCamera : MonoBehaviour
    {
        private Transform target;
        private TramController tram;
        private Camera cameraComponent;
        private Vector3 velocity;
        public Vector3 offset = new(0f, 42f, -34f);

        [Header("Scroll zoom")]
        [Tooltip("Multiplier applied to the offset. Smaller = closer to the tram.")]
        public float minZoom = 0.3f;
        [Tooltip("Multiplier applied to the offset. Larger = further from the tram.")]
        public float maxZoom = 1.6f;
        [Tooltip("How much the zoom multiplier changes per scroll notch.")]
        public float zoomStep = 0.1f;
        [Tooltip("Higher = snappier zoom.")]
        public float zoomSmoothing = 10f;

        private float targetZoom = 1f;
        private float currentZoom = 1f;

        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            tram = newTarget.GetComponent<TramController>();
            cameraComponent = GetComponent<Camera>();
        }

        private void LateUpdate()
        {
            if (target == null) return;

            HandleZoom();

            Vector3 desired = target.position + target.rotation * (offset * currentZoom);
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref velocity, 0.18f);
            transform.LookAt(target.position + Vector3.up * 2f);
            if (cameraComponent != null && tram != null)
                cameraComponent.fieldOfView = Mathf.Lerp(cameraComponent.fieldOfView, 58f + Mathf.Abs(tram.Speed) * 0.35f, Time.deltaTime * 3f);
        }

        private void HandleZoom()
        {
            // Scroll up = zoom in, scroll down = zoom out.
            targetZoom = Mathf.Clamp(targetZoom - ReadScroll() * zoomStep, minZoom, maxZoom);

            // Frame-rate independent smoothing.
            currentZoom = Mathf.Lerp(currentZoom, targetZoom, 1f - Mathf.Exp(-zoomSmoothing * Time.deltaTime));
        }

        private static float ReadScroll()
        {
#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            // The new Input System reports raw wheel units (e.g. 120 per notch on Windows), so clamp to +/-1.
            return mouse != null ? Mathf.Clamp(mouse.scroll.ReadValue().y, -1f, 1f) : 0f;
#else
            return Input.mouseScrollDelta.y;
#endif
        }
    }
}