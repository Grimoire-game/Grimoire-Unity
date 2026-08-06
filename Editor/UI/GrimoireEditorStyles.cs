using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Shared Grimoire brand styling for editor windows (tabs, sections, cards).
    /// Matches the login card palette in <see cref="GrimoireLoginUi"/>.
    /// </summary>
    public static class GrimoireEditorStyles
    {
        public static readonly Color Purple = new Color(0.431f, 0.208f, 1f);
        public static readonly Color Teal = new Color(0.188f, 0.890f, 0.553f);
        public static readonly Color TextPrimary = new Color(0.09f, 0.09f, 0.11f);
        public static readonly Color TextSecondary = new Color(0.42f, 0.45f, 0.50f);
        public static readonly Color SectionBorder = new Color(0.82f, 0.84f, 0.87f);
        public static readonly Color SectionBackground = new Color(0.99f, 0.99f, 1f);
        public static readonly Color TabInactiveBackground = new Color(0.94f, 0.95f, 0.97f);

        private static readonly Dictionary<string, bool> SectionExpanded = new Dictionary<string, bool>();

        private static Texture2D _purpleTexture;
        private static Texture2D _purpleHoverTexture;
        private static Texture2D _whiteTexture;
        private static Texture2D _tabInactiveTexture;

        private static GUIStyle _tabActiveStyle;
        private static GUIStyle _tabInactiveStyle;
        private static GUIStyle _sectionHeaderStyle;
        private static GUIStyle _sectionBoxStyle;
        private static GUIStyle _toolbarButtonStyle;
        private static GUIStyle _toolbarButtonActiveStyle;
        private static GUIStyle _primaryButtonStyle;
        private static GUIStyle _fieldLabelStyle;
        private static GUIStyle _miniSecondaryStyle;
        private static GUIStyle _titleStyle;

        public static void EnsureStyles()
        {
            _whiteTexture ??= MakeSolidTexture(Color.white);
            _purpleTexture ??= MakeSolidTexture(Purple);
            _purpleHoverTexture ??= MakeSolidTexture(Purple * 1.08f);
            _tabInactiveTexture ??= MakeSolidTexture(TabInactiveBackground);

            if (_tabActiveStyle != null)
            {
                return;
            }

            _tabActiveStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                fixedHeight = 32f,
                margin = new RectOffset(2, 2, 0, 0),
                padding = new RectOffset(16, 16, 6, 6),
                normal = { background = _purpleTexture, textColor = Color.white },
                hover = { background = _purpleHoverTexture, textColor = Color.white },
                active = { background = _purpleHoverTexture, textColor = Color.white },
                focused = { background = _purpleTexture, textColor = Color.white },
            };

            _tabInactiveStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                fixedHeight = 32f,
                margin = new RectOffset(2, 2, 0, 0),
                padding = new RectOffset(16, 16, 6, 6),
                normal = { background = _tabInactiveTexture, textColor = TextSecondary },
                hover = { background = _whiteTexture, textColor = TextPrimary },
                active = { background = _whiteTexture, textColor = TextPrimary },
                focused = { background = _tabInactiveTexture, textColor = TextSecondary },
            };

            _sectionHeaderStyle = new GUIStyle(EditorStyles.foldout)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(18, 4, 4, 4),
                normal = { textColor = TextPrimary },
                onNormal = { textColor = TextPrimary },
                focused = { textColor = Purple },
                onFocused = { textColor = Purple },
            };

            _sectionBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 6, 10),
                margin = new RectOffset(0, 0, 4, 4),
            };

            _toolbarButtonStyle = new GUIStyle(EditorStyles.toolbarButton)
            {
                fontSize = 11,
                normal = { textColor = TextSecondary },
                hover = { textColor = Purple },
                active = { textColor = Purple },
            };

            _toolbarButtonActiveStyle = new GUIStyle(_toolbarButtonStyle)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = Purple },
            };

            _primaryButtonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                fixedHeight = 28f,
                padding = new RectOffset(12, 12, 4, 4),
                normal = { background = _purpleTexture, textColor = Color.white },
                hover = { background = _purpleHoverTexture, textColor = Color.white },
                active = { background = _purpleHoverTexture, textColor = Color.white },
            };

            _fieldLabelStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                normal = { textColor = TextPrimary },
            };

            _miniSecondaryStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = TextSecondary },
            };

            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                normal = { textColor = TextPrimary },
            };
        }

        public static GUIStyle TitleStyle
        {
            get
            {
                EnsureStyles();
                return _titleStyle;
            }
        }

        public static GUIStyle FieldLabelStyle
        {
            get
            {
                EnsureStyles();
                return _fieldLabelStyle;
            }
        }

        public static GUIStyle MiniSecondaryStyle
        {
            get
            {
                EnsureStyles();
                return _miniSecondaryStyle;
            }
        }

        public static GUIStyle PrimaryButtonStyle
        {
            get
            {
                EnsureStyles();
                return _primaryButtonStyle;
            }
        }

        /// <summary>Segmented tab bar with purple active state.</summary>
        public static int DrawTabBar(int selected, string[] labels)
        {
            EnsureStyles();

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.Space(8);

            for (var i = 0; i < labels.Length; i++)
            {
                var style = i == selected ? _tabActiveStyle : _tabInactiveStyle;
                if (GUILayout.Button(labels[i], style, GUILayout.MinWidth(88)))
                {
                    selected = i;
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);

            DrawAccentLine();
            EditorGUILayout.Space(6);
            return selected;
        }

        public static void BeginContentArea()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical();
        }

        public static void EndContentArea()
        {
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Toolbar button; pass active=true for toggled-on appearance (e.g. Settings).</summary>
        public static bool ToolbarButton(string label, bool active = false, params GUILayoutOption[] options)
        {
            EnsureStyles();
            var style = active ? _toolbarButtonActiveStyle : _toolbarButtonStyle;
            return GUILayout.Button(label, style, options);
        }

        /// <summary>
        /// Collapsible section header. Returns true when expanded; call <see cref="EndCollapsibleSection"/> to close.
        /// </summary>
        public static bool BeginCollapsibleSection(string id, string title, bool defaultExpanded = true)
        {
            EnsureStyles();

            if (!SectionExpanded.TryGetValue(id, out var expanded))
            {
                expanded = defaultExpanded;
                SectionExpanded[id] = expanded;
            }

            EditorGUILayout.BeginVertical(_sectionBoxStyle);

            var headerRect = GUILayoutUtility.GetRect(GUIContent.none, _sectionHeaderStyle, GUILayout.Height(24f));
            SectionExpanded[id] = EditorGUI.Foldout(headerRect, expanded, title, true, _sectionHeaderStyle);

            if (!SectionExpanded[id])
            {
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
                return false;
            }

            EditorGUILayout.Space(2);
            return true;
        }

        public static void EndCollapsibleSection()
        {
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        public static void DrawInfoBox(string message)
        {
            EnsureStyles();
            EditorGUILayout.BeginVertical(_sectionBoxStyle);
            EditorGUILayout.LabelField(message, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        public static void DrawErrorBox(string message)
        {
            EnsureStyles();
            var bg = new Color(0.99f, 0.89f, 0.89f);
            var border = new Color(0.94f, 0.27f, 0.27f);
            var textStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                normal = { textColor = new Color(0.67f, 0.12f, 0.12f) },
            };

            var width = EditorGUIUtility.currentViewWidth - 24f;
            var height = textStyle.CalcHeight(new GUIContent(message), width) + 16f;
            var rect = EditorGUILayout.GetControlRect(false, height);
            EditorGUI.DrawRect(rect, bg);
            DrawBorder(rect, border);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 12f), message, textStyle);
            EditorGUILayout.Space(4);
        }

        public static void BeginToolbar()
        {
            EnsureStyles();
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        }

        public static void EndToolbar()
        {
            EditorGUILayout.EndHorizontal();
        }

        public static void DrawToolbarBreadcrumb(string company, string game, string extra = null)
        {
            EnsureStyles();
            GUILayout.Label(company, _titleStyle);
            GUILayout.Label("·", _miniSecondaryStyle);
            GUILayout.Label(game, EditorStyles.label);

            if (!string.IsNullOrEmpty(extra))
            {
                GUILayout.Label("·", _miniSecondaryStyle);
                GUILayout.Label(extra, EditorStyles.label);
            }
        }

        private static void DrawAccentLine()
        {
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.Height(2f));
            var left = rect;
            left.width = rect.width * 0.55f;
            EditorGUI.DrawRect(left, Purple);

            var right = rect;
            right.x = left.xMax;
            right.width = rect.width - left.width;
            EditorGUI.DrawRect(right, Teal);
        }

        private static void DrawBorder(Rect rect, Color color, float thickness = 1f)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Repeat,
            };

            texture.SetPixels(new[] { color, color, color, color });
            texture.Apply();
            return texture;
        }
    }
}
