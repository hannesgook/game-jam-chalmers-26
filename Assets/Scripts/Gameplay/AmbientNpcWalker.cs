using System.Collections.Generic;
using SparvagnRush.Map;
using UnityEngine;

namespace SparvagnRush.Gameplay
{
    /// <summary>
    /// A small, procedural pedestrian which follows the tram graph at a safe
    /// offset. Using the graph keeps pedestrians close to streets without
    /// requiring every generated map to contain a baked NavMesh.
    /// </summary>
    public sealed class AmbientNpcWalker : MonoBehaviour
    {
        private const float GroundProbeHeight = 80f;
        private static readonly RaycastHit[] GroundHits = new RaycastHit[12];

        private TramTrackNetwork network;
        private PedestrianWalkableArea walkableArea;
        private System.Random random;
        private int fromNode;
        private int toNode;
        private float edgeProgress;
        private float walkingSpeed;
        private float sidewalkOffset;
        private float groundProbeTimer;
        private float groundedY;
        private float stride;

        private Transform model;
        private Transform leftArm;
        private Transform rightArm;
        private Transform leftLeg;
        private Transform rightLeg;
        private Material shirtMaterial;
        private Material trouserMaterial;
        private Material skinMaterial;

        public void Initialize(
            TramTrackNetwork trackNetwork,
            PedestrianWalkableArea area,
            System.Random sharedRandom)
        {
            network = trackNetwork;
            walkableArea = area;
            random = sharedRandom;
            BuildModel();
            CapsuleCollider hitbox = gameObject.AddComponent<CapsuleCollider>();
            hitbox.center = new Vector3(0f, 1.2f, 0f);
            hitbox.height = 2.4f;
            hitbox.radius = 0.5f;
            hitbox.isTrigger = true;
        }

        public bool ResetTo(TramTrackNetwork.ClosestEdge edge)
        {
            bool reverse = random.Next(2) == 0;
            fromNode = reverse ? edge.B : edge.A;
            toNode = reverse ? edge.A : edge.B;

            float length = CurrentEdgeLength();
            edgeProgress = length * (reverse ? 1f - edge.T : edge.T);
            walkingSpeed = Mathf.Lerp(1.25f, 2.05f, (float)random.NextDouble());
            sidewalkOffset = Mathf.Lerp(4.5f, 8.5f, (float)random.NextDouble()) * (random.Next(2) == 0 ? -1f : 1f);
            stride = (float)random.NextDouble() * Mathf.PI * 2f;
            groundProbeTimer = 0f;

            Vector3 position = RoutePosition();
            if (!walkableArea.Allows(position, position)) return false;
            position.y = FindGroundHeight(position, position.y);
            groundedY = position.y;
            transform.position = position;

            Vector3 direction = EdgeDirection();
            if (direction.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
            return true;
        }

        private void Update()
        {
            if (network == null || model == null) return;

            int oldFrom = fromNode, oldTo = toNode;
            float oldProgress = edgeProgress, oldOffset = sidewalkOffset;
            Advance(walkingSpeed * Time.deltaTime);
            Vector3 desired = RoutePosition();
            // Check the complete swept move, including junction shortcuts and
            // long frames. Safe endpoints alone can still straddle water/rails.
            if (!walkableArea.Allows(transform.position, desired))
            {
                fromNode = oldTo;
                toNode = oldFrom;
                edgeProgress = CurrentEdgeLength() - oldProgress;
                sidewalkOffset = -oldOffset;
                return;
            }
            groundProbeTimer -= Time.deltaTime;
            if (groundProbeTimer <= 0f)
            {
                groundedY = FindGroundHeight(desired, desired.y);
                groundProbeTimer = 0.18f + (float)random.NextDouble() * 0.12f;
            }
            desired.y = groundedY;

            float follow = 1f - Mathf.Exp(-8f * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desired, follow);

            Vector3 direction = EdgeDirection();
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f)
            {
                Quaternion facing = Quaternion.LookRotation(direction, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, facing, 240f * Time.deltaTime);
            }

            AnimateWalk();
        }

        private void Advance(float distance)
        {
            for (int safety = 0; safety < 12 && distance > 0f; safety++)
            {
                float length = CurrentEdgeLength();
                if (length < 0.01f)
                {
                    MoveToNextEdge();
                    continue;
                }

                float remaining = length - edgeProgress;
                if (distance < remaining)
                {
                    edgeProgress += distance;
                    return;
                }

                distance -= remaining;
                MoveToNextEdge();
            }
        }

        private void MoveToNextEdge()
        {
            int previous = fromNode;
            int reached = toNode;
            int next = ChooseNext(previous, reached);
            // Reversing direction must preserve the physical side of the road.
            if (next == previous) sidewalkOffset = -sidewalkOffset;
            fromNode = reached;
            toNode = next;
            edgeProgress = 0f;
        }

        private int ChooseNext(int previous, int reached)
        {
            IReadOnlyList<TramTrackNetwork.GraphNode> graph = network.Graph;
            List<int> neighbours = graph[reached].neighbours;
            if (neighbours.Count == 0) return previous;

            int alternatives = 0;
            foreach (int neighbour in neighbours)
                if (neighbour != previous) alternatives++;

            if (alternatives == 0) return previous;
            int selected = random.Next(alternatives);
            foreach (int neighbour in neighbours)
            {
                if (neighbour == previous) continue;
                if (selected-- == 0) return neighbour;
            }
            return previous;
        }

        private Vector3 RoutePosition()
        {
            IReadOnlyList<TramTrackNetwork.GraphNode> graph = network.Graph;
            Vector3 a = graph[fromNode].position;
            Vector3 b = graph[toNode].position;
            float length = Vector3.Distance(a, b);
            float t = length < 0.01f ? 0f : Mathf.Clamp01(edgeProgress / length);
            Vector3 centre = Vector3.Lerp(a, b, t);
            Vector3 direction = b - a;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return centre;
            Vector3 right = Vector3.Cross(Vector3.up, direction.normalized);
            return centre + right * sidewalkOffset;
        }

        private Vector3 EdgeDirection()
        {
            IReadOnlyList<TramTrackNetwork.GraphNode> graph = network.Graph;
            return (graph[toNode].position - graph[fromNode].position).normalized;
        }

        private float CurrentEdgeLength()
        {
            IReadOnlyList<TramTrackNetwork.GraphNode> graph = network.Graph;
            return Vector3.Distance(graph[fromNode].position, graph[toNode].position);
        }

        private static float FindGroundHeight(Vector3 position, float fallback)
        {
            int hitCount = Physics.RaycastNonAlloc(
                position + Vector3.up * GroundProbeHeight,
                Vector3.down,
                GroundHits,
                GroundProbeHeight * 2f);
            float highest = float.NegativeInfinity;
            for (int i = 0; i < hitCount; i++)
            {
                if (GroundHits[i].collider.gameObject.name != "Ground") continue;
                highest = Mathf.Max(highest, GroundHits[i].point.y);
            }
            return float.IsNegativeInfinity(highest) ? fallback : highest + 0.03f;
        }

        private void AnimateWalk()
        {
            stride += Time.deltaTime * walkingSpeed * 6.2f;
            float swing = Mathf.Sin(stride) * 28f;
            leftLeg.localRotation = Quaternion.Euler(swing, 0f, 0f);
            rightLeg.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            leftArm.localRotation = Quaternion.Euler(-swing * 0.75f, 0f, 7f);
            rightArm.localRotation = Quaternion.Euler(swing * 0.75f, 0f, -7f);
            model.localPosition = Vector3.up * (Mathf.Abs(Mathf.Sin(stride)) * 0.035f);
        }

        private void BuildModel()
        {
            model = new GameObject("Model").transform;
            model.SetParent(transform, false);

            Color clothing = Color.HSVToRGB((float)random.NextDouble(), 0.48f, 0.82f);
            Color trousers = Color.Lerp(clothing, new Color(0.08f, 0.1f, 0.14f), 0.68f);
            Color skin = Color.Lerp(
                new Color(0.36f, 0.18f, 0.10f),
                new Color(1f, 0.75f, 0.57f),
                (float)random.NextDouble());
            shirtMaterial = SparvagnRushBootstrap.CreateRuntimeMaterial(clothing);
            trouserMaterial = SparvagnRushBootstrap.CreateRuntimeMaterial(trousers);
            skinMaterial = SparvagnRushBootstrap.CreateRuntimeMaterial(skin);

            CreatePart("Torso", PrimitiveType.Capsule, model, new Vector3(0f, 1.43f, 0f), new Vector3(0.38f, 0.52f, 0.27f), shirtMaterial);
            CreatePart("Head", PrimitiveType.Sphere, model, new Vector3(0f, 2.18f, 0f), Vector3.one * 0.43f, skinMaterial);

            leftLeg = CreatePart("Left Leg", PrimitiveType.Cylinder, model, new Vector3(-0.14f, 0.62f, 0f), new Vector3(0.115f, 0.38f, 0.115f), trouserMaterial);
            rightLeg = CreatePart("Right Leg", PrimitiveType.Cylinder, model, new Vector3(0.14f, 0.62f, 0f), new Vector3(0.115f, 0.38f, 0.115f), trouserMaterial);
            leftArm = CreatePart("Left Arm", PrimitiveType.Cylinder, model, new Vector3(-0.43f, 1.44f, 0f), new Vector3(0.085f, 0.36f, 0.085f), skinMaterial);
            rightArm = CreatePart("Right Arm", PrimitiveType.Cylinder, model, new Vector3(0.43f, 1.44f, 0f), new Vector3(0.085f, 0.36f, 0.085f), skinMaterial);

            // Limbs rotate around their upper ends rather than their centres.
            leftLeg = ReparentAroundPivot(leftLeg, new Vector3(-0.14f, 1f, 0f));
            rightLeg = ReparentAroundPivot(rightLeg, new Vector3(0.14f, 1f, 0f));
            leftArm = ReparentAroundPivot(leftArm, new Vector3(-0.43f, 1.8f, 0f));
            rightArm = ReparentAroundPivot(rightArm, new Vector3(0.43f, 1.8f, 0f));
        }

        private void OnDestroy()
        {
            if (shirtMaterial != null) Destroy(shirtMaterial);
            if (trouserMaterial != null) Destroy(trouserMaterial);
            if (skinMaterial != null) Destroy(skinMaterial);
        }

        private static Transform CreatePart(
            string partName,
            PrimitiveType primitive,
            Transform parent,
            Vector3 position,
            Vector3 scale,
            Material material)
        {
            GameObject part = GameObject.CreatePrimitive(primitive);
            part.name = partName;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            part.GetComponent<Renderer>().sharedMaterial = material;
            Destroy(part.GetComponent<Collider>());
            return part.transform;
        }

        private static Transform ReparentAroundPivot(Transform limb, Vector3 pivotPosition)
        {
            Transform originalParent = limb.parent;
            Vector3 originalPosition = limb.localPosition;
            var pivot = new GameObject($"{limb.name} Pivot").transform;
            pivot.SetParent(originalParent, false);
            pivot.localPosition = pivotPosition;
            limb.SetParent(pivot, false);
            limb.localPosition = originalPosition - pivotPosition;
            return pivot;
        }
    }
}
