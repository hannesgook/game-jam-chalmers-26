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
        public void SkipIntro() { if (intro != null) intro.Skip(); }

        public void Initialize(Camera camera, TramGameManager game, TramTrackNetwork network, Transform city)
        {
            view = camera;
            follow = camera.GetComponent<TramFollowCamera>();
            manager = game;
            bounds = new Bounds(network.Graph[0].position, Vector3.zero);
            foreach (var node in network.Graph) bounds.Encapsulate(node.position);
            stops = manager.Stations;
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
            if (keys != null)
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
            wantedFocus = stops[index].position;
            desiredHeight = Mathf.Max(105f, wantedFocus.y + 95f);
            touring = true;
            dragging = false;
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
