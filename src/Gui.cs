using UnityEngine;

namespace ChainedIsHard
{
    /// <summary>
    /// The drawing primitives the overlay and the menu are built from.
    ///
    /// Only GUI.Label and GUI.DrawTexture: GUI.Box, GUI.Button and GUI.TextField are stripped
    /// in this IL2CPP build and crash the game, so a panel is a filled rectangle and a button
    /// is a filled rectangle with a label on it.
    /// </summary>
    internal static class Gui
    {
        public static readonly Color Backdrop = new(0.04f, 0.04f, 0.06f, 0.93f);
        public static readonly Color Accent = new(0.95f, 0.78f, 0.42f);
        public static readonly Color Selection = new(0.95f, 0.78f, 0.42f, 0.2f);
        public static readonly Color Highlight = new(1f, 0.96f, 0.88f);
        public static readonly Color Normal = new(0.78f, 0.78f, 0.8f);
        public static readonly Color Dimmed = new(0.55f, 0.55f, 0.6f);

        public static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        // This interop only exposes GUIStyle's parameterless constructor, so styles are built
        // from scratch instead of copied from GUI.skin.
        public static GUIStyle Style(int fontSize, FontStyle fontStyle, Color color, TextAnchor anchor)
        {
            var style = new GUIStyle
            {
                alignment = anchor,
                fontSize = fontSize,
                fontStyle = fontStyle,
                richText = false,
                wordWrap = false,
            };

            style.font = GUI.skin.label.font;
            style.normal.textColor = color;
            return style;
        }
    }
}
