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
        private Vector3 framingForward = Vector3.forward;

        [Header("Reverse view flip")]
        [Tooltip("Seconds the 180 degree swing takes. Lower = snappier.")]
        public float flipTime = 0.45f;
        [Tooltip("The camera flips once the tram is moving backward faster than this, and flips back once it is moving forward faster than this.")]
        public float reverseSpeedThreshold = 1f;

        private bool flipped;
        private float currentYaw;
        private float yawVelocity;

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
            HandleFlip();

            // Frame off the heading alone. Following the tram's full rotation would roll
            // the camera with every lean and spin it end over end once it derails.
            Vector3 flatForward = target.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude > 0.0001f) framingForward = flatForward.normalized;
            Quaternion framing = Quaternion.LookRotation(framingForward, Vector3.up);

            Vector3 desired = target.position + framing * (offset * currentZoom);
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

        private void HandleFlip()
        {
            // Automatic: move to the other end of the tram while it is clearly driving in reverse,
            // so the camera always looks along the direction of travel. The gap between the two
            // thresholds keeps the view from flickering when the tram is nearly stopped.
            if (tram != null)
            {
                if (!flipped && tram.Speed < -reverseSpeedThreshold) flipped = true;
                else if (flipped && tram.Speed > reverseSpeedThreshold) flipped = false;
            }

            // Animate the yaw so the camera orbits around the tram instead of cutting through it.
            currentYaw = Mathf.SmoothDampAngle(currentYaw, flipped ? 180f : 0f, ref yawVelocity, flipTime);
            currentYaw = Mathf.Repeat(currentYaw, 360f); // keep the number from growing with every flip
        }
    }
}