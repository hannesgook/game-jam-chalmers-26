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

        private TramTrackNetwork network;
        private int currentNode;
        private int targetNode;
        private float edgeProgress;
        private float speed;
        private int junctionChoice;

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
                if (keyboard.aKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame) junctionChoice = -1;
                if (keyboard.dKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame) junctionChoice = 1;
            }

            speed = throttle == 0f
                ? Mathf.MoveTowards(speed, 0f, coastingDrag * Time.deltaTime)
                : Mathf.MoveTowards(speed, throttle * maximumSpeed, acceleration * Time.deltaTime);

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
                    junctionChoice = 0;
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
                    junctionChoice = 0;
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
            foreach (int candidate in neighbours)
            {
                if (candidate == previous && neighbours.Count > 1) continue;
                Vector3 outgoing = (graph[candidate].position - graph[reached].position).normalized;
                float angle = Vector3.SignedAngle(incoming, outgoing, Vector3.up);
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
            return best >= 0 ? best : previous;
        }

        private float CurrentEdgeLength() => Vector3.Distance(network.Graph[currentNode].position, network.Graph[targetNode].position);

        private void ApplyTransform()
        {
            Vector3 a = network.Graph[currentNode].position;
            Vector3 b = network.Graph[targetNode].position;
            float length = Vector3.Distance(a, b);
            float t = length < 0.001f ? 0f : Mathf.Clamp01(edgeProgress / length);
            transform.position = Vector3.Lerp(a, b, t) + Vector3.up * 1.1f;
            Vector3 direction = b - a;
            if (direction.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        }
    }
}
