using UnityEngine;

namespace SparvagnRush.Gameplay
{
    /// <summary>
    /// Freezes the game the instant the tram touches somebody and orbits the wreck.
    /// Everything here runs on unscaled time, because the point is that nothing else
    /// is moving: the crowd, the tram and the physics are all stopped at the impact.
    /// </summary>
    public sealed class TramCollisionCinematic : MonoBehaviour
    {
        // How long the camera has the screen to itself before the result window opens.
        private const float RevealDelay = 2.6f;
        private const float OrbitSpeed = 26f;

        private Camera view;
        private TramFollowCamera follow;
        private Vector3 impact;
        private float elapsed;
        private float orbit;
        private float restoreFieldOfView;

        public bool Playing { get; private set; }
        /// <summary>True once the camera has shown enough for the result to appear.</summary>
        public bool Revealed => !Playing || elapsed >= RevealDelay;

        public void Begin(Vector3 point, Transform tram)
        {
            if (Playing) return;
            view = Camera.main;
            if (view == null) return;
            impact = point;
            follow = view.GetComponent<TramFollowCamera>();
            if (follow != null) follow.enabled = false;
            restoreFieldOfView = view.fieldOfView;
            // Open from the side the tram came in on, so the first frame reads as
            // an impact rather than an arbitrary shot of a stopped tram.
            orbit = tram != null
                ? Quaternion.LookRotation(tram.forward, Vector3.up).eulerAngles.y + 125f
                : 0f;
            elapsed = 0f;
            Playing = true;
            Time.timeScale = 0f;
        }

        public void Stop()
        {
            Time.timeScale = 1f;
            if (!Playing) return;
            Playing = false;
            if (view != null) view.fieldOfView = restoreFieldOfView;
            if (follow != null)
            {
                follow.enabled = true;
                follow.ResetFraming();
            }
        }

        private void LateUpdate()
        {
            if (!Playing || view == null) return;
            elapsed += Time.unscaledDeltaTime;
            orbit += OrbitSpeed * Time.unscaledDeltaTime;

            // Start tight on the impact and ease out, so the moment reads first and
            // the street around it afterwards.
            float opening = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / 5f));
            Vector3 focus = impact + Vector3.up * 1.3f;
            Vector3 wanted = focus
                + Quaternion.Euler(0f, orbit, 0f) * (Vector3.back * Mathf.Lerp(6.5f, 15f, opening))
                + Vector3.up * Mathf.Lerp(1.8f, 5.5f, opening);

            // Never let a wall sit between the camera and the impact.
            Vector3 line = wanted - focus;
            if (Physics.Raycast(focus, line.normalized, out RaycastHit hit, line.magnitude,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                wanted = hit.point - line.normalized * 0.4f;

            view.transform.SetPositionAndRotation(wanted, Quaternion.LookRotation(focus - wanted, Vector3.up));
            view.fieldOfView = Mathf.Lerp(38f, 55f, opening);
        }

        // Leaving play mode mid-freeze must not strand the editor at timeScale 0.
        private void OnDisable()
        {
            if (Playing) Time.timeScale = 1f;
        }
    }
}
