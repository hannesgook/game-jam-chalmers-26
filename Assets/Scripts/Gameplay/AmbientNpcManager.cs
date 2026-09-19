using System.Collections.Generic;
using SparvagnRush.Map;
using UnityEngine;

namespace SparvagnRush.Gameplay
{
    /// <summary>Keeps a small crowd distributed around the moving player.</summary>
    public sealed class AmbientNpcManager : MonoBehaviour
    {
        [SerializeField, Range(8, 80)] private int pedestrianCount = 80;
        [SerializeField] private float minimumSpawnRadius = 30f;
        [SerializeField] private float maximumSpawnRadius = 50f;
        [SerializeField] private float recycleDistance = 150f;

        private readonly List<AmbientNpcWalker> pedestrians = new();
        private readonly System.Random random = new();
        private TramTrackNetwork network;
        private Transform focus;
        private Transform crowdRoot;
        private float recycleTimer;
        private PedestrianWalkableArea walkableArea;

        public void Initialize(TramTrackNetwork trackNetwork, Transform player)
        {
            network = trackNetwork;
            focus = player;
            walkableArea = new PedestrianWalkableArea(transform);
            if (!walkableArea.Ready) return;
            crowdRoot = new GameObject("Pedestrians").transform;
            crowdRoot.SetParent(transform, false);

            for (int i = 0; i < pedestrianCount; i++)
            {
                var pedestrianObject = new GameObject($"Pedestrian {i + 1:00}");
                pedestrianObject.SetActive(false);
                pedestrianObject.transform.SetParent(crowdRoot, false);
                AmbientNpcWalker pedestrian = pedestrianObject.AddComponent<AmbientNpcWalker>();
                pedestrian.Initialize(network, walkableArea, random);
                TrySpawn(pedestrian, i < 8 ? 12f : minimumSpawnRadius, i < 8 ? 60f : maximumSpawnRadius);
                pedestrians.Add(pedestrian);
            }
        }

        private void Update()
        {
            if (network == null || focus == null) return;
            recycleTimer -= Time.deltaTime;
            if (recycleTimer > 0f) return;
            recycleTimer = 0.65f;

            float recycleDistanceSquared = recycleDistance * recycleDistance;
            foreach (AmbientNpcWalker pedestrian in pedestrians)
            {
                if (pedestrian == null) continue;
                Vector3 difference = pedestrian.transform.position - focus.position;
                difference.y = 0f;
                if (pedestrian.gameObject.activeSelf && difference.sqrMagnitude <= recycleDistanceSquared) continue;
                TrySpawn(pedestrian, minimumSpawnRadius, maximumSpawnRadius);
            }
        }

        private void TrySpawn(AmbientNpcWalker pedestrian, float minimumRadius, float maximumRadius)
        {
            pedestrian.gameObject.SetActive(false);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                if (!pedestrian.ResetTo(FindSpawnEdge(minimumRadius, maximumRadius))) continue;
                pedestrian.gameObject.SetActive(true);
                return;
            }
            // Leave it hidden when no valid spawn is found; Update retries later.
        }

        private TramTrackNetwork.ClosestEdge FindSpawnEdge(float minimumRadius, float maximumRadius)
        {
            float angle = (float)random.NextDouble() * Mathf.PI * 2f;
            float radius = Mathf.Lerp(minimumRadius, maximumRadius, Mathf.Sqrt((float)random.NextDouble()));
            Vector3 candidate = focus.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            return network.FindClosestEdge(candidate);
        }
    }
}
