using System.Collections.Generic;
using TramRush.Map;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TramRush.Gameplay
{
    public sealed class TramGameManager : MonoBehaviour
    {
        private const float SessionDuration = 120f;
        private const float ReachDistance = 13f;
        private const float BoardingDuration = 1.25f;
        private TramTrackNetwork network;
        private TramController tram;
        private TramAudio tramAudio;
        private readonly System.Random random = new();
        private GameObject marker;
        private LineRenderer stationRing;
        private Material markerMaterial;
        private readonly List<Transform> passengers = new();
        private readonly List<Vector3> route = new();
        // Station choice scratch. Kept as fields so issuing a job allocates nothing.
        private readonly List<TramStation> reachable = new();
        private readonly List<float> reachableDistance = new();
        private readonly List<TramStation> shortlist = new();
        private readonly List<TramStation> recentStations = new();
        private readonly List<Vector3> scratchRoute = new();
        // How many recent destinations to avoid repeating, when there is the choice.
        private const int RecentMemory = 4;

        private TramStation destination;
        private TramStation lastStation;
        private float sessionTime, jobTime, nextPassengerDelay, boarding, routeTimer, toastTime;
        private bool carryingPassenger, sessionEnded, derailed, hitPedestrian;
        private TramCollisionCinematic cinematic;
        private int score, combo;
        private string toast = "";
        private Vector3 stationTangent, stationSide;
        private readonly Vector3[] waitingPositions = new Vector3[3];
        private Collider ground;
        public List<TramStation> Stations { get; private set; }
        public bool ChoosingStop { get; private set; }
        public TramController Player => tram;
        public TramStation Destination => destination;
        public IReadOnlyList<Vector3> Route => route;
        public int Score => score;
        public int Combo => combo;
        public int Delivered { get; private set; }
        public float SessionTime => sessionTime;
        /// <summary>0 at the start of a run, 1 at the end. Drives how busy the streets get.</summary>
        public float SessionProgress => 1f - Mathf.Clamp01(sessionTime / SessionDuration);
        /// <summary>Bumped by every run, so the crowd can tell a retry from a running clock.</summary>
        public int RunId { get; private set; }
        public float JobTime => jobTime;
        public float BoardingProgress => boarding / BoardingDuration;
        public bool Carrying => carryingPassenger;
        public bool Ended => sessionEnded;
        public bool Derailed => derailed;
        public bool HitPedestrian => hitPedestrian;
        /// <summary>True while the crash camera has the screen to itself.</summary>
        public bool CinematicPlaying => cinematic != null && cinematic.Playing;
        /// <summary>The result window waits for the crash camera to make its point.</summary>
        public bool ShowResult => sessionEnded && (cinematic == null || cinematic.Revealed);
        public string Notice => toastTime > 0 ? toast : "";
        public float RemainingDistance => RouteLength(route);
        public void Retry() => BeginSession();
        public Vector3 GuidancePoint
        {
            get
            {
                float distance = 0;
                for (int i = 1; i < route.Count; i++)
                {
                    distance += Vector3.Distance(route[i - 1], route[i]);
                    if (distance >= 35) return route[i];
                }
                return destination != null ? destination.position : tram.transform.position;
            }
        }
        private Color ObjectiveColour => carryingPassenger ? new Color(0.35f, 0.83f, 0.76f) : TramInterface.Accent;

        public void Initialize(TramTrackNetwork trackNetwork, TramController player)
        {
            network = trackNetwork;
            tram = player;
            tramAudio = player.GetComponent<TramAudio>();
            cinematic = gameObject.AddComponent<TramCollisionCinematic>();
            ground = transform.Find("Ground")?.GetComponent<Collider>();
            Stations = TramStation.Collect(transform, network);
            if (Stations.Count == 0)
                Stations.Add(new TramStation { name = "City rail access", position = network.Graph[0].position });

            ShowCity();
        }

        public void ShowCity()
        {
            cinematic?.Stop();
            ChoosingStop = true;
            tram.Respawn();
            tram.gameObject.SetActive(false);
            ClearObjective();
        }

        public void StartAt(Vector3 position)
        {
            tram.gameObject.SetActive(true);
            tram.RespawnAt(position);
            ChoosingStop = false;
            BeginSession();
        }

        private void BeginSession()
        {
            cinematic?.Stop();
            RunId++;
            sessionTime = SessionDuration;
            score = combo = Delivered = 0;
            carryingPassenger = sessionEnded = derailed = hitPedestrian = false;
            lastStation = null;
            recentStations.Clear();
            toastTime = boarding = 0;
            nextPassengerDelay = 0.25f;
            tram.Respawn();
            Camera.main?.GetComponent<TramFollowCamera>()?.ResetFraming();
            ClearObjective();
        }

        /// <summary>
        /// Touching anybody ends the run outright. The tram is stopped where it is
        /// and the crash camera freezes the game around the impact.
        /// </summary>
        public void EndByCollision(Vector3 point)
        {
            if (sessionEnded || ChoosingStop) return;
            sessionEnded = hitPedestrian = true;
            combo = 0;
            ClearObjective();
            tram.enabled = false;
            cinematic?.Begin(point, tram.transform);
        }

        private void Update()
        {
            if (network == null || tram == null || ChoosingStop) return;
            toastTime = Mathf.Max(0, toastTime - Time.deltaTime);
            if (!sessionEnded && tram.Derailed)
            {
                sessionEnded = derailed = true;
                ClearObjective();
            }
            if (sessionEnded)
            {
                if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame) BeginSession();
                return;
            }
            sessionTime = Mathf.Max(0, sessionTime - Time.deltaTime);
            if (sessionTime <= 0)
            {
                sessionEnded = true;
                tram.enabled = false;
                ClearObjective();
                return;
            }
            if (destination == null)
            {
                nextPassengerDelay -= Time.deltaTime;
                if (nextPassengerDelay <= 0) SpawnObjective(carryingPassenger);
                return;
            }
            jobTime -= Time.deltaTime;
            routeTimer -= Time.deltaTime;
            if (routeTimer <= 0)
            {
                routeTimer = 1f;
                if (!network.FindRoute(tram.transform.position, destination.position, route))
                {
                    toast = "Route lost — finding another station";
                    toastTime = 3f;
                    ClearObjective();
                    nextPassengerDelay = 0.5f;
                    return;
                }
            }
            Vector3 delta = tram.transform.position - destination.position;
            delta.y = 0;
            bool atStation = delta.sqrMagnitude <= ReachDistance * ReachDistance;
            bool stopped = Mathf.Abs(tram.Speed) <= 2.5f;
            boarding = atStation && stopped ? Mathf.Min(BoardingDuration, boarding + Time.deltaTime) : Mathf.Max(0, boarding - Time.deltaTime * 2);
            AnimatePassengers();
            float pulse = 1f + 0.04f * Mathf.Sin(Time.time * 4f);
            marker.transform.localScale = new Vector3(pulse, 1, pulse);
            if (boarding >= BoardingDuration)
            {
                TramStation served = destination;
                lastStation = served;
                tramAudio?.PlaySuccess();
                SpawnSuccessBurst(served.position + Vector3.up * 2);
                if (!carryingPassenger)
                {
                    score += 25;
                    carryingPassenger = true;
                    toast = $"All aboard!  +25\n3 passengers picked up at {served.name}";
                    ClearObjective();
                    SpawnObjective(true);
                }
                else
                {
                    combo++;
                    Delivered++;
                    score += 100 * combo;
                    carryingPassenger = false;
                    toast = $"Perfect arrival!  +{100 * combo}\nPassengers delivered to {served.name}";
                    ClearObjective();
                    nextPassengerDelay = 2f;
                    if (Delivered >= 3)
                    {
                        sessionEnded = true;
                        tram.enabled = false;
                    }
                }
                toastTime = 3.5f;
            }
            else if (jobTime <= 0)
            {
                combo = 0;
                carryingPassenger = false;
                toast = "Service missed — another station needs you";
                toastTime = 3f;
                ClearObjective();
                nextPassengerDelay = 1f;
            }
        }

        private void ClearObjective()
        {
            destination = null;
            route.Clear();
            boarding = 0;
            if (marker != null) marker.SetActive(false);
        }

        private void SpawnObjective(bool isDropOff)
        {
            // Every named stop this tram can actually reach, and how far the rails say
            // it is. Never issue an unreachable rail job.
            reachable.Clear();
            reachableDistance.Clear();
            foreach (TramStation station in Stations)
            {
                if (station == lastStation) continue;
                if (!network.FindRoute(tram.transform.position, station.position, scratchRoute)) continue;
                reachable.Add(station);
                reachableDistance.Add(RouteLength(scratchRoute));
            }
            if (reachable.Count == 0)
            {
                toast = "No other connected station on this line\nEsc to choose another departure";
                toastTime = 5f;
                nextPassengerDelay = 8f;
                return;
            }

            // Then pick at random from the ones that suit. Scoring the whole list and
            // taking the best always returns the same stop from the same place, which
            // is what made a run visit the same two or three stations all the way
            // through. Each pass is looser than the last: a stop that has not come up
            // lately at a sensible distance, then any stop at a sensible distance,
            // then whatever is reachable at all.
            float nearest = isDropOff ? 150f : 40f;
            float furthest = isDropOff ? 650f : 420f;
            if (!Shortlist(nearest, furthest, true) && !Shortlist(nearest, furthest, false))
                Shortlist(0f, float.MaxValue, false);

            TramStation chosen = shortlist[random.Next(shortlist.Count)];
            network.FindRoute(tram.transform.position, chosen.position, route);
            recentStations.Remove(chosen);
            recentStations.Add(chosen);
            while (recentStations.Count > RecentMemory) recentStations.RemoveAt(0);

            destination = chosen;
            var edge = network.FindClosestEdge(chosen.position);
            stationTangent = (network.Graph[edge.B].position - network.Graph[edge.A].position).normalized;
            stationSide = Vector3.Cross(Vector3.up, stationTangent).normalized;
            for (int i = 0; i < waitingPositions.Length; i++)
            {
                Vector3 point = chosen.position + stationSide * 4 + stationTangent * ((i - 1) * 1.4f);
                if (ground != null && ground.Raycast(new Ray(point + Vector3.up * 100, Vector3.down), out RaycastHit hit, 200)) point.y = hit.point.y;
                waitingPositions[i] = point;
            }
            EnsureStationMarker();
            marker.transform.position = chosen.position + Vector3.up * 0.15f;
            marker.transform.localScale = Vector3.one;
            for (int i = 0; i < stationRing.positionCount; i++)
            {
                float angle = i * Mathf.PI * 2 / stationRing.positionCount;
                Vector3 point = chosen.position + new Vector3(Mathf.Sin(angle) * ReachDistance, 0, Mathf.Cos(angle) * ReachDistance);
                if (ground != null && ground.Raycast(new Ray(point + Vector3.up * 100, Vector3.down), out RaycastHit hit, 200)) point.y = hit.point.y + 0.18f;
                stationRing.SetPosition(i, point - marker.transform.position);
            }
            marker.SetActive(true);
            stationRing.startColor = stationRing.endColor = ObjectiveColour;
            markerMaterial.color = ObjectiveColour;
            foreach (Transform passenger in passengers) passenger.gameObject.SetActive(!isDropOff);
            boarding = 0;
            routeTimer = 1f;
            jobTime = Mathf.Clamp(RouteLength(route) / 10f + 20f, 32f, 100f);
            AnimatePassengers();
        }

        /// <summary>
        /// Collects the reachable stops inside a distance band into <see cref="shortlist"/>.
        /// Returns false when that leaves nothing, so the caller can loosen and retry.
        /// </summary>
        private bool Shortlist(float nearest, float furthest, bool unvisitedOnly)
        {
            shortlist.Clear();
            for (int i = 0; i < reachable.Count; i++)
            {
                if (reachableDistance[i] < nearest || reachableDistance[i] > furthest) continue;
                if (unvisitedOnly && recentStations.Contains(reachable[i])) continue;
                shortlist.Add(reachable[i]);
            }
            return shortlist.Count > 0;
        }

        private static float RouteLength(List<Vector3> points)
        {
            float result = 0;
            for (int i = 1; i < points.Count; i++) result += Vector3.Distance(points[i - 1], points[i]);
            return result;
        }

        private void EnsureStationMarker()
        {
            if (marker != null) return;
            marker = new GameObject("Station boarding area");
            marker.transform.SetParent(transform, false);
            markerMaterial = TramRushBootstrap.CreateRuntimeMaterial(Color.white);
            stationRing = marker.AddComponent<LineRenderer>();
            stationRing.sharedMaterial = markerMaterial;
            stationRing.useWorldSpace = false;
            stationRing.loop = true;
            stationRing.widthMultiplier = 0.3f;
            stationRing.positionCount = 48;
            var beacon = new GameObject("Station beacon").AddComponent<LineRenderer>();
            beacon.transform.SetParent(marker.transform, false);
            beacon.sharedMaterial = markerMaterial;
            beacon.useWorldSpace = false;
            beacon.positionCount = 2;
            beacon.SetPosition(0, Vector3.up * 0.5f);
            beacon.SetPosition(1, Vector3.up * 24f);
            beacon.startWidth = 0.8f;
            beacon.endWidth = 0.2f;
            for (int i = 0; i < 48; i++)
            {
                float angle = i * Mathf.PI * 2 / 48;
                stationRing.SetPosition(i, new Vector3(Mathf.Sin(angle) * ReachDistance, 0, Mathf.Cos(angle) * ReachDistance));
            }
            for (int i = 0; i < 3; i++)
            {
                GameObject person = new GameObject("Waiting passenger " + (i + 1));
                person.transform.SetParent(marker.transform, false);
                Color coat = Color.Lerp(TramInterface.Accent, new Color(0.3f, 0.6f, 0.65f), i / 2f);
                Part(person.transform, PrimitiveType.Capsule, new Vector3(0, 1.25f, 0), new Vector3(0.65f, 0.5f, 0.42f), coat);
                Part(person.transform, PrimitiveType.Sphere, new Vector3(0, 1.96f, 0), Vector3.one * 0.4f, new Color(0.72f, 0.51f, 0.38f));
                for (int side = -1; side <= 1; side += 2)
                {
                    Part(person.transform, PrimitiveType.Capsule, new Vector3(side * 0.18f, 0.45f, 0), new Vector3(0.22f, 0.45f, 0.24f), new Color(0.16f, 0.2f, 0.24f));
                    Part(person.transform, PrimitiveType.Capsule, new Vector3(side * 0.4f, 1.2f, 0), new Vector3(0.19f, 0.4f, 0.2f), coat);
                }
                passengers.Add(person.transform);
            }
        }

        private void Part(Transform parent, PrimitiveType shape, Vector3 position, Vector3 scale, Color colour)
        {
            GameObject part = GameObject.CreatePrimitive(shape);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            Destroy(part.GetComponent<Collider>());
            Renderer renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = markerMaterial;
            var tint = new MaterialPropertyBlock();
            tint.SetColor("_BaseColor", colour);
            renderer.SetPropertyBlock(tint);
        }

        private void AnimatePassengers()
        {
            if (destination == null || carryingPassenger) return;
            for (int i = 0; i < passengers.Count; i++)
            {
                float progress = Mathf.SmoothStep(0, 1, Mathf.Clamp01(boarding / BoardingDuration * 1.7f - i * 0.25f));
                Vector3 start = waitingPositions[i];
                passengers[i].position = Vector3.Lerp(start, tram.transform.position + Vector3.up, progress) + Vector3.up * (Mathf.Sin(progress * Mathf.PI) * 0.35f);
                passengers[i].rotation = Quaternion.LookRotation(-stationSide, Vector3.up);
                passengers[i].localScale = Vector3.one * (0.8f * (1f - progress * 0.95f));
            }
        }

        private void SpawnSuccessBurst(Vector3 position)
        {
            var burstObject = new GameObject("Station celebration");
            burstObject.transform.position = position;
            ParticleSystem particles = burstObject.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.duration = 0.8f;
            main.loop = false;
            main.startLifetime = 0.7f;
            main.startSpeed = 4f;
            main.startSize = 0.18f;
            main.startColor = ObjectiveColour;
            main.gravityModifier = 0.25f;
            main.maxParticles = 32;
            particles.GetComponent<ParticleSystemRenderer>().sharedMaterial = markerMaterial;
            var emission = particles.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0, 28) });
            particles.Play();
            Destroy(burstObject, 2f);
        }

        private void OnDestroy()
        {

            if (markerMaterial != null) Destroy(markerMaterial);
        }
    }
}
