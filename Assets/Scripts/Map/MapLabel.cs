using UnityEngine;

namespace SparvagnRush.Map
{
    /// <summary>Keeps generated world labels readable without turning the city into HUD clutter.</summary>
    public sealed class MapLabel : MonoBehaviour
    {
        [SerializeField] private float maximumDistance = 190f;
        [SerializeField] private float minimumScale = 0.8f;
        [SerializeField] private float maximumScale = 2.1f;

        private Renderer[] labelRenderers;
        private Vector3 baseScale;

        private void Awake()
        {
            labelRenderers = GetComponentsInChildren<Renderer>();
            baseScale = transform.localScale;
        }

        private void LateUpdate()
        {
            Camera camera = Camera.main;
            if (camera == null) return;

            float distance = Vector3.Distance(camera.transform.position, transform.position);
            bool visible = distance <= maximumDistance;
            foreach (Renderer item in labelRenderers) item.enabled = visible;
            if (!visible) return;

            // Match the camera's yaw but stay vertical when the view pitches down.
            Vector3 direction = camera.transform.position - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.01f)
            {
                // TextMesh's readable face points opposite its transform's forward axis.
                // Aim the billboard at the camera, then turn the glyph plane around so
                // we see its front rather than a horizontally mirrored back face.
                Quaternion faceCamera = Quaternion.LookRotation(direction.normalized, Vector3.up);
                transform.rotation = faceCamera * Quaternion.Euler(0f, 180f, 0f);
            }

            float scale = Mathf.Clamp(distance / 55f, minimumScale, maximumScale);
            transform.localScale = baseScale * scale;
        }
    }
}
