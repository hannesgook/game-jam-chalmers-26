using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace SparvagnRush.Gameplay
{
    /// <summary>Plain canvas windows; no immediate-mode HUD or custom GUI skin.</summary>
    public sealed class TramInterface : MonoBehaviour
    {
        public static readonly Color Accent = new(0.08f, 0.39f, 0.85f);
        private static readonly Color TextColour = new(0.12f, 0.15f, 0.19f);
        private TramGameManager game;
        private CityOverview overview;
        private RectTransform root, selection, instructions, hud, map, drivingHelp, status, notice, ending, intro, destinationBadge;
        private Text selectedText, objective, directions, numbers, speed, notification, result, destinationText, mapCaption;
        private Image progress, balance;
        private readonly List<Button> stationButtons = new();
        private readonly List<Button> pins = new();
        private readonly List<Rect> pinRects = new();
        private Font font;
        private LineRenderer routeLine;
        private Material routeMaterial;

        public void Initialize(TramGameManager manager, CityOverview cityView)
        {
            game = manager; overview = cityView;
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var canvasObject = new GameObject("Game information", typeof(RectTransform));
            canvasObject.transform.SetParent(transform, false);
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(960, 640);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            canvasObject.AddComponent<GraphicRaycaster>();
            if (EventSystem.current == null)
            {
                var events = new GameObject("Game UI input", typeof(EventSystem), typeof(InputSystemUIInputModule));
                events.transform.SetParent(transform, false);
            }
            root = Rect("Safe area", canvasObject.transform, 0, 0, 0, 0);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one; root.offsetMin = root.offsetMax = Vector2.zero;

            selection = Window("Choose a station", root, 20, 20, 280, 540);
            selection.anchorMin = new Vector2(0, 0); selection.anchorMax = new Vector2(0, 1);
            selection.offsetMin = new Vector2(20, 100); selection.offsetMax = new Vector2(300, -20);
            Label(selection, "Spårvagn Rush", 18, 16, 244, 32, 24, true);
            Label(selection, "Choose your starting station", 18, 55, 244, 30, 16);
            RectTransform viewport = Rect("Station list", selection, 12, 98, 256, 280);
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(12, 135); viewport.offsetMax = new Vector2(-12, -98);
            viewport.gameObject.AddComponent<RectMask2D>();
            Image hitArea = viewport.gameObject.AddComponent<Image>(); hitArea.color = new Color(0.97f, 0.97f, 0.97f);
            RectTransform content = Rect("Stations", viewport, 0, 0, 256, game.Stations.Count * 42);
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            for (int i = 0; i < game.Stations.Count; i++)
            {
                int index = i;
                stationButtons.Add(Button(content, game.Stations[i].name, 0, i * 42, 252, 38, () => overview.SelectStop(index)));
                Button pin = Button(root, game.Stations[i].name, 0, 0, 142, 34, () => overview.SelectStop(index));
                ((RectTransform)pin.transform).anchorMin = ((RectTransform)pin.transform).anchorMax = new Vector2(0.5f, 0.5f);
                ((RectTransform)pin.transform).pivot = new Vector2(0.5f, 0.5f);
                pins.Add(pin);
            }
            RectTransform departure = Rect("Departure", selection, 0, 0, 280, 122);
            departure.anchorMin = departure.anchorMax = Vector2.zero; departure.pivot = Vector2.zero;
            selectedText = Label(departure, "", 18, 8, 244, 34, 16, true);
            Button(departure, "Start run", 18, 49, 244, 42, overview.Spawn);
            Label(departure, "Enter to start", 18, 95, 244, 20, 12);
            instructions = Window("Map controls", root, 20, 0, 640, 64);
            Bottom(instructions, 20, 20);
            Label(instructions, "Drag with the left mouse button to pan. A / D rotates. Scroll to zoom.\nHome resets the view. Select a station to take a closer look.", 14, 10, 612, 46, 14);

            hud = Window("Your next stop", root, 20, 20, 350, 194);
            objective = Label(hud, "", 16, 12, 318, 55, 23, true);
            directions = Label(hud, "", 16, 74, 318, 57, 16);
            numbers = Label(hud, "", 16, 139, 318, 40, 14);
            var bar = Rect("Boarding progress", hud, 16, 184, 318, 4);
            progress = bar.gameObject.AddComponent<Image>(); progress.color = Accent;
            status = Window("Driving", root, 20, 0, 350, 64); Bottom(status, 20, 76);
            speed = Label(status, "", 14, 10, 322, 25, 16);
            var track = Rect("Balance track", status, 14, 45, 322, 3);
            track.gameObject.AddComponent<Image>().color = new Color(0.8f, 0.82f, 0.85f);
            balance = Rect("Balance", status, 170, 40, 8, 13).gameObject.AddComponent<Image>(); balance.color = Accent;
            drivingHelp = Window("Controls", root, 20, 0, 640, 42); Bottom(drivingHelp, 20, 20);
            Label(drivingHelp, "W / S drive    A / D balance and turn    RMB camera    Esc stations", 14, 10, 612, 24, 14);

            map = Window("Minimap", root, 0, 20, 260, 322);
            map.anchorMin = map.anchorMax = new Vector2(1, 1); map.pivot = new Vector2(1, 1); map.anchoredPosition = new Vector2(-20, -20);
            var image = Rect("City view", map, 10, 10, 240, 240).gameObject.AddComponent<RawImage>();
            image.raycastTarget = false;
            image.gameObject.AddComponent<TramMinimapView>().Initialize(game);
            Label(map, "N ↑", 17, 14, 34, 22, 14, true);
            Label(map, "You", 114, 141, 45, 22, 13, true);
            mapCaption = Label(map, "", 12, 260, 236, 55, 14);
            notice = Window("Message", root, 0, 0, 400, 70); CentreBottom(notice, 156);
            notification = Label(notice, "", 16, 12, 368, 48, 16);
            ending = Window("Run complete", root, 0, 0, 430, 238); Centre(ending);
            result = Label(ending, "", 22, 20, 386, 116, 22, true);
            Button(ending, "Try again", 22, 161, 182, 44, game.Retry);
            Button(ending, "Choose a station", 220, 161, 188, 44, overview.Open);
            intro = Window("Welcome", root, 0, 0, 440, 164); CentreBottom(intro, 35);
            Label(intro, "Spårvagn Rush", 22, 16, 396, 36, 28, true);
            Label(intro, "Balance your tram. Follow the blue route.\nPick up passengers and complete three deliveries.", 22, 57, 396, 48, 16);
            Button(intro, "Choose a station  ·  Skip intro", 22, 112, 396, 36, overview.SkipIntro);
            destinationBadge = Window("Station marker", root, 0, 0, 180, 48); Centre(destinationBadge);
            destinationText = Label(destinationBadge, "", 8, 5, 164, 38, 14, true);

            routeLine = new GameObject("Follow this rail").AddComponent<LineRenderer>();
            routeLine.transform.SetParent(transform, false);
            routeMaterial = SparvagnRushBootstrap.CreateRuntimeMaterial(Accent);
            routeLine.sharedMaterial = routeMaterial;
            routeLine.widthMultiplier = 0.35f;
            routeLine.useWorldSpace = true;
            routeLine.numCornerVertices = 2;
        }

        private void LateUpdate()
        {
            if (game == null) return;
            Rect safe = Screen.safeArea;
            root.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
            root.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
            bool starting = overview.IntroPlaying, selecting = game.ChoosingStop && !starting, driving = !game.ChoosingStop;
            intro.gameObject.SetActive(starting);
            selection.gameObject.SetActive(selecting); instructions.gameObject.SetActive(selecting);
            hud.gameObject.SetActive(driving); status.gameObject.SetActive(driving); drivingHelp.gameObject.SetActive(driving);
            map.gameObject.SetActive(driving);
            ending.gameObject.SetActive(driving && game.Ended);
            notice.gameObject.SetActive(driving && !game.Ended && game.Notice.Length > 0);
            destinationBadge.gameObject.SetActive(false);
            routeLine.enabled = driving && !game.Ended && game.Destination != null;
            pinRects.Clear();
            for (int i = 0; i < pins.Count; i++)
            {
                stationButtons[i].GetComponent<Image>().color = i == overview.Selected ? new Color(0.83f, 0.91f, 1f) : new Color(0.95f, 0.96f, 0.97f);
                pins[i].gameObject.SetActive(false);
                if (!selecting) continue;
                Vector3 screen = overview.View.WorldToScreenPoint(game.Stations[i].position);
                if (screen.z <= 0) continue;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, null, out Vector2 local);
                Rect r = new Rect(local.x - 71, local.y - 17, 142, 34);
                Rect limits = root.rect;
                if (r.xMin < limits.xMin + 320 || r.xMax > limits.xMax - 16 || r.yMin < limits.yMin + 100 || r.yMax > limits.yMax - 16 || pinRects.Exists(other => other.Overlaps(r))) continue;
                pinRects.Add(r);
                pins[i].gameObject.SetActive(true);
                ((RectTransform)pins[i].transform).anchoredPosition = local;
            }
            selectedText.text = game.Stations[overview.Selected].name;
            if (!driving) return;
            notification.text = game.Notice;
            objective.text = game.Destination == null ? "Finding passengers…" : (game.Carrying ? "Drop off at\n" : "Pick up at\n") + game.Destination.name;
            string turn = "Follow the blue route";
            if (game.Destination != null)
            {
                Vector3 travel = game.Player.transform.forward * game.Player.TravelSign;
                float angle = Vector3.SignedAngle(travel, game.GuidancePoint - game.Player.transform.position, Vector3.up);
                turn = Mathf.Abs(angle) > 115 ? "Reverse to follow the route" : angle > 18 ? "Take the right-hand rail" : angle < -18 ? "Take the left-hand rail" : "Continue ahead";
            }
            directions.text = game.BoardingProgress > 0 ? (game.Carrying ? "Passengers getting off…" : "Passengers boarding…") :
                $"{turn}  ·  {game.RemainingDistance:0} m\nSlow below 9 km/h inside the station ring.";
            numbers.text = $"{game.Delivered}/3 deliveries   ·   Score {game.Score}\n{game.SessionTime:0}s remaining   ·   Station {game.JobTime:0}s";
            progress.rectTransform.sizeDelta = new Vector2(318 * game.BoardingProgress, 4);
            speed.text = $"{Mathf.Abs(game.Player.Speed) * 3.6f:0} km/h    ·    {(game.Player.TravelSign < 0 ? "Reverse" : "Forward")}    ·    Balance";
            float lean = TramController.TravelRelativeLean(game.Player.LeanAngle, game.Player.TravelSign) / game.Player.FallAngle;
            balance.rectTransform.anchoredPosition = new Vector2(171 + Mathf.Clamp(lean, -1, 1) * 153, -40);
            balance.color = Mathf.Abs(lean) > 0.7f ? new Color(0.88f, 0.24f, 0.16f) : Accent;
            mapCaption.text = game.Destination == null ? "Blue marker: your tram" : $"{game.Destination.name}  ·  {game.RemainingDistance:0} m\nOrange arrow: follow this direction";
            result.text = $"{(game.Delivered >= 3 ? "Route complete!" : game.Derailed ? "Tram derailed" : "Time is up")}\n\n{game.Delivered} deliveries   ·   Score {game.Score}";
            if (routeLine.enabled)
            {
                int count = Mathf.Min(game.Route.Count, 80);
                routeLine.positionCount = count;
                for (int i = 0; i < count; i++) routeLine.SetPosition(i, game.Route[i] + Vector3.up * 0.65f);
                Vector3 screen = overview.View.WorldToScreenPoint(game.Destination.position + Vector3.up * 8);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, null, out Vector2 local);
                if (screen.z > 0 && local.x > root.rect.xMin + 380 && local.x < root.rect.xMax - 290 && Mathf.Abs(local.y) < root.rect.height * 0.5f - 90)
                {
                    destinationBadge.gameObject.SetActive(true);
                    destinationBadge.anchoredPosition = local;
                    destinationText.text = game.Destination.name + "\nStop here";
                }
            }
        }

        private RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform; rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); return rect;
        }
        private RectTransform Window(string name, Transform parent, float x, float y, float width, float height)
        {
            var rect = Rect(name, parent, x, y, width, height);
            rect.gameObject.AddComponent<Image>().color = Color.white;
            var shadow = rect.gameObject.AddComponent<Shadow>(); shadow.effectColor = new Color(0, 0, 0, 0.14f); shadow.effectDistance = new Vector2(0, -2);
            return rect;
        }
        private Text Label(Transform parent, string text, float x, float y, float width, float height, int size, bool bold = false)
        {
            var label = Rect("Text", parent, x, y, width, height).gameObject.AddComponent<Text>();
            label.font = font; label.fontSize = size; label.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            label.text = text; label.color = TextColour; label.raycastTarget = false;
            label.resizeTextForBestFit = true; label.resizeTextMinSize = Mathf.Min(12, size); label.resizeTextMaxSize = size;
            label.horizontalOverflow = HorizontalWrapMode.Wrap; label.verticalOverflow = VerticalWrapMode.Truncate;
            return label;
        }
        private Button Button(Transform parent, string text, float x, float y, float width, float height, UnityEngine.Events.UnityAction action)
        {
            var rect = Rect(text, parent, x, y, width, height);
            rect.gameObject.AddComponent<Image>().color = new Color(0.94f, 0.95f, 0.96f);
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = rect.GetComponent<Image>(); button.onClick.AddListener(action);
            Text label = Label(rect, text, 10, 0, width - 20, height, 15); label.alignment = TextAnchor.MiddleCenter;
            return button;
        }
        private static void Bottom(RectTransform rect, float x, float y) { rect.anchorMin = rect.anchorMax = Vector2.zero; rect.pivot = Vector2.zero; rect.anchoredPosition = new Vector2(x, y); }
        private static void CentreBottom(RectTransform rect, float y) { rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0); rect.pivot = new Vector2(0.5f, 0); rect.anchoredPosition = new Vector2(0, y); }
        private static void Centre(RectTransform rect) { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f); rect.anchoredPosition = Vector2.zero; }
        private void OnDestroy() { if (routeMaterial != null) Destroy(routeMaterial); }
    }
}
