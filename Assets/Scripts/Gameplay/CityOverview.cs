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
        private sealed class Stop
        {
            public string name;
            public Vector3 position;
        }

        private readonly List<Stop> stops = new();
        private Camera view;
        private TramFollowCamera follow;
        private TramGameManager manager;
        private Vector3 focus;
        private Bounds bounds;
        private float height;
        private float desiredHeight;
        private int selected;
        private Vector2 listScroll;
        private GUIStyle title, body, button, pin;

        public void Initialize(Camera camera, TramGameManager game, TramTrackNetwork network, Transform city)
        {
            view = camera;
            follow = camera.GetComponent<TramFollowCamera>();
            manager = game;
            bounds = new Bounds(network.Graph[0].position, Vector3.zero);
            foreach (var node in network.Graph) bounds.Encapsulate(node.position);
            var names = new HashSet<string>();
            foreach (Transform child in city.GetComponentsInChildren<Transform>())
            {
                if (!child.name.StartsWith("Tram Stop")) continue;
                TextMesh label = child.GetComponentInChildren<TextMesh>();
                string stopName = label != null ? label.text : child.name;
                if (!names.Add(stopName)) continue;
                var edge = network.FindClosestEdge(child.position);
                stops.Add(new Stop { name = stopName, position = edge.Point });
            }
            // A map imported without the optional stop data must still be playable.
            if (stops.Count == 0)
                stops.Add(new Stop { name = "City rail access", position = network.FindClosestEdge(bounds.center).Point });
            stops.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            float closest = float.MaxValue;
            for (int i = 0; i < stops.Count; i++)
            {
                float distance = (stops[i].position - bounds.center).sqrMagnitude;
                if (distance >= closest) continue;
                closest = distance;
                selected = i;
            }
            Open();
        }

        private void Open()
        {
            manager.ShowCity();
            follow.enabled = false;
            focus = stops[selected].position;
            height = desiredHeight = defaultHeight;
            view.fieldOfView = 55f;
            ApplyView();
        }

        private void Update()
        {
            if (manager == null) return;
            Keyboard keys = Keyboard.current;
            if (!manager.ChoosingStop)
            {
                if (keys != null && keys.escapeKey.wasPressedThisFrame) Open();
                return;
            }
            Mouse mouse = Mouse.current;
            // UI occupies the left column; scrolling it must not zoom the city.
            bool overPanel = mouse != null && GameUi.FromScreen(mouse.position.ReadValue()).x < PanelWidth + 32f;
            if (mouse != null && !overPanel)
            {
                desiredHeight = Mathf.Clamp(desiredHeight * Mathf.Exp(-Mathf.Clamp(mouse.scroll.ReadValue().y, -1f, 1f) * 0.13f), 140f, 1800f);
                if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
                {
                    Vector2 current = mouse.position.ReadValue();
                    Vector2 previous = current - mouse.delta.ReadValue();
                    var plane = new Plane(Vector3.up, focus);
                    Ray before = view.ScreenPointToRay(previous), after = view.ScreenPointToRay(current);
                    if (plane.Raycast(before, out float d0) && plane.Raycast(after, out float d1))
                        focus += Vector3.ClampMagnitude(before.GetPoint(d0) - after.GetPoint(d1), height * 0.3f);
                }
            }
            Vector3 move = Vector3.zero;
            if (keys != null)
            {
                if (keys.wKey.isPressed || keys.upArrowKey.isPressed) move.z++;
                if (keys.sKey.isPressed || keys.downArrowKey.isPressed) move.z--;
                if (keys.dKey.isPressed || keys.rightArrowKey.isPressed) move.x++;
                if (keys.aKey.isPressed || keys.leftArrowKey.isPressed) move.x--;
                if (keys.qKey.isPressed) yaw -= 35f * Time.unscaledDeltaTime;
                if (keys.eKey.isPressed) yaw += 35f * Time.unscaledDeltaTime;
                if (keys.homeKey.wasPressedThisFrame) { focus = stops[selected].position; desiredHeight = defaultHeight; yaw = -15f; }
                if (keys.enterKey.wasPressedThisFrame) { Spawn(); return; }
            }
            focus += Quaternion.Euler(0, yaw, 0) * move.normalized * (desiredHeight * 0.65f * Time.unscaledDeltaTime);
            focus.x = Mathf.Clamp(focus.x, bounds.min.x - 100f, bounds.max.x + 100f);
            focus.z = Mathf.Clamp(focus.z, bounds.min.z - 100f, bounds.max.z + 100f);
            height = Mathf.Lerp(height, desiredHeight, 1f - Mathf.Exp(-9f * Time.unscaledDeltaTime));
            ApplyView();
        }

        private void Spawn()
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

        private const float PanelWidth = 286f;
        private readonly List<Rect> occupiedPins = new();

        private void OnGUI()
        {
            if (manager == null || !manager.ChoosingStop) return;
            using var ui = new GameUi.Scope(true);
            if (title == null)
            {
                title = new GUIStyle(GUI.skin.label) { fontSize = 27, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.94f, 0.92f, 0.85f) } };
                body = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, normal = { textColor = new Color(0.69f, 0.74f, 0.74f) } };
                button = new GUIStyle(GUIStyle.none) { fontSize = 15, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(14, 10, 0, 0), normal = { textColor = Color.white }, clipping = TextClipping.Clip };
                pin = new GUIStyle(button) { fontSize = 12, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(8, 8, 0, 0) };
            }
            float h = GameUi.Height;
            GameUi.Fill(new Rect(20, 20, PanelWidth, h - 40), GameUi.Ink);
            GUI.Label(new Rect(40, 38, 246, 24), "GÖTEBORG   /   SPÅRVAGN RUSH", body);
            GUI.Label(new Rect(40, 76, 246, 42), "Departure", title);
            GUI.Label(new Rect(40, 119, 246, 42), "Choose a stop to begin your run.", body);
            GameUi.Fill(new Rect(40, 166, 246, 1), new Color(0.3f, 0.32f, 0.3f));

            Rect list = new Rect(32, 180, PanelWidth - 24, h - 410);
            listScroll = GUI.BeginScrollView(list, listScroll, new Rect(0, 0, list.width - 18, stops.Count * 46));
            for (int i = 0; i < stops.Count; i++)
                if (GameUi.Button(new Rect(0, i * 46, list.width - 18, 42), stops[i].name, button, i == selected))
                {
                    selected = i;
                    focus = stops[i].position;
                }
            GUI.EndScrollView();
            GUI.Label(new Rect(40, h - 213, 246, 22), "DEPARTING FROM", body);
            GUI.Label(new Rect(40, h - 188, 246, 28), stops[selected].name, button);
            if (GameUi.Button(new Rect(40, h - 149, 246, 46), "Start run", button, true)) Spawn();
            GUI.Label(new Rect(40, h - 89, 246, 46), "Enter to depart\nHome to restore the city view", body);

            GameUi.Fill(new Rect(326, h - 78, GameUi.Width - 346, 58), GameUi.Ink);
            GUI.Label(new Rect(342, h - 68, GameUi.Width - 378, 42),
                $"{height:0} m    •    Scroll to zoom    •    Drag RMB / MMB or WASD to pan\nQ / E to rotate    •    Map © OpenStreetMap  /  Imagery © Göteborgs Stad (CC0)", body);

            occupiedPins.Clear();
            DrawPin(selected);
            for (int i = 0; i < stops.Count; i++) if (i != selected) DrawPin(i);
        }

        private void DrawPin(int index)
        {
            Vector3 screen = view.WorldToScreenPoint(stops[index].position);
            Vector2 point = GameUi.FromScreen(screen);
            Rect rect = new Rect(point.x - 80, point.y - 17, 160, 34);
            if (screen.z <= 0 || rect.x < 324 || rect.xMax > GameUi.Width - 20 || rect.y < 20 || rect.yMax > GameUi.Height - 96) return;
            foreach (Rect previous in occupiedPins) if (previous.Overlaps(rect)) return;
            occupiedPins.Add(rect);
            if (GameUi.Button(rect, stops[index].name, pin, index == selected)) selected = index;
        }
    }
}