using UnityEngine;

namespace TramRush.Map
{
    /// <summary>Keeps generated world labels readable without turning the city into HUD clutter.</summary>
    public sealed class MapLabel : MonoBehaviour
    {
        public enum LabelStyle { Building, Shop, Stop, Place }
        public LabelStyle Style => style;

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
        [SerializeField] private Color signColour = Color.clear;
        private bool facadeMounted;
        private bool mountFailed;
        private static Mesh facadeSource;
        private static int[] facadeTriangles;
        private static Vector3[] facadeVertices;
        private Material letteringMaterial;
        private TextMesh lettering;

        public void SetSignColour(Color colour) => signColour = colour;

        private System.Collections.IEnumerator Start()
        {
            if (style == LabelStyle.Stop) yield break;
            // Bootstrap fits colliders to older generated maps during Start.
            yield return null;
            MountOnFacade();
        }

        private void MountOnFacade()
        {
            Transform city = transform.root;
            MeshCollider walls = city.Find("Buildings")?.GetComponent<MeshCollider>();
            MeshCollider ground = city.Find("Ground")?.GetComponent<MeshCollider>();
            TextMesh text = GetComponent<TextMesh>();
            if (walls == null || text == null) { mountFailed = true; return; }
            Vector3 source = transform.position;
            if (ground != null && ground.Raycast(new Ray(source + Vector3.up * 300f, Vector3.down), out RaycastHit floor, 600f))
                source.y = style == LabelStyle.Building ? Mathf.Max(source.y, floor.point.y + 4f) : floor.point.y + 3.8f;
            float nearest = 36f;
            RaycastHit best = default;
            for (int i = 0; i < 24; i++)
            {
                float angle = i * Mathf.PI * 2f / 24f;
                Vector3 direction = new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle));
                if (!walls.Raycast(new Ray(source + direction * 40f, -direction), out RaycastHit hit, 80f) || Mathf.Abs(hit.normal.y) > 0.25f) continue;
                float distance = Vector3.Distance(hit.point, source);
                if (distance >= nearest) continue;
                best = hit;
                nearest = distance;
            }
            if (nearest >= 36f) { mountFailed = true; return; }
            Vector3 outward = Vector3.ProjectOnPlane(best.normal, Vector3.up).normalized;
            transform.SetPositionAndRotation(best.point + outward * 0.15f, Quaternion.LookRotation(-outward, Vector3.up));
            transform.localScale = Vector3.one;
            text.characterSize = style == LabelStyle.Building ? 0.7f : 0.55f;
            text.fontStyle = FontStyle.Bold;
            Color colour = signColour.a > 0 ? signColour : SignPalette(text.text);
            text.color = Color.Lerp(colour, Color.white, 0.6f);

            // Keep the lettering inside this wall's horizontal span, including corners.
            float available = 7f;
            float wallMin = -3.5f, wallMax = 3.5f;
            Mesh mesh = walls.sharedMesh;
            if (mesh != null && mesh.isReadable && best.triangleIndex >= 0)
            {
                if (facadeSource != mesh)
                {
                    facadeSource = mesh;
                    facadeTriangles = mesh.triangles;
                    facadeVertices = mesh.vertices;
                }
                int[] triangles = facadeTriangles;
                Vector3[] vertices = facadeVertices;
                Vector3 tangent = transform.right;
                float min = float.MaxValue, max = float.MinValue;
                for (int k = 0; k < 3; k++)
                {
                    Vector3 vertex = walls.transform.TransformPoint(vertices[triangles[best.triangleIndex * 3 + k]]);
                    float dot = Vector3.Dot(vertex - best.point, tangent);
                    min = Mathf.Min(min, dot); max = Mathf.Max(max, dot);
                }
                if (max - min < 0.8f) { mountFailed = true; return; }
                available = Mathf.Min(max - min - 0.4f, 9f);
                wallMin = min;
                wallMax = max;
            }
            Renderer face = text.GetComponent<Renderer>();
            Shader letteringShader = Resources.Load<Shader>("FacadeLettering");
            if (letteringShader != null)
            {
                letteringMaterial = new Material(letteringShader) { mainTexture = face.sharedMaterial.mainTexture };
                face.sharedMaterial = letteringMaterial;
                lettering = text;
                Font.textureRebuilt += RefreshFont;
            }
            Bounds glyphBounds = face.localBounds;
            float size = Mathf.Min(1f, available / Mathf.Max(0.1f, glyphBounds.size.x + 0.28f));
            transform.localScale = Vector3.one * size;
            float halfSign = (glyphBounds.size.x + 0.28f) * size * 0.5f;
            float centreOffset = Mathf.Clamp(0f, wallMin + halfSign + 0.2f, wallMax - halfSign - 0.2f);
            transform.position += transform.right * (centreOffset - glyphBounds.center.x * size);
            Transform backing = transform.Find("Label Backing");
            if (backing != null)
            {
                backing.localPosition = new Vector3(glyphBounds.center.x, glyphBounds.center.y, 0.09f);
                backing.localScale = new Vector3(glyphBounds.size.x + 0.28f, Mathf.Max(0.45f, glyphBounds.size.y + 0.20f), 1);
                // Reuse the material; per-sign colour lives in a property block.
                var properties = new MaterialPropertyBlock();
                properties.SetColor("_BaseColor", colour * 0.34f);
                properties.SetColor("_Color", colour * 0.34f);
                backing.GetComponent<Renderer>().SetPropertyBlock(properties);
            }
            // Raised faces with recessed coloured letter layers give the sign physical
            // depth from oblique street views, without camera-facing label billboards.
            for (int i = 1; i <= 3; i++)
            {
                var layer = new GameObject("Letter depth " + i);
                layer.transform.SetParent(transform, false);
                layer.transform.localPosition = Vector3.forward * (i * 0.022f);
                TextMesh letters = layer.AddComponent<TextMesh>();
                letters.text = text.text;
                letters.font = text.font;
                letters.fontSize = text.fontSize;
                letters.characterSize = text.characterSize;
                letters.fontStyle = text.fontStyle;
                letters.anchor = text.anchor;
                letters.alignment = text.alignment;
                letters.color = colour * 0.7f;
                letters.GetComponent<Renderer>().sharedMaterial = face.sharedMaterial;
            }
            labelRenderers = GetComponentsInChildren<Renderer>();
            facadeMounted = true;
        }

        private void RefreshFont(Font font)
        {
            if (letteringMaterial != null && lettering != null && lettering.font == font)
                letteringMaterial.mainTexture = font.material.mainTexture;
        }

        private void OnDestroy()
        {
            Font.textureRebuilt -= RefreshFont;
            if (letteringMaterial != null) Destroy(letteringMaterial);
        }

        private static Color SignPalette(string name)
        {
            // No brand colours are invented: untagged businesses receive a restrained
            // name-stable enamel finish. Explicit source colours take precedence.
            uint hash = 2166136261;
            foreach (char c in name) hash = unchecked((hash ^ c) * 16777619);
            return (hash % 4) switch
            {
                0 => new Color(0.24f, 0.48f, 0.40f),
                1 => new Color(0.65f, 0.37f, 0.25f),
                2 => new Color(0.28f, 0.43f, 0.57f),
                _ => new Color(0.67f, 0.56f, 0.32f)
            };
        }

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

            if (style != LabelStyle.Stop)
            {
                bool show = facadeMounted && !mountFailed &&
                    (camera.transform.position - transform.position).sqrMagnitude < maximumDistance * maximumDistance &&
                    Vector3.Dot(-transform.forward, camera.transform.position - transform.position) > 0;
                foreach (Renderer item in labelRenderers) item.enabled = show;
                return;
            }

            float distance = Vector3.Distance(camera.transform.position, transform.position);
            if (!visibilityInitialized || (Time.frameCount + Mathf.Abs(GetInstanceID())) % 6 == 0)
            {
                Vector3 sightLine = transform.position - camera.transform.position;
                float sightDistance = sightLine.magnitude;
                bool inFront = Vector3.Dot(camera.transform.forward, sightLine) > 0f;
                unobstructed = inFront && distance <= maximumDistance;
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
