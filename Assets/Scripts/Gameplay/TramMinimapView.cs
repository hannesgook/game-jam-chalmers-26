using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace TramRush.Gameplay
{
    public sealed class TramMinimapView : MonoBehaviour
    {
        public const float Span = 360f;
        private Camera mapCamera;
        private RenderTexture texture;
        private TramGameManager game;
        private RailMapOverlay overlay;

        public void Initialize(TramGameManager manager)
        {
            game = manager;
            texture = new RenderTexture(320, 320, 16) { name = "Overhead city minimap" };
            texture.Create();
            GetComponent<RawImage>().texture = texture;
            mapCamera = new GameObject("Minimap camera").AddComponent<Camera>();
            mapCamera.transform.SetParent(manager.transform, false);
            mapCamera.orthographic = true;
            mapCamera.orthographicSize = Span * 0.5f;
            mapCamera.aspect = 1;
            mapCamera.nearClipPlane = 1;
            mapCamera.farClipPlane = 1000;
            mapCamera.clearFlags = CameraClearFlags.SolidColor;
            mapCamera.backgroundColor = new Color(0.55f, 0.75f, 0.77f);
            mapCamera.cullingMask = ~(1 << 5); // Screen-space UI never enters the camera image.
            mapCamera.targetTexture = texture;
            mapCamera.allowHDR = false;
            mapCamera.allowMSAA = false;
            var data = mapCamera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.renderShadows = false;
            mapCamera.enabled = false;
            var drawing = new GameObject("Route and tram", typeof(RectTransform), typeof(CanvasRenderer));
            drawing.transform.SetParent(transform, false);
            var rect = (RectTransform)drawing.transform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            overlay = drawing.AddComponent<RailMapOverlay>();
            overlay.Game = game;
            overlay.raycastTarget = false;
            gameObject.AddComponent<RectMask2D>();
        }

        private void LateUpdate()
        {
            if (game == null) return;
            mapCamera.enabled = !game.ChoosingStop;
            if (!mapCamera.enabled) return;
            // No smoothing: the marked tram is always exactly at the map centre.
            Vector3 position = game.Player.transform.position;
            mapCamera.transform.SetPositionAndRotation(position + Vector3.up * 500, Quaternion.Euler(90, 0, 0));
            overlay.SetVerticesDirty();
        }

        private void OnDisable() { if (mapCamera != null) mapCamera.enabled = false; }
        private void OnDestroy()
        {
            if (mapCamera != null) Destroy(mapCamera.gameObject);
            if (texture != null) { texture.Release(); Destroy(texture); }
        }
    }

    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class RailMapOverlay : MaskableGraphic
    {
        public TramGameManager Game;
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (Game == null || Game.Player == null) return;
            float size = rectTransform.rect.width;
            Vector2 Point(Vector3 p) => new((p.x - Game.Player.transform.position.x) / TramMinimapView.Span * size,
                (p.z - Game.Player.transform.position.z) / TramMinimapView.Span * size);
            var route = Game.Route;
            for (int i = 1; i < route.Count; i++)
            {
                Stroke(vh, Point(route[i - 1]), Point(route[i]), Color.white, 7);
                Stroke(vh, Point(route[i - 1]), Point(route[i]), TramInterface.Accent, 4);
            }
            if (Game.Destination != null)
            {
                Vector2 p = Point(Game.Destination.position);
                float limit = size * 0.5f - 12;
                float extent = Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y));
                if (extent > limit) p *= limit / extent;
                Circle(vh, p, 9, Color.white);
                Circle(vh, p, 6, new Color(0.95f, 0.46f, 0.08f));
                Vector2 direction = Point(Game.GuidancePoint).normalized;
                if (direction.sqrMagnitude > 0.1f) Arrow(vh, direction * 32, direction, new Color(1f, 0.47f, 0.05f), 14);
            }
            Circle(vh, Vector2.zero, 12, Color.white);
            Circle(vh, Vector2.zero, 9, new Color(0.03f, 0.23f, 0.55f));
            float heading = Game.Player.transform.eulerAngles.y * Mathf.Deg2Rad;
            Arrow(vh, Vector2.zero, new Vector2(Mathf.Sin(heading), Mathf.Cos(heading)), Color.white, 7);
        }

        private static void Triangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color colour)
        {
            int index = vh.currentVertCount;
            vh.AddVert(a, colour, Vector2.zero); vh.AddVert(b, colour, Vector2.zero); vh.AddVert(c, colour, Vector2.zero);
            vh.AddTriangle(index, index + 1, index + 2);
        }
        private static void Arrow(VertexHelper vh, Vector2 centre, Vector2 forward, Color colour, float size)
        {
            Vector2 side = new(-forward.y, forward.x);
            Triangle(vh, centre + forward * size, centre - forward * size * 0.5f + side * size * 0.6f,
                centre - forward * size * 0.5f - side * size * 0.6f, colour);
        }
        private static void Circle(VertexHelper vh, Vector2 centre, float radius, Color colour)
        {
            for (int i = 0; i < 24; i++)
            {
                float a = i * Mathf.PI / 12, b = (i + 1) * Mathf.PI / 12;
                Triangle(vh, centre, centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius,
                    centre + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * radius, colour);
            }
        }
        private static void Stroke(VertexHelper vh, Vector2 a, Vector2 b, Color colour, float width)
        {
            Vector2 d = (b - a).normalized;
            Vector2 side = new Vector2(-d.y, d.x) * width * 0.5f;
            Triangle(vh, a - side, a + side, b + side, colour);
            Triangle(vh, a - side, b + side, b - side, colour);
        }
    }
}
