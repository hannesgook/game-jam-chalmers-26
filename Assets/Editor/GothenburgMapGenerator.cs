using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SparvagnRush.Map;
using SparvagnRush.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace SparvagnRush.Editor
{
    public static class GothenburgMapGenerator
    {
        private const string RootName = "GeneratedCity";
        private const string GeneratedAssetFolder = "Assets/GeneratedCity";
        private const string DefaultSource = "Assets/Map_data/export.geojson";

        private const double South = 57.695;
        private const double West = 11.965;
        private const double North = 57.710;
        private const double East = 11.985;
        private const double CenterLat = (South + North) * 0.5;
        private const double CenterLon = (West + East) * 0.5;
        private const double EarthRadius = 6378137.0;

        [InitializeOnLoadMethod]
        private static void GenerateInitialMapAfterImport()
        {
            if (AssetDatabase.IsValidFolder(GeneratedAssetFolder)) return;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string sourcePath = Path.Combine(projectRoot, DefaultSource);
            if (!File.Exists(sourcePath)) return;

            EditorApplication.delayCall += () =>
            {
                if (!AssetDatabase.IsValidFolder(GeneratedAssetFolder)) GenerateFromPath(sourcePath);
            };
        }

        private readonly struct GeoPoint
        {
            public readonly double Lon;
            public readonly double Lat;

            public GeoPoint(double lon, double lat)
            {
                Lon = lon;
                Lat = lat;
            }
        }

        private sealed class MeshBuilder
        {
            public readonly List<Vector3> Vertices = new();
            public readonly List<int> Triangles = new();

            public void AddRibbon(IReadOnlyList<GeoPoint> line, float width, float y)
            {
                float halfWidth = width * 0.5f;
                for (int i = 0; i < line.Count - 1; i++)
                {
                    Vector3 a = Project(line[i], y);
                    Vector3 b = Project(line[i + 1], y);
                    Vector3 direction = b - a;
                    if (direction.sqrMagnitude < 0.0001f) continue;
                    Vector3 side = new Vector3(-direction.z, 0f, direction.x).normalized * halfWidth;
                    int start = Vertices.Count;
                    Vertices.Add(a - side);
                    Vertices.Add(a + side);
                    Vertices.Add(b + side);
                    Vertices.Add(b - side);
                    Triangles.Add(start);
                    Triangles.Add(start + 1);
                    Triangles.Add(start + 2);
                    Triangles.Add(start);
                    Triangles.Add(start + 2);
                    Triangles.Add(start + 3);
                }
            }

            public void AddPolygon(IReadOnlyList<GeoPoint> polygon, float y)
            {
                var projected = new List<Vector3>(polygon.Count);
                for (int i = 0; i < polygon.Count; i++) projected.Add(Project(polygon[i], y));
                if (projected.Count > 1 && (projected[0] - projected[^1]).sqrMagnitude < 0.0001f)
                    projected.RemoveAt(projected.Count - 1);
                if (projected.Count < 3) return;

                List<int> localTriangles = Triangulate(projected);
                int start = Vertices.Count;
                Vertices.AddRange(projected);
                foreach (int index in localTriangles) Triangles.Add(start + index);
            }

            public Mesh Build(string name)
            {
                var mesh = new Mesh { name = name };
                if (Vertices.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(Vertices);
                mesh.SetTriangles(Triangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        [MenuItem("Tools/Göteborg/Generate Map")]
        public static void GenerateMapMenu()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string defaultAbsolute = Path.Combine(projectRoot, DefaultSource);
            string initialDirectory = Directory.Exists(Path.GetDirectoryName(defaultAbsolute))
                ? Path.GetDirectoryName(defaultAbsolute)
                : projectRoot;
            string sourcePath = EditorUtility.OpenFilePanel("Select Overpass GeoJSON", initialDirectory, "geojson,json");
            if (string.IsNullOrWhiteSpace(sourcePath)) return;

            GenerateFromPath(sourcePath);
        }

        public static void GenerateFromDefaultFile() => GenerateFromPath(DefaultSource);

        public static void GenerateFromPath(string sourcePath)
        {
            string absolutePath = Path.IsPathRooted(sourcePath)
                ? sourcePath
                : Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, sourcePath));
            if (!File.Exists(absolutePath)) throw new FileNotFoundException("GeoJSON file not found.", absolutePath);

            Dictionary<string, object> root = GeoJsonLite.ParseObject(File.ReadAllText(absolutePath));
            if (!root.TryGetValue("features", out object featureValue) || featureValue is not List<object> features)
                throw new FormatException("GeoJSON has no features array.");

            GameObject oldRoot = GameObject.Find(RootName);
            if (oldRoot != null) UnityEngine.Object.DestroyImmediate(oldRoot);
            if (AssetDatabase.IsValidFolder(GeneratedAssetFolder)) AssetDatabase.DeleteAsset(GeneratedAssetFolder);
            AssetDatabase.CreateFolder("Assets", "GeneratedCity");

            var roadMesh = new MeshBuilder();
            var tramMesh = new MeshBuilder();
            var waterMesh = new MeshBuilder();
            var trackPaths = new List<TramTrackNetwork.TrackPath>();
            int roadFeatures = 0;
            int tramFeatures = 0;
            int waterFeatures = 0;

            foreach (object item in features)
            {
                if (item is not Dictionary<string, object> feature ||
                    !TryObject(feature, "geometry", out Dictionary<string, object> geometry) ||
                    !TryObject(feature, "properties", out Dictionary<string, object> properties) ||
                    !TryString(geometry, "type", out string geometryType) ||
                    !geometry.TryGetValue("coordinates", out object coordinates))
                    continue;

                bool isTram = PropertyEquals(properties, "railway", "tram");
                bool isRoad = properties.ContainsKey("highway");
                bool isWater = PropertyEquals(properties, "natural", "water");

                if (geometryType == "LineString" && (isTram || isRoad))
                {
                    List<GeoPoint> line = ReadLine(coordinates);
                    List<List<GeoPoint>> clippedLines = ClipPolyline(line);
                    if (isRoad)
                    {
                        foreach (List<GeoPoint> clipped in clippedLines) roadMesh.AddRibbon(clipped, 2.2f, 0.025f);
                        roadFeatures++;
                    }
                    if (isTram)
                    {
                        string osmId = TryString(properties, "@id", out string id) ? id : "tram";
                        foreach (List<GeoPoint> clipped in clippedLines)
                        {
                            tramMesh.AddRibbon(clipped, 3.4f, 0.06f);
                            var points = new Vector3[clipped.Count];
                            for (int i = 0; i < clipped.Count; i++) points[i] = Project(clipped[i], 0.06f);
                            trackPaths.Add(new TramTrackNetwork.TrackPath { osmId = osmId, points = points });
                        }
                        tramFeatures++;
                    }
                }
                else if (geometryType == "Polygon" && isWater && coordinates is List<object> rings && rings.Count > 0)
                {
                    List<GeoPoint> polygon = ClipPolygon(ReadLine(rings[0]));
                    waterMesh.AddPolygon(polygon, 0.01f);
                    waterFeatures++;
                }
            }

            var generatedRoot = new GameObject(RootName);
            CreateLayer(generatedRoot.transform, "Ground", CreateGroundMesh(), CreateMaterial("Ground", new Color(0.18f, 0.22f, 0.19f)));
            CreateLayer(generatedRoot.transform, "Water", waterMesh.Build("WaterMesh"), CreateMaterial("Water", new Color(0.06f, 0.38f, 0.58f)));
            CreateLayer(generatedRoot.transform, "Roads", roadMesh.Build("RoadMesh"), CreateMaterial("Roads", new Color(0.25f, 0.27f, 0.28f)));
            GameObject tracks = CreateLayer(generatedRoot.transform, "TramTracks", tramMesh.Build("TramTrackMesh"), CreateMaterial("TramTracks", new Color(0.96f, 0.72f, 0.12f)));
            TramTrackNetwork network = tracks.AddComponent<TramTrackNetwork>();
            network.ReplacePaths(trackPaths);
            generatedRoot.AddComponent<SparvagnRushBootstrap>().SetNetwork(network);
            CreateLandmark(generatedRoot.transform);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = generatedRoot;
            AssetDatabase.SaveAssets();
            if (EditorSceneManager.GetActiveScene().IsValid() && !string.IsNullOrEmpty(EditorSceneManager.GetActiveScene().path))
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log($"Generated Göteborg map from {Path.GetFileName(absolutePath)}: {tramFeatures} tram features ({trackPaths.Count} clipped paths), {roadFeatures} roads, {waterFeatures} water polygons.");
        }

        private static GameObject CreateLayer(Transform parent, string name, Mesh mesh, Material material)
        {
            string meshPath = $"{GeneratedAssetFolder}/{name}.asset";
            AssetDatabase.CreateAsset(mesh, meshPath);
            var layer = new GameObject(name);
            layer.transform.SetParent(parent, false);
            layer.AddComponent<MeshFilter>().sharedMesh = mesh;
            layer.AddComponent<MeshRenderer>().sharedMaterial = material;
            return layer;
        }

        private static Material CreateMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = name, color = color };
            AssetDatabase.CreateAsset(material, $"{GeneratedAssetFolder}/{name}.mat");
            return material;
        }

        private static void CreateLandmark(Transform parent)
        {
            GameObject tower = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tower.name = "Karlatornet_Stylized";
            tower.transform.SetParent(parent, false);
            tower.transform.position = Project(new GeoPoint(11.9793, 57.6974), 90f);
            tower.transform.localScale = new Vector3(18f, 180f, 18f);
            UnityEngine.Object.DestroyImmediate(tower.GetComponent<Collider>());
            tower.GetComponent<MeshRenderer>().sharedMaterial = CreateMaterial("Landmark", new Color(0.72f, 0.82f, 0.9f));
        }

        private static Mesh CreateGroundMesh()
        {
            Vector3 southWest = Project(new GeoPoint(West, South), 0f);
            Vector3 northEast = Project(new GeoPoint(East, North), 0f);
            var mesh = new Mesh { name = "GroundMesh" };
            mesh.vertices = new[]
            {
                new Vector3(southWest.x, 0f, southWest.z),
                new Vector3(southWest.x, 0f, northEast.z),
                new Vector3(northEast.x, 0f, northEast.z),
                new Vector3(northEast.x, 0f, southWest.z)
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3 Project(GeoPoint point, float y)
        {
            double latRadians = CenterLat * Math.PI / 180.0;
            float x = (float)((point.Lon - CenterLon) * Math.PI / 180.0 * EarthRadius * Math.Cos(latRadians));
            float z = (float)((point.Lat - CenterLat) * Math.PI / 180.0 * EarthRadius);
            return new Vector3(x, y, z);
        }

        private static List<GeoPoint> ReadLine(object coordinateValue)
        {
            var result = new List<GeoPoint>();
            if (coordinateValue is not List<object> coordinates) return result;
            foreach (object coordinate in coordinates)
            {
                if (coordinate is not List<object> pair || pair.Count < 2) continue;
                result.Add(new GeoPoint(ToDouble(pair[0]), ToDouble(pair[1])));
            }
            return result;
        }

        private static double ToDouble(object value) => value switch
        {
            double number => number,
            long integer => integer,
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture)
        };

        private static List<List<GeoPoint>> ClipPolyline(IReadOnlyList<GeoPoint> source)
        {
            var result = new List<List<GeoPoint>>();
            List<GeoPoint> current = null;
            for (int i = 0; i < source.Count - 1; i++)
            {
                GeoPoint a = source[i];
                GeoPoint b = source[i + 1];
                if (!ClipSegment(ref a, ref b))
                {
                    current = null;
                    continue;
                }

                if (current == null || !SamePoint(current[^1], a))
                {
                    current = new List<GeoPoint> { a, b };
                    result.Add(current);
                }
                else if (!SamePoint(current[^1], b))
                {
                    current.Add(b);
                }
            }
            return result;
        }

        private static bool ClipSegment(ref GeoPoint a, ref GeoPoint b)
        {
            double dx = b.Lon - a.Lon;
            double dy = b.Lat - a.Lat;
            double t0 = 0.0;
            double t1 = 1.0;
            if (!ClipTest(-dx, a.Lon - West, ref t0, ref t1) ||
                !ClipTest(dx, East - a.Lon, ref t0, ref t1) ||
                !ClipTest(-dy, a.Lat - South, ref t0, ref t1) ||
                !ClipTest(dy, North - a.Lat, ref t0, ref t1))
                return false;

            GeoPoint original = a;
            a = new GeoPoint(original.Lon + t0 * dx, original.Lat + t0 * dy);
            b = new GeoPoint(original.Lon + t1 * dx, original.Lat + t1 * dy);
            return true;
        }

        private static bool ClipTest(double p, double q, ref double t0, ref double t1)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0.0;
            double r = q / p;
            if (p < 0.0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }

        private static List<GeoPoint> ClipPolygon(List<GeoPoint> polygon)
        {
            if (polygon.Count > 1 && SamePoint(polygon[0], polygon[^1])) polygon.RemoveAt(polygon.Count - 1);
            polygon = ClipEdge(polygon, p => p.Lon >= West, (a, b) => IntersectLon(a, b, West));
            polygon = ClipEdge(polygon, p => p.Lon <= East, (a, b) => IntersectLon(a, b, East));
            polygon = ClipEdge(polygon, p => p.Lat >= South, (a, b) => IntersectLat(a, b, South));
            polygon = ClipEdge(polygon, p => p.Lat <= North, (a, b) => IntersectLat(a, b, North));
            return polygon;
        }

        private static List<GeoPoint> ClipEdge(List<GeoPoint> input, Func<GeoPoint, bool> inside, Func<GeoPoint, GeoPoint, GeoPoint> intersect)
        {
            var output = new List<GeoPoint>();
            if (input.Count == 0) return output;
            GeoPoint previous = input[^1];
            bool previousInside = inside(previous);
            foreach (GeoPoint current in input)
            {
                bool currentInside = inside(current);
                if (currentInside != previousInside) output.Add(intersect(previous, current));
                if (currentInside) output.Add(current);
                previous = current;
                previousInside = currentInside;
            }
            return output;
        }

        private static GeoPoint IntersectLon(GeoPoint a, GeoPoint b, double lon)
        {
            double t = Math.Abs(b.Lon - a.Lon) < 1e-12 ? 0.0 : (lon - a.Lon) / (b.Lon - a.Lon);
            return new GeoPoint(lon, a.Lat + (b.Lat - a.Lat) * t);
        }

        private static GeoPoint IntersectLat(GeoPoint a, GeoPoint b, double lat)
        {
            double t = Math.Abs(b.Lat - a.Lat) < 1e-12 ? 0.0 : (lat - a.Lat) / (b.Lat - a.Lat);
            return new GeoPoint(a.Lon + (b.Lon - a.Lon) * t, lat);
        }

        private static bool SamePoint(GeoPoint a, GeoPoint b) => Math.Abs(a.Lon - b.Lon) < 1e-10 && Math.Abs(a.Lat - b.Lat) < 1e-10;

        private static List<int> Triangulate(IReadOnlyList<Vector3> points)
        {
            var indices = new List<int>();
            var remaining = new List<int>(points.Count);
            bool clockwise = SignedArea(points) < 0f;
            if (clockwise)
            {
                for (int i = 0; i < points.Count; i++) remaining.Add(i);
            }
            else
            {
                for (int i = points.Count - 1; i >= 0; i--) remaining.Add(i);
            }

            int guard = points.Count * points.Count;
            while (remaining.Count > 2 && guard-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int previous = remaining[(i - 1 + remaining.Count) % remaining.Count];
                    int current = remaining[i];
                    int next = remaining[(i + 1) % remaining.Count];
                    if (!IsConvex(points[previous], points[current], points[next])) continue;

                    bool containsPoint = false;
                    for (int j = 0; j < remaining.Count; j++)
                    {
                        int candidate = remaining[j];
                        if (candidate == previous || candidate == current || candidate == next) continue;
                        if (PointInTriangle(points[candidate], points[previous], points[current], points[next]))
                        {
                            containsPoint = true;
                            break;
                        }
                    }
                    if (containsPoint) continue;

                    indices.Add(previous);
                    indices.Add(current);
                    indices.Add(next);
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            return indices;
        }

        private static float SignedArea(IReadOnlyList<Vector3> points)
        {
            float area = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 a = points[i];
                Vector3 b = points[(i + 1) % points.Count];
                area += a.x * b.z - b.x * a.z;
            }
            return area * 0.5f;
        }

        private static bool IsConvex(Vector3 a, Vector3 b, Vector3 c) => Cross(a, b, c) < -0.00001f;

        private static float Cross(Vector3 a, Vector3 b, Vector3 c) => (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);

        private static bool PointInTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            float ab = Cross(a, b, p);
            float bc = Cross(b, c, p);
            float ca = Cross(c, a, p);
            bool hasNegative = ab < 0f || bc < 0f || ca < 0f;
            bool hasPositive = ab > 0f || bc > 0f || ca > 0f;
            return !(hasNegative && hasPositive);
        }

        private static bool PropertyEquals(Dictionary<string, object> properties, string key, string expected) =>
            TryString(properties, key, out string value) && string.Equals(value, expected, StringComparison.Ordinal);

        private static bool TryString(Dictionary<string, object> source, string key, out string value)
        {
            if (source.TryGetValue(key, out object raw) && raw is string text)
            {
                value = text;
                return true;
            }
            value = null;
            return false;
        }

        private static bool TryObject(Dictionary<string, object> source, string key, out Dictionary<string, object> value)
        {
            if (source.TryGetValue(key, out object raw) && raw is Dictionary<string, object> result)
            {
                value = result;
                return true;
            }
            value = null;
            return false;
        }
    }
}
