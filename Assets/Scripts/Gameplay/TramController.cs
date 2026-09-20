using System.Collections.Generic;
using TramRush.Map;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TramRush.Gameplay
{
    public sealed class TramController : MonoBehaviour
    {
        [SerializeField] private float maximumSpeed = 24f;
        [SerializeField] private float acceleration = 15f;
        [SerializeField] private float coastingDrag = 8f;

        [Header("Curve handling")]
        [Tooltip("Sharpest branch a switch will route onto. Only there to refuse the ones that double back: measured on the real map, genuine branches run up to about 130 degrees and reversals start near 140.")]
        [SerializeField] private float maximumJunctionTurn = 130f;
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
        [Tooltip("Lean this many degrees before a junction is committed left or right. High enough that a switch follows a deliberate lean rather than the wobble of holding the tram up.")]
        [SerializeField] private float junctionLeanThreshold = 15f;
        [Tooltip("Lean further than this and the switch is not thrown at all. Past it the tram is being fought back upright rather than steered, and a recovery swing should not pick a branch nobody asked for.")]
        [SerializeField] private float junctionLeanLimit = 30f;

        [Header("Balance")]
        [Tooltip("How hard the world fights to tip the tram over. Scales both the sideways throw of a curve and how fast a lean runs away. Lower is more forgiving; 1 is the physically honest value.")]
        [SerializeField] private float balanceSensitivity = 1f;
        [Tooltip("Height of the centre of mass above the rails. Lower topples faster and is twitchier to hold.")]
        [SerializeField] private float centreOfMassHeight = 1.8f;
        [Tooltip("Degrees per second squared of lean the driver can force by holding A or D.")]
        [SerializeField] private float leanAuthority = 360f;
        [Tooltip("Viscous drag on lean movement. It slows how fast a lean changes; it never pushes the tram back towards upright.")]
        [SerializeField] private float leanDamping = 2.6f;
        [Tooltip("How quickly A/D lean input ramps in and out.")]
        [SerializeField] private float leanInputResponse = 7f;
        [Tooltip("Lean past this and the tram is gone.")]
        [SerializeField] private float fallAngle = 60f;

        [Header("Derailment")]
        [Tooltip("Sideways speed the wreck is thrown at, the way it was falling. Very large on purpose: losing the tram should fling it across the street.")]
        [SerializeField] private float derailSidewaysSpeed = 40f;
        [Tooltip("Upward kick as the wheels leave the rail. This is what gets the wreck properly airborne rather than sliding.")]
        [SerializeField] private float derailLift = 15f;
        [Tooltip("Degrees per second the wreck tumbles at.")]
        [SerializeField] private float derailSpin = 520f;
        [SerializeField] private float derailMass = 1200f;
        [Tooltip("How far the underside of the wreck may sink into the ground before it is pushed back out.")]
        [SerializeField] private float wreckSinkTolerance = 0.5f;

        // Terrain tops out around 45 m, but a wreck can be thrown well above that.
        private const float GroundProbeHeight = 300f;
        // The generator's name for the ground layer, and the only surface the wreck
        // rescue below accepts as a floor.
        private const string GroundLayerName = "Ground";
        // A ray dropped through a city block crosses the roofs and walls as well as the
        // ground, and a saturated buffer is a buffer that can miss the floor.
        private static readonly RaycastHit[] GroundHits = new RaycastHit[16];

        // 45 m of look-ahead is ~18 nodes on densified track, ~4 at raw OSM node spacing.
        private const int LookAheadNodeLimit = 48;

        private TramTrackNetwork network;
        private int currentNode;
        private int targetNode;
        private float edgeProgress;
        private float speed;
        private int junctionChoice;
        private bool headingInitialised;
        private Quaternion trackRotation = Quaternion.identity;
        private float leanAngle;
        private float leanVelocity;
        private float smoothedLeanInput;
        private int travelSign = 1;
        public int TravelSign => travelSign;
        public static float TravelRelativeLean(float lean, int direction) => lean * direction;
        /// <summary>
        /// Which way a switch is thrown for a given lean: -1 left, 1 right, 0 straight.
        /// Only the band between <paramref name="threshold"/> and <paramref name="limit"/>
        /// steers. Below it the tram is merely wobbling; above it the driver is fighting
        /// to stay upright, and a recovery swing must not choose a branch for them.
        /// </summary>
        public static int JunctionForLean(float bodyLean, float threshold, float limit, int direction)
        {
            float lean = TravelRelativeLean(bodyLean, direction);
            if (Mathf.Abs(lean) > limit) return 0;
            return lean < -threshold ? -1 : lean > threshold ? 1 : 0;
        }
        private bool derailed;
        // The prefab's own collider, resolved on derail and used to measure the wreck.
        private Collider hull;
        private float derailFloor;
        private Vector3 startRequest;
        // How far the underside of the body sits below the transform pivot, measured
        // off the real collider rather than guessed. The rail runs through that
        // point, so leaning rotates about it and the bottom never leaves the rail.
        private float railOffset = 1.1f;
        private bool railOffsetMeasured;

        public float Speed => speed;
        public float LeanAngle => leanAngle;
        public float FallAngle => fallAngle;
        /// <summary>The lean band that throws a switch, for the balance meter to mark.</summary>
        public float SteerFrom => junctionLeanThreshold;
        public float SteerTo => junctionLeanLimit;
        public bool Derailed => derailed;

        public void Initialize(TramTrackNetwork trackNetwork, Vector3 requestedStart)
        {
            network = trackNetwork;
            startRequest = requestedStart;
            TramTrackNetwork.ClosestEdge edge = network.FindClosestEdge(requestedStart);
            currentNode = edge.A;
            targetNode = edge.B;
            float length = Vector3.Distance(network.Graph[currentNode].position, network.Graph[targetNode].position);
            edgeProgress = edge.T * length;
            ApplyTransform();
        }

        // Initialize runs before the collider has a world position to measure, so
        // the real offset is taken once the first frame has placed everything.
        private void Start()
        {
            if (railOffsetMeasured) return;
            railOffsetMeasured = true;
            Collider body = GetComponentInChildren<Collider>();
            if (body == null) return;
            Physics.SyncTransforms();
            // Bounds are world space and the tram is still upright here, so this is
            // simply how far the underside hangs below the pivot.
            float measured = transform.position.y - body.bounds.min.y;
            if (measured > 0.05f) railOffset = measured;
            ApplyTransform();
        }

        private void Update()
        {
            if (derailed) return;
            if (network == null || network.Graph.Count < 2) return;
            Keyboard keyboard = Keyboard.current;
            float throttle = 0f;
            float leanInput = 0f;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) throttle += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) throttle -= 1f;
                bool left = keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed;
                bool right = keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed;
                leanInput = left == right ? 0f : left ? -1f : 1f;
            }

            // Junctions follow the tram's physical pose, not the key currently held.
            // This keeps a committed lean meaningful even if the player releases A/D
            // just before the wheels reach the switch.
            speed = throttle == 0f
                ? Mathf.MoveTowards(speed, 0f, coastingDrag * Time.deltaTime)
                : Mathf.MoveTowards(speed, throttle * maximumSpeed, acceleration * Time.deltaTime);

            // Keep the last direction at rest so the controls do not flip while braking.
            if (speed > 0.15f) travelSign = 1;
            else if (speed < -0.15f) travelSign = -1;
            junctionChoice = JunctionForLean(leanAngle, junctionLeanThreshold, junctionLeanLimit, travelSign);

            // A tram has to be slow enough to hold the rail through the bend it is entering.
            bool forward = (Mathf.Abs(speed) > 0.01f ? speed : throttle) >= 0f;
            float curveLimit = CurveSpeedLimit(forward);
            speed = Mathf.Clamp(speed, -curveLimit, curveLimit);

            edgeProgress += speed * Time.deltaTime;
            AdvanceAcrossNodes();
            smoothedLeanInput = Mathf.MoveTowards(smoothedLeanInput, leanInput, leanInputResponse * Time.deltaTime);
            UpdateLean(TravelRelativeLean(smoothedLeanInput, travelSign), Time.deltaTime);
            if (derailed) return;
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
            int straightest = -1, committed = -1, fallback = -1;
            float straightestTurn = float.MaxValue, committedTurn = -1f, fallbackTurn = float.MaxValue;

            foreach (int candidate in neighbours)
            {
                if (candidate == previous) continue;
                Vector3 outgoing = (graph[candidate].position - graph[reached].position).normalized;
                float angle = Vector3.SignedAngle(incoming, outgoing, Vector3.up);
                float turn = Mathf.Abs(angle);

                // Kept regardless of the turn limit: where the geometry leaves only one
                // way on, refusing it would strand the tram mid-track.
                if (turn < fallbackTurn) { fallbackTurn = turn; fallback = candidate; }

                // No switch routes a tram back the way it came, so branches that double
                // back are not on offer however hard the player steers into them.
                if (turn > maximumJunctionTurn) continue;
                if (turn < straightestTurn) { straightestTurn = turn; straightest = candidate; }

                // A committed lean takes the sharpest branch on the side it asked for,
                // and only that side. Taking the most rightward of two left branches
                // would send the player the opposite way to the one they leaned.
                if (junctionChoice == 0 || (angle < 0f ? -1 : 1) != junctionChoice) continue;
                if (turn > committedTurn) { committedTurn = turn; committed = candidate; }
            }

            if (junctionChoice != 0 && committed >= 0) return committed;
            return straightest >= 0 ? straightest : fallback;
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
        private Vector3 TrackPointAhead(float distance, bool reverse = false)
        {
            IReadOnlyList<TramTrackNetwork.GraphNode> graph = network.Graph;
            int previous = reverse ? targetNode : currentNode;
            int reached = reverse ? currentNode : targetNode;
            float remaining = Mathf.Max(0f, reverse ? edgeProgress : CurrentEdgeLength() - edgeProgress);

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

        // The tram is an inverted pendulum sitting on one rail line: gravity tips it
        // further over the moment it leaves upright, and shifting weight with A/D is
        // the only thing holding it up.
        //
        // Corners are deliberately left out of it. The rails take the sideways load
        // of a bend, so a curve neither tips the tram nor props it up, and leaning
        // through a junction stays a steering decision rather than a balance one.
        //
        // Nothing here rights the tram on the driver's behalf either. There is no
        // correction near upright and no angle the controls settle at by themselves:
        // every lean, however small, keeps growing until it is answered. Damping only
        // slows how fast that happens.
        private void UpdateLean(float steer, float deltaTime)
        {
            if (derailed || deltaTime <= 0f) return;

            // Softening this slows how fast a wobble runs away. It scales the
            // pendulum, not the driver's share.
            float sensitivity = Mathf.Max(0f, balanceSensitivity);
            float leanRadians = leanAngle * Mathf.Deg2Rad;
            float toppling = Physics.gravity.magnitude * Mathf.Sin(leanRadians)
                             / Mathf.Max(0.2f, centreOfMassHeight) * Mathf.Rad2Deg * sensitivity;

            // A/D shifts weight at a constant rate. Holding a lean steady means
            // feeding in exactly as much counter-weight as gravity is taking away,
            // so the driver is working the whole time the tram is off upright.
            leanVelocity += (toppling + steer * leanAuthority - leanDamping * leanVelocity) * deltaTime;
            leanAngle += leanVelocity * deltaTime;
            if (Mathf.Abs(leanAngle) > fallAngle) Derail();
        }

        private void Derail()
        {
            derailed = true;
            leanAngle = Mathf.Clamp(leanAngle, -fallAngle, fallAngle);
            float side = Mathf.Sign(leanAngle);
            ApplyTransform();

            // The prefab brings its own collider on a child object. A rigidbody composes
            // every collider under it that has no rigidbody of its own, so the wreck is
            // already shaped correctly and fitting a second box here would only wrap the
            // model in a hull that does not match it.
            hull = GetComponentInChildren<Collider>();

            Rigidbody body = gameObject.AddComponent<Rigidbody>();
            body.mass = derailMass;
            // Carries the momentum it had, thrown hard the way it was already
            // falling. The sideways throw scales with how fast it was going, so a
            // derail at speed hurls the wreck across the street and a slow topple
            // just falls over.
            float momentum = Mathf.Clamp(Mathf.Abs(speed) / Mathf.Max(1f, maximumSpeed), 0.6f, 1f);
            body.linearVelocity = transform.forward * speed
                                  + transform.right * (side * derailSidewaysSpeed * momentum)
                                  + Vector3.up * (derailLift * momentum);
            body.angularVelocity = transform.forward * (-side * derailSpin * momentum * Mathf.Deg2Rad);
            // A wreck thrown at 24 m/s clears half a metre per physics step, which is
            // plenty to pass straight through a mesh collider that has no thickness.
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            derailFloor = transform.position.y;
            speed = 0f;
        }

        private void FixedUpdate()
        {
            if (!derailed || !TryGetComponent(out Rigidbody body)) return;
            if (hull == null) return;

            // Measured off the hull rather than the pivot, so it works wherever the
            // model's origin sits, and it lifts by exactly how far the underside has
            // sunk. Only ever a rescue: a wreck resting on the surface is left alone.
            Bounds bounds = hull.bounds;
            float ground = GroundHeightBelow(bounds.center);
            if (bounds.min.y >= ground - wreckSinkTolerance) return;

            Vector3 position = body.position;
            position.y += ground - bounds.min.y;
            body.position = position;
            Vector3 velocity = body.linearVelocity;
            if (velocity.y < 0f) velocity.y = 0f;
            body.linearVelocity = velocity;
        }

        // Falls back to the height of the rails it came off, so the wreck still has a
        // floor on a map generated before the ground had a collider.
        private float GroundHeightBelow(Vector3 position)
        {
            int count = Physics.RaycastNonAlloc(
                position + Vector3.up * GroundProbeHeight, Vector3.down, GroundHits, GroundProbeHeight * 2f);
            float highest = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                if (GroundHits[i].collider.transform.IsChildOf(transform)) continue;
                // Only the ground counts. This is a rescue for a wreck that tunnelled
                // through a thin mesh, and the buildings now under the ray would lift
                // one that merely came to rest beside a wall up onto its roof.
                if (GroundHits[i].collider.gameObject.name != GroundLayerName) continue;
                if (GroundHits[i].point.y > highest) highest = GroundHits[i].point.y;
            }
            return float.IsNegativeInfinity(highest) ? derailFloor : highest;
        }

        public void RespawnAt(Vector3 position)
        {
            startRequest = position;
            Respawn();
        }

        public void Respawn()
        {
            if (TryGetComponent(out Rigidbody body))
            {
                // Stops physics writing to the transform in the frames before it is gone.
                body.isKinematic = true;
                Destroy(body);
            }
            // The collider belongs to the prefab, so it is released rather than destroyed.
            hull = null;

            derailed = false;
            leanAngle = 0f;
            leanVelocity = 0f;
            smoothedLeanInput = 0f;
            travelSign = 1;
            speed = 0f;
            junctionChoice = 0;
            headingInitialised = false;
            enabled = true;
            Initialize(network, startRequest);
            GetComponent<TramImpactEffects>()?.ResetSweep();
        }


        private float CurrentEdgeLength() => Vector3.Distance(network.Graph[currentNode].position, network.Graph[targetNode].position);

        private void ApplyTransform()
        {
            Vector3 a = network.Graph[currentNode].position;
            Vector3 b = network.Graph[targetNode].position;
            float length = Vector3.Distance(a, b);
            float t = length < 0.001f ? 0f : Mathf.Clamp01(edgeProgress / length);
            Vector3 position = Vector3.Lerp(a, b, t) + Vector3.up * railOffset;
            transform.position = position;

            // Aiming at a point further down the track sweeps the body through a bend
            // rather than pivoting it on the node, and keeps the pitch of the slope.
            Vector3 aim = TrackPointAhead(headingLookAhead, travelSign < 0) + Vector3.up * railOffset;
            Vector3 direction = travelSign < 0 ? position - aim : aim - position;
            if (direction.sqrMagnitude < 0.001f) direction = b - a;
            if (direction.sqrMagnitude < 0.001f) return;

            Quaternion desired = Quaternion.LookRotation(direction, Vector3.up);
            trackRotation = headingInitialised
                ? Quaternion.RotateTowards(trackRotation, desired, maximumYawRate * Time.deltaTime)
                : desired;
            headingInitialised = true;
            // Roll sits outside the slew so leaning never drags the heading with it.
            transform.rotation = trackRotation * Quaternion.Euler(0f, 0f, -leanAngle);
            // Pure roll about the rail contact. The pivot is the underside of the
            // body, so the tram tilts on the rail rather than sliding off it.
            transform.position = Vector3.Lerp(a, b, t) + transform.rotation * (Vector3.up * railOffset);
        }
    }
}
