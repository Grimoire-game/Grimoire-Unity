using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>Shared IMGUI layout for the workflow views hosted in the dockable window.</summary>
    internal static class GrimoireWorkflowGUI
    {
        private static GUIStyle _title;
        private static GUIStyle _subtitle;
        private static GUIStyle _rowTitle;
        private static GUIStyle _rowMeta;
        private static GUIStyle _rowSide;
        private static GUIStyle _section;
        private static GUIStyle _detailTitle;
        private static bool _wasDark;

        public static bool IsNarrow(float width) => width < 690f;

        public static void Header(string title, string subtitle)
        {
            EnsureStyles();
            EditorGUILayout.LabelField(title, _title, GUILayout.Height(27f));
            EditorGUILayout.LabelField(subtitle, _subtitle, GUILayout.MinHeight(20f));
            GUILayout.Space(8f);
        }

        public static void Section(string title)
        {
            EnsureStyles();
            GUILayout.Space(8f);
            EditorGUILayout.LabelField(title.ToUpperInvariant(), _section, GUILayout.Height(23f));
        }

        public static bool Row(string title, string metadata, string side, string status, bool selected)
        {
            EnsureStyles();
            var rect = GUILayoutUtility.GetRect(10f, 63f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, selected ? ColorSelected : ColorRow);
                EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), ColorBorder);
                if (selected) EditorGUI.DrawRect(new Rect(rect.x, rect.y, 3f, rect.height), ColorAccent);
            }

            var sideWidth = Mathf.Min(126f, rect.width * 0.33f);
            var contentWidth = Mathf.Max(50f, rect.width - sideWidth - 28f);
            GUI.Label(new Rect(rect.x + 13f, rect.y + 9f, contentWidth, 20f), title ?? "", _rowTitle);
            GUI.Label(new Rect(rect.x + 13f, rect.y + 31f, contentWidth, 18f), metadata ?? "", _rowMeta);
            GUI.Label(new Rect(rect.xMax - sideWidth - 10f, rect.y + 8f, sideWidth, 20f), side ?? "", _rowSide);
            GUI.Label(new Rect(rect.xMax - sideWidth - 10f, rect.y + 32f, sideWidth, 16f), status ?? "", _rowMeta);
            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }

        public static void DetailHeading(string kind, string title, string key)
        {
            EnsureStyles();
            EditorGUILayout.LabelField(kind.ToUpperInvariant(), _section, GUILayout.Height(18f));
            EditorGUILayout.LabelField(title ?? "", _detailTitle, GUILayout.MinHeight(24f));
            if (!string.IsNullOrEmpty(key)) EditorGUILayout.LabelField(key, _rowMeta, GUILayout.MinHeight(19f));
            GUILayout.Space(9f);
        }

        public static void DetailLine(string label, string value)
        {
            EnsureStyles();
            var rect = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), ColorBorder);
            GUI.Label(new Rect(rect.x + 5f, rect.y + 6f, rect.width * 0.45f, 20f), label, _rowMeta);
            GUI.Label(new Rect(rect.x + rect.width * 0.47f, rect.y + 6f, rect.width * 0.51f, 20f), value ?? "—", _rowSide);
        }

        public static void Note(string text)
        {
            GUILayout.Space(9f);
            EditorGUILayout.HelpBox(text, MessageType.Info);
        }

        private static void EnsureStyles()
        {
            var dark = EditorGUIUtility.isProSkin;
            if (_title != null && _wasDark == dark) return;
            _wasDark = dark;
            var main = dark ? new Color(0.96f, 0.95f, 0.98f) : new Color(0.17f, 0.15f, 0.21f);
            var muted = dark ? new Color(0.68f, 0.69f, 0.75f) : new Color(0.39f, 0.40f, 0.44f);
            var accent = dark ? new Color(0.76f, 0.66f, 0.96f) : new Color(0.40f, 0.27f, 0.69f);
            _title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, normal = { textColor = main } };
            _subtitle = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { normal = { textColor = muted } };
            _rowTitle = new GUIStyle(EditorStyles.boldLabel) { clipping = TextClipping.Clip, normal = { textColor = main } };
            _rowMeta = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip, normal = { textColor = muted } };
            _rowSide = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Clip, normal = { textColor = main } };
            _section = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = accent } };
            _detailTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15, wordWrap = true, normal = { textColor = main } };
        }

        private static Color ColorRow => EditorGUIUtility.isProSkin ? new Color(0.16f, 0.17f, 0.20f) : new Color(0.95f, 0.95f, 0.96f);
        private static Color ColorSelected => EditorGUIUtility.isProSkin ? new Color(0.25f, 0.21f, 0.33f) : new Color(0.87f, 0.83f, 0.94f);
        private static Color ColorBorder => EditorGUIUtility.isProSkin ? new Color(0.27f, 0.27f, 0.31f) : new Color(0.80f, 0.80f, 0.83f);
        private static Color ColorAccent => EditorGUIUtility.isProSkin ? new Color(0.67f, 0.52f, 0.97f) : new Color(0.42f, 0.29f, 0.77f);
    }
}
