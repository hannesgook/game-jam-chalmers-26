using System;
using System.Collections.Generic;
using SparvagnRush.Map;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SparvagnRush.Gameplay
{
    public sealed class CityIntro : MonoBehaviour
    {
        private readonly List<List<Vector3>> streets = new();
        private Camera view;
        private Action complete;
        private float elapsed;
        private int previousShot = -1;
        public bool Playing { get; private set; }

        public void Initialize(Camera camera, TramTrackNetwork network, Transform city, Action onComplete)
        {
            view = camera;
            complete = onComplete;
            // Road overlays stay hidden; their centre lines remain useful flight paths.
            Transform roads = city.Find("Roads");
            Mesh mesh = roads != null ? roads.GetComponent<MeshFilter>()?.sharedMesh : null;
            if (mesh != null && mesh.isReadable)
            {
                Vector3[] v = mesh.vertices;
                int[] triangles = mesh.triangles;
                List<Vector3> street = null;
                int previous = -10;
                for (int t = 0; t + 5 < triangles.Length; t += 6)
                {
                    int first = triangles[t];
                    if (first + 3 >= v.Length) continue;
                    if (first != previous + 2)
                    {
                        street = new List<Vector3>();
                        streets.Add(street);
                        street.Add(roads.TransformPoint((v[first] + v[first + 1]) * 0.5f));
                    }
                    street.Add(roads.TransformPoint((v[first + 2] + v[first + 3]) * 0.5f));
                    previous = first;
                }
            }
            if (streets.Count == 0)
                foreach (var path in network.Paths) if (path.points.Length > 1) streets.Add(new List<Vector3>(path.points));
            streets.Sort((a, b) => Length(b).CompareTo(Length(a)));
            Playing = streets.Count > 0;
            if (!Playing) complete();
        }

        private static float Length(List<Vector3> points)
        {
            float length = 0;
            for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
            return length;
        }

        private static Vector3 Sample(List<Vector3> points, float distance)
        {
            for (int i = 1; i < points.Count; i++)
            {
                float length = Vector3.Distance(points[i - 1], points[i]);
                if (distance <= length) return Vector3.Lerp(points[i - 1], points[i], distance / Mathf.Max(0.001f, length));
                distance -= length;
            }
            return points[points.Count - 1];
        }

        private void LateUpdate()
        {
            if (!Playing) return;
            var keys = Keyboard.current;
            if (keys != null && (keys.spaceKey.wasPressedThisFrame || keys.enterKey.wasPressedThisFrame || keys.escapeKey.wasPressedThisFrame)) { Skip(); return; }
            elapsed += Time.unscaledDeltaTime;
            if (elapsed >= 10.5f) { Skip(); return; }
            int shot = Mathf.Min((int)(elapsed / 3.5f), streets.Count - 1);
            var street = streets[shot];
            float length = Length(street);
            float distance = elapsed % 3.5f / 3.5f * Mathf.Min(297.5f, Mathf.Max(0, length - 25f));
            Vector3 position = Sample(street, distance) + Vector3.up * 30f;
            Vector3 ahead = Sample(street, Mathf.Min(length, distance + 55f)) + Vector3.up * 9f;
            // Stay above rooftops where a source street crosses an imported building.
            if (Physics.Raycast(position + Vector3.up * 200f, Vector3.down, out RaycastHit hit, 230f, ~0, QueryTriggerInteraction.Ignore))
                position.y = Mathf.Max(position.y, hit.point.y + 12f);
            if (shot != previousShot) view.transform.position = position;
            else view.transform.position = Vector3.Lerp(view.transform.position, position, 1 - Mathf.Exp(-10f * Time.unscaledDeltaTime));
            Quaternion rotation = Quaternion.LookRotation(ahead - view.transform.position, Vector3.up);
            view.transform.rotation = shot != previousShot ? rotation : Quaternion.Slerp(view.transform.rotation, rotation, 1 - Mathf.Exp(-6f * Time.unscaledDeltaTime));
            view.fieldOfView = 68f;
            previousShot = shot;
        }

        public void Skip()
        {
            if (!Playing) return;
            Playing = false;
            complete?.Invoke();
        }
    }
}
