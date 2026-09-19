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
    public static partial class GothenburgMapGenerator
    {
        private const string RootName = "GeneratedCity";
        private const string GeneratedAssetFolder = "Assets/GeneratedCity";
        private const string DefaultSource = "Assets/Map_data/export.geojson";
        private const string DefaultHeightSource = "Assets/Map_data/gothenburg_height_513.bytes";
        private const string DefaultBuildingSource = "Assets/Map_data/buildings.geojson";
        // Matches the heightmap grid exactly so ground vertices land on real samples;
        // a coarser grid interpolates across them and lets roads sink into hillsides.
        private const int GroundMeshResolution = 513;
        // Heightmap cells are ~2.5 m across. OSM leaves long straight runs undivided
        // (12% of segments exceed 25 m), so without resampling a ribbon only samples the
        // terrain at its endpoints and buries itself in, or floats over, everything between.
        private const double MaxSegmentMeters = 2.5;
        // OSM tags storeys far more often than metres around Järntorget (296 footprints
        // against 46), so most heights come out of these. 3.2 m is the floor-to-floor of
        // the stone city; a roof storey is the shallower space inside the pitch.
        private const float LevelHeight = 3.2f;
        private const float RoofLevelHeight = 2.4f;
        // Walls start below the lowest ground sample under the footprint, so a building
        // on a Masthugget slope meets the hillside instead of hovering over its low side.
        private const float FoundationDepth = 0.6f;

        private const double South = 57.695;
        private const double West = 11.965;
        private const double North = 57.710;
        private const double East = 11.985;
        private const double CenterLat = (South + North) * 0.5;
        private const double CenterLon = (West + East) * 0.5;
        private const double EarthRadius = 6378137.0;
        private static HeightField currentHeightField;

        private sealed class HeightField
        {
            private readonly int width;
            private readonly int height;
            private readonly float minimum;
            private readonly float range;
            private readonly ushort[] samples;

            private HeightField(int width, int height, float minimum, float maximum, ushort[] samples)
            {
                this.width = width;
                this.height = height;
                this.minimum = minimum;
                range = maximum - minimum;
                this.samples = samples;
            }

            public static HeightField Load(string path)
            {
                if (!File.Exists(path)) return null;
                using var reader = new BinaryReader(File.OpenRead(path));
                string magic = new string(reader.ReadChars(4));
                if (magic != "SRH1") throw new FormatException($"Unsupported heightmap format in {path}.");
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                float minimum = reader.ReadSingle();
                float maximum = reader.ReadSingle();
                if (width < 2 || height < 2 || maximum <= minimum) throw new FormatException($"Invalid heightmap header in {path}.");
                var samples = new ushort[width * height];
                for (int i = 0; i < samples.Length; i++) samples[i] = reader.ReadUInt16();
                return new HeightField(width, height, minimum, maximum, samples);
            }

            public float Sample(double longitude, double latitude)
            {
                double u = Math.Clamp((longitude - West) / (East - West), 0.0, 1.0) * (width - 1);
                double v = Math.Clamp((latitude - South) / (North - South), 0.0, 1.0) * (height - 1);
                int x0 = Math.Min(width - 1, (int)Math.Floor(u));
                int y0 = Math.Min(height - 1, (int)Math.Floor(v));
                int x1 = Math.Min(width - 1, x0 + 1);
                int y1 = Math.Min(height - 1, y0 + 1);
                float tx = (float)(u - x0);
                float ty = (float)(v - y0);
                float a = Mathf.Lerp(Decode(x0, y0), Decode(x1, y0), tx);
                float b = Mathf.Lerp(Decode(x0, y1), Decode(x1, y1), tx);
                return Mathf.Lerp(a, b, ty);
            }

            private float Decode(int x, int y) => minimum + samples[y * width + x] / 65535f * range;
        }

        [InitializeOnLoadMethod]
        private static void GenerateInitialMapAfterImport()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string sourcePath = Path.Combine(projectRoot, DefaultSource);
            if (!File.Exists(sourcePath)) return;

            bool generatedAssetsExist = AssetDatabase.IsValidFolder(GeneratedAssetFolder);
            bool heightDataExists = File.Exists(Path.Combine(projectRoot, DefaultHeightSource));
            Mesh groundMesh = AssetDatabase.LoadAssetAtPath<Mesh>($"{GeneratedAssetFolder}/Ground.asset");
            bool elevationUpgradeNeeded = heightDataExists && (groundMesh == null || groundMesh.vertexCount != GroundMeshResolution * GroundMeshResolution);
            // A project generated before the building import has every other layer already,
            // so nothing would ever rebuild it without asking after this one too.
            bool buildingUpgradeNeeded = File.Exists(Path.Combine(projectRoot, DefaultBuildingSource)) &&
                AssetDatabase.LoadAssetAtPath<Mesh>($"{GeneratedAssetFolder}/Buildings.asset") == null;
            if (generatedAssetsExist && !elevationUpgradeNeeded && !buildingUpgradeNeeded) return;

            EditorApplication.delayCall += () =>
            {
                GenerateFromPath(sourcePath);
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

            // One continuous strip with mitred joints. Emitting a loose quad per segment
            // instead leaves a wedge of open ground on the outside of every bend, which
            // at tram width is most of a metre on the sharper curves.
            public void AddRibbon(IReadOnlyList<GeoPoint> line, float width, float y)
            {
                float halfWidth = width * 0.5f;
                var points = new List<Vector3>(line.Count);
                for (int i = 0; i < line.Count; i++)
                {
                    Vector3 point = Project(line[i], y);
                    if (points.Count == 0 || (point - points[^1]).sqrMagnitude > 0.0001f) points.Add(point);
                }
                if (points.Count < 2) return;

                int start = Vertices.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    Vector3 incoming = i > 0 ? Flatten(points[i] - points[i - 1]) : Vector3.zero;
                    Vector3 outgoing = i < points.Count - 1 ? Flatten(points[i + 1] - points[i]) : Vector3.zero;
                    if (incoming.sqrMagnitude < 0.0001f) incoming = outgoing;
                    if (outgoing.sqrMagnitude < 0.0001f) outgoing = incoming;
                    if (incoming.sqrMagnitude < 0.0001f) continue;
                    Vector3 side = MitredOffset(incoming.normalized, outgoing.normalized, halfWidth);
                    Vertices.Add(points[i] - side);
                    Vertices.Add(points[i] + side);
                }

                for (int i = 0; i < points.Count - 1; i++)
                {
                    int corner = start + i * 2;
                    Triangles.Add(corner);
                    Triangles.Add(corner + 1);
                    Triangles.Add(corner + 3);
                    Triangles.Add(corner);
                    Triangles.Add(corner + 3);
                    Triangles.Add(corner + 2);
                }
            }

            private static Vector3 Flatten(Vector3 value) => new(value.x, 0f, value.z);

            // Widens the joint by 1/cos(half the turn) so the strip keeps its width
            // through a bend. The clamp stops a hairpin from throwing out a long spike.
            private static Vector3 MitredOffset(Vector3 incoming, Vector3 outgoing, float halfWidth)
            {
                Vector3 tangent = incoming + outgoing;
                // Doubling straight back has no bisector; square the joint off instead.
                if (tangent.sqrMagnitude < 0.0001f) tangent = outgoing;
                tangent = tangent.normalized;
                var miter = new Vector3(-tangent.z, 0f, tangent.x);
                var outgoingNormal = new Vector3(-outgoing.z, 0f, outgoing.x);
                float cosHalfTurn = Mathf.Abs(Vector3.Dot(miter, outgoingNormal));
                return miter * (halfWidth / Mathf.Max(0.35f, cosHalfTurn));
            }

            public void AddPolygon(IReadOnlyList<GeoPoint> polygon, float y)
            {
                var projected = new List<Vector3>(polygon.Count);
                var elevations = new List<float>(polygon.Count);
                for (int i = 0; i < polygon.Count; i++)
                    elevations.Add(currentHeightField?.Sample(polygon[i].Lon, polygon[i].Lat) ?? 0f);
                elevations.Sort();
                float waterLevel = elevations.Count == 0 ? 0f : elevations[elevations.Count / 2];
                for (int i = 0; i < polygon.Count; i++) projected.Add(ProjectAtElevation(polygon[i], waterLevel + y));
                if (projected.Count > 1 && (projected[0] - projected[^1]).sqrMagnitude < 0.0001f)
                    projected.RemoveAt(projected.Count - 1);
                if (projected.Count < 3) return;

                List<int> localTriangles = Triangulate(projected);
                int start = Vertices.Count;
                Vertices.AddRange(projected);
                foreach (int index in localTriangles) Triangles.Add(start + index);
            }

            // Every quad carries its own vertices: sharing them around a corner would let
            // RecalculateNormals average the two wall planes and round the building off.
            public void AddWallRing(IReadOnlyList<Vector3> ring, float baseY, float topY)
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    Vector3 a = ring[i];
                    Vector3 b = ring[(i + 1) % ring.Count];
                    int corner = Vertices.Count;
                    Vertices.Add(new Vector3(a.x, baseY, a.z));
                    Vertices.Add(new Vector3(b.x, baseY, b.z));
                    Vertices.Add(new Vector3(b.x, topY, b.z));
                    Vertices.Add(new Vector3(a.x, topY, a.z));
                    Triangles.Add(corner);
                    Triangles.Add(corner + 2);
                    Triangles.Add(corner + 3);
                    Triangles.Add(corner);
                    Triangles.Add(corner + 1);
                    Triangles.Add(corner + 2);
                }
            }

            // Caps an already-projected outline at one height. Unlike AddPolygon this
            // takes the elevation as given, because a roof sits on its walls rather than
            // on whatever the terrain happens to do underneath it.
            public void AddCap(IReadOnlyList<Vector3> outline, float y)
            {
                if (outline.Count < 3) return;
                var flat = new List<Vector3>(outline.Count);
                for (int i = 0; i < outline.Count; i++) flat.Add(new Vector3(outline[i].x, y, outline[i].z));
                List<int> localTriangles = Triangulate(flat);
                int start = Vertices.Count;
                Vertices.AddRange(flat);
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

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string heightPath = Path.GetFullPath(Path.Combine(projectRoot, DefaultHeightSource));
            currentHeightField = HeightField.Load(heightPath);
            if (currentHeightField == null)
                Debug.LogWarning($"Heightmap not found at {DefaultHeightSource}; generating the legacy flat map.");

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
            var buildingMesh = new MeshBuilder();
            var roofMesh = new MeshBuilder();
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
                    for (int i = 0; i < clippedLines.Count; i++) clippedLines[i] = Densify(clippedLines[i]);
                    if (isRoad)
                    {
                        foreach (List<GeoPoint> clipped in clippedLines) roadMesh.AddRibbon(clipped, 2.2f, 0.12f);
                        roadFeatures++;
                    }
                    if (isTram)
                    {
                        string osmId = TryString(properties, "@id", out string id) ? id : "tram";
                        foreach (List<GeoPoint> clipped in clippedLines)
                        {
                            tramMesh.AddRibbon(clipped, 3.4f, 0.28f);
                            var points = new Vector3[clipped.Count];
                            for (int i = 0; i < clipped.Count; i++) points[i] = Project(clipped[i], 0.28f);
                            trackPaths.Add(new TramTrackNetwork.TrackPath { osmId = osmId, points = points });
                        }
                        tramFeatures++;
                    }
                }
                else if (geometryType == "Polygon" && isWater && coordinates is List<object> rings && rings.Count > 0)
                {
                    List<GeoPoint> polygon = ClipPolygon(ReadLine(rings[0]));
                    waterMesh.AddPolygon(polygon, 0.08f);
                    waterFeatures++;
                }
            }

            int buildingCount = AppendBuildings(buildingMesh, roofMesh);

            var generatedRoot = new GameObject(RootName);
            CreateLayer(generatedRoot.transform, "Ground", CreateGroundMesh(), CreateMaterial("Ground", new Color(0.18f, 0.22f, 0.19f), true), true);
            CreateLayer(generatedRoot.transform, "Water", waterMesh.Build("WaterMesh"), CreateMaterial("Water", new Color(0.06f, 0.38f, 0.58f)));
            CreateLayer(generatedRoot.transform, "Roads", roadMesh.Build("RoadMesh"), CreateMaterial("Roads", new Color(0.25f, 0.27f, 0.28f)));
            // Walls and roofs are split so the city reads as blocks from the tram window;
            // both are lit, because an unlit extrusion is a silhouette with no corners.
            CreateLayer(generatedRoot.transform, "Buildings", buildingMesh.Build("BuildingMesh"), CreateMaterial("Buildings", new Color(0.62f, 0.58f, 0.53f), true), true);
            CreateLayer(generatedRoot.transform, "BuildingRoofs", roofMesh.Build("BuildingRoofMesh"), CreateMaterial("BuildingRoofs", new Color(0.29f, 0.28f, 0.3f), true), true);
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
            Debug.Log($"Generated Göteborg map from {Path.GetFileName(absolutePath)} using {(currentHeightField == null ? "flat ground" : "513x513 Göteborg elevation data")}: {tramFeatures} tram features ({trackPaths.Count} clipped paths), {roadFeatures} roads, {waterFeatures} water polygons, {buildingCount} buildings.");
        }

        private static GameObject CreateLayer(Transform parent, string name, Mesh mesh, Material material, bool collidable = false)
        {
            string meshPath = $"{GeneratedAssetFolder}/{name}.asset";
            AssetDatabase.CreateAsset(mesh, meshPath);
            var layer = new GameObject(name);
            layer.transform.SetParent(parent, false);
            layer.AddComponent<MeshFilter>().sharedMesh = mesh;
            layer.AddComponent<MeshRenderer>().sharedMaterial = material;
            // The ground gives a derailed tram somewhere to land; the walls and roofs
            // give it something to hit on the way. Roads and water stay collider-free:
            // they sit on the ground surface and would only add cook time.
            if (collidable) layer.AddComponent<MeshCollider>().sharedMesh = mesh;
            return layer;
        }

        private static Material CreateMaterial(string name, Color color, bool lit = false)
        {
            Shader shader = lit
                ? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")
                : Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = name, color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0f);
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
            var mesh = new Mesh { name = "GroundMesh" };
            if (currentHeightField == null)
            {
                Vector3 southWest = Project(new GeoPoint(West, South), 0f);
                Vector3 northEast = Project(new GeoPoint(East, North), 0f);
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

            int resolution = GroundMeshResolution;
            var vertices = new Vector3[resolution * resolution];
            var triangles = new int[(resolution - 1) * (resolution - 1) * 6];
            for (int z = 0; z < resolution; z++)
            {
                double latitude = South + (North - South) * z / (resolution - 1);
                for (int x = 0; x < resolution; x++)
                {
                    double longitude = West + (East - West) * x / (resolution - 1);
                    vertices[z * resolution + x] = Project(new GeoPoint(longitude, latitude), 0f);
                }
            }

            int triangle = 0;
            for (int z = 0; z < resolution - 1; z++)
            for (int x = 0; x < resolution - 1; x++)
            {
                int bottomLeft = z * resolution + x;
                int topLeft = bottomLeft + resolution;
                triangles[triangle++] = bottomLeft;
                triangles[triangle++] = topLeft;
                triangles[triangle++] = topLeft + 1;
                triangles[triangle++] = bottomLeft;
                triangles[triangle++] = topLeft + 1;
                triangles[triangle++] = bottomLeft + 1;
            }

            mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Buildings come from their own Overpass export (Tools/FetchBuildings.py) because
        // the road export predates them; keeping the two files apart also means a refetch
        // of one cannot disturb the other.
        private static int AppendBuildings(MeshBuilder walls, MeshBuilder roofs)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string path = Path.GetFullPath(Path.Combine(projectRoot, DefaultBuildingSource));
            if (!File.Exists(path))
            {
                Debug.LogWarning($"Building data not found at {DefaultBuildingSource}; generating the map without buildings. Run Tools/Göteborg/Download Building Data to fetch it.");
                return 0;
            }

            Dictionary<string, object> root = GeoJsonLite.ParseObject(File.ReadAllText(path));
            if (!root.TryGetValue("features", out object featureValue) || featureValue is not List<object> features)
                throw new FormatException($"{DefaultBuildingSource} has no features array.");

            int built = 0;
            foreach (object item in features)
            {
                if (item is not Dictionary<string, object> feature ||
                    !TryObject(feature, "geometry", out Dictionary<string, object> geometry) ||
                    !TryObject(feature, "properties", out Dictionary<string, object> properties) ||
                    !TryString(geometry, "type", out string geometryType) ||
                    geometryType != "Polygon" ||
                    !geometry.TryGetValue("coordinates", out object coordinates) ||
                    coordinates is not List<object> rings ||
                    rings.Count == 0)
                    continue;

                if (AppendBuilding(walls, roofs, rings, properties)) built++;
            }
            return built;
        }

        private static bool AppendBuilding(MeshBuilder walls, MeshBuilder roofs, List<object> rings, Dictionary<string, object> properties)
        {
            List<GeoPoint> outline = ClipPolygon(ReadLine(rings[0]));
            if (outline.Count < 3) return false;

            float lowestGround = float.MaxValue;
            float groundSum = 0f;
            foreach (GeoPoint point in outline)
            {
                float elevation = currentHeightField?.Sample(point.Lon, point.Lat) ?? 0f;
                lowestGround = Mathf.Min(lowestGround, elevation);
                groundSum += elevation;
            }
            // OSM measures height from the ground the building stands on, which on a slope
            // is neither corner: the average over the footprint is the closest thing to it.
            float ground = groundSum / outline.Count;

            List<Vector3> outer = ProjectRing(outline);
            float area = Mathf.Abs(SignedArea(outer));
            if (outer.Count < 3 || area < 1f) return false;

            float topY = ground + ResolveBuildingHeight(properties, area);
            float baseY = TryLength(properties, "min_height", out float minHeight)
                ? ground + minHeight
                : lowestGround - FoundationDepth;
            if (topY - baseY < 0.5f) return false;

            // Triangulate walks a clockwise outline, and the wall quads are wound to face
            // outwards from one; the holes run the other way so their walls turn inwards
            // and a courtyard reads as a courtyard.
            if (SignedArea(outer) > 0f) outer.Reverse();

            var holes = new List<List<Vector3>>();
            for (int i = 1; i < rings.Count; i++)
            {
                List<GeoPoint> ring = ReadLine(rings[i]);
                // A courtyard only survives whole. Clipping one against the map edge would
                // leave the hole open to the outside, so drop it and roof the block over.
                if (!WithinBounds(ring)) continue;
                List<Vector3> hole = ProjectRing(ring);
                if (hole.Count < 3 || Mathf.Abs(SignedArea(hole)) < 1f) continue;
                if (SignedArea(hole) < 0f) hole.Reverse();
                holes.Add(hole);
            }

            walls.AddWallRing(outer, baseY, topY);
            foreach (List<Vector3> hole in holes) walls.AddWallRing(hole, baseY, topY);
            roofs.AddCap(MergeHoles(outer, holes), topY);
            return true;
        }

        private static float ResolveBuildingHeight(Dictionary<string, object> properties, float footprintArea)
        {
            if (TryLength(properties, "height", out float height)) return Mathf.Max(2f, height);

            if (TryLength(properties, "building:levels", out float levels) && levels > 0f)
            {
                float roofLevels = TryLength(properties, "roof:levels", out float tagged) ? tagged : 0f;
                return levels * LevelHeight + roofLevels * RoofLevelHeight;
            }

            // Two thirds of the footprints here carry no vertical tag at all, so the rest is
            // inferred from what the building is. The guesses lean on this window being
            // Linnestaden and Masthugget: mostly five- and six-storey stone-city blocks with
            // single-storey yard buildings behind them.
            TryString(properties, "building", out string kind);
            return kind switch
            {
                "roof" or "carport" or "shed" or "garage" or "garages" or "hut" or "kiosk"
                    or "shelter" or "service" or "container" or "toilets" => 3.2f,
                "church" or "cathedral" or "chapel" => 20f,
                "apartments" or "dormitory" or "residential" or "hotel" => 5f * LevelHeight,
                "house" or "detached" or "semidetached_house" or "terrace" or "bungalow" => 2f * LevelHeight + RoofLevelHeight,
                "industrial" or "warehouse" or "hangar" => 8f,
                // A "yes" of a few square metres is a bin store, not a block of flats.
                _ => footprintArea < 60f ? 3.2f : 4f * LevelHeight,
            };
        }

        // Accepts the bare numbers OSM uses here, and tolerates the "12 m" and "12,5"
        // spellings that turn up wherever the data has been edited by hand.
        private static bool TryLength(Dictionary<string, object> properties, string key, out float value)
        {
            value = 0f;
            if (!properties.TryGetValue(key, out object raw)) return false;
            string text = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim().Replace(",", ".");
            if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase)) text = text[..^1].Trim();
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool WithinBounds(IReadOnlyList<GeoPoint> ring)
        {
            foreach (GeoPoint point in ring)
                if (point.Lon < West || point.Lon > East || point.Lat < South || point.Lat > North)
                    return false;
            return true;
        }

        private static List<Vector3> ProjectRing(IReadOnlyList<GeoPoint> ring)
        {
            var points = new List<Vector3>(ring.Count);
            foreach (GeoPoint point in ring)
            {
                Vector3 projected = ProjectAtElevation(point, 0f);
                if (points.Count > 0 && (projected - points[^1]).sqrMagnitude < 0.0001f) continue;
                points.Add(projected);
            }
            if (points.Count > 1 && (points[0] - points[^1]).sqrMagnitude < 0.0001f) points.RemoveAt(points.Count - 1);
            return points;
        }

        // Ear clipping cannot see a courtyard, so every hole is stitched into the outline
        // along a bridge first: walk in along the bridge, round the hole, walk back out.
        // The shortest bridge that crosses no edge keeps the seam off the roof's silhouette.
        private static List<Vector3> MergeHoles(List<Vector3> outline, List<List<Vector3>> holes)
        {
            if (holes.Count == 0) return outline;

            List<Vector3> merged = outline;
            var pending = new List<List<Vector3>>(holes);
            while (pending.Count > 0)
            {
                // A vertex an earlier seam already used appears twice in the outline, and
                // anchoring a second bridge on it hands ear clipping a knot it cannot pick
                // apart. One bridge per vertex keeps the seams separable.
                var anchored = new bool[merged.Count];
                for (int i = 0; i < merged.Count; i++)
                {
                    for (int k = i + 1; k < merged.Count; k++)
                        if (SamePosition(merged[i], merged[k])) anchored[i] = anchored[k] = true;
                }

                int bestHole = -1;
                int bestOuter = -1;
                int bestInner = -1;
                float bestDistance = float.MaxValue;
                for (int h = 0; h < pending.Count; h++)
                {
                    for (int i = 0; i < merged.Count; i++)
                    {
                        if (anchored[i]) continue;
                        for (int j = 0; j < pending[h].Count; j++)
                        {
                            float distance = (merged[i] - pending[h][j]).sqrMagnitude;
                            if (distance >= bestDistance) continue;
                            if (CrossesRing(merged, merged[i], pending[h][j])) continue;
                            bool blocked = false;
                            for (int k = 0; k < pending.Count && !blocked; k++)
                                blocked = CrossesRing(pending[k], merged[i], pending[h][j]);
                            if (blocked) continue;
                            bestDistance = distance;
                            bestHole = h;
                            bestOuter = i;
                            bestInner = j;
                        }
                    }
                }

                // No bridge could be threaded to any hole that is left: roof those over
                // rather than lose the cap entirely. Their walls are in place either way.
                if (bestHole < 0) break;

                List<Vector3> hole = pending[bestHole];
                var bridged = new List<Vector3>(merged.Count + hole.Count + 2);
                bridged.AddRange(merged.GetRange(0, bestOuter + 1));
                for (int k = 0; k <= hole.Count; k++) bridged.Add(hole[(bestInner + k) % hole.Count]);
                bridged.AddRange(merged.GetRange(bestOuter, merged.Count - bestOuter));
                merged = bridged;
                pending.RemoveAt(bestHole);
            }
            return merged;
        }

        private static bool CrossesRing(IReadOnlyList<Vector3> ring, Vector3 from, Vector3 to)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                Vector3 a = ring[i];
                Vector3 b = ring[(i + 1) % ring.Count];
                // An edge ending where the bridge does touches it by definition.
                if (SamePosition(a, from) || SamePosition(a, to) || SamePosition(b, from) || SamePosition(b, to)) continue;
                if (SegmentsIntersect(from, to, a, b)) return true;
            }
            return false;
        }

        private static bool SegmentsIntersect(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            float aSide = Cross(c, d, a);
            float bSide = Cross(c, d, b);
            float cSide = Cross(a, b, c);
            float dSide = Cross(a, b, d);
            return aSide > 0f != bSide > 0f && cSide > 0f != dSide > 0f;
        }

        private static bool SamePosition(Vector3 a, Vector3 b) =>
            Mathf.Abs(a.x - b.x) < 0.0005f && Mathf.Abs(a.z - b.z) < 0.0005f;

        private static Vector3 Project(GeoPoint point, float y)
        {
            float elevation = currentHeightField?.Sample(point.Lon, point.Lat) ?? 0f;
            return ProjectAtElevation(point, elevation + y);
        }

        private static Vector3 ProjectAtElevation(GeoPoint point, float elevation)
        {
            double latRadians = CenterLat * Math.PI / 180.0;
            float x = (float)((point.Lon - CenterLon) * Math.PI / 180.0 * EarthRadius * Math.Cos(latRadians));
            float z = (float)((point.Lat - CenterLat) * Math.PI / 180.0 * EarthRadius);
            return new Vector3(x, elevation, z);
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

        private static List<GeoPoint> Densify(IReadOnlyList<GeoPoint> line)
        {
            var result = new List<GeoPoint>(line.Count);
            if (line.Count == 0) return result;
            double metersPerLon = Math.PI / 180.0 * EarthRadius * Math.Cos(CenterLat * Math.PI / 180.0);
            const double metersPerLat = Math.PI / 180.0 * EarthRadius;
            result.Add(line[0]);
            for (int i = 1; i < line.Count; i++)
            {
                GeoPoint a = line[i - 1];
                GeoPoint b = line[i];
                double dx = (b.Lon - a.Lon) * metersPerLon;
                double dz = (b.Lat - a.Lat) * metersPerLat;
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / MaxSegmentMeters));
                for (int step = 1; step < steps; step++)
                {
                    double t = (double)step / steps;
                    result.Add(new GeoPoint(a.Lon + (b.Lon - a.Lon) * t, a.Lat + (b.Lat - a.Lat) * t));
                }
                // Re-add the original node verbatim so shared way endpoints still weld in TramTrackNetwork.
                result.Add(b);
            }
            return result;
        }

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
                int ear = FindEar(points, remaining, true);
                // A bridged courtyard can reach a state where every convex corner is
                // blocked by the seam it was stitched along. Forcing the sharpest corner
                // through keeps the rest of the cap; what it can leave behind is a sliver
                // of overlap on a flat roof, where there is nothing to see.
                if (ear < 0) ear = FindEar(points, remaining, false);
                if (ear < 0) break;

                int count = remaining.Count;
                indices.Add(remaining[(ear - 1 + count) % count]);
                indices.Add(remaining[ear]);
                indices.Add(remaining[(ear + 1) % count]);
                remaining.RemoveAt(ear);
            }
            return indices;
        }

        // Returns a position in remaining, or -1. With demandEmpty the ear must hold no
        // other corner of the outline; without it the sharpest corner wins regardless.
        private static int FindEar(IReadOnlyList<Vector3> points, List<int> remaining, bool demandEmpty)
        {
            int best = -1;
            float bestTurn = 0f;
            for (int i = 0; i < remaining.Count; i++)
            {
                int count = remaining.Count;
                int previous = remaining[(i - 1 + count) % count];
                int current = remaining[i];
                int next = remaining[(i + 1) % count];
                if (!IsConvex(points[previous], points[current], points[next])) continue;

                if (!demandEmpty)
                {
                    float turn = Cross(points[previous], points[current], points[next]);
                    if (best < 0 || turn < bestTurn)
                    {
                        best = i;
                        bestTurn = turn;
                    }
                    continue;
                }

                if (EarIsEmpty(points, remaining, i)) return i;
            }
            return best;
        }

        private static bool EarIsEmpty(IReadOnlyList<Vector3> points, List<int> remaining, int ear)
        {
            int count = remaining.Count;
            int previous = remaining[(ear - 1 + count) % count];
            int current = remaining[ear];
            int next = remaining[(ear + 1) % count];
            for (int j = 0; j < count; j++)
            {
                int candidate = remaining[j];
                if (candidate == previous || candidate == current || candidate == next) continue;
                // A bridged courtyard repeats a position on both sides of its seam, and a
                // duplicate sitting on the ear's own corner blocks nothing.
                if (SamePosition(points[candidate], points[previous]) ||
                    SamePosition(points[candidate], points[current]) ||
                    SamePosition(points[candidate], points[next])) continue;
                // Only a reflex corner can trap part of the outline inside an ear, and
                // testing the convex ones as well would stall on every bridge seam.
                int before = remaining[(j - 1 + count) % count];
                int after = remaining[(j + 1) % count];
                if (IsConvex(points[before], points[candidate], points[after])) continue;
                if (PointInTriangle(points[candidate], points[previous], points[current], points[next])) return false;
            }
            return true;
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
