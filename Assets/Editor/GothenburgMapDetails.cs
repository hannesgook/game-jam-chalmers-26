using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using SparvagnRush.Map;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace SparvagnRush.Editor
{
    public static partial class GothenburgMapGenerator
    {
        private const string DefaultDetailsSource = "Assets/Map_data/map_details.geojson";

        private static string DetailsQuery =>
            "[out:json][timeout:120];\n" +
            "(\n" +
            $"  nwr[\"railway\"=\"tram_stop\"]({Deg(South)},{Deg(West)},{Deg(North)},{Deg(East)});\n" +
            $"  nwr[\"public_transport\"=\"stop_position\"][\"tram\"=\"yes\"]({Deg(South)},{Deg(West)},{Deg(North)},{Deg(East)});\n" +
            $"  nwr[\"name\"][\"shop\"]({Deg(South)},{Deg(West)},{Deg(North)},{Deg(East)});\n" +
            $"  nwr[\"name\"][\"amenity\"]({Deg(South)},{Deg(West)},{Deg(North)},{Deg(East)});\n" +
            $"  nwr[\"name\"][\"tourism\"]({Deg(South)},{Deg(West)},{Deg(North)},{Deg(East)});\n" +
            $"  nwr[\"name\"][\"office\"]({Deg(South)},{Deg(West)},{Deg(North)},{Deg(East)});\n" +
            ");\n" +
            "out center tags;\n";

        [MenuItem("Tools/Göteborg/Download Stops and Store Names")]
        public static void DownloadMapDetailsMenu()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string destination = Path.GetFullPath(Path.Combine(projectRoot, DefaultDetailsSource));
            if (File.Exists(destination) && !EditorUtility.DisplayDialog(
                    "Refresh map details",
                    "The current stops and business names will be replaced with fresh OpenStreetMap data.",
                    "Refresh", "Cancel"))
                return;

            try
            {
                string response = QueryDetails();
                if (response == null) return;
                Dictionary<string, object> root = GeoJsonLite.ParseObject(response);
                if (!root.TryGetValue("elements", out object raw) || raw is not List<object> elements)
                    throw new FormatException("Overpass response has no elements array.");

                string geoJson = WriteDetails(elements);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, geoJson, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(DefaultDetailsSource, ImportAssetOptions.ForceUpdate);
                Debug.Log($"Downloaded {DefaultDetailsSource}. Run Tools/Göteborg/Generate Map to rebuild labels.");
            }
            catch (Exception error)
            {
                Debug.LogError($"Map detail download failed: {error.Message}");
                EditorUtility.DisplayDialog("Download map details", error.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static string QueryDetails()
        {
            using var request = new UnityWebRequest(OverpassEndpoint, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("data=" + UnityWebRequest.EscapeURL(DetailsQuery)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            request.SetRequestHeader("User-Agent", "SparvagnRush-gamejam/1.0 (OSM map detail import)");
            request.timeout = 240;
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Downloading map details", "Querying OpenStreetMap…",
                        Mathf.Max(0.2f, request.downloadProgress)))
                {
                    request.Abort();
                    return null;
                }
                Thread.Sleep(50);
            }
            if (request.result != UnityWebRequest.Result.Success)
                throw new IOException($"Overpass request failed ({request.responseCode}): {request.error}");
            return request.downloadHandler.text;
        }

        private static string WriteDetails(List<object> elements)
        {
            var json = new StringBuilder("{\"type\":\"FeatureCollection\",\"copyright\":\"Map data © OpenStreetMap contributors, ODbL 1.0\",\"features\":[");
            bool first = true;
            foreach (object item in elements)
            {
                if (item is not Dictionary<string, object> element ||
                    !TryObject(element, "tags", out Dictionary<string, object> tags) ||
                    !TryString(tags, "name", out string name) || string.IsNullOrWhiteSpace(name) ||
                    !TryElementPosition(element, out double lon, out double lat))
                    continue;

                bool stop = PropertyEquals(tags, "railway", "tram_stop") || PropertyEquals(tags, "public_transport", "stop_position");
                string kind = stop ? "tram_stop" : tags.ContainsKey("shop") ? "shop" : "place";
                if (!first) json.Append(',');
                first = false;
                json.Append("{\"type\":\"Feature\",\"properties\":{\"name\":");
                AppendString(json, name);
                json.Append(",\"kind\":");
                AppendString(json, kind);
                json.Append("},\"geometry\":{\"type\":\"Point\",\"coordinates\":[")
                    .Append(Deg(lon)).Append(',').Append(Deg(lat)).Append("]}}");
            }
            return json.Append("]}").ToString();
        }

        private static bool TryElementPosition(Dictionary<string, object> element, out double lon, out double lat)
        {
            if (TryNumber(element, "lon", out lon) && TryNumber(element, "lat", out lat)) return true;
            if (TryObject(element, "center", out Dictionary<string, object> center) &&
                TryNumber(center, "lon", out lon) && TryNumber(center, "lat", out lat)) return true;
            lon = lat = 0;
            return false;
        }

        private static int CreateMapDetails(Transform parent)
        {
            var detailsRoot = new GameObject("MapDetails");
            detailsRoot.transform.SetParent(parent, false);
            Material stopMaterial = CreateMaterial("TramStops", new Color(0.08f, 0.35f, 0.72f), true);
            int count = CreateBuildingLabels(detailsRoot.transform);

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string path = Path.GetFullPath(Path.Combine(projectRoot, DefaultDetailsSource));
            if (!File.Exists(path))
            {
                Debug.LogWarning($"No {DefaultDetailsSource}; building names were generated, but stops and stores need Tools/Göteborg/Download Stops and Store Names.");
                return count;
            }

            Dictionary<string, object> root = GeoJsonLite.ParseObject(File.ReadAllText(path));
            if (!root.TryGetValue("features", out object raw) || raw is not List<object> features) return count;
            foreach (object item in features)
            {
                if (item is not Dictionary<string, object> feature ||
                    !TryObject(feature, "properties", out Dictionary<string, object> properties) ||
                    !TryString(properties, "name", out string name) ||
                    !TryObject(feature, "geometry", out Dictionary<string, object> geometry) ||
                    !TryString(geometry, "type", out string type) || type != "Point" ||
                    !geometry.TryGetValue("coordinates", out object coordinates) ||
                    coordinates is not List<object> pair || pair.Count < 2)
                    continue;
                var point = new GeoPoint(ToDouble(pair[0]), ToDouble(pair[1]));
                if (point.Lon < West || point.Lon > East || point.Lat < South || point.Lat > North) continue;
                bool stop = PropertyEquals(properties, "kind", "tram_stop");
                Vector3 position = Project(point, stop ? 0.2f : 3.2f);
                if (stop) CreateStop(detailsRoot.transform, position, name, stopMaterial);
                else CreateWorldLabel(detailsRoot.transform, position, name, PropertyEquals(properties, "kind", "shop") ? new Color(1f, 0.78f, 0.2f) : Color.white, 145f);
                count++;
            }
            return count;
        }

        private static int CreateBuildingLabels(Transform parent)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string path = Path.GetFullPath(Path.Combine(projectRoot, DefaultBuildingSource));
            if (!File.Exists(path)) return 0;
            Dictionary<string, object> root = GeoJsonLite.ParseObject(File.ReadAllText(path));
            if (!root.TryGetValue("features", out object raw) || raw is not List<object> features) return 0;
            int count = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object item in features)
            {
                if (item is not Dictionary<string, object> feature ||
                    !TryObject(feature, "properties", out Dictionary<string, object> properties) ||
                    !TryString(properties, "name", out string name) || !seen.Add(name) ||
                    !TryObject(feature, "geometry", out Dictionary<string, object> geometry) ||
                    !geometry.TryGetValue("coordinates", out object coordinates) || coordinates is not List<object> rings || rings.Count == 0)
                    continue;
                List<GeoPoint> outline = ReadLine(rings[0]);
                if (outline.Count < 3) continue;
                double lon = 0, lat = 0;
                foreach (GeoPoint point in outline) { lon += point.Lon; lat += point.Lat; }
                var centre = new GeoPoint(lon / outline.Count, lat / outline.Count);
                float height = ResolveBuildingHeight(properties, 200f);
                CreateWorldLabel(parent, Project(centre, height + 1.5f), name, new Color(0.92f, 0.95f, 1f), 240f);
                count++;
            }
            return count;
        }

        private static void CreateStop(Transform parent, Vector3 position, string name, Material material)
        {
            var root = new GameObject("Tram Stop — " + name);
            root.transform.SetParent(parent, false);
            root.transform.position = position;
            GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.transform.SetParent(root.transform, false);
            pole.transform.localPosition = Vector3.up * 2.1f;
            pole.transform.localScale = new Vector3(0.09f, 2.1f, 0.09f);
            pole.GetComponent<Renderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(pole.GetComponent<Collider>());
            GameObject sign = GameObject.CreatePrimitive(PrimitiveType.Cube);
            sign.transform.SetParent(root.transform, false);
            sign.transform.localPosition = Vector3.up * 4.1f;
            sign.transform.localScale = new Vector3(0.9f, 0.75f, 0.12f);
            sign.GetComponent<Renderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(sign.GetComponent<Collider>());
            CreateWorldLabel(root.transform, Vector3.up * 5.25f, name, new Color(0.4f, 0.85f, 1f), 280f, true);
        }

        private static void CreateWorldLabel(Transform parent, Vector3 position, string text, Color color, float range, bool local = false)
        {
            var labelObject = new GameObject("Label — " + text);
            labelObject.transform.SetParent(parent, false);
            labelObject.transform.position = local ? parent.TransformPoint(position) : position;
            TextMesh label = labelObject.AddComponent<TextMesh>();
            label.text = text;
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 64;
            label.characterSize = 0.085f;
            label.color = color;
            var billboard = labelObject.AddComponent<MapLabel>();
            var serialized = new SerializedObject(billboard);
            serialized.FindProperty("maximumDistance").floatValue = range;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
