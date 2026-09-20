using System.Collections.Generic;
using UnityEngine;

namespace TramRush.Gameplay
{
    // Test the rendered map footprint in XZ: ground also exists under the water,
    // so a ground raycast alone cannot tell whether a pedestrian is on dry land.
    public sealed class PedestrianWalkableArea
    {
        private const float CellSize = 32f;
        private const float Clearance = 0.8f;
        private readonly Dictionary<Vector2Int, List<int>> cells = new();
        private readonly List<Vector2[]> triangles = new();
        // Rails are the one obstruction somebody can decide to walk onto. Water and
        // walls stop everybody, so each triangle remembers which kind it is.
        private readonly List<bool> isTrack = new();
        private readonly HashSet<int> visited = new();
        public bool Ready { get; }

        public PedestrianWalkableArea(Transform mapRoot)
        {
            Ready = AddLayer(mapRoot.Find("TramTracks"), true) && AddLayer(mapRoot.Find("Water"), false);
            AddLayer(mapRoot.Find("Buildings"), false);
            if (!Ready) Debug.LogWarning("Pedestrians disabled: readable track and water meshes are required.");
        }

        private bool AddLayer(Transform layer, bool track)
        {
            Mesh mesh = layer != null ? layer.GetComponent<MeshFilter>()?.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) return false;
            Vector3[] vertices = mesh.vertices;
            int[] indices = mesh.triangles;
            for (int i = 0; i < indices.Length; i += 3)
            {
                var triangle = new Vector2[3];
                for (int j = 0; j < 3; j++) triangle[j] = Flat(layer.TransformPoint(vertices[indices[i + j]]));
                int index = triangles.Count;
                triangles.Add(triangle);
                isTrack.Add(track);
                Vector2 min = Vector2.Min(triangle[0], Vector2.Min(triangle[1], triangle[2]));
                Vector2 max = Vector2.Max(triangle[0], Vector2.Max(triangle[1], triangle[2]));
                Vector2Int first = Cell(min), last = Cell(max);
                for (int x = first.x; x <= last.x; x++)
                for (int y = first.y; y <= last.y; y++)
                {
                    var key = new Vector2Int(x, y);
                    if (!cells.TryGetValue(key, out List<int> bucket)) cells.Add(key, bucket = new List<int>());
                    bucket.Add(index);
                }
            }
            return true;
        }

        /// <param name="avoidTrack">
        /// False for somebody walking onto the rails on purpose. Water and buildings
        /// still block them; only the track exclusion is lifted.
        /// </param>
        public bool Allows(Vector3 from, Vector3 to, bool avoidTrack = true)
        {
            if (!Ready) return false;
            Vector2 a = Flat(from), b = Flat(to);
            Vector2Int first = Cell(Vector2.Min(a, b) - Vector2.one * Clearance);
            Vector2Int last = Cell(Vector2.Max(a, b) + Vector2.one * Clearance);
            visited.Clear();
            for (int x = first.x; x <= last.x; x++)
            for (int y = first.y; y <= last.y; y++)
            {
                if (!cells.TryGetValue(new Vector2Int(x, y), out List<int> bucket)) continue;
                foreach (int index in bucket)
                {
                    if (!visited.Add(index)) continue;
                    if (!avoidTrack && isTrack[index]) continue;
                    Vector2[] t = triangles[index];
                    if (Inside(a, t) || Inside(b, t)) return false;
                    for (int edge = 0; edge < 3; edge++)
                        if (SegmentDistanceSquared(a, b, t[edge], t[(edge + 1) % 3]) <= Clearance * Clearance)
                            return false;
                }
            }
            return true;
        }

        private static Vector2 Flat(Vector3 point) => new(point.x, point.z);
        private static Vector2Int Cell(Vector2 p) => new(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize));
        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static bool Inside(Vector2 p, Vector2[] t)
        {
            if (Mathf.Abs(Cross(t[1] - t[0], t[2] - t[0])) < 0.00001f) return false;
            float a = Cross(t[1] - t[0], p - t[0]);
            float b = Cross(t[2] - t[1], p - t[1]);
            float c = Cross(t[0] - t[2], p - t[2]);
            return (a >= 0f && b >= 0f && c >= 0f) || (a <= 0f && b <= 0f && c <= 0f);
        }

        private static float SegmentDistanceSquared(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            Vector2 ab = b - a, cd = d - c;
            float denominator = Cross(ab, cd);
            if (Mathf.Abs(denominator) > 0.000001f)
            {
                float t = Cross(c - a, cd) / denominator;
                float u = Cross(c - a, ab) / denominator;
                if (t >= 0f && t <= 1f && u >= 0f && u <= 1f) return 0f;
            }
            return Mathf.Min(Mathf.Min(PointDistanceSquared(a, c, d), PointDistanceSquared(b, c, d)),
                Mathf.Min(PointDistanceSquared(c, a, b), PointDistanceSquared(d, a, b)));
        }

        private static float PointDistanceSquared(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 delta = b - a;
            float t = delta.sqrMagnitude < 0.000001f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, delta) / delta.sqrMagnitude);
            return (p - (a + delta * t)).sqrMagnitude;
        }
    }
}
