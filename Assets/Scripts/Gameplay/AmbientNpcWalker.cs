using System.Collections.Generic;
using TramRush.Map;
using UnityEngine;

namespace TramRush.Gameplay
{
    /// <summary>
    /// A small, procedural pedestrian which follows the tram graph at a safe
    /// offset. Using the graph keeps pedestrians close to streets without
    /// requiring every generated map to contain a baked NavMesh.
    /// </summary>
    public sealed class AmbientNpcWalker : MonoBehaviour
    {
        private const float GroundProbeHeight = 80f;
        // Nobody may cross a street faster than a brisk walk, so corners and kerb
        // corrections are travelled rather than teleported through.
        private const float MaximumStepFactor = 2.4f;
        // How close to the rails a blocked walker is willing to squeeze past. The
        // walkable area still rejects anything overlapping the track itself.
        private const float MinimumKerbOffset = 2.6f;
        // How near the tram has to come before somebody turns and walks it down.
        private const float ChaseRadius = 90f;
        private const float ChaseSpeedFactor = 1.6f;
        private static readonly RaycastHit[] GroundHits = new RaycastHit[12];
        // Outfits are shared rather than made per person. With a crowd this size a
        // material each would mean a draw call each, and none of them could batch.
        private const int OutfitCount = 14;
        private const int SkinCount = 6;
        private static Material[] shirtPalette, trouserPalette, skinPalette;

        private TramTrackNetwork network;
        private PedestrianWalkableArea walkableArea;
        private System.Random random;
        private int fromNode;
        private int toNode;
        private float edgeProgress;
        private float walkingSpeed;
        private float sidewalkOffset;
        private float appliedOffset;
        private float preferredOffset;
        private float groundProbeTimer;
        private float groundedY;
        private float stride;
        private CapsuleCollider hitbox;

        private Transform model;
        private Transform leftArm;
        private Transform rightArm;
        private Transform leftLeg;
        private Transform rightLeg;
        private Material shirtMaterial;
        private Material trouserMaterial;
        private Material skinMaterial;
        private bool chasing;
        private bool flying;
        private Vector3 flightVelocity;
        private float tumble;
        // A honk buys real breathing room: somebody thrown clear picks themselves up
        // and walks normally for a while before they will lock on again.
        private float mayHuntFrom;
        public bool IsDown { get; private set; }

        /// <summary>
        /// Somebody who reached a tram that was barely moving got on board rather
        /// than being run down. They simply leave the world; the manager places them
        /// somewhere out of sight on its next sweep.
        /// </summary>
        public void Board() => gameObject.SetActive(false);

        /// <summary>
        /// Thrown clear by the horn. They stop being a hazard the moment they leave
        /// the ground and sprawl wherever they come down.
        /// </summary>
        public void LaunchAside(Vector3 velocity)
        {
            if (IsDown || flying) return;
            flying = true;
            flightVelocity = velocity;
            tumble = 420f + (float)random.NextDouble() * 340f;
            hitbox.enabled = false;
            chasing = false;
        }

        public void KnockDown()
        {
            if (IsDown) return;
            IsDown = true;
            hitbox.enabled = false;
            model.localRotation = Quaternion.Euler(85f, 0, 0);
            model.localPosition = Vector3.up * 0.2f;
        }

        public void Initialize(
            TramTrackNetwork trackNetwork,
            PedestrianWalkableArea area,
            System.Random sharedRandom)
        {
            network = trackNetwork;
            walkableArea = area;
            random = sharedRandom;
            BuildModel();
            hitbox = gameObject.AddComponent<CapsuleCollider>();
            hitbox.center = new Vector3(0f, 1.2f, 0f);
            hitbox.height = 2.4f;
            hitbox.radius = 0.5f;
            hitbox.isTrigger = true;
        }

        public bool ResetTo(TramTrackNetwork.ClosestEdge edge)
        {
            IsDown = false;
            chasing = false;
            flying = false;
            hitbox.enabled = true;
            model.localRotation = Quaternion.identity;
            model.localPosition = Vector3.zero;
            bool reverse = random.Next(2) == 0;
            fromNode = reverse ? edge.B : edge.A;
            toNode = reverse ? edge.A : edge.B;

            float length = CurrentEdgeLength();
            edgeProgress = length * (reverse ? 1f - edge.T : edge.T);
            walkingSpeed = Mathf.Lerp(1.9f, 3.0f, (float)random.NextDouble());
            stride = (float)random.NextDouble() * Mathf.PI * 2f;
            groundProbeTimer = 0f;

            float wanted = Mathf.Lerp(4.5f, 8.5f, (float)random.NextDouble());
            float firstSide = random.Next(2) == 0 ? -1f : 1f;
            // Try both pavements, narrowing toward the kerb. A street may be built
            // over on one side only, and refusing outright would thin the crowd out
            // exactly where the city is densest. Placement itself is instant; only
            // later corrections are walked out.
            Vector3 position = Vector3.zero;
            bool placed = false;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                float width = wanted - attempt / 2 * 1.2f;
                if (width < MinimumKerbOffset) break;
                float side = attempt % 2 == 0 ? firstSide : -firstSide;
                preferredOffset = side * wanted;
                sidewalkOffset = appliedOffset = side * width;
                position = RoutePosition();
                if (!walkableArea.Allows(position, position)) continue;
                placed = true;
                break;
            }
            if (!placed) return false;
            position.y = FindGroundHeight(position, position.y);
            if (Physics.CheckCapsule(position + Vector3.up * 0.5f, position + Vector3.up * 2f,
                0.3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
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
            // A frozen game freezes the crowd with it, and saves the walkable-area
            // queries that would otherwise run for nothing behind the end screen.
            if (Time.deltaTime <= 0f) return;
            if (flying) { Fly(); return; }
            if (IsDown) return;

            if (!chasing && AmbientNpcManager.Hunting && Time.time >= mayHuntFrom &&
                (transform.position - AmbientNpcManager.ChaseTarget).sqrMagnitude < ChaseRadius * ChaseRadius)
                chasing = true;
            // Once somebody has locked on they stay locked on until they are
            // recycled, so there is no snapping back onto the graph behind you.
            if (chasing) { Chase(); return; }

            int oldFrom = fromNode, oldTo = toNode;
            float oldProgress = edgeProgress;
            Advance(walkingSpeed * Time.deltaTime);
            // Walk the lateral offset out rather than switching sides between two
            // frames: a corner, a kerb correction and a turn all take real steps.
            float stepped = Mathf.MoveTowards(appliedOffset, sidewalkOffset, walkingSpeed * 0.9f * Time.deltaTime);
            Vector3 desired = RoutePosition(stepped);
            // Check the complete swept move, including junction shortcuts and
            // long frames. Safe endpoints alone can still straddle water/rails.
            if (!walkableArea.Allows(transform.position, desired))
            {
                // Rewind the step. A building or the water juts into this stretch,
                // so try the kerb first; only a closed street turns somebody round.
                fromNode = oldFrom;
                toNode = oldTo;
                edgeProgress = oldProgress;
                if (!StepTowardKerb()) TurnAround(oldFrom, oldTo, oldProgress);
                return;
            }
            appliedOffset = stepped;
            // Once the obstruction is behind us, drift back out to a normal pavement
            // distance instead of hugging the rails for the rest of the walk.
            if (Mathf.Abs(sidewalkOffset) < Mathf.Abs(preferredOffset) - 0.05f)
            {
                float wider = Mathf.MoveTowards(sidewalkOffset, preferredOffset, 1.2f * Time.deltaTime);
                if (walkableArea.Allows(transform.position, RoutePosition(wider))) sidewalkOffset = wider;
            }

            groundProbeTimer -= Time.deltaTime;
            if (groundProbeTimer <= 0f)
            {
                groundedY = FindGroundHeight(desired, desired.y);
                groundProbeTimer = 0.18f + (float)random.NextDouble() * 0.12f;
            }
            desired.y = groundedY;

            float follow = 1f - Mathf.Exp(-8f * Time.deltaTime);
            Vector3 smoothed = Vector3.Lerp(transform.position, desired, follow);
            // Hard cap on how far anybody may travel in one frame. A graph junction
            // swings the pavement line around its node, and without this the person
            // would flick across the street instead of walking round the corner.
            transform.position = Vector3.MoveTowards(transform.position, smoothed,
                walkingSpeed * MaximumStepFactor * Time.deltaTime);

            Vector3 direction = EdgeDirection();
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f)
            {
                Quaternion facing = Quaternion.LookRotation(direction, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, facing, 240f * Time.deltaTime);
            }

            AnimateWalk();
        }

        /// <summary>
        /// Ballistic arc for somebody the horn has thrown. The walkable area is not
        /// consulted: they are in the air, and where they land is where they land.
        /// </summary>
        private void Fly()
        {
            flightVelocity += Physics.gravity * Time.deltaTime;
            transform.position += flightVelocity * Time.deltaTime;
            model.localRotation *= Quaternion.Euler(tumble * Time.deltaTime, 0f, tumble * 0.4f * Time.deltaTime);

            float floor = FindGroundHeight(transform.position, groundedY);
            if (transform.position.y > floor) return;
            Vector3 resting = transform.position;
            resting.y = floor;
            transform.position = resting;
            groundedY = floor;
            StandUp();
        }

        /// <summary>
        /// Picks somebody up where they landed. A horn moves people out of the way,
        /// it does not kill them, so they rejoin the street rather than staying down.
        /// </summary>
        private void StandUp()
        {
            flying = false;
            model.localRotation = Quaternion.identity;
            model.localPosition = Vector3.zero;
            hitbox.enabled = true;
            mayHuntFrom = Time.time + 6f;

            // Re-anchor to whatever street they came down beside, keeping the side
            // they actually landed on. The transform stays put and the per-frame
            // travel cap walks them back to the pavement, so nobody snaps across
            // the road after a honk.
            TramTrackNetwork.ClosestEdge edge = network.FindClosestEdge(transform.position);
            bool reverse = random.Next(2) == 0;
            fromNode = reverse ? edge.B : edge.A;
            toNode = reverse ? edge.A : edge.B;
            float length = CurrentEdgeLength();
            edgeProgress = length * (reverse ? 1f - edge.T : edge.T);

            Vector3 along = EdgeDirection();
            along.y = 0f;
            Vector3 right = along.sqrMagnitude > 0.0001f
                ? Vector3.Cross(Vector3.up, along.normalized)
                : Vector3.right;
            float side = Vector3.Dot(transform.position - RoutePosition(0f), right);
            float width = Mathf.Clamp(Mathf.Abs(side), MinimumKerbOffset, 8.5f);
            preferredOffset = sidewalkOffset = appliedOffset = side < 0f ? -width : width;
        }

        /// <summary>
        /// Walks straight at the tram, fanning out around whatever is in the way.
        /// The rails are deliberately fair game here: stepping onto the track is the
        /// whole threat. Water and buildings still stop them.
        /// </summary>
        private void Chase()
        {
            Vector3 direction = AmbientNpcManager.ChaseTarget - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) return;
            direction.Normalize();

            float step = walkingSpeed * ChaseSpeedFactor * Time.deltaTime;
            Vector3 moved = Vector3.zero;
            bool stepped = false;
            for (int i = 0; i < 5; i++)
            {
                float sweep = i == 0 ? 0f : (i % 2 == 1 ? 1f : -1f) * 35f * ((i + 1) / 2);
                Vector3 heading = Quaternion.Euler(0f, sweep, 0f) * direction;
                Vector3 candidate = transform.position + heading * step;
                if (!walkableArea.Allows(transform.position, candidate, false)) continue;
                moved = candidate;
                direction = heading;
                stepped = true;
                break;
            }
            if (!stepped) return;

            groundProbeTimer -= Time.deltaTime;
            if (groundProbeTimer <= 0f)
            {
                groundedY = FindGroundHeight(moved, moved.y);
                groundProbeTimer = 0.18f + (float)random.NextDouble() * 0.12f;
            }
            moved.y = groundedY;
            transform.position = moved;
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                Quaternion.LookRotation(direction, Vector3.up), 360f * Time.deltaTime);
            AnimateWalk();
        }

        /// <summary>
        /// Narrows the pavement offset until the swept move clears the obstruction.
        /// Returns false when even the kerb is blocked and the walker must turn back.
        /// </summary>
        private bool StepTowardKerb()
        {
            float side = appliedOffset < 0f ? -1f : 1f;
            for (float width = Mathf.Abs(appliedOffset) - 0.6f; width >= MinimumKerbOffset; width -= 0.6f)
            {
                if (!walkableArea.Allows(transform.position, RoutePosition(side * width))) continue;
                // Commit the applied offset as well as the target. The swept check
                // above cleared the path from where the walker stands to this narrower
                // line, and leaving appliedOffset behind would retry a blocked step
                // every frame without ever reaching the kerb. The per-frame travel
                // cap still turns the jump into a visible sidestep.
                sidewalkOffset = appliedOffset = side * width;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reverses along the current edge. The pavement normal flips with the edge
        /// direction, so every offset is mirrored to keep the person where they are.
        /// </summary>
        private void TurnAround(int oldFrom, int oldTo, float oldProgress)
        {
            fromNode = oldTo;
            toNode = oldFrom;
            edgeProgress = CurrentEdgeLength() - oldProgress;
            sidewalkOffset = -sidewalkOffset;
            appliedOffset = -appliedOffset;
            preferredOffset = -preferredOffset;
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
            if (next == previous)
            {
                sidewalkOffset = -sidewalkOffset;
                appliedOffset = -appliedOffset;
                preferredOffset = -preferredOffset;
            }
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

        private Vector3 RoutePosition() => RoutePosition(appliedOffset);

        private Vector3 RoutePosition(float offset)
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
            return centre + right * offset;
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
            stride += Time.deltaTime * walkingSpeed * (chasing ? ChaseSpeedFactor : 1f) * 6.2f;
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

            EnsurePalette();
            int outfit = random.Next(OutfitCount);
            shirtMaterial = shirtPalette[outfit];
            trouserMaterial = trouserPalette[outfit];
            skinMaterial = skinPalette[random.Next(SkinCount)];

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

        // The palette is shared, so nothing here owns a material to destroy.
        private static void EnsurePalette()
        {
            // Entering play mode again with domain reload off leaves the arrays
            // populated but their materials already destroyed, so check both.
            if (shirtPalette != null && shirtPalette[0] != null) return;
            shirtPalette = new Material[OutfitCount];
            trouserPalette = new Material[OutfitCount];
            for (int i = 0; i < OutfitCount; i++)
            {
                Color clothing = Color.HSVToRGB(i / (float)OutfitCount, 0.48f, 0.82f);
                shirtPalette[i] = TramRushBootstrap.CreateRuntimeMaterial(clothing);
                trouserPalette[i] = TramRushBootstrap.CreateRuntimeMaterial(
                    Color.Lerp(clothing, new Color(0.08f, 0.1f, 0.14f), 0.68f));
            }
            skinPalette = new Material[SkinCount];
            for (int i = 0; i < SkinCount; i++)
                skinPalette[i] = TramRushBootstrap.CreateRuntimeMaterial(Color.Lerp(
                    new Color(0.36f, 0.18f, 0.10f), new Color(1f, 0.75f, 0.57f), i / (SkinCount - 1f)));
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
