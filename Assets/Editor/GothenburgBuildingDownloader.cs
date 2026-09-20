using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using TramRush.Map;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace TramRush.Editor
{
    /// <summary>
    /// Editor-side twin of Tools/FetchBuildings.py, so a fresh clone can pull the
    /// building footprints from the menu instead of needing Python on the machine.
    /// Both write the same file; whichever is more convenient wins.
    /// </summary>
    public static partial class GothenburgMapGenerator
    {
        private const string OverpassEndpoint = "https://overpass-api.de/api/interpreter";

        // A slab of the footprint may fall outside the window; the generator clips it.
        // The bounds are the generator's own consts, so the two cannot drift apart.
        private static string BuildingQuery =>
            "[out:json][timeout:180];\n" +
            "(\n" +
            $"  way[\"building\"]({Deg(South)},{Deg(West)},{Deg(North)},{Deg(East)});\n" +
            $"  relation[\"building\"]({Deg(South)},{Deg(West)},{Deg(North)},{Deg(East)});\n" +
            ");\n" +
            "out body geom;\n";

        // Everything the extruder reads. Dropping the rest keeps the asset ~10x smaller.
        private static readonly string[] KeptBuildingTags =
        {
            "building",
            "building:levels",
            "building:min_level",
            "height",
            "min_height",
            "roof:levels",
            "roof:height",
            "roof:shape",
            "name",
        };

        [MenuItem("Tools/Göteborg/Download Building Data")]
        public static void DownloadBuildingDataMenu()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string destination = Path.GetFullPath(Path.Combine(projectRoot, DefaultBuildingSource));

            if (File.Exists(destination) && !EditorUtility.DisplayDialog(
                    "Download building data",
                    $"{DefaultBuildingSource} already exists and will be overwritten with a fresh Overpass query.\n\nContinue?",
                    "Download", "Cancel"))
                return;

            try
            {
                string response = QueryOverpass();
                if (response == null) return;

                Dictionary<string, object> payload = GeoJsonLite.ParseObject(response);
                if (!payload.TryGetValue("elements", out object elementValue) || elementValue is not List<object> elements)
                    throw new FormatException("Overpass response has no elements array.");

                EditorUtility.DisplayProgressBar("Downloading building data", "Assembling footprints…", 0.9f);
                List<BuildingFeature> features = ToBuildingFeatures(elements);
                if (features.Count == 0)
                    throw new FormatException("Overpass returned no usable building footprints.");

                string timestamp = TryObject(payload, "osm3s", out Dictionary<string, object> osm3s) &&
                                   TryString(osm3s, "timestamp_osm_base", out string stamp)
                    ? stamp
                    : null;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, WriteCollection(features, timestamp), new UTF8Encoding(false));
            }
            catch (Exception error)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"Building download failed: {error.Message}");
                EditorUtility.DisplayDialog("Download building data",
                    $"The download failed:\n\n{error.Message}\n\nOverpass rate-limits heavy users; waiting a minute and retrying usually clears it.",
                    "OK");
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.ImportAsset(DefaultBuildingSource, ImportAssetOptions.ForceUpdate);
            Debug.Log($"Downloaded building data to {DefaultBuildingSource}. Run Tools/Göteborg/Generate Map to rebuild the city.");
        }

        /// <summary>Blocks the editor behind a cancelable bar; the query runs 10-60 s.</summary>
        private static string QueryOverpass()
        {
            using var request = new UnityWebRequest(OverpassEndpoint, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(
                Encoding.UTF8.GetBytes("data=" + UnityWebRequest.EscapeURL(BuildingQuery)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            request.SetRequestHeader("User-Agent", "TramRush-gamejam/1.0 (OSM building import)");
            request.timeout = 300;

            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                // Overpass reports no length until it starts streaming, so the bar sits
                // at a third through the wait and tracks the body after that.
                float progress = Mathf.Max(request.downloadProgress, 0f) * 0.6f + 0.3f;
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Downloading building data", "Querying Overpass…", progress))
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

        private sealed class BuildingFeature
        {
            public string Id;
            public List<KeyValuePair<string, string>> Tags;
            public List<List<RingPoint>> Rings;
        }

        private readonly struct RingPoint : IEquatable<RingPoint>
        {
            public readonly double Lon;
            public readonly double Lat;

            // Rounding at construction is what makes the ring chaining below work:
            // shared corners have to compare exactly equal across member ways.
            public RingPoint(double lon, double lat)
            {
                Lon = Math.Round(lon, 7);
                Lat = Math.Round(lat, 7);
            }

            public bool Equals(RingPoint other) => Lon == other.Lon && Lat == other.Lat;
            public override bool Equals(object other) => other is RingPoint point && Equals(point);
            public override int GetHashCode() => HashCode.Combine(Lon, Lat);
        }

        private static List<BuildingFeature> ToBuildingFeatures(List<object> elements)
        {
            var features = new List<BuildingFeature>();
            foreach (object item in elements)
            {
                if (item is not Dictionary<string, object> element ||
                    !TryObject(element, "tags", out Dictionary<string, object> tags) ||
                    !TryString(element, "type", out string type))
                    continue;

                if (!TryString(tags, "building", out string building) || building == "no") continue;
                // An underground garage has a footprint but nothing above the street.
                if (TryString(tags, "location", out string location) && location == "underground") continue;

                string id = $"{type}/{ReadId(element)}";
                List<KeyValuePair<string, string>> kept = KeepTags(tags);

                if (type == "way")
                {
                    List<RingPoint> ring = Close(ReadRing(element));
                    if (!IsRing(ring)) continue;
                    features.Add(new BuildingFeature
                    {
                        Id = id,
                        Tags = kept,
                        Rings = new List<List<RingPoint>> { ring },
                    });
                    continue;
                }

                List<List<RingPoint>> outers = AssembleRings(ReadMembers(element, "outer"));
                List<List<RingPoint>> inners = AssembleRings(ReadMembers(element, "inner"));
                if (outers.Count == 0) continue;

                // A relation with several outers is several buildings sharing one tag set,
                // and a courtyard belongs to whichever of them encloses it -- the largest
                // is not always the right one.
                foreach (List<RingPoint> outer in outers)
                {
                    var rings = new List<List<RingPoint>> { outer };
                    foreach (List<RingPoint> hole in inners)
                        if (Contains(outer, hole[0]))
                            rings.Add(hole);
                    features.Add(new BuildingFeature { Id = id, Tags = kept, Rings = rings });
                }
            }
            return features;
        }

        private static long ReadId(Dictionary<string, object> element) =>
            element.TryGetValue("id", out object value) && value is long id ? id : 0L;

        private static List<KeyValuePair<string, string>> KeepTags(Dictionary<string, object> tags)
        {
            var kept = new List<KeyValuePair<string, string>>();
            // Emitted in this fixed order rather than the order Overpass happened to send,
            // so two downloads of the same block produce the same bytes.
            foreach (string key in KeptBuildingTags)
                if (TryString(tags, key, out string value))
                    kept.Add(new KeyValuePair<string, string>(key, value));
            return kept;
        }

        private static List<RingPoint> ReadRing(Dictionary<string, object> source)
        {
            var ring = new List<RingPoint>();
            if (!source.TryGetValue("geometry", out object value) || value is not List<object> nodes) return ring;
            foreach (object item in nodes)
            {
                if (item is not Dictionary<string, object> node ||
                    !TryNumber(node, "lon", out double lon) ||
                    !TryNumber(node, "lat", out double lat))
                    continue;
                ring.Add(new RingPoint(lon, lat));
            }
            return ring;
        }

        private static bool TryNumber(Dictionary<string, object> source, string key, out double value)
        {
            if (source.TryGetValue(key, out object raw) && raw is double or long)
            {
                value = ToDouble(raw);
                return true;
            }
            value = 0.0;
            return false;
        }

        private static List<List<RingPoint>> ReadMembers(Dictionary<string, object> element, string role)
        {
            var segments = new List<List<RingPoint>>();
            if (!element.TryGetValue("members", out object value) || value is not List<object> members) return segments;
            foreach (object item in members)
            {
                if (item is not Dictionary<string, object> member ||
                    !TryString(member, "role", out string memberRole) ||
                    memberRole != role)
                    continue;
                List<RingPoint> ring = ReadRing(member);
                if (ring.Count > 0) segments.Add(ring);
            }
            return segments;
        }

        private static List<RingPoint> Close(List<RingPoint> ring)
        {
            if (ring.Count > 2 && !ring[0].Equals(ring[^1])) ring.Add(ring[0]);
            return ring;
        }

        private static bool IsRing(List<RingPoint> ring) => ring.Count >= 4 && ring[0].Equals(ring[^1]);

        /// <summary>
        /// Chains member ways end-to-end into closed rings. Overpass hands back a
        /// multipolygon's ways in arbitrary order and direction, and a Gothenburg city
        /// block is routinely split across four or five of them, so a ring only appears
        /// once the pieces are walked and flipped into place.
        /// </summary>
        private static List<List<RingPoint>> AssembleRings(List<List<RingPoint>> segments)
        {
            var pending = new List<List<RingPoint>>();
            foreach (List<RingPoint> segment in segments)
                if (segment.Count >= 2)
                    pending.Add(new List<RingPoint>(segment));

            var rings = new List<List<RingPoint>>();
            while (pending.Count > 0)
            {
                List<RingPoint> current = pending[^1];
                pending.RemoveAt(pending.Count - 1);

                bool extended = true;
                while (!IsRing(current) && extended)
                {
                    extended = false;
                    for (int index = 0; index < pending.Count; index++)
                    {
                        List<RingPoint> candidate = pending[index];
                        if (candidate[0].Equals(current[^1]))
                        {
                            current.AddRange(candidate.GetRange(1, candidate.Count - 1));
                        }
                        else if (candidate[^1].Equals(current[^1]))
                        {
                            for (int i = candidate.Count - 2; i >= 0; i--) current.Add(candidate[i]);
                        }
                        else if (candidate[^1].Equals(current[0]))
                        {
                            List<RingPoint> merged = candidate.GetRange(0, candidate.Count - 1);
                            merged.AddRange(current);
                            current = merged;
                        }
                        else if (candidate[0].Equals(current[0]))
                        {
                            var merged = new List<RingPoint>();
                            for (int i = candidate.Count - 1; i >= 1; i--) merged.Add(candidate[i]);
                            merged.AddRange(current);
                            current = merged;
                        }
                        else
                        {
                            continue;
                        }

                        pending.RemoveAt(index);
                        extended = true;
                        break;
                    }
                }

                if (IsRing(current)) rings.Add(current);
            }
            return rings;
        }

        private static double RingSignedArea(List<RingPoint> ring)
        {
            double total = 0.0;
            for (int i = 0; i + 1 < ring.Count; i++)
                total += ring[i].Lon * ring[i + 1].Lat - ring[i + 1].Lon * ring[i].Lat;
            return total * 0.5;
        }

        /// <summary>Crossing-number test; the rings never touch, so the boundary case is moot.</summary>
        private static bool Contains(List<RingPoint> ring, RingPoint point)
        {
            bool inside = false;
            for (int i = 0; i + 1 < ring.Count; i++)
            {
                RingPoint a = ring[i];
                RingPoint b = ring[i + 1];
                if (a.Lat > point.Lat != b.Lat > point.Lat &&
                    point.Lon < a.Lon + (point.Lat - a.Lat) / (b.Lat - a.Lat) * (b.Lon - a.Lon))
                    inside = !inside;
            }
            return inside;
        }

        private static string WriteCollection(List<BuildingFeature> features, string timestamp)
        {
            var json = new StringBuilder();
            json.Append("{\"type\":\"FeatureCollection\",\"generator\":\"Tools/Göteborg/Download Building Data\",");
            json.Append("\"copyright\":\"Map data © OpenStreetMap contributors, ODbL 1.0\",\"timestamp\":");
            if (timestamp == null) json.Append("null");
            else AppendString(json, timestamp);
            json.Append(",\"features\":[");

            for (int f = 0; f < features.Count; f++)
            {
                if (f > 0) json.Append(',');
                BuildingFeature feature = features[f];
                json.Append("{\"type\":\"Feature\",\"properties\":{\"@id\":");
                AppendString(json, feature.Id);
                foreach (KeyValuePair<string, string> tag in feature.Tags)
                {
                    json.Append(',');
                    AppendString(json, tag.Key);
                    json.Append(':');
                    AppendString(json, tag.Value);
                }
                json.Append("},\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[");

                for (int r = 0; r < feature.Rings.Count; r++)
                {
                    if (r > 0) json.Append(',');
                    json.Append('[');
                    // GeoJSON wants the outer ring counter-clockwise and holes clockwise.
                    List<RingPoint> ring = feature.Rings[r];
                    bool outward = RingSignedArea(ring) > 0;
                    bool forward = outward == (r == 0);
                    for (int i = 0; i < ring.Count; i++)
                    {
                        if (i > 0) json.Append(',');
                        RingPoint point = ring[forward ? i : ring.Count - 1 - i];
                        json.Append('[').Append(Deg(point.Lon)).Append(',').Append(Deg(point.Lat)).Append(']');
                    }
                    json.Append(']');
                }
                json.Append("]}}");
            }

            json.Append("]}");
            return json.ToString();
        }

        private static string Deg(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);

        private static void AppendString(StringBuilder json, string value)
        {
            json.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
                    default:
                        if (c < ' ') json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else json.Append(c);
                        break;
                }
            }
            json.Append('"');
        }
    }
}
