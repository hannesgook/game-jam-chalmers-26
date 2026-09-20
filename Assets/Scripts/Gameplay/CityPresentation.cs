using System.Collections.Generic;
using TramRush.Map;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TramRush.Gameplay
{
    /// <summary>Non-destructive presentation upgrade for both existing and regenerated cities.</summary>
    public sealed class CityPresentation : MonoBehaviour
    {
        private readonly List<Object> owned = new();

        public void Initialize(TramTrackNetwork network, Camera camera)
        {
            Transform oldTracks = transform.Find("TramTracks");
            if (oldTracks != null && oldTracks.TryGetComponent(out Renderer oldRenderer)) oldRenderer.enabled = false;
            BuildRails(network);
            Transform roads = transform.Find("Roads");
            if (roads != null && roads.TryGetComponent(out Renderer roadRenderer)) roadRenderer.enabled = false;
            DressSurface("Water", new Color(0.10f, 0.27f, 0.32f), 0.72f, 0.3f);

            var volume = new GameObject("City Colour Grade").AddComponent<Volume>();
            volume.transform.SetParent(transform, false);
            volume.isGlobal = true;
            volume.priority = 10f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            owned.Add(profile);
            volume.sharedProfile = profile;
            var tone = profile.Add<Tonemapping>();
            tone.mode.Override(TonemappingMode.ACES);
            var colour = profile.Add<ColorAdjustments>();
            colour.postExposure.Override(0.2f);
            colour.contrast.Override(12f);
            colour.saturation.Override(-8f);
            var bloom = profile.Add<Bloom>();
            bloom.threshold.Override(1.2f);
            bloom.intensity.Override(0.15f);
            bloom.scatter.Override(0.55f);
            var vignette = profile.Add<Vignette>();
            vignette.intensity.Override(0.16f);
            vignette.smoothness.Override(0.4f);
            foreach (VolumeComponent component in profile.components) owned.Add(component);
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
        }

        private Material Lit(string name, Color colour, float smoothness, float metallic = 0f)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name, color = colour, enableInstancing = true };
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", metallic);
            owned.Add(material);
            return material;
        }

        private void DressSurface(string layer, Color colour, float smoothness, float metallic)
        {
            Transform item = transform.Find(layer);
            if (item != null && item.TryGetComponent(out Renderer renderer)) renderer.sharedMaterial = Lit(layer + " Finish", colour, smoothness, metallic);
        }

        private void BuildRails(TramTrackNetwork network)
        {
            Material steel = Lit("Brushed Steel Rail Head", new Color(0.55f, 0.63f, 0.66f), 0.65f, 0.78f);
            Material web = Lit("Dark Rail Web", new Color(0.17f, 0.19f, 0.20f), 0.35f, 0.55f);
            Material sleepers = Lit("Concrete Rail Supports", new Color(0.32f, 0.31f, 0.28f), 0.12f);
            Material ballast = Lit("Weathered Brown Ballast", Color.white, 0.08f);
            var gravel = new Texture2D(64, 64, TextureFormat.RGBA32, true) { name = "Ballast Aggregate", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var gravelPixels = new Color[64 * 64];
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                uint hash = unchecked((uint)((x / 2) * 73856093 ^ (y / 2) * 19349663));
                float noise = hash % 251 / 250f;
                gravelPixels[y * 64 + x] = Color.Lerp(new Color(0.18f, 0.125f, 0.085f), new Color(0.43f, 0.33f, 0.22f), noise);
            }
            gravel.SetPixels(gravelPixels);
            gravel.Apply(true, true);
            owned.Add(gravel);
            ballast.SetTexture("_BaseMap", gravel);
            Material edging = Lit("Track Bed Retaining Edges", new Color(0.24f, 0.20f, 0.16f), 0.17f);
            var heads = new RailMesh();
            var webs = new RailMesh();
            var supports = new RailMesh();
            var bed = new RailMesh();
            var edges = new RailMesh();
            int chunk = 0;
            foreach (var path in network.Paths)
            {
                if (path.points == null || path.points.Length < 2) continue;
                heads.Ribbon(path.points, 0.34f, 0.13f, 0.23f);
                webs.Ribbon(path.points, 0.13f, -0.10f, 0.13f);
                bed.Ribbon(path.points, 2.2f, -0.25f, -0.16f, 3.2f);
                edges.Ribbon(path.points, 0.12f, -0.24f, -0.12f, 0.16f, -1.48f);
                edges.Ribbon(path.points, 0.12f, -0.24f, -0.12f, 0.16f, 1.48f);
                float distanceToSupport = 0f;
                for (int i = 1; i < path.points.Length; i++)
                {
                    Vector3 a = path.points[i - 1], b = path.points[i];
                    float length = Vector3.Distance(a, b);
                    if (length < 0.001f) continue;
                    for (; distanceToSupport < length; distanceToSupport += 3f)
                    {
                        Vector3 point = Vector3.Lerp(a, b, distanceToSupport / length);
                        Vector3 along = (b - a).normalized * 0.16f;
                        supports.Ribbon(new[] { point - along, point + along }, 1.05f, -0.16f, -0.08f);
                    }
                    distanceToSupport -= length;
                }
                // Small batches keep bounds local and allow frustum culling along the route.
                if (heads.Count > 12000) Flush();
            }
            Flush();

            void Flush()
            {
                if (heads.Count == 0) return;
                Make("Single Steel Rail " + chunk, heads, steel);
                Make("Rail Web " + chunk, webs, web);
                Make("Brown Ballast Bed " + chunk, bed, ballast);
                Make("Track Bed Outer Edges " + chunk, edges, edging);
                Make("Rail Supports " + chunk++, supports, sleepers);
                heads = new RailMesh(); webs = new RailMesh(); supports = new RailMesh();
                bed = new RailMesh(); edges = new RailMesh();
            }
        }

        private void Make(string name, RailMesh builder, Material material)
        {
            Mesh mesh = builder.Build(name);
            owned.Add(mesh);
            Layer(name, mesh, material);
        }

        private void Layer(string name, Mesh mesh, Material material)
        {
            var item = new GameObject(name);
            item.transform.SetParent(transform, false);
            item.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = item.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        private void OnDestroy()
        {
            foreach (Object item in owned) if (item != null) Destroy(item);
        }

        private sealed class RailMesh
        {
            private readonly List<Vector3> vertices = new();
            private readonly List<int> indices = new();
            private readonly List<Vector2> uvs = new();
            public int Count => vertices.Count;

            public void Ribbon(Vector3[] points, float width, float bottom, float top, float baseWidth = 0f, float lateralOffset = 0f)
            {
                // Three continuous faces with mitred joins; each path is one balance rail.
                Vector3 previousSide = Vector3.zero;
                for (int i = 0; i < points.Length; i++)
                {
                    Vector3 incoming = Vector3.ProjectOnPlane(points[i] - points[Mathf.Max(0, i - 1)], Vector3.up).normalized;
                    Vector3 outgoing = Vector3.ProjectOnPlane(points[Mathf.Min(points.Length - 1, i + 1)] - points[i], Vector3.up).normalized;
                    if (incoming.sqrMagnitude < 0.01f) incoming = outgoing;
                    if (outgoing.sqrMagnitude < 0.01f) outgoing = incoming;
                    Vector3 tangent = (incoming + outgoing).normalized;
                    if (tangent.sqrMagnitude < 0.01f) tangent = outgoing;
                    Vector3 normal = Vector3.Cross(Vector3.up, tangent);
                    Vector3 side = normal * (width * 0.5f / Mathf.Max(0.4f, Mathf.Abs(Vector3.Dot(normal, Vector3.Cross(Vector3.up, outgoing)))));
                    if (i > 0)
                    {
                        Vector3 a = points[i - 1] + previousSide * (lateralOffset * 2f / width);
                        Vector3 b = points[i] + side * (lateralOffset * 2f / width);
                        float bottomRatio = baseWidth > 0f ? baseWidth / width : 1f;
                        Quad(a - previousSide + Vector3.up * top, b - side + Vector3.up * top, b + side + Vector3.up * top, a + previousSide + Vector3.up * top);
                        Quad(a - previousSide * bottomRatio + Vector3.up * bottom, b - side * bottomRatio + Vector3.up * bottom, b - side + Vector3.up * top, a - previousSide + Vector3.up * top);
                        Quad(a + previousSide + Vector3.up * top, b + side + Vector3.up * top, b + side * bottomRatio + Vector3.up * bottom, a + previousSide * bottomRatio + Vector3.up * bottom);
                        if (i == 1)
                            Quad(a - previousSide * bottomRatio + Vector3.up * bottom, a - previousSide + Vector3.up * top,
                                a + previousSide + Vector3.up * top, a + previousSide * bottomRatio + Vector3.up * bottom);
                        if (i == points.Length - 1)
                            Quad(b + side * bottomRatio + Vector3.up * bottom, b + side + Vector3.up * top,
                                b - side + Vector3.up * top, b - side * bottomRatio + Vector3.up * bottom);
                    }
                    previousSide = side;
                }
            }

            private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int first = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
                uvs.Add(new Vector2(a.x, a.z)); uvs.Add(new Vector2(b.x, b.z));
                uvs.Add(new Vector2(c.x, c.z)); uvs.Add(new Vector2(d.x, d.z));
                indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
                indices.Add(first); indices.Add(first + 2); indices.Add(first + 3);
            }

            public Mesh Build(string name)
            {
                var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(vertices);
                mesh.SetTriangles(indices, 0);
                mesh.SetUVs(0, uvs);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                mesh.UploadMeshData(true);
                return mesh;
            }
        }
    }
}
