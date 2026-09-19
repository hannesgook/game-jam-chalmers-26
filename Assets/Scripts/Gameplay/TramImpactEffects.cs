using UnityEngine;

namespace SparvagnRush.Gameplay
{
    // The tram follows transforms while on rails and becomes a rigidbody when
    // derailed. Sweeps handle the former; physics contacts handle the wreck.
    public sealed class TramImpactEffects : MonoBehaviour
    {
        [SerializeField] private float minimumImpactSpeed = 2f;
        [SerializeField] private float explosionCooldown = 0.75f;
        private BoxCollider hull;
        private Vector3 previousCentre;
        private float nextExplosion;
        private Material blastMaterial;
        private Material markMaterial;
        private TramAudio impactAudio;
        private TramController tram;

        private void Start()
        {
            hull = GetComponentInChildren<BoxCollider>();
            impactAudio = GetComponent<TramAudio>();
            tram = GetComponent<TramController>();
            if (hull != null) previousCentre = hull.transform.TransformPoint(hull.center);
            blastMaterial = SparvagnRushBootstrap.CreateRuntimeMaterial(new Color(1f, 0.45f, 0.08f));
            markMaterial = SparvagnRushBootstrap.CreateRuntimeMaterial(new Color(0.12f, 0.10f, 0.09f));
        }

        // Respawning teleports the tram and must not sweep through the entire city.
        public void ResetSweep()
        {
            if (hull != null) previousCentre = hull.transform.TransformPoint(hull.center);
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

            if (distance > 0.001f)
            {
                foreach (RaycastHit hit in Physics.BoxCastAll(previousCentre, half, movement / distance,
                    rotation, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
                    Hit(hit.collider, hit.point, hit.normal, speed);
            }
            foreach (Collider other in Physics.OverlapBox(centre, half, rotation,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            {
                if (other.transform.IsChildOf(transform)) continue;
                if (!Physics.ComputePenetration(hull, hull.transform.position, rotation,
                    other, other.transform.position, other.transform.rotation, out Vector3 normal, out float depth)) continue;
                Vector3 point = hull.ClosestPoint(centre - normal * (half.magnitude + depth));
                Hit(other, point, normal, speed);
            }
            previousCentre = centre;
        }

        private void Hit(Collider other, Vector3 point, Vector3 normal, float speed)
        {
            if (other.transform.IsChildOf(transform)) return;
            AmbientNpcWalker pedestrian = other.GetComponentInParent<AmbientNpcWalker>();
            if (pedestrian != null)
            {
                if (!pedestrian.gameObject.activeInHierarchy) return;
                impactAudio?.PlayNpcHit();
                pedestrian.gameObject.SetActive(false);
                Destroy(pedestrian.gameObject);
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
