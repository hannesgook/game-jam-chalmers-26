using System;
using System.Collections.Generic;
using UnityEngine;

namespace TramRush.Map
{
    [DisallowMultipleComponent]
    public sealed class TramTrackNetwork : MonoBehaviour
    {
        public sealed class GraphNode
        {
            public Vector3 position;
            public readonly List<int> neighbours = new();
        }

        public readonly struct ClosestEdge
        {
            public readonly int A;
            public readonly int B;
            public readonly float T;
            public readonly Vector3 Point;

            public ClosestEdge(int a, int b, float t, Vector3 point)
            {
                A = a;
                B = b;
                T = t;
                Point = point;
            }
        }

        [Serializable]
        public sealed class TrackPath
        {
            public string osmId;
            public Vector3[] points;
        }

        [SerializeField] private List<TrackPath> paths = new();
        private List<GraphNode> graph;

        public IReadOnlyList<TrackPath> Paths => paths;
        public IReadOnlyList<GraphNode> Graph => graph ??= BuildGraph();

        public void ReplacePaths(List<TrackPath> generatedPaths)
        {
            paths = generatedPaths;
            graph = null;
        }

        public ClosestEdge FindClosestEdge(Vector3 worldPoint)
        {
            IReadOnlyList<GraphNode> nodes = Graph;
            float bestDistance = float.MaxValue;
            ClosestEdge best = default;
            for (int a = 0; a < nodes.Count; a++)
            {
                foreach (int b in nodes[a].neighbours)
                {
                    if (b < a) continue;
                    Vector3 start = nodes[a].position;
                    Vector3 delta = nodes[b].position - start;
                    float lengthSquared = delta.sqrMagnitude;
                    if (lengthSquared < 0.001f) continue;
                    float t = Mathf.Clamp01(Vector3.Dot(worldPoint - start, delta) / lengthSquared);
                    Vector3 point = start + delta * t;
                    float distance = (worldPoint - point).sqrMagnitude;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = new ClosestEdge(a, b, t, point);
                }
            }
            return best;
        }

        public Vector3 GetRandomTrackPoint(System.Random random)
        {
            IReadOnlyList<GraphNode> nodes = Graph;
            if (nodes.Count == 0) return transform.position;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                int a = random.Next(nodes.Count);
                if (nodes[a].neighbours.Count == 0) continue;
                int b = nodes[a].neighbours[random.Next(nodes[a].neighbours.Count)];
                return Vector3.Lerp(nodes[a].position, nodes[b].position, (float)random.NextDouble());
            }
            return nodes[0].position;
        }

        /// <summary>Shortest connected rail route, including partial first and last edges.</summary>
        public bool FindRoute(Vector3 from, Vector3 to, List<Vector3> route)
            => FindRoute(Graph, FindClosestEdge(from), FindClosestEdge(to), route);

        public static bool FindRoute(IReadOnlyList<GraphNode> nodes, ClosestEdge start, ClosestEdge finish, List<Vector3> route)
        {
            route.Clear();
            if (nodes.Count < 2) return false;
            if (start.A == finish.A && start.B == finish.B)
            {
                route.Add(start.Point); route.Add(finish.Point);
                return true;
            }
            var costs = new float[nodes.Count];
            var parents = new int[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) { costs[i] = float.PositiveInfinity; parents[i] = -1; }
            var queue = new SortedSet<(float cost, int node)>();
            Seed(start.A); Seed(start.B);
            float best = float.PositiveInfinity;
            int last = -1;
            while (queue.Count > 0)
            {
                var next = queue.Min;
                queue.Remove(next);
                if (next.cost >= best) break;
                if (next.node == finish.A || next.node == finish.B)
                {
                    float total = next.cost + Vector3.Distance(nodes[next.node].position, finish.Point);
                    if (total < best) { best = total; last = next.node; }
                }
                foreach (int neighbour in nodes[next.node].neighbours)
                {
                    float cost = next.cost + Vector3.Distance(nodes[next.node].position, nodes[neighbour].position);
                    if (cost >= costs[neighbour]) continue;
                    queue.Remove((costs[neighbour], neighbour));
                    costs[neighbour] = cost;
                    parents[neighbour] = next.node;
                    queue.Add((cost, neighbour));
                }
            }
            if (last < 0) return false;
            for (int i = last; i >= 0; i = parents[i]) route.Add(nodes[i].position);
            route.Reverse();
            route.Insert(0, start.Point);
            route.Add(finish.Point);
            return true;

            void Seed(int node)
            {
                costs[node] = Vector3.Distance(start.Point, nodes[node].position);
                queue.Add((costs[node], node));
            }
        }

        private List<GraphNode> BuildGraph()
        {
            const float mergeDistance = 0.35f;
            var result = new List<GraphNode>();
            var lookup = new Dictionary<Vector2Int, int>();

            int FindOrAdd(Vector3 point)
            {
                var key = new Vector2Int(
                    Mathf.RoundToInt(point.x / mergeDistance),
                    Mathf.RoundToInt(point.z / mergeDistance));
                if (lookup.TryGetValue(key, out int existing)) return existing;
                int index = result.Count;
                lookup.Add(key, index);
                result.Add(new GraphNode { position = point });
                return index;
            }

            foreach (TrackPath path in paths)
            {
                if (path.points == null) continue;
                for (int i = 0; i < path.points.Length - 1; i++)
                {
                    int a = FindOrAdd(path.points[i]);
                    int b = FindOrAdd(path.points[i + 1]);
                    if (a == b) continue;
                    if (!result[a].neighbours.Contains(b)) result[a].neighbours.Add(b);
                    if (!result[b].neighbours.Contains(a)) result[b].neighbours.Add(a);
                }
            }
            return result;
        }
    }
}
