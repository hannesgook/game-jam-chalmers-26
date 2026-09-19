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

        [Header("Framing")]
        [Tooltip("How far ahead of the tram the camera looks. This makes corners visible before the tram enters them.")]
        public float lookAhead = 9f;
        [Tooltip("How strongly speed pushes the camera farther back.")]
        public float speedPullBack = 0.22f;
        [Tooltip("Smallest gap kept between the camera and scenery.")]
        public float collisionPadding = 0.7f;
        [Tooltip("Layers which may push the camera closer. The tram itself is ignored.")]
        public LayerMask collisionMask = ~0;

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

        [Header("Reverse view flip")]
        [Tooltip("Seconds the 180 degree swing takes. Lower = snappier.")]
        public float flipTime = 0.45f;
        [Tooltip("The camera flips once the tram is moving backward faster than this, and flips back once it is moving forward faster than this.")]
        public float reverseSpeedThreshold = 1f;

        private bool flipped;
        private float currentYaw;
        private float yawVelocity;
        private float manualYaw;
        private float manualPitch;
        private Vector3 lookVelocity;
        private Vector3 currentLookPoint;

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
            HandleOrbit();

            // Follow heading but not roll: copying the tram's lean into the horizon is
            // disorienting, especially while the player is already correcting balance.
            Vector3 forward = Vector3.ProjectOnPlane(target.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            Quaternion heading = Quaternion.LookRotation(forward, Vector3.up);
            float travelSign = tram != null && tram.Speed < -reverseSpeedThreshold ? -1f : 1f;
            Vector3 wantedLook = target.position + Vector3.up * 2.2f + forward * (lookAhead * travelSign);
            currentLookPoint = Vector3.SmoothDamp(currentLookPoint, wantedLook, ref lookVelocity, 0.14f);

            float pullBack = 1f + (tram != null ? Mathf.Abs(tram.Speed) * speedPullBack / Mathf.Max(1f, offset.magnitude) : 0f);
            Vector3 localOffset = offset * (currentZoom * pullBack);
            Quaternion orbit = Quaternion.Euler(manualPitch, currentYaw + manualYaw, 0f);
            Vector3 desired = target.position + heading * (orbit * localOffset);
            desired = AvoidScenery(currentLookPoint, desired);
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref velocity, 0.16f);
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(currentLookPoint - transform.position, Vector3.up),
                1f - Mathf.Exp(-12f * Time.deltaTime));
            if (cameraComponent != null && tram != null)
                cameraComponent.fieldOfView = Mathf.Lerp(cameraComponent.fieldOfView, 57f + Mathf.Abs(tram.Speed) * 0.28f,
                    1f - Mathf.Exp(-3f * Time.deltaTime));
        }

        private Vector3 AvoidScenery(Vector3 origin, Vector3 desired)
        {
            Vector3 ray = desired - origin;
            if (ray.sqrMagnitude < 0.01f) return desired;
            RaycastHit[] hits = Physics.RaycastAll(origin, ray.normalized, ray.magnitude, collisionMask, QueryTriggerInteraction.Ignore);
            float nearest = ray.magnitude;
            foreach (RaycastHit hit in hits)
            {
                if (hit.transform == target || hit.transform.IsChildOf(target)) continue;
                nearest = Mathf.Min(nearest, hit.distance);
            }
            return origin + ray.normalized * Mathf.Max(2f, nearest - collisionPadding);
        }

        private void HandleOrbit()
        {
#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                manualYaw += delta.x * 0.14f;
                manualPitch = Mathf.Clamp(manualPitch - delta.y * 0.1f, -18f, 28f);
            }
            if (mouse != null && mouse.middleButton.wasPressedThisFrame)
            {
                manualYaw = 0f;
                manualPitch = 0f;
            }
#endif
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
