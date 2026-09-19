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

        [Header("Balance")]
        [Tooltip("How hard the world fights to tip the tram over. Scales both the sideways throw of a curve and how fast a lean runs away. Lower is more forgiving; 1 is the physically honest value.")]
        [SerializeField] private float balanceSensitivity = 0.25f;
        [Tooltip("Height of the centre of mass above the rails. Lower topples faster and is twitchier to hold.")]
        [SerializeField] private float centreOfMassHeight = 1.8f;
        [Tooltip("Degrees per second squared of lean the driver can force by holding A or D.")]
        [SerializeField] private float leanAuthority = 320f;
        [Tooltip("How fast lean movement bleeds away. Higher is easier to hold steady.")]
        [SerializeField] private float leanDamping = 2f;
        [Tooltip("Gentle automatic correction near upright. It removes tiny oscillations without playing the hard corners for you.")]
        [SerializeField] private float stabilityAssist = 42f;
        [Range(0f, 1f)]
        [Tooltip("Fraction of curve force automatically compensated. 0 is fully manual; 1 perfectly counters a steady curve.")]
        [SerializeField] private float curveBalanceAssist = 0.22f;
        [Tooltip("How quickly A/D lean input ramps in and out.")]
        [SerializeField] private float leanInputResponse = 7f;
        [Tooltip("Lean past this and the tram is gone.")]
        [SerializeField] private float fallAngle = 35f;
        [Tooltip("Length of track read to work out the curve the tram is entering.")]
        [SerializeField] private float curvatureSample = 8f;

        [Header("Derailment")]
        [Tooltip("Sideways speed the wreck is thrown at, the way it was falling.")]
        [SerializeField] private float derailSidewaysSpeed = 9f;
        [Tooltip("Upward kick as the wheels leave the rail.")]
        [SerializeField] private float derailLift = 5f;
        [Tooltip("Degrees per second the wreck tumbles at.")]
        [SerializeField] private float derailSpin = 320f;
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
        private bool derailed;
        // The prefab's own collider, resolved on derail and used to measure the wreck.
        private Collider hull;
        private float derailFloor;
        private Vector3 startRequest;

        public float Speed => speed;
        public float LeanAngle => leanAngle;
        public float FallAngle => fallAngle;
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

        private void Update()
        {
            if (derailed) return;
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
            smoothedLeanInput = Mathf.MoveTowards(smoothedLeanInput, junctionChoice, leanInputResponse * Time.deltaTime);
            UpdateLean(smoothedLeanInput, Time.deltaTime);
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

        // The tram is an inverted pendulum sitting on one rail line: gravity tips it
        // further over the moment it leaves upright, a curve throws it towards the
        // outside, and shifting weight with A/D is the only thing holding it up.
        private void UpdateLean(float steer, float deltaTime)
        {
            if (derailed || deltaTime <= 0f) return;

            float sensitivity = Mathf.Max(0f, balanceSensitivity);
            // Softening the throw lowers the lean a curve demands; softening the whole
            // term slows how fast a wobble runs away. One knob, both effects.
            float lateral = LateralAcceleration() * sensitivity * (1f - curveBalanceAssist);
            float leanRadians = leanAngle * Mathf.Deg2Rad;
            float toppling = (Physics.gravity.magnitude * Mathf.Sin(leanRadians) - lateral * Mathf.Cos(leanRadians))
                             / Mathf.Max(0.2f, centreOfMassHeight) * Mathf.Rad2Deg * sensitivity;

            // Only assists the calm centre of the meter. Past half way the player still
            // has to commit to the correction, preserving the risk/reward mechanic.
            float safeZone = Mathf.Clamp01(1f - Mathf.Abs(leanAngle) / Mathf.Max(1f, fallAngle * 0.55f));
            float restoring = -leanAngle / Mathf.Max(1f, fallAngle) * stabilityAssist * safeZone;

            leanVelocity += (toppling + restoring + steer * leanAuthority - leanDamping * leanVelocity) * deltaTime;
            leanAngle += leanVelocity * deltaTime;
            if (Mathf.Abs(leanAngle) > fallAngle) Derail();
        }

        // Sideways acceleration the rails are about to impose, positive into a right turn.
        // Read slightly ahead of the tram so the curve is felt as it is entered.
        private float LateralAcceleration()
        {
            float half = Mathf.Max(1f, curvatureSample * 0.5f);
            Vector3 first = TrackPointAhead(half) - TrackPointAhead(0f);
            Vector3 second = TrackPointAhead(half * 2f) - TrackPointAhead(half);
            first.y = 0f;
            second.y = 0f;
            if (first.sqrMagnitude < 0.01f || second.sqrMagnitude < 0.01f) return 0f;

            float turn = Vector3.SignedAngle(first, second, Vector3.up) * Mathf.Deg2Rad;
            return speed * speed * (turn / half);
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
            // Carries the momentum it had, thrown the way it was already falling.
            body.linearVelocity = transform.forward * speed
                                  + transform.right * (side * derailSidewaysSpeed)
                                  + Vector3.up * derailLift;
            body.angularVelocity = transform.forward * (-side * derailSpin * Mathf.Deg2Rad);
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
            speed = 0f;
            junctionChoice = 0;
            headingInitialised = false;
            enabled = true;
            Initialize(network, startRequest);
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
            trackRotation = headingInitialised
                ? Quaternion.RotateTowards(trackRotation, desired, maximumYawRate * Time.deltaTime)
                : desired;
            headingInitialised = true;
            // Roll sits outside the slew so leaning never drags the heading with it.
            transform.rotation = trackRotation * Quaternion.Euler(0f, 0f, -leanAngle);
        }
    }
}
