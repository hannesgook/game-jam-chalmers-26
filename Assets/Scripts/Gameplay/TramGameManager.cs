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
        private Texture2D minimapTexture;

        // These are the projected half-extents of the OSM download used by the city
        // generator (57.695,11.965 to 57.710,11.985). World X points east and Z north.
        private const float MapHalfWidth = 594.80f;
        private const float MapHalfHeight = 834.90f;

        public void Initialize(TramTrackNetwork trackNetwork, TramController player)
        {
            network = trackNetwork;
            tram = player;
            tramAudio = player.GetComponent<TramAudio>();
            minimapTexture = Resources.Load<Texture2D>("Minimap/osm_minimap");
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
            GUI.Label(new Rect(20, Screen.height - 42, 900, 28), "W/S: drive   A/D: lean — the way you are leaning chooses the junction   RMB drag: orbit", hud);

            DrawMinimap(hud);

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
                GUI.Box(new Rect(Screen.width * 0.25f, 24f, Screen.width * 0.5f, 164f),
                    "SPÅRVAGN RUSH\nMap data © OpenStreetMap contributors\nAerial imagery: Göteborgs Stad, 2025 (CC0)\nElevation: Göteborgs Stad, 2022 (CC0)", title);
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

        private void DrawMinimap(GUIStyle hud)
        {
            float size = Mathf.Clamp(Screen.height * 0.31f, 190f, 310f);
            var frame = new Rect(Screen.width - size - 18f, 18f, size, size);
            GUI.Box(new Rect(frame.x - 5f, frame.y - 5f, frame.width + 10f, frame.height + 32f), GUIContent.none, hud);

            if (minimapTexture != null)
                GUI.DrawTexture(frame, minimapTexture, ScaleMode.StretchToFill, false);
            else
            {
                Color old = GUI.color;
                GUI.color = new Color(0.14f, 0.17f, 0.16f);
                GUI.DrawTexture(frame, Texture2D.whiteTexture);
                GUI.color = old;
            }

            if (marker != null && marker.activeSelf)
            {
                Vector2 objectivePoint = WorldToMap(marker.transform.position, frame);
                DrawMapDot(objectivePoint, carryingPassenger
                    ? new Color(0.1f, 0.9f, 1f)
                    : new Color(1f, 0.82f, 0.1f), 14f);
            }

            Vector2 playerPoint = WorldToMap(tram.transform.position, frame);
            DrawPlayerArrow(playerPoint, tram.transform.eulerAngles.y);

            GUIStyle credit = new(GUI.skin.label)
            {
                fontSize = Mathf.Max(9, Mathf.RoundToInt(size * 0.043f)),
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white }
            };
            GUI.Label(new Rect(frame.x, frame.yMax + 1f, frame.width, 22f), "© OpenStreetMap contributors", credit);
        }

        private static Vector2 WorldToMap(Vector3 world, Rect map)
        {
            float u = Mathf.InverseLerp(-MapHalfWidth, MapHalfWidth, world.x);
            float v = Mathf.InverseLerp(-MapHalfHeight, MapHalfHeight, world.z);
            return new Vector2(map.x + u * map.width, map.y + (1f - v) * map.height);
        }

        private static void DrawMapDot(Vector2 centre, Color color, float diameter)
        {
            Color old = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(centre.x - diameter * 0.65f, centre.y - diameter * 0.65f,
                diameter * 1.3f, diameter * 1.3f), Texture2D.whiteTexture);
            GUI.color = color;
            GUI.DrawTexture(new Rect(centre.x - diameter * 0.5f, centre.y - diameter * 0.5f,
                diameter, diameter), Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static void DrawPlayerArrow(Vector2 centre, float heading)
        {
            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            GUIUtility.RotateAroundPivot(heading, centre);
            GUIStyle arrow = new(GUI.skin.label)
            {
                fontSize = 25,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.black }
            };
            GUI.Label(new Rect(centre.x - 17f, centre.y - 17f, 34f, 34f), "▲", arrow);
            arrow.fontSize = 19;
            arrow.normal.textColor = new Color(1f, 0.25f, 0.18f);
            GUI.Label(new Rect(centre.x - 17f, centre.y - 16f, 34f, 34f), "▲", arrow);
            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
        }
    }
}
