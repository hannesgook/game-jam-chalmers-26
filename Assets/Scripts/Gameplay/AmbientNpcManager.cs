using System.Collections;
using System.Collections.Generic;
using SparvagnRush.Map;
using UnityEngine;

namespace SparvagnRush.Gameplay
{
    public sealed class AmbientNpcManager : MonoBehaviour
    {
        [SerializeField, Range(8, 400)] private int pedestrianCount = 220;
        // Close enough that somebody who starts walking the tram down can reach it,
        // but never anywhere the player can currently see.
        [SerializeField] private float minimumSpawnRadius = 90f;
        [SerializeField] private float maximumSpawnRadius = 200f;
        [SerializeField] private float recycleDistance = 300f;
        /// <summary>Where the crowd walks to. The tram, while a run is live.</summary>
        public static Vector3 ChaseTarget;
        /// <summary>False while choosing a station or once the game has frozen.</summary>
        public static bool Hunting;
        private readonly List<AmbientNpcWalker> pedestrians = new();
        private readonly System.Random random = new();
        private readonly Plane[] frustum = new Plane[6];
        private TramTrackNetwork network;
        private Transform focus;
        private Camera view;
        private PedestrianWalkableArea walkableArea;
        private Transform crowdRoot;
        private int cursor;
        public bool Ready { get; private set; }

        public void Initialize(TramTrackNetwork trackNetwork, Transform player)
        {
            network = trackNetwork;
            focus = player;
            view = Camera.main;
            // Statics survive entering play mode again with domain reload off.
            Hunting = false;
            ChaseTarget = player.position;
            walkableArea = new PedestrianWalkableArea(transform);
            if (!walkableArea.Ready) { Ready = true; return; }
            crowdRoot = new GameObject("Pedestrians").transform;
            crowdRoot.SetParent(transform, false);
            StartCoroutine(BuildCrowd());
        }

        private IEnumerator BuildCrowd()
        {
            for (int i = 0; i < pedestrianCount; i++)
            {
                var item = new GameObject($"Pedestrian {i + 1:00}");
                item.SetActive(false);
                item.transform.SetParent(crowdRoot, false);
                var walker = item.AddComponent<AmbientNpcWalker>();
                walker.Initialize(network, walkableArea, random);
                pedestrians.Add(walker);
                // Populate the whole city behind the loading screen, before any camera tour.
                TrySpawn(walker, true);
                if (i % 4 == 3) yield return null;
            }
            Ready = true;
        }

        private void Update()
        {
            if (!Ready || pedestrians.Count == 0 || focus == null) return;
            // A frozen game stops the crowd being recycled behind the end screen.
            bool live = focus.gameObject.activeInHierarchy && Time.timeScale > 0f;
            Hunting = live;
            if (!live) return;
            ChaseTarget = focus.position;
            if (view == null) view = Camera.main;
            if (view != null) GeometryUtility.CalculateFrustumPlanes(view, frustum);
            // Bounded work: inspect a few pooled people a frame, not the whole crowd.
            for (int i = 0; i < 4; i++)
            {
                AmbientNpcWalker walker = pedestrians[cursor++ % pedestrians.Count];
                if (walker == null) continue;
                bool far = (walker.transform.position - focus.position).sqrMagnitude > recycleDistance * recycleDistance;
                // Somebody the tram knocked over is recycled as soon as nobody is
                // looking, rather than lying in the street for the rest of the run.
                if (walker.gameObject.activeSelf && ((!far && !walker.IsDown) || Visible(walker.transform.position))) continue;
                TrySpawn(walker, false);
            }
        }

        private bool Visible(Vector3 position)
        {
            // The overhead map is deliberately not treated as a view. It covers 360 m
            // in a few hundred pixels, so a person is under a pixel there and cannot
            // be seen to appear; counting it would ban every spawn near the tram.
            if (view == null) return true;
            if (!GeometryUtility.TestPlanesAABB(frustum, new Bounds(position + Vector3.up * 1.2f, new Vector3(1.4f, 2.8f, 1.4f)))) return false;
            for (int i = 0; i < 3; i++)
            {
                Vector3 target = position + Vector3.up * (0.25f + i * 1.05f);
                Vector3 ray = target - view.transform.position;
                if (!Physics.Raycast(view.transform.position, ray.normalized, out RaycastHit hit, ray.magnitude - 0.5f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return true;
            }
            return false;
        }

        private void TrySpawn(AmbientNpcWalker walker, bool loading)
        {
            walker.gameObject.SetActive(false);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Vector3 candidate;
                if (loading) candidate = network.GetRandomTrackPoint(random);
                else
                {
                    float angle = (float)random.NextDouble() * Mathf.PI * 2;
                    float radius = Mathf.Lerp(minimumSpawnRadius, maximumSpawnRadius, (float)random.NextDouble());
                    candidate = focus.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                }
                if (!walker.ResetTo(network.FindClosestEdge(candidate))) continue;
                if (!loading && Visible(walker.transform.position)) continue;
                walker.gameObject.SetActive(true);
                return;
            }
        }
    }
}
