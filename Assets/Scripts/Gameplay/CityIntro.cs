using System;
using System.Collections.Generic;
using TramRush.Map;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TramRush.Gameplay
{
    public sealed class CityIntro : MonoBehaviour
    {
        private readonly List<List<Vector3>> streets = new();
        private Camera view;
        private Action complete;
        private float elapsed;
        private int previousShot = -1;
        private const float MaximumLoadWait = 25f;
        private float warmup, stableTime, averageFrame = 1f / 60f;
        private Vector3 transitionFrom;
        private Vector3 flightVelocity;
        private TramRushBootstrap bootstrap;
        public bool Playing { get; private set; }
        public bool Loading { get; private set; }
        public string LoadingMessage => bootstrap != null && !bootstrap.StartupReady ? "Preparing the city and pedestrians…" : "Settling frame timing…";

        public void Initialize(Camera camera, TramTrackNetwork network, Transform city, Action onComplete)
        {
            view = camera;
            complete = onComplete;
            bootstrap = city.GetComponent<TramRushBootstrap>();
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
            Loading = Playing;
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
            if (Loading)
            {
                warmup += Time.unscaledDeltaTime;
                float frame = Time.unscaledDeltaTime;
                averageFrame = Mathf.Lerp(averageFrame, frame, 0.05f);
                bool ready = bootstrap == null || bootstrap.StartupReady;
                bool steady = frame <= Mathf.Max(1f / 35f, averageFrame * 1.25f) && frame < 0.1f;
                stableTime = ready && warmup >= 3f && steady ? stableTime + frame : 0f;
                // The main scene still renders behind the loading screen, warming its
                // materials before any cinematic movement starts. Never count this as intro time.
                // A machine that never reaches the frame target would otherwise sit on the
                // loading screen for good, so give up waiting once startup work is done.
                if (stableTime < 1.25f && !(ready && warmup >= MaximumLoadWait)) return;
                Loading = false;
                transitionFrom = view.transform.position;
            }
            var keys = Keyboard.current;
            if (keys != null && (keys.spaceKey.wasPressedThisFrame || keys.enterKey.wasPressedThisFrame || keys.escapeKey.wasPressedThisFrame)) { Skip(); return; }
            elapsed += Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (elapsed >= 36f) { Skip(); return; }
            int shot = (int)(elapsed / 12f);
            var street = streets[shot % streets.Count];
            float length = Length(street);
            float shotTime = elapsed % 12f;
            float progress = shotTime / 12f;
            float distance = progress * Mathf.Min(660f, Mathf.Max(0, length - 25f));
            Vector3 position = Sample(street, distance) + Vector3.up * (38f + Mathf.Sin(progress * Mathf.PI) * 22f);
            Vector3 ahead = Sample(street, Mathf.Min(length, distance + 55f)) + Vector3.up * 9f;
            Vector3 side = Vector3.Cross(Vector3.up, (ahead - position).normalized);
            position += side * Mathf.Sin(progress * Mathf.PI) * 9f;
            if (shot != previousShot) { transitionFrom = view.transform.position; flightVelocity = Vector3.zero; }
            if (shotTime < 2f)
            {
                float blend = Mathf.SmoothStep(0, 1, shotTime / 2f);
                position = Vector3.Lerp(transitionFrom, position, blend) + Vector3.up * (Mathf.Sin(blend * Mathf.PI) * 35f);
            }
            // Stay above rooftops where a source street crosses an imported building.
            if (Physics.Raycast(position + Vector3.up * 200f, Vector3.down, out RaycastHit hit, 230f, ~0, QueryTriggerInteraction.Ignore))
                position.y = Mathf.Max(position.y, hit.point.y + 12f);
            view.transform.position = Vector3.SmoothDamp(view.transform.position, position, ref flightVelocity, 0.35f, Mathf.Infinity, Mathf.Min(Time.unscaledDeltaTime, 0.05f));
            Quaternion rotation = Quaternion.LookRotation(ahead - view.transform.position, Vector3.up);
            view.transform.rotation = Quaternion.Slerp(view.transform.rotation, rotation, 1 - Mathf.Exp(-2.5f * Time.unscaledDeltaTime));
            view.fieldOfView = Mathf.Lerp(view.fieldOfView, 57f + Mathf.Sin(progress * Mathf.PI) * 5f, 1 - Mathf.Exp(-2f * Time.unscaledDeltaTime));
            previousShot = shot;
        }

        public void Skip()
        {
            if (!Playing) return;
            if (Loading && bootstrap != null && !bootstrap.StartupReady) return;
            Loading = false;
            Playing = false;
            complete?.Invoke();
        }
    }
}
