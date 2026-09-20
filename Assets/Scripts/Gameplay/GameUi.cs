using System;
using UnityEngine;

namespace SparvagnRush.Gameplay
{
    /// <summary>One safe-area coordinate system for every screen-space game interface.</summary>
    public static class GameUi
    {
        public static Rect Safe => Screen.safeArea.width > 0 ? Screen.safeArea : new Rect(0, 0, Screen.width, Screen.height);
        public static float Scale => Mathf.Max(0.01f, Mathf.Min(Safe.width / 960f, Safe.height / 640f));
        public static float Width => Safe.width / Scale;
        public static float Height => Safe.height / Scale;
        public static readonly Color Ink = new(0.065f, 0.09f, 0.10f, 0.96f);
        public static readonly Color Accent = new(0.79f, 0.69f, 0.43f);
        public static Vector2 FromScreen(Vector2 point) => new((point.x - Safe.x) / Scale, (Safe.yMax - point.y) / Scale);

        public readonly struct Scope : IDisposable
        {
            private readonly Matrix4x4 previous;
            public Scope(bool unused)
            {
                previous = GUI.matrix;
                GUI.matrix = Matrix4x4.TRS(new Vector3(Safe.x, Screen.height - Safe.yMax, 0), Quaternion.identity, Vector3.one * Scale);
            }
            public void Dispose() => GUI.matrix = previous;
        }

        public static void Fill(Rect rect, Color colour)
        {
            Color previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        public static bool Button(Rect rect, string text, GUIStyle style, bool selected = false)
        {
            bool hover = rect.Contains(Event.current.mousePosition);
            Fill(rect, selected ? new Color(0.24f, 0.27f, 0.24f) : hover ? new Color(0.18f, 0.21f, 0.21f) : new Color(0.105f, 0.13f, 0.14f));
            if (selected) Fill(new Rect(rect.x, rect.y, 3, rect.height), Accent);
            bool pressed = GUI.Button(rect, GUIContent.none, GUIStyle.none);
            GUI.Label(rect, text, style);
            return pressed;
        }
    }
}
