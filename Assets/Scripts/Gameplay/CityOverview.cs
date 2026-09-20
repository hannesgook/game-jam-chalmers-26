using System.Collections.Generic;
using SparvagnRush.Map;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SparvagnRush.Gameplay
{
    /// <summary>Pre-session city navigation. Uses actual generated stops, including older scenes.</summary>
    public sealed class CityOverview : MonoBehaviour
    {
        public float defaultHeight = 480f;
        [Range(35f, 75f)] public float pitch = 55f;
        private float yaw = -15f;
        private List<TramStation> stops;
        private Vector3 wantedFocus;
        private bool touring;
        private bool dragging;
        private Vector2 dragStart;
        private Camera view;
        private TramFollowCamera follow;
        private TramGameManager manager;
        private Vector3 focus;
        private Bounds bounds;
        private float height;
        private float desiredHeight;
        private int selected;

        private CityIntro intro;
        public bool IntroPlaying => intro != null && intro.Playing;
        public int Selected => selected;
        public Camera View => view;
        public bool Loading => intro != null && intro.Loading;
        public string LoadingMessage => intro != null ? intro.LoadingMessage : "Preparing city…";
        public readonly List<CityPlace> Places = new();
        public string FocusedPlaceName { get; private set; }
        public string FocusedPlaceKind { get; private set; }
        public void SkipIntro() { if (intro != null) intro.Skip(); }

        public void Initialize(Camera camera, TramGameManager game, TramTrackNetwork network, Transform city)
        {
            view = camera;
            follow = camera.GetComponent<TramFollowCamera>();
            manager = game;
            bounds = new Bounds(network.Graph[0].position, Vector3.zero);
            foreach (var node in network.Graph) bounds.Encapsulate(node.position);
            stops = manager.Stations;
            for (int i = 0; i < stops.Count; i++)
                Places.Add(new CityPlace { name = stops[i].name, kind = "Tram station", position = stops[i].position, stationIndex = i });
            var seen = new HashSet<string>();
            foreach (MapLabel label in city.GetComponentsInChildren<MapLabel>())
            {
                if (label.Style == MapLabel.LabelStyle.Stop) continue;
                TextMesh text = label.GetComponent<TextMesh>();
                if (text == null || string.IsNullOrWhiteSpace(text.text)) continue;
                string key = text.text + ":" + Mathf.RoundToInt(label.transform.position.x / 8) + ":" + Mathf.RoundToInt(label.transform.position.z / 8);
                if (!seen.Add(key)) continue;
                Places.Add(new CityPlace { name = text.text, position = label.transform.position,
                    kind = label.Style == MapLabel.LabelStyle.Shop ? "Shop" : label.Style == MapLabel.LabelStyle.Building ? "Building" : "Place" });
            }
            float closest = float.MaxValue;
            for (int i = 0; i < stops.Count; i++)
            {
                float distance = (stops[i].position - bounds.center).sqrMagnitude;
                if (distance >= closest) continue;
                closest = distance;
                selected = i;
            }
            Open();
            intro = gameObject.AddComponent<CityIntro>();
            intro.Initialize(camera, network, city, Open);
        }

        public void Open()
        {
            manager.ShowCity();
            follow.enabled = false;
            focus = wantedFocus = stops[selected].position;
            height = desiredHeight = defaultHeight;
            touring = dragging = false;
            view.fieldOfView = 55f;
            ApplyView();
        }

        private void Update()
        {
            if (manager == null || IntroPlaying) return;
            Keyboard keys = Keyboard.current;
            if (!manager.ChoosingStop)
            {
                if (keys != null && keys.escapeKey.wasPressedThisFrame) Open();
                return;
            }
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                bool onMap = UnityEngine.EventSystems.EventSystem.current == null ||
                    !UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    dragging = onMap;
                    dragStart = mouse.position.ReadValue();
                }
                if (mouse.leftButton.wasReleasedThisFrame) dragging = false;
                if (onMap)
                {
                    float scroll = Mathf.Clamp(mouse.scroll.ReadValue().y, -1f, 1f);
                    if (scroll != 0) touring = false;
                    desiredHeight = Mathf.Clamp(desiredHeight * Mathf.Exp(-scroll * 0.13f), 95f, 1800f);
                }
                if (dragging && mouse.leftButton.isPressed && (mouse.position.ReadValue() - dragStart).sqrMagnitude > 16f)
                {
                    touring = false;
                    Vector2 current = mouse.position.ReadValue();
                    var plane = new Plane(Vector3.up, focus);
                    Ray before = view.ScreenPointToRay(current - mouse.delta.ReadValue()), after = view.ScreenPointToRay(current);
                    if (plane.Raycast(before, out float d0) && plane.Raycast(after, out float d1))
                        focus += Vector3.ClampMagnitude(before.GetPoint(d0) - after.GetPoint(d1), height * 0.3f);
                    wantedFocus = focus;
                }
            }
            var input = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject?.GetComponent<UnityEngine.UI.InputField>();
            if (keys != null && (input == null || !input.isFocused))
            {
                float rotate = (keys.dKey.isPressed ? 1f : 0f) - (keys.aKey.isPressed ? 1f : 0f);
                if (rotate != 0) { touring = false; yaw += rotate * 40f * Time.unscaledDeltaTime; }
                if (keys.homeKey.wasPressedThisFrame)
                {
                    wantedFocus = stops[selected].position;
                    desiredHeight = defaultHeight;
                    yaw = -15f;
                    touring = false;
                }
                if (keys.enterKey.wasPressedThisFrame) { Spawn(); return; }
            }
            if (touring) yaw += 8f * Time.unscaledDeltaTime;
            wantedFocus.x = Mathf.Clamp(wantedFocus.x, bounds.min.x - 100f, bounds.max.x + 100f);
            wantedFocus.z = Mathf.Clamp(wantedFocus.z, bounds.min.z - 100f, bounds.max.z + 100f);
            focus = Vector3.Lerp(focus, wantedFocus, 1f - Mathf.Exp(-3f * Time.unscaledDeltaTime));
            focus.x = Mathf.Clamp(focus.x, bounds.min.x - 100f, bounds.max.x + 100f);
            focus.z = Mathf.Clamp(focus.z, bounds.min.z - 100f, bounds.max.z + 100f);
            height = Mathf.Lerp(height, desiredHeight, 1f - Mathf.Exp(-9f * Time.unscaledDeltaTime));
            ApplyView();
        }

        public void SelectStop(int index)
        {
            selected = index;
            FocusedPlaceName = stops[index].name;
            FocusedPlaceKind = "Tram station";
            wantedFocus = stops[index].position;
            desiredHeight = Mathf.Max(105f, wantedFocus.y + 95f);
            touring = true;
            dragging = false;
        }

        public void FocusPlace(CityPlace place)
        {
            if (place.stationIndex >= 0) { SelectStop(place.stationIndex); return; }
            FocusedPlaceName = place.name;
            FocusedPlaceKind = place.kind;
            // Start run always departs from a station, so searching for a shop also
            // arms the nearest one. Otherwise flying to a place leaves the button
            // pointing at wherever the camera happened to be beforehand.
            selected = NearestStop(place.position);
            wantedFocus = place.position;
            desiredHeight = Mathf.Max(105f, wantedFocus.y + 95f);
            touring = true;
            dragging = false;
        }

        /// <summary>Distance to the nearest station, in metres, from the focused place.</summary>
        public float FocusedPlaceWalk => FocusedPlaceName == null ? 0f
            : Vector3.Distance(wantedFocus, stops[selected].position);

        private int NearestStop(Vector3 position)
        {
            int best = selected;
            float closest = float.MaxValue;
            for (int i = 0; i < stops.Count; i++)
            {
                float distance = (stops[i].position - position).sqrMagnitude;
                if (distance >= closest) continue;
                closest = distance;
                best = i;
            }
            return best;
        }

        public void Spawn()
        {
            manager.StartAt(stops[selected].position);
            follow.ResetFraming();
            follow.enabled = true;
        }

        private void ApplyView()
        {
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0);
            float distance = Mathf.Max(60f, height - focus.y) / Mathf.Sin(pitch * Mathf.Deg2Rad);
            view.transform.SetPositionAndRotation(focus - rotation * Vector3.forward * distance, rotation);
        }

    }
}
