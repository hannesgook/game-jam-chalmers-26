using System.Collections.Generic;
using SparvagnRush.Map;
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
        private RectTransform root, selection, instructions, hud, map, drivingHelp, notice, ending, intro, destinationBadge;
        private Text selectedText, objective, directions, numbers, speed, notification, result, destinationText, mapCaption, focusText;
        private Image progress, balance;
        private readonly List<Button> resultButtons = new();
        private readonly List<CityPlace> searchResults = new();
        private RectTransform searchContent, loadingScreen;
        private InputField search;
        private ScrollRect placeScroll;
        private Text searchSummary, loadingText, introDescription;
        private Button skipIntro;
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

            // One column on the left holds everything the selector needs: name it,
            // search it, pick it, start. Nothing else competes for the screen.
            selection = Window("Choose a station", root, 20, 20, 300, 540);
            selection.anchorMin = new Vector2(0, 0); selection.anchorMax = new Vector2(0, 1);
            selection.offsetMin = new Vector2(20, 104); selection.offsetMax = new Vector2(320, -20);
            Label(selection, "Spårvagn Rush", 16, 14, 268, 34, 24, true);

            var inputRect = Rect("Search places", selection, 16, 56, 268, 40);
            inputRect.gameObject.AddComponent<Image>().color = new Color(0.94f, 0.96f, 0.98f);
            search = inputRect.gameObject.AddComponent<InputField>();
            Text entry = Label(inputRect, "", 12, 0, 244, 40, 16);
            entry.alignment = TextAnchor.MiddleLeft;
            entry.resizeTextForBestFit = false;
            Text placeholder = Label(inputRect, "Search shops, places, stops…", 12, 0, 244, 40, 14);
            placeholder.color = new Color(0.45f, 0.48f, 0.52f); placeholder.alignment = TextAnchor.MiddleLeft;
            search.textComponent = entry; search.placeholder = placeholder;
            search.lineType = InputField.LineType.SingleLine;
            search.onValueChanged.AddListener(RefreshSearch);
            searchSummary = Label(selection, "", 16, 100, 268, 20, 12);

            RectTransform viewport = Rect("Place list", selection, 0, 0, 0, 0);
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(12, 130); viewport.offsetMax = new Vector2(-12, -126);
            viewport.gameObject.AddComponent<RectMask2D>();
            viewport.gameObject.AddComponent<Image>().color = new Color(0.97f, 0.975f, 0.98f);
            RectTransform content = Rect("Places", viewport, 0, 0, 276, 46);
            searchContent = content;
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            placeScroll = scroll;
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            for (int i = 0; i < 40; i++) resultButtons.Add(Button(content, "", 0, i * 48, 272, 44, () => { }));
            RefreshSearch("");

            RectTransform departure = Rect("Departure", selection, 0, 0, 300, 120);
            departure.anchorMin = departure.anchorMax = Vector2.zero; departure.pivot = Vector2.zero;
            focusText = Label(departure, "", 16, 8, 268, 32, 13);
            selectedText = Label(departure, "", 16, 42, 268, 26, 15, true);
            Button(departure, "Start run", 16, 72, 268, 42, overview.Spawn);

            for (int i = 0; i < game.Stations.Count; i++)
            {
                int index = i;
                Button pin = Button(root, game.Stations[i].name, 0, 0, 142, 32, () => overview.SelectStop(index));
                ((RectTransform)pin.transform).anchorMin = ((RectTransform)pin.transform).anchorMax = new Vector2(0.5f, 0.5f);
                ((RectTransform)pin.transform).pivot = new Vector2(0.5f, 0.5f);
                pins.Add(pin);
            }

            instructions = ControlBar("Map controls", 78);
            ControlChips(instructions,
                "Search", "Find shops and stops",
                "Click", "Fly to a place",
                "Drag", "Pan the map",
                "Wheel", "Zoom",
                "A / D", "Rotate",
                "Enter", "Start run");

            // Driving needs one panel, not three: what to do, how you are doing,
            // and how close the tram is to going over.
            hud = Window("Your run", root, 20, 20, 340, 210);
            objective = Label(hud, "", 16, 12, 308, 50, 22, true);
            directions = Label(hud, "", 16, 66, 308, 44, 15);
            numbers = Label(hud, "", 16, 114, 308, 32, 13);
            var bar = Rect("Boarding progress", hud, 16, 150, 308, 4);
            progress = bar.gameObject.AddComponent<Image>(); progress.color = Accent;
            speed = Label(hud, "", 16, 158, 308, 22, 14);
            var track = Rect("Balance track", hud, 16, 186, 308, 3);
            track.gameObject.AddComponent<Image>().color = new Color(0.82f, 0.84f, 0.87f);
            balance = Rect("Balance", hud, 166, 182, 8, 13).gameObject.AddComponent<Image>(); balance.color = Accent;

            drivingHelp = ControlBar("Controls", 78);
            ControlChips(drivingHelp,
                "W / S", "Drive and brake",
                "A / D", "Lean: steer and dodge",
                "Space", "Honk",
                "Wheel / RMB", "Camera",
                "R", "Retry",
                "Esc", "Back to map");

            map = Window("Minimap", root, 0, 20, 230, 292);
            map.anchorMin = map.anchorMax = new Vector2(1, 1); map.pivot = new Vector2(1, 1); map.anchoredPosition = new Vector2(-20, -20);
            var image = Rect("City view", map, 10, 10, 210, 210).gameObject.AddComponent<RawImage>();
            image.raycastTarget = false;
            image.gameObject.AddComponent<TramMinimapView>().Initialize(game);
            Label(map, "N ↑", 16, 14, 34, 20, 13, true);
            mapCaption = Label(map, "", 10, 226, 210, 56, 13);

            notice = Window("Message", root, 0, 0, 400, 64); CentreBottom(notice, 106);
            notification = Label(notice, "", 16, 10, 368, 44, 15);
            ending = Window("Run complete", root, 0, 0, 420, 224); Centre(ending);
            result = Label(ending, "", 20, 20, 380, 112, 21, true);
            Button(ending, "Try again", 20, 152, 180, 44, game.Retry);
            Button(ending, "Choose a station", 216, 152, 184, 44, overview.Open);

            intro = Window("Welcome", root, 0, 0, 430, 158); CentreBottom(intro, 30);
            Label(intro, "Spårvagn Rush", 20, 14, 390, 34, 26, true);
            introDescription = Label(intro, "Balance the tram, follow the blue route, deliver three times.\nSpace honks and throws people clear of the rails.", 20, 54, 390, 46, 15);
            skipIntro = Button(intro, "Choose a station  ·  Skip intro", 20, 106, 390, 36, overview.SkipIntro);

            loadingScreen = Window("Loading city", root, 0, 0, 0, 0);
            loadingScreen.anchorMin = Vector2.zero; loadingScreen.anchorMax = Vector2.one;
            loadingScreen.offsetMin = loadingScreen.offsetMax = Vector2.zero;
            RectTransform loadingCard = Rect("Loading message", loadingScreen, 0, 0, 430, 146); Centre(loadingCard);
            Label(loadingCard, "Spårvagn Rush", 20, 12, 390, 38, 26, true);
            loadingText = Label(loadingCard, "Preparing the city…", 20, 64, 390, 68, 16);
            destinationBadge = Window("Station marker", root, 0, 0, 180, 46); Centre(destinationBadge);
            destinationText = Label(destinationBadge, "", 8, 5, 164, 36, 14, true);

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
            // A minimised window reports a zero-sized screen, and dividing by it
            // would push the whole interface off to NaN and never bring it back.
            if (Screen.width > 0 && Screen.height > 0)
            {
                Rect safe = Screen.safeArea;
                root.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
                root.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
            }
            bool starting = overview.IntroPlaying, selecting = game.ChoosingStop && !starting, driving = !game.ChoosingStop;
            intro.gameObject.SetActive(starting && !overview.Loading);
            loadingScreen.gameObject.SetActive(overview.Loading);
            loadingText.text = overview.LoadingMessage + "\nThe tour begins after startup work and frame timing settle.";
            selection.gameObject.SetActive(selecting); instructions.gameObject.SetActive(selecting);
            // The crash camera gets a clear screen until it has shown the impact.
            bool crashShot = game.CinematicPlaying && !game.ShowResult;
            bool onRoad = driving && !crashShot;
            hud.gameObject.SetActive(onRoad); drivingHelp.gameObject.SetActive(onRoad);
            map.gameObject.SetActive(onRoad);
            ending.gameObject.SetActive(driving && game.ShowResult);
            notice.gameObject.SetActive(onRoad && !game.Ended && game.Notice.Length > 0);
            destinationBadge.gameObject.SetActive(false);
            routeLine.enabled = onRoad && !game.Ended && game.Destination != null;
            pinRects.Clear();
            for (int i = 0; i < pins.Count; i++)
            {

                pins[i].gameObject.SetActive(false);
                if (!selecting) continue;
                Vector3 screen = overview.View.WorldToScreenPoint(game.Stations[i].position);
                if (screen.z <= 0) continue;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, null, out Vector2 local);
                Rect r = new Rect(local.x - 71, local.y - 16, 142, 32);
                Rect limits = root.rect;
                if (r.xMin < limits.xMin + 340 || r.xMax > limits.xMax - 16 || r.yMin < limits.yMin + 110 || r.yMax > limits.yMax - 16 || pinRects.Exists(other => other.Overlaps(r))) continue;
                pinRects.Add(r);
                pins[i].gameObject.SetActive(true);
                ((RectTransform)pins[i].transform).anchoredPosition = local;
            }
            selectedText.text = "Start at " + game.Stations[overview.Selected].name;
            if (overview.FocusedPlaceName == null) focusText.text = "Search a shop or pick a stop to fly there.";
            else if (overview.FocusedPlaceKind == "Tram station") focusText.text = "Showing " + overview.FocusedPlaceName + ".";
            else focusText.text = $"Showing {overview.FocusedPlaceName} ({overview.FocusedPlaceKind}).\nNearest stop is {overview.FocusedPlaceWalk:0} m away.";
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
            progress.rectTransform.sizeDelta = new Vector2(308 * game.BoardingProgress, 4);
            speed.text = $"{Mathf.Abs(game.Player.Speed) * 3.6f:0} km/h    ·    {(game.Player.TravelSign < 0 ? "Reverse" : "Forward")}    ·    Balance";
            float lean = TramController.TravelRelativeLean(game.Player.LeanAngle, game.Player.TravelSign) / game.Player.FallAngle;
            balance.rectTransform.anchoredPosition = new Vector2(166 + Mathf.Clamp(lean, -1, 1) * 150, -182);
            balance.color = Mathf.Abs(lean) > 0.7f ? new Color(0.88f, 0.24f, 0.16f) : Accent;
            mapCaption.text = game.Destination == null ? "Blue marker: your tram" : $"{game.Destination.name}  ·  {game.RemainingDistance:0} m\nOrange arrow: follow this direction";
            result.text = $"{(game.Delivered >= 3 ? "Route complete!" : game.HitPedestrian ? "You hit a pedestrian" : game.Derailed ? "Tram derailed" : "Time is up")}\n\n{game.Delivered} deliveries   ·   Score {game.Score}";
            if (routeLine.enabled)
            {
                int count = Mathf.Min(game.Route.Count, 80);
                routeLine.positionCount = count;
                for (int i = 0; i < count; i++) routeLine.SetPosition(i, game.Route[i] + Vector3.up * 0.65f);
                Vector3 screen = overview.View.WorldToScreenPoint(game.Destination.position + Vector3.up * 8);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, null, out Vector2 local);
                if (screen.z > 0 && local.x > root.rect.xMin + 380 && local.x < root.rect.xMax - 270 && Mathf.Abs(local.y) < root.rect.height * 0.5f - 90)
                {
                    destinationBadge.gameObject.SetActive(true);
                    destinationBadge.anchoredPosition = local;
                    destinationText.text = game.Destination.name + "\nStop here";
                }
            }
        }

        private void RefreshSearch(string query)
        {
            searchResults.Clear();
            bool empty = string.IsNullOrWhiteSpace(query);
            int matches = 0;
            foreach (CityPlace place in overview.Places)
            {
                if (empty ? place.stationIndex < 0 : !CityPlace.Matches(place.name, query)) continue;
                matches++;
                if (searchResults.Count < resultButtons.Count) searchResults.Add(place);
            }
            for (int i = 0; i < resultButtons.Count; i++)
            {
                Button button = resultButtons[i];
                button.gameObject.SetActive(i < searchResults.Count);
                button.onClick.RemoveAllListeners();
                if (i >= searchResults.Count) continue;
                CityPlace place = searchResults[i];
                button.GetComponentInChildren<Text>().text = place.name + "\n" + place.kind;
                button.onClick.AddListener(() => overview.FocusPlace(place));
            }
            searchContent.sizeDelta = new Vector2(276, Mathf.Max(46, searchResults.Count * 48));
            placeScroll.StopMovement();
            searchContent.anchoredPosition = Vector2.zero;
            searchSummary.text = matches == 0 ? "No places found. Try another name." : empty ? "Tram stations · search to find other places" :
                matches > resultButtons.Count ? $"Showing {resultButtons.Count} of {matches}. Refine your search." : $"{matches} places found";
        }
        private RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform; rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); return rect;
        }
        /// <summary>A full-width strip along the bottom edge that spells out the controls.</summary>
        private RectTransform ControlBar(string name, float height)
        {
            RectTransform bar = Window(name, root, 0, 0, 0, height);
            bar.anchorMin = new Vector2(0, 0); bar.anchorMax = new Vector2(1, 0);
            bar.pivot = new Vector2(0.5f, 0);
            bar.offsetMin = new Vector2(20, 16);
            bar.offsetMax = new Vector2(-20, 16 + height);
            return bar;
        }

        /// <summary>Key/action pairs, spread evenly so the strip fits any window width.</summary>
        private void ControlChips(RectTransform bar, params string[] pairs)
        {
            int count = pairs.Length / 2;
            for (int i = 0; i < count; i++)
            {
                RectTransform cell = Rect(pairs[i * 2], bar, 0, 0, 0, 0);
                cell.pivot = new Vector2(0.5f, 0.5f);
                cell.anchorMin = new Vector2(i / (float)count, 0);
                cell.anchorMax = new Vector2((i + 1) / (float)count, 1);
                cell.offsetMin = new Vector2(6, 8); cell.offsetMax = new Vector2(-6, -8);

                RectTransform plate = Rect("Key", cell, 0, 0, 0, 0);
                plate.pivot = new Vector2(0.5f, 0.5f);
                plate.anchorMin = new Vector2(0, 0.52f); plate.anchorMax = Vector2.one;
                plate.offsetMin = plate.offsetMax = Vector2.zero;
                plate.gameObject.AddComponent<Image>().color = Accent;
                Text key = Label(plate, pairs[i * 2], 0, 0, 0, 0, 15, true);
                Stretch((RectTransform)key.transform, 6, 2);
                key.alignment = TextAnchor.MiddleCenter;
                key.color = Color.white;

                Text action = Label(cell, pairs[i * 2 + 1], 0, 0, 0, 0, 12);
                RectTransform actionRect = (RectTransform)action.transform;
                actionRect.pivot = new Vector2(0.5f, 0.5f);
                actionRect.anchorMin = Vector2.zero; actionRect.anchorMax = new Vector2(1, 0.48f);
                actionRect.offsetMin = new Vector2(2, 0); actionRect.offsetMax = new Vector2(-2, 0);
                action.alignment = TextAnchor.MiddleCenter;
                action.color = new Color(0.33f, 0.36f, 0.41f);
            }
        }

        private static void Stretch(RectTransform rect, float padX, float padY)
        {
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padX, padY); rect.offsetMax = new Vector2(-padX, -padY);
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
            // Text always gives way before the layout does, so nothing is ever cut
            // off on a small window however narrow the box it has to live in.
            label.resizeTextForBestFit = true; label.resizeTextMinSize = Mathf.Min(9, size); label.resizeTextMaxSize = size;
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
