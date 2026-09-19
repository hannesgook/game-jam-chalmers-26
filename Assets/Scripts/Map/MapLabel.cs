using UnityEngine;

namespace SparvagnRush.Map
{
    /// <summary>Keeps generated world labels readable without turning the city into HUD clutter.</summary>
    public sealed class MapLabel : MonoBehaviour
    {
        public enum LabelStyle { Building, Shop, Stop, Place }

        [SerializeField] private float maximumDistance = 190f;
        [SerializeField] private float minimumScale = 0.8f;
        [SerializeField] private float maximumScale = 2.1f;
        [SerializeField] private LabelStyle style;
        [SerializeField] private LayerMask occlusionMask = ~0;

        private Renderer[] labelRenderers;
        private Vector3 baseScale;
        private bool visibilityInitialized;
        private bool unobstructed;
        private static Mesh backingMesh;
        private static readonly Material[] BackingMaterials = new Material[4];

        public void SetStyle(LabelStyle value) => style = value;

        private void Awake()
        {
            ImproveReadability();
            labelRenderers = GetComponentsInChildren<Renderer>();
            baseScale = transform.localScale;
        }

        private void ImproveReadability()
        {
            TextMesh text = GetComponent<TextMesh>();
            if (text == null) return;

            text.fontStyle = FontStyle.Bold;
            text.characterSize = Mathf.Max(text.characterSize, 0.24f);

            // Domain reloads while play-testing keep runtime-created children alive in
            // some editor configurations. Never stack another plate over the first one.
            if (transform.Find("Label Backing") != null) return;

            var backing = new GameObject("Label Backing");
            backing.transform.SetParent(transform, false);
            // TextMesh's readable face is local -Z, so +Z is safely behind it.
            backing.transform.localPosition = new Vector3(0f, -0.01f, 0.035f);
            float width = Mathf.Clamp(text.text.Length * text.characterSize * 0.57f + 0.55f, 1.8f, 11f);
            float height = text.characterSize * 1.75f + 0.28f;
            backing.transform.localScale = new Vector3(width, height, 1f);
            backing.AddComponent<MeshFilter>().sharedMesh = GetBackingMesh();
            backing.AddComponent<MeshRenderer>().sharedMaterial = GetBackingMaterial(style);
        }

        private static Mesh GetBackingMesh()
        {
            if (backingMesh != null) return backingMesh;
            backingMesh = new Mesh { name = "Map Label Backing" };
            backingMesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            };
            backingMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            backingMesh.RecalculateNormals();
            return backingMesh;
        }

        private static Material GetBackingMaterial(LabelStyle requestedStyle)
        {
            int index = (int)requestedStyle;
            if (BackingMaterials[index] != null) return BackingMaterials[index];
            Color fill = requestedStyle switch
            {
                LabelStyle.Shop => new Color(0.58f, 0.22f, 0.035f, 1f),
                LabelStyle.Stop => new Color(0.025f, 0.27f, 0.62f, 1f),
                LabelStyle.Place => new Color(0.055f, 0.34f, 0.20f, 1f),
                _ => new Color(0.055f, 0.12f, 0.22f, 1f),
            };
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            Material material = new(shader)
            {
                name = $"Map Label {requestedStyle}",
                color = fill,
            };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", fill);
            BackingMaterials[index] = material;
            return material;
        }

        private void LateUpdate()
        {
            Camera camera = Camera.main;
            if (camera == null) return;

            float distance = Vector3.Distance(camera.transform.position, transform.position);
            if (!visibilityInitialized || (Time.frameCount + Mathf.Abs(GetInstanceID())) % 6 == 0)
            {
                Vector3 sightLine = transform.position - camera.transform.position;
                float sightDistance = sightLine.magnitude;
                bool inFront = Vector3.Dot(camera.transform.forward, sightLine) > 0f;
                unobstructed = inFront;
                if (unobstructed && sightDistance > 0.01f &&
                    Physics.Raycast(camera.transform.position, sightLine / sightDistance, out RaycastHit hit,
                        sightDistance, occlusionMask, QueryTriggerInteraction.Ignore))
                {
                    // A label sits a few centimetres off its own wall. A hit right at the
                    // endpoint is that wall; an earlier hit is a different obstruction.
                    unobstructed = hit.distance >= sightDistance - 1.25f;
                }
                visibilityInitialized = true;
            }
            bool visible = distance <= maximumDistance && unobstructed;
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

            float scale = Mathf.Clamp(distance / 42f, minimumScale, maximumScale);
            transform.localScale = baseScale * scale;
        }
    }
}
