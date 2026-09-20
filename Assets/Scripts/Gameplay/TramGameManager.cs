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
        private Texture2D minimapTexture;
        public bool ChoosingStop { get; private set; }

        public void ShowCity()
        {
            ChoosingStop = true;
            tram.Respawn();
            tram.gameObject.SetActive(false);
            if (marker != null) marker.SetActive(false);
        }

        public void StartAt(Vector3 position)
        {
            tram.gameObject.SetActive(true);
            tram.RespawnAt(position);
            ChoosingStop = false;
            BeginSession();
        }

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
            ShowCity();
        }

        private void Update()
        {
            if (network == null || tram == null || ChoosingStop) return;
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
            derailed = false;
            tram.Respawn();
            Camera.main?.GetComponent<TramFollowCamera>()?.ResetFraming();
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

        private GUIStyle hudStyle, captionStyle, endStyle;
        private float hudScreenHeight;

        private void OnGUI()
        {
            if (ChoosingStop || tram == null) return;
            using var ui = new GameUi.Scope(true);
            if (hudStyle == null || hudScreenHeight != GameUi.Height)
            {
                hudScreenHeight = GameUi.Height;
                hudStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 19,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = Color.white }
                };
                captionStyle = new GUIStyle(hudStyle) { fontSize = 12, fontStyle = FontStyle.Normal, wordWrap = true };
                endStyle = new GUIStyle(hudStyle) { fontSize = 28, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            }
            float width = Mathf.Min(350f, GameUi.Width * 0.44f);
            Panel(new Rect(18, 18, width, 146));
            GUI.Label(new Rect(32, 28, width - 28, 30), "SPÅRVAGN RUSH", hudStyle);
            GUI.Label(new Rect(32, 63, width - 28, 30), $"{score:00000}    /    COMBO ×{Mathf.Max(1, combo)}", hudStyle);
            GUI.Label(new Rect(32, 100, width - 28, 24), $"RUN  {Mathf.CeilToInt(sessionTime):00}s     JOB  {Mathf.CeilToInt(jobTime):00}s", captionStyle);
            GUI.Label(new Rect(32, 126, width - 28, 28), carryingPassenger ? "DROP OFF  /  cyan beacon" : "PICK UP  /  gold beacon", captionStyle);

            DrawMinimap(hudStyle);
            var meter = new Rect(18, GameUi.Height - 103, width, 48);
            Panel(meter);
            GUI.Label(new Rect(meter.x + 12, meter.y + 4, width - 24, 22),
                $"BALANCE  {tram.LeanAngle:0}°       SPEED  {Mathf.Abs(tram.Speed) * 3.6f:0} km/h", captionStyle);
            Color old = GUI.color;
            GUI.color = new Color(0.25f, 0.32f, 0.37f);
            GUI.DrawTexture(new Rect(meter.x + 14, meter.y + 32, width - 28, 3), Texture2D.whiteTexture);
            float lean = Mathf.Clamp(tram.LeanAngle / Mathf.Max(1f, tram.FallAngle), -1f, 1f);
            GUI.color = Mathf.Abs(lean) > 0.7f ? new Color(1f, 0.42f, 0.25f) : GameUi.Accent;
            GUI.DrawTexture(new Rect(meter.center.x + lean * (width * 0.5f - 22) - 4, meter.y + 27, 8, 13), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(20, GameUi.Height - 43, GameUi.Width - 40, 38),
                "W/S drive   ·   A/D balance & junctions   ·   RMB orbit   ·   Wheel zoom   ·   MMB reset   ·   Esc city", captionStyle);

            if (!sessionEnded) return;
            Rect end = new Rect(GameUi.Width * 0.25f, GameUi.Height * 0.28f, GameUi.Width * 0.5f, GameUi.Height * 0.40f);
            Panel(end);
            GUI.Label(end, $"{(derailed ? "DERAILED" : "TIME'S UP")}\n\nSCORE  {score}\n\nR to retry  ·  Esc to choose a stop", endStyle);
        }

        private static void Panel(Rect rect)
        {
            Color old = GUI.color;
            GUI.color = GameUi.Ink;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }
        private void DrawMinimap(GUIStyle hud)
        {
            float size = Mathf.Min(240f, GameUi.Width * 0.28f, GameUi.Height - 210f);
            var frame = new Rect(GameUi.Width - size - 18f, 18f, size, size);
            Panel(new Rect(frame.x - 5f, frame.y - 5f, frame.width + 10f, frame.height + 32f));

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
