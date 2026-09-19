using System;
using SparvagnRush.Map;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SparvagnRush.Gameplay
{
    public sealed class TramGameManager : MonoBehaviour
    {
        private const float SessionDuration = 120f;
        private const float JobDuration = 32f;
        private const float ReachDistance = 13f;

        private TramTrackNetwork network;
        private TramController tram;
        private TramAudio tramAudio;
        private readonly System.Random random = new();
        private GameObject marker;
        private float sessionTime;
        private float jobTime;
        private float nextPassengerDelay;
        private bool carryingPassenger;
        private bool sessionEnded;
        private bool derailed;
        private int score;
        private int combo;
        private float titleCardTime;

        public void Initialize(TramTrackNetwork trackNetwork, TramController player)
        {
            network = trackNetwork;
            tram = player;
            tramAudio = player.GetComponent<TramAudio>();
            BeginSession();
        }

        private void Update()
        {
            if (network == null || tram == null) return;
            titleCardTime = Mathf.Max(0f, titleCardTime - Time.deltaTime);
            if (!sessionEnded && tram.Derailed)
            {
                sessionEnded = true;
                derailed = true;
                if (marker != null) marker.SetActive(false);
                return;
            }
            if (sessionEnded)
            {
                if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame) BeginSession();
                return;
            }

            sessionTime -= Time.deltaTime;
            if (sessionTime <= 0f)
            {
                sessionTime = 0f;
                sessionEnded = true;
                tram.enabled = false;
                if (marker != null) marker.SetActive(false);
                return;
            }

            if (marker == null || !marker.activeSelf)
            {
                nextPassengerDelay -= Time.deltaTime;
                if (nextPassengerDelay <= 0f) SpawnObjective(false);
                return;
            }

            jobTime -= Time.deltaTime;
            marker.transform.Rotate(Vector3.up, 65f * Time.deltaTime, Space.World);
            if (Vector3.Distance(tram.transform.position, marker.transform.position) <= ReachDistance)
            {
                if (!carryingPassenger)
                {
                    carryingPassenger = true;
                    SpawnObjective(true);
                }
                else
                {
                    combo++;
                    score += 100 * combo;
                    tramAudio?.PlaySuccess();
                    SpawnSuccessBurst(marker.transform.position);
                    carryingPassenger = false;
                    marker.SetActive(false);
                    nextPassengerDelay = 1.25f;
                }
            }
            else if (jobTime <= 0f)
            {
                combo = 0;
                carryingPassenger = false;
                marker.SetActive(false);
                nextPassengerDelay = 0.8f;
            }
        }

        private void BeginSession()
        {
            sessionTime = SessionDuration;
            jobTime = JobDuration;
            score = 0;
            combo = 0;
            carryingPassenger = false;
            sessionEnded = false;
            nextPassengerDelay = 0.5f;
            titleCardTime = 3.5f;
            derailed = false;
            tram.Respawn();
            if (marker != null) marker.SetActive(false);
        }

        private static void SpawnSuccessBurst(Vector3 position)
        {
            var burstObject = new GameObject("DropOffBurst");
            burstObject.transform.position = position;
            ParticleSystem particles = burstObject.AddComponent<ParticleSystem>();
            var main = particles.main;
            main.duration = 0.8f;
            main.loop = false;
            main.startLifetime = 0.8f;
            main.startSpeed = 9f;
            main.startSize = 0.8f;
            main.startColor = new Color(0.15f, 0.95f, 1f);
            main.maxParticles = 48;
            var emission = particles.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 40) });
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 2f;
            particles.Play();
            Destroy(burstObject, 2f);
        }

        private void SpawnObjective(bool isDropOff)
        {
            if (marker == null)
            {
                marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = "ObjectiveMarker";
                Destroy(marker.GetComponent<Collider>());
                marker.transform.localScale = new Vector3(7f, 5f, 7f);
                marker.GetComponent<Renderer>().sharedMaterial = SparvagnRushBootstrap.CreateRuntimeMaterial(Color.white);
            }

            Vector3 point = network.GetRandomTrackPoint(random);
            float minimumDistance = isDropOff ? 260f : 100f;
            for (int attempt = 0; attempt < 30 && Vector3.Distance(point, tram.transform.position) < minimumDistance; attempt++)
                point = network.GetRandomTrackPoint(random);

            marker.transform.position = point + Vector3.up * 5f;
            marker.GetComponent<Renderer>().material.color = isDropOff
                ? new Color(0.1f, 0.9f, 1f)
                : new Color(1f, 0.82f, 0.1f);
            marker.SetActive(true);
            jobTime = JobDuration;
        }

        private void OnGUI()
        {
            GUIStyle hud = new(GUI.skin.box)
            {
                fontSize = Mathf.RoundToInt(Screen.height * 0.027f),
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };
            string objective = carryingPassenger ? "DROP OFF — cyan beacon" : "PICK UP — yellow beacon";
            GUI.Box(new Rect(18, 18, 430, 126), $"SPÅRVAGN RUSH\nScore  {score:00000}    Combo  x{Mathf.Max(1, combo)}\nTime  {Mathf.CeilToInt(sessionTime):00}s    Job  {Mathf.CeilToInt(jobTime):00}s\n{objective}", hud);
            GUI.Label(new Rect(20, Screen.height - 42, 850, 28), "W/S: drive   Hold A/D: lean + choose junction   RMB drag: orbit   MMB: reset camera", hud);

            if (!sessionEnded)
            {
                // Leaning is unreadable without seeing how much edge is left.
                var meter = new Rect(18, 152, 430, 30);
                float lean01 = Mathf.Clamp(tram.LeanAngle / Mathf.Max(1f, tram.FallAngle), -1f, 1f);
                GUI.Box(meter, GUIContent.none, hud);
                float centre = meter.x + meter.width * 0.5f;
                GUI.Box(new Rect(centre + lean01 * (meter.width * 0.5f - 14f) - 11f, meter.y + 4f, 22f, 22f), GUIContent.none, hud);
                GUI.Label(new Rect(meter.x + 8f, meter.y + 4f, 260f, 22f), $"LEAN {tram.LeanAngle,4:0}°", hud);
            }

            if (titleCardTime > 0f)
            {
                GUIStyle title = new(GUI.skin.box)
                {
                    fontSize = Mathf.RoundToInt(Screen.height * 0.045f),
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white }
                };
                GUI.Box(new Rect(Screen.width * 0.27f, 24f, Screen.width * 0.46f, 132f),
                    "SPÅRVAGN RUSH\nMap data © OpenStreetMap contributors\nElevation: Göteborgs Stad, 2022 (CC0)", title);
            }

            if (!sessionEnded) return;
            GUIStyle end = new(GUI.skin.box)
            {
                fontSize = Mathf.RoundToInt(Screen.height * 0.05f),
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            GUI.Box(new Rect(Screen.width * 0.25f, Screen.height * 0.3f, Screen.width * 0.5f, Screen.height * 0.34f),
                $"{(derailed ? "DERAILED!" : "TIME'S UP!")}\n\nSCORE  {score}\n\nPress R to rush again", end);
        }
    }
}
