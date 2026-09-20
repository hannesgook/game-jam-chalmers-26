using System.Collections;
using System.Collections.Generic;
using TramRush.Map;
using UnityEngine;

namespace TramRush.Gameplay
{
    public sealed class AmbientNpcManager : MonoBehaviour
    {
        [Tooltip("Size of the pool, and the crowd a run ends with.")]
        [SerializeField, Range(8, 400)] private int pedestrianCount = 220;
        [Tooltip("How many are on the streets when a run starts. The rest arrive as the clock runs down.")]
        [SerializeField, Range(4, 200)] private int startingCrowd = 45;
        [Tooltip("Spawn ring at the start of a run, far enough out that the opening is quiet.")]
        [SerializeField] private float startingMinimumSpawnRadius = 260f;
        [SerializeField] private float startingMaximumSpawnRadius = 420f;
        [Tooltip("Spawn ring by the end of a run. Close enough that somebody who starts walking the tram down can reach it, but never anywhere the player can currently see.")]
        [SerializeField] private float minimumSpawnRadius = 90f;
        [SerializeField] private float maximumSpawnRadius = 200f;
        [Tooltip("How far past the spawn ring somebody must get before being collected. Needs room, or a fresh spawn is stood down again at once.")]
        [SerializeField] private float recycleMargin = 110f;
        [Tooltip("Pooled people inspected per frame at the start and end of a run. Higher refills the streets faster.")]
        [SerializeField] private int startingSweepRate = 2;
        [SerializeField] private int finalSweepRate = 10;
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
        private TramGameManager game;
        private int cursor;
        private int liveCount;
        private int knownRun = -1;
        // Ring and recycle distance for this frame, held so TrySpawn shares them.
        private float spawnNear, spawnFar, recycleDistance;
        public bool Ready { get; private set; }

        public void Initialize(TramTrackNetwork trackNetwork, Transform player, TramGameManager manager)
        {
            network = trackNetwork;
            focus = player;
            game = manager;
            view = Camera.main;
            // Statics survive entering play mode again with domain reload off.
            Hunting = false;
            ChaseTarget = player.position;
            spawnNear = startingMinimumSpawnRadius;
            spawnFar = startingMaximumSpawnRadius;
            recycleDistance = spawnFar + recycleMargin;
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
            bool driving = focus.gameObject.activeInHierarchy;
            bool live = driving && Time.timeScale > 0f;
            Hunting = live;
            if (!driving) knownRun = -1;
            if (!live) return;
            ChaseTarget = focus.position;
            if (view == null) view = Camera.main;
            if (view != null) GeometryUtility.CalculateFrustumPlanes(view, frustum);

            if (game != null && game.RunId != knownRun)
            {
                knownRun = game.RunId;
                OpenQuiet();
            }

            // The streets fill up as the clock runs down: a bigger crowd, spawning
            // closer in, and inspected faster so the pool turns over quicker.
            float progress = game != null ? game.SessionProgress : 1f;
            int crowd = Mathf.RoundToInt(Mathf.Lerp(Mathf.Min(startingCrowd, pedestrianCount), pedestrianCount, progress));
            spawnNear = Mathf.Lerp(startingMinimumSpawnRadius, minimumSpawnRadius, progress);
            spawnFar = Mathf.Lerp(startingMaximumSpawnRadius, maximumSpawnRadius, progress);
            recycleDistance = spawnFar + recycleMargin;

            // Recounting beats bookkeeping: people also leave the world by boarding a
            // stopped tram, which this manager never hears about.
            if (Time.frameCount % 12 == 0) liveCount = CountLive();

            int sweep = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(startingSweepRate, finalSweepRate, progress)));
            for (int i = 0; i < sweep; i++)
            {
                AmbientNpcWalker walker = pedestrians[cursor++ % pedestrians.Count];
                if (walker == null) continue;
                bool far = (walker.transform.position - focus.position).sqrMagnitude > recycleDistance * recycleDistance;

                if (walker.gameObject.activeSelf)
                {
                    // Nobody is ever removed while they can be seen, or while they are
                    // still close enough to matter, however far over the cap we are.
                    if (Visible(walker.transform.position) || (!far && !walker.IsDown)) continue;
                    if (liveCount > crowd) { walker.gameObject.SetActive(false); liveCount--; continue; }
                    if (!TrySpawn(walker, false)) liveCount--;
                    continue;
                }
                if (liveCount >= crowd) continue;
                if (TrySpawn(walker, false)) liveCount++;
            }
        }

        /// <summary>
        /// A run opens on quiet streets. Anybody already standing near the start line
        /// is stood down, so the first arrivals walk in from the far ring instead of
        /// being on top of the tram from the first second.
        /// </summary>
        private void OpenQuiet()
        {
            float clear = startingMinimumSpawnRadius;
            foreach (AmbientNpcWalker walker in pedestrians)
            {
                if (walker == null || !walker.gameObject.activeSelf) continue;
                if ((walker.transform.position - focus.position).sqrMagnitude > clear * clear) continue;
                walker.gameObject.SetActive(false);
            }
            liveCount = CountLive();
        }

        private int CountLive()
        {
            int count = 0;
            foreach (AmbientNpcWalker walker in pedestrians)
                if (walker != null && walker.gameObject.activeSelf) count++;
            return count;
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

        /// <summary>Returns true if the walker ended up on the streets.</summary>
        private bool TrySpawn(AmbientNpcWalker walker, bool loading)
        {
            walker.gameObject.SetActive(false);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Vector3 candidate;
                if (loading) candidate = network.GetRandomTrackPoint(random);
                else
                {
                    float angle = (float)random.NextDouble() * Mathf.PI * 2;
                    float radius = Mathf.Lerp(spawnNear, spawnFar, (float)random.NextDouble());
                    candidate = focus.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                }
                if (!walker.ResetTo(network.FindClosestEdge(candidate))) continue;
                if (!loading && Visible(walker.transform.position)) continue;
                walker.gameObject.SetActive(true);
                return true;
            }
            return false;
        }
    }
}
