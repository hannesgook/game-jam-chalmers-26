using System.Collections.Generic;
using SparvagnRush.Map;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SparvagnRush.Gameplay
{
    public sealed class TramController : MonoBehaviour
    {
        [SerializeField] private float maximumSpeed = 24f;
        [SerializeField] private float acceleration = 15f;
        [SerializeField] private float coastingDrag = 8f;

        [Header("Curve handling")]
        [Tooltip("Sharpest branch, in degrees, the tram will switch onto at a junction. Anything sharper doubles back on itself and is never offered.")]
        [SerializeField] private float maximumJunctionTurn = 50f;
        [Tooltip("Lateral acceleration allowed in a bend. 0 carries full speed through every curve; raise it to make tight curves slow the tram down.")]
        [SerializeField] private float curveLateralAcceleration = 0f;
        [Tooltip("How hard the tram brakes for the end of a line, and for curves when the setting above is above zero.")]
        [SerializeField] private float curveBraking = 6f;
        [Tooltip("How far along the track the brakes look for something to slow for.")]
        [SerializeField] private float curveLookAhead = 45f;
        [Tooltip("Speed the tram may always creep at, so a tight corner can never strand it.")]
        [SerializeField] private float minimumCurveSpeed = 1.5f;
        [Tooltip("Degrees per second the body may swing round. A backstop against a snap, not a brake: the track needs at most ~115 deg/s at full speed.")]
        [SerializeField] private float maximumYawRate = 180f;
        [Tooltip("How far ahead the nose aims, which sweeps the body through a corner instead of pivoting it on the node.")]
        [SerializeField] private float headingLookAhead = 6f;

        // 45 m of look-ahead is ~18 nodes on densified track, ~4 at raw OSM node spacing.
        private const int LookAheadNodeLimit = 48;

        private TramTrackNetwork network;
        private int currentNode;
        private int targetNode;
        private float edgeProgress;
        private float speed;
        private int junctionChoice;
        private bool headingInitialised;

        public float Speed => speed;

        public void Initialize(TramTrackNetwork trackNetwork, Vector3 requestedStart)
        {
            network = trackNetwork;
            TramTrackNetwork.ClosestEdge edge = network.FindClosestEdge(requestedStart);
            currentNode = edge.A;
            targetNode = edge.B;
            float length = Vector3.Distance(network.Graph[currentNode].position, network.Graph[targetNode].position);
            edgeProgress = edge.T * length;
            ApplyTransform();
        }

        private void Update()
        {
            if (network == null || network.Graph.Count < 2) return;
            Keyboard keyboard = Keyboard.current;
            float throttle = 0f;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) throttle += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) throttle -= 1f;
                // Held, not tapped: lean on the key and every junction reached while it is
                // down is taken that way, so the turn never has to be timed.
                bool left = keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed;
                bool right = keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed;
                junctionChoice = left == right ? 0 : left ? -1 : 1;
            }

            speed = throttle == 0f
                ? Mathf.MoveTowards(speed, 0f, coastingDrag * Time.deltaTime)
                : Mathf.MoveTowards(speed, throttle * maximumSpeed, acceleration * Time.deltaTime);

            // A tram has to be slow enough to hold the rail through the bend it is entering.
            bool forward = (Mathf.Abs(speed) > 0.01f ? speed : throttle) >= 0f;
            float curveLimit = CurveSpeedLimit(forward);
            speed = Mathf.Clamp(speed, -curveLimit, curveLimit);

            edgeProgress += speed * Time.deltaTime;
            AdvanceAcrossNodes();
            ApplyTransform();
        }

        private void AdvanceAcrossNodes()
        {
            for (int guard = 0; guard < 8; guard++)
            {
                float length = CurrentEdgeLength();
                if (edgeProgress > length)
                {
                    float overflow = edgeProgress - length;
                    int previous = currentNode;
                    int reached = targetNode;
                    int next = ChooseNext(previous, reached);
                    if (next < 0)
                    {
                        edgeProgress = length;
                        speed = -Mathf.Abs(speed) * 0.35f;
                        return;
                    }
                    currentNode = reached;
                    targetNode = next;
                    edgeProgress = overflow;
                    continue;
                }

                if (edgeProgress < 0f)
                {
                    float overflow = -edgeProgress;
                    int previous = targetNode;
                    int reached = currentNode;
                    int next = ChooseNext(previous, reached);
                    if (next < 0)
                    {
                        edgeProgress = 0f;
                        speed = Mathf.Abs(speed) * 0.35f;
                        return;
                    }
                    targetNode = reached;
                    currentNode = next;
                    edgeProgress = Mathf.Max(0f, CurrentEdgeLength() - overflow);
                    continue;
                }
                return;
            }
        }

        private int ChooseNext(int previous, int reached)
        {
            IReadOnlyList<TramTrackNetwork.GraphNode> graph = network.Graph;
            List<int> neighbours = graph[reached].neighbours;
            if (neighbours.Count == 0) return -1;
            if (neighbours.Count == 1) return neighbours[0] == previous ? -1 : neighbours[0];

            Vector3 incoming = (graph[reached].position - graph[previous].position).normalized;
            int best = -1;
            float bestScore = junctionChoice < 0 ? float.MaxValue : float.MinValue;
            float straightest = float.MaxValue;
            int fallback = -1;
            float fallbackTurn = float.MaxValue;
            foreach (int candidate in neighbours)
            {
                if (candidate == previous) continue;
                Vector3 outgoing = (graph[candidate].position - graph[reached].position).normalized;
                float angle = Vector3.SignedAngle(incoming, outgoing, Vector3.up);
                // Kept regardless of the turn limit: where the geometry leaves only one
                // way on, refusing it would strand the tram mid-track.
                if (Mathf.Abs(angle) < fallbackTurn)
                {
                    fallbackTurn = Mathf.Abs(angle);
                    fallback = candidate;
                }

                // No switch routes a tram back the way it came, so branches that double
                // back are not on offer however hard the player steers into them.
                if (Mathf.Abs(angle) > maximumJunctionTurn) continue;
                if (junctionChoice < 0 && angle < bestScore)
                {
                    bestScore = angle;
                    best = candidate;
                }
                else if (junctionChoice > 0 && angle > bestScore)
                {
                    bestScore = angle;
                    best = candidate;
                }
                else if (junctionChoice == 0 && Mathf.Abs(angle) < straightest)
                {
                    straightest = Mathf.Abs(angle);
                    best = candidate;
                }
            }
            return best >= 0 ? best : fallback;
        }

        // Fastest the tram may be going right now to still hold every curve ahead of it.
        private float CurveSpeedLimit(bool forward)
        {
            IReadOnlyList<TramTrackNetwork.GraphNode> graph = network.Graph;
            int previous = forward ? currentNode : targetNode;
            int reached = forward ? targetNode : currentNode;
            float toNode = Mathf.Max(0f, forward ? CurrentEdgeLength() - edgeProgress : edgeProgress);
            float limit = maximumSpeed;
            float walked = 0f;

            for (int step = 0; step < LookAheadNodeLimit && walked < curveLookAhead; step++)
            {
                int next = ChooseNext(previous, reached);
                // Nothing beyond this node, so the tram has to arrive stopped.
                float corner = next < 0 ? 0f : CornerSpeed(previous, reached, next);
                // Braking at curveBraking from `limit` leaves exactly `corner` at the node.
                limit = Mathf.Min(limit, Mathf.Sqrt(corner * corner + 2f * curveBraking * toNode));
                if (next < 0) break;

                float edge = Vector3.Distance(graph[reached].position, graph[next].position);
                previous = reached;
                reached = next;
                toNode += edge;
                walked += edge;
            }
            return Mathf.Max(limit, minimumCurveSpeed);
        }

        // Treats the corner at `reached` as a circular arc through its two edges and
        // returns the speed that keeps lateral acceleration comfortable.
        private float CornerSpeed(int previous, int reached, int next)
        {
            IReadOnlyList<TramTrackNetwork.GraphNode> graph = network.Graph;
            Vector3 incoming = graph[reached].position - graph[previous].position;
            Vector3 outgoing = graph[next].position - graph[reached].position;
            if (incoming.sqrMagnitude < 0.0001f || outgoing.sqrMagnitude < 0.0001f) return maximumSpeed;

            // Zero means bends never cost speed, so only a dead end can slow the tram.
            if (curveLateralAcceleration <= 0f) return maximumSpeed;

            float turn = Vector3.Angle(incoming, outgoing);
            if (turn < 0.01f) return maximumSpeed;
            float chord = (incoming.magnitude + outgoing.magnitude) * 0.5f;
            float radius = chord / (2f * Mathf.Sin(turn * Mathf.Deg2Rad * 0.5f));
            return Mathf.Sqrt(curveLateralAcceleration * Mathf.Max(0.5f, radius));
        }

        // Point `distance` further along the track, used to aim the nose into a bend.
        private Vector3 TrackPointAhead(float distance)
        {
            IReadOnlyList<TramTrackNetwork.GraphNode> graph = network.Graph;
            int previous = currentNode;
            int reached = targetNode;
            float remaining = Mathf.Max(0f, CurrentEdgeLength() - edgeProgress);

            for (int step = 0; step < LookAheadNodeLimit; step++)
            {
                if (distance <= remaining)
                {
                    Vector3 from = graph[previous].position;
                    Vector3 to = graph[reached].position;
                    float edge = Vector3.Distance(from, to);
                    if (edge < 0.001f) return to;
                    return Vector3.Lerp(to, from, (remaining - distance) / edge);
                }

                distance -= remaining;
                int next = ChooseNext(previous, reached);
                if (next < 0) return graph[reached].position;
                previous = reached;
                reached = next;
                remaining = Vector3.Distance(graph[previous].position, graph[reached].position);
            }
            return graph[reached].position;
        }

        private float CurrentEdgeLength() => Vector3.Distance(network.Graph[currentNode].position, network.Graph[targetNode].position);

        private void ApplyTransform()
        {
            Vector3 a = network.Graph[currentNode].position;
            Vector3 b = network.Graph[targetNode].position;
            float length = Vector3.Distance(a, b);
            float t = length < 0.001f ? 0f : Mathf.Clamp01(edgeProgress / length);
            Vector3 position = Vector3.Lerp(a, b, t) + Vector3.up * 1.1f;
            transform.position = position;

            // Aiming at a point further down the track sweeps the body through a bend
            // rather than pivoting it on the node, and keeps the pitch of the slope.
            Vector3 direction = TrackPointAhead(headingLookAhead) + Vector3.up * 1.1f - position;
            if (direction.sqrMagnitude < 0.001f) direction = b - a;
            if (direction.sqrMagnitude < 0.001f) return;

            Quaternion desired = Quaternion.LookRotation(direction, Vector3.up);
            transform.rotation = headingInitialised
                ? Quaternion.RotateTowards(transform.rotation, desired, maximumYawRate * Time.deltaTime)
                : desired;
            headingInitialised = true;
        }
    }
}
