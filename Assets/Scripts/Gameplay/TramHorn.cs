using UnityEngine;
using UnityEngine.InputSystem;

namespace SparvagnRush.Gameplay
{
    /// <summary>
    /// The horn, and the driver's answer to a crowd that walks straight at them.
    /// Anybody inside the corridor ahead of the tram is thrown towards the pavement
    /// they are already nearest, which opens the rails instead of shoving the whole
    /// street one way.
    /// </summary>
    [RequireComponent(typeof(TramController))]
    public sealed class TramHorn : MonoBehaviour
    {
        [Tooltip("How far ahead of the tram the horn reaches.")]
        [SerializeField] private float range = 32f;
        [Tooltip("Half the width of the corridor the horn clears.")]
        [SerializeField] private float halfWidth = 7.5f;
        [Tooltip("Seconds between honks.")]
        [SerializeField] private float cooldown = 1.1f;
        [Tooltip("Sideways speed somebody is thrown at. Enough to clear the corridor, not to send them across the city.")]
        [SerializeField] private float launchSpeed = 8f;
        [Tooltip("Upward kick. This is what makes it read as a launch rather than a shove.")]
        [SerializeField] private float launchLift = 4.5f;

        private readonly Collider[] caught = new Collider[128];
        private TramController tram;
        private TramAudio horn;
        private float nextHonk;

        /// <summary>True when the horn will actually do something if pressed.</summary>
        public bool Ready => Time.time >= nextHonk;

        private void Start()
        {
            tram = GetComponent<TramController>();
            horn = GetComponent<TramAudio>();
        }

        private void Update()
        {
            // Nothing to sound while the run is over, the tram is a wreck, or the
            // crash camera has the game frozen.
            if (tram == null || tram.Derailed || !tram.enabled || Time.timeScale <= 0f) return;
            Keyboard keys = Keyboard.current;
            if (keys == null || !keys.spaceKey.wasPressedThisFrame || !Ready) return;
            nextHonk = Time.time + cooldown;
            horn?.PlayHorn();
            ClearTheRails();
        }

        private void ClearTheRails()
        {
            Vector3 travel = transform.forward * tram.TravelSign;
            travel.y = 0f;
            if (travel.sqrMagnitude < 0.0001f) return;
            travel.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, travel);

            Vector3 centre = transform.position + travel * (range * 0.5f) + Vector3.up * 1.2f;
            int count = Physics.OverlapBoxNonAlloc(centre,
                new Vector3(halfWidth, 3.5f, range * 0.5f), caught,
                Quaternion.LookRotation(travel, Vector3.up),
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);

            for (int i = 0; i < count; i++)
            {
                AmbientNpcWalker walker = caught[i].GetComponentInParent<AmbientNpcWalker>();
                if (walker == null) continue;
                float side = Vector3.Dot(walker.transform.position - transform.position, right);
                Vector3 away = right * (side >= 0f ? 1f : -1f);
                walker.LaunchAside(away * launchSpeed + Vector3.up * launchLift);
            }
        }
    }
}
