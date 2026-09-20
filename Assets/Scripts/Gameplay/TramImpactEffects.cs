using UnityEngine;

namespace SparvagnRush.Gameplay
{
    // The tram follows transforms while on rails and becomes a rigidbody when
    // derailed. Sweeps handle the former; physics contacts handle the wreck.
    public sealed class TramImpactEffects : MonoBehaviour
    {
        [SerializeField] private float minimumImpactSpeed = 2f;
        [Tooltip("Below this speed somebody reaching the tram is boarding it, not being run over. Kept above the speed the game counts as stopped at a station.")]
        [SerializeField] private float boardingSpeed = 3f;
        [SerializeField] private float explosionCooldown = 0.75f;
        private BoxCollider hull;
        private Vector3 previousCentre;
        private Vector3 previousHullPosition;
        private Quaternion previousRotation;
        private readonly Collider[] overlaps = new Collider[48];
        private float nextExplosion;
        private Material blastMaterial;
        private Material markMaterial;
        private TramAudio impactAudio;
        private TramController tram;
        private TramGameManager game;

        private void Start()
        {
            hull = GetComponentInChildren<BoxCollider>();
            impactAudio = GetComponent<TramAudio>();
            tram = GetComponent<TramController>();
            ResetSweep();
            blastMaterial = SparvagnRushBootstrap.CreateRuntimeMaterial(new Color(1f, 0.45f, 0.08f));
            markMaterial = SparvagnRushBootstrap.CreateRuntimeMaterial(new Color(0.12f, 0.10f, 0.09f));
        }

        // Respawning teleports the tram and must not sweep through the entire city.
        public void ResetSweep()
        {
            if (hull == null) return;
            previousCentre = hull.transform.TransformPoint(hull.center);
            previousHullPosition = hull.transform.position;
            previousRotation = hull.transform.rotation;
        }

        private void LateUpdate()
        {
            if (hull == null) return;
            Vector3 centre = hull.transform.TransformPoint(hull.center);
            Vector3 scale = hull.transform.lossyScale;
            Vector3 half = Vector3.Scale(hull.size * 0.5f,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            Quaternion rotation = hull.transform.rotation;
            Vector3 movement = centre - previousCentre;
            float distance = movement.magnitude;
            float speed = distance / Mathf.Max(Time.deltaTime, 0.0001f);
            Physics.SyncTransforms();

            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(distance / 0.6f,
                Quaternion.Angle(previousRotation, rotation) / 5f)), 1, 16);
            for (int step = 1; step <= steps; step++)
            {
                float t = step / (float)steps;
                Quaternion sampleRotation = Quaternion.Slerp(previousRotation, rotation, t);
                Vector3 sampleCentre = Vector3.Lerp(previousCentre, centre, t);
                Vector3 samplePosition = Vector3.Lerp(previousHullPosition, hull.transform.position, t);
                int count = Physics.OverlapBoxNonAlloc(sampleCentre, half, overlaps, sampleRotation,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
                for (int i = 0; i < count; i++)
                {
                    Collider other = overlaps[i];
                    if (other.transform.IsChildOf(transform)) continue;
                    // Test the actual tilted hull at each step, not a broad cast box.
                    if (!Physics.ComputePenetration(hull, samplePosition, sampleRotation,
                        other, other.transform.position, other.transform.rotation, out Vector3 normal, out float depth)) continue;
                    Hit(other, other.ClosestPoint(sampleCentre), normal, speed);
                }
            }
            previousHullPosition = hull.transform.position;
            previousRotation = rotation;
            previousCentre = centre;
        }

        private void Hit(Collider other, Vector3 point, Vector3 normal, float speed)
        {
            if (other.transform.IsChildOf(transform)) return;
            AmbientNpcWalker pedestrian = other.GetComponentInParent<AmbientNpcWalker>();
            if (pedestrian != null)
            {
                if (!pedestrian.gameObject.activeInHierarchy || pedestrian.IsDown) return;
                // A tram stopped or crawling at a station is being boarded, not
                // driven into somebody. Passengers walk up to it and get on; only a
                // tram with real speed behind it runs a person down.
                if (tram == null || Mathf.Abs(tram.Speed) < boardingSpeed)
                {
                    pedestrian.Board();
                    return;
                }
                pedestrian.KnockDown();
                // The manager is added during the same Start phase as this component,
                // so it is found on first use rather than cached in Start.
                if (game == null) game = FindFirstObjectByType<TramGameManager>();
                game?.EndByCollision(point);
                return;
            }
            // While the tram is held to the rails, nearby building geometry can overlap
            // its swept hull on tight corners. Only a free-moving wreck can crash.
            if (tram == null || !tram.Derailed) return;
            if (other.name != "Buildings" && other.name != "BuildingRoofs") return;
            if (speed < minimumImpactSpeed || Time.time < nextExplosion) return;
            nextExplosion = Time.time + explosionCooldown;
            Explode(point, normal, other.transform);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision.contactCount == 0) return;
            ContactPoint contact = collision.GetContact(0);
            Hit(collision.collider, contact.point, contact.normal, collision.relativeVelocity.magnitude);
        }

        private void Explode(Vector3 point, Vector3 normal, Transform building)
        {
            impactAudio?.PlayExplosion();
            GameObject burst = new GameObject("Building impact explosion");
            burst.transform.position = point + normal * 0.2f;
            ParticleSystem particles = burst.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.loop = false;
            main.duration = 0.5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 12f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 1.3f);
            main.maxParticles = 64;
            main.gravityModifier = 0.4f;
            var emission = particles.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 56) });
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.7f;
            burst.transform.rotation = Quaternion.LookRotation(normal.sqrMagnitude > 0.001f ? normal : Vector3.up);
            particles.GetComponent<ParticleSystemRenderer>().sharedMaterial = blastMaterial;
            particles.Play();
            Destroy(burst, 1.5f);

            // A shallow impact scar on the surface. The city is a single sparse
            // mesh, so moving its corner vertices would deform whole facades.
            GameObject mark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            mark.name = "Building impact scar";
            Collider markCollider = mark.GetComponent<Collider>();
            markCollider.enabled = false;
            Destroy(markCollider);
            mark.transform.SetParent(building, true);
            mark.transform.position = point + normal * 0.025f;
            mark.transform.rotation = burst.transform.rotation;
            mark.transform.localScale = new Vector3(4f, 4f, 0.08f);
            mark.GetComponent<Renderer>().sharedMaterial = markMaterial;
            Destroy(mark, 20f);
        }

        private void OnDestroy()
        {
            if (blastMaterial != null) Destroy(blastMaterial);
            if (markMaterial != null) Destroy(markMaterial);
        }
    }
}
