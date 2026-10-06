using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Scene tab: lists everything in loaded scenes that is linked to Grimoire —
    /// <see cref="GrimoireObjectLink"/> objects and <see cref="GrimoireTextLink"/>
    /// copy — with change badges, search, and filters for kind / changed / template.
    /// </summary>
    public class GrimoireScenePanel
    {
        private const string AllTemplatesKey = "";

        private const int KindAll = 0;
        private const int KindObjects = 1;
        private const int KindTexts = 2;

        private static readonly string[] KindLabels = { "Everything", "Objects", "Texts" };

        private static readonly Color TextBadgeColor = new Color(0.20f, 0.55f, 0.95f);

        private static readonly List<GrimoireObjectLink> LinkBuffer = new List<GrimoireObjectLink>();
        private static readonly List<GrimoireTextLink> TextLinkBuffer = new List<GrimoireTextLink>();

        private Vector2 _scroll;
        private string _search = "";
        private bool _changedOnly;
        private int _kindFilterIndex;
        private int _templateFilterIndex;
        private string[] _templateKeys = { AllTemplatesKey };
        private string[] _templateLabels = { "All templates" };
        private bool _enriching;
        private int _enrichGeneration;
        private int _clearedOverrides;

        public event Action RepaintNeeded;
        public event Action<GrimoireObjectLink> OpenObjectRequested;

        public void Activate()
        {
            GrimoireGameEngineDirtyTracker.Changed -= OnDirtyChanged;
            GrimoireGameEngineDirtyTracker.Changed += OnDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed -= OnDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed += OnDirtyChanged;
            GrimoireLinkedFieldStore.Changed -= OnDirtyChanged;
            GrimoireLinkedFieldStore.Changed += OnDirtyChanged;
            GrimoireTextLinkStore.Changed -= OnDirtyChanged;
            GrimoireTextLinkStore.Changed += OnDirtyChanged;
            GrimoireGameLanguages.Changed -= OnDirtyChanged;
            GrimoireGameLanguages.Changed += OnDirtyChanged;
            GrimoireSettings.LocaleChanged -= OnDirtyChanged;
            GrimoireSettings.LocaleChanged += OnDirtyChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Selection.selectionChanged -= OnSelectionChanged;
            Selection.selectionChanged += OnSelectionChanged;

            GrimoireGameLanguages.EnsureLoaded();
            _clearedOverrides = 0;
            RefreshTemplateOptions();
            EnsureSummariesLoaded();
            RequestRepaint();
        }

        public void Deactivate()
        {
            GrimoireGameEngineDirtyTracker.Changed -= OnDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed -= OnDirtyChanged;
            GrimoireLinkedFieldStore.Changed -= OnDirtyChanged;
            GrimoireTextLinkStore.Changed -= OnDirtyChanged;
            GrimoireGameLanguages.Changed -= OnDirtyChanged;
            GrimoireSettings.LocaleChanged -= OnDirtyChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            Selection.selectionChanged -= OnSelectionChanged;
        }

        public void Draw()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Sign in and choose a workspace first to browse linked scene objects.");
                return;
            }

            CollectSceneLinks();

            DrawLanguageBar();
            DrawFilterBar();

            RefreshTemplateOptions();

            var rows = BuildVisibleRows();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                BuildCountLabel(rows) + (_enriching ? "  ·  loading templates…" : ""),
                GrimoireEditorStyles.MiniSecondaryStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(
                    new GUIContent("Create object", "Create a draft library object in the selected game."),
                    GUILayout.Width(120)))
            {
                GrimoireCreateObjectWindow.Open(null);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (rows.Count == 0)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    LinkBuffer.Count == 0 && TextLinkBuffer.Count == 0
                        ? "No GameObjects with a Grimoire Object Link or Grimoire Text Link in loaded scenes."
                        : "Nothing matches the current search or filters.");
            }
            else
            {
                foreach (var row in rows)
                {
                    DrawRow(row);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private static void CollectSceneLinks()
        {
            GrimoireGameEngineDirtyTracker.CollectSceneLinks(LinkBuffer);
            GrimoireTextLinkStore.CollectSceneLinks(TextLinkBuffer);
        }

        private static string BuildCountLabel(List<SceneRow> rows)
        {
            if (rows.Count == 0)
            {
                return "Nothing linked";
            }

            var objects = rows.Count(r => r.Kind == SceneRowKind.Object);
            var texts = rows.Count - objects;

            var parts = new List<string>(2);
            if (objects > 0)
            {
                parts.Add($"{objects} object{(objects == 1 ? "" : "s")}");
            }

            if (texts > 0)
            {
                parts.Add($"{texts} text{(texts == 1 ? "" : "s")}");
            }

            return string.Join("  ·  ", parts);
        }

        /// <summary>
        /// One dropdown that switches every text link in the loaded scenes to a
        /// language the game actually supports.
        /// </summary>
        private void DrawLanguageBar()
        {
            if (!GrimoireEditorStyles.BeginCollapsibleSection("scene-language", "Scene language", defaultExpanded: true))
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Language", GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(64));

            EditorGUI.BeginChangeCheck();
            var picked = GrimoireLanguagePopup.Draw(
                GrimoireSettings.Locale,
                GrimoireGameLanguages.SourceLanguageLabel);
            var changed = EditorGUI.EndChangeCheck();

            using (new EditorGUI.DisabledScope(GrimoireGameLanguages.IsLoading))
            {
                if (GrimoireEditorStyles.ToolbarButton("Refresh", false, GUILayout.Width(64)))
                {
                    GrimoireGameLanguages.Refresh();
                }
            }

            EditorGUILayout.EndHorizontal();

            if (changed)
            {
                ApplySceneLanguage(picked);
            }

            var unavailable = GrimoireLanguagePopup.DescribeUnavailable();
            if (unavailable != null)
            {
                EditorGUILayout.LabelField(unavailable, EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.LabelField(DescribeSceneLanguage(), EditorStyles.wordWrappedMiniLabel);

            if (_clearedOverrides > 0)
            {
                EditorGUILayout.LabelField(
                    $"Cleared {_clearedOverrides} per-object preview " +
                    $"{(_clearedOverrides == 1 ? "language" : "languages")} so every link follows the scene.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            GrimoireEditorStyles.EndCollapsibleSection();
        }

        private void ApplySceneLanguage(string languageCode)
        {
            _clearedOverrides = GrimoireTextLinkStore.ApplySceneLanguage(languageCode);
            RequestRepaint();
        }

        private static string DescribeSceneLanguage()
        {
            var locale = GrimoireSettings.Locale;
            var shown = string.IsNullOrEmpty(locale)
                ? "source text"
                : GrimoireGameLanguages.DisplayName(locale);

            var count = TextLinkBuffer.Count;
            if (count == 0)
            {
                return $"No Grimoire Text Links in the loaded scenes yet. " +
                       $"Translatable object fields load in {shown}.";
            }

            var scenes = CountScenes();
            var where = $"{count} text {(count == 1 ? "link" : "links")}" +
                        (scenes > 1 ? $" across {scenes} loaded scenes" : "");

            return $"Showing {shown} on {where}, and on translatable object fields.";
        }

        private static int CountScenes()
        {
            var scenes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in TextLinkBuffer)
            {
                if (link != null && link.gameObject != null)
                {
                    scenes.Add(link.gameObject.scene.path ?? "");
                }
            }

            return scenes.Count;
        }

        private void DrawFilterBar()
        {
            if (!GrimoireEditorStyles.BeginCollapsibleSection("scene-filter", "Filter scene objects", defaultExpanded: true))
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Search", GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(56));
            EditorGUI.BeginChangeCheck();
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
            {
                RequestRepaint();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Show", GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(56));
            EditorGUI.BeginChangeCheck();
            _kindFilterIndex = EditorGUILayout.Popup(_kindFilterIndex, KindLabels);
            if (EditorGUI.EndChangeCheck())
            {
                RequestRepaint();
            }

            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(_kindFilterIndex == KindTexts))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Template", GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(56));
                EditorGUI.BeginChangeCheck();
                _templateFilterIndex = EditorGUILayout.Popup(_templateFilterIndex, _templateLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    RequestRepaint();
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _changedOnly = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Changed only",
                    "Show only items with unsynced game-engine, editable field, or text deviations."),
                _changedOnly);
            if (EditorGUI.EndChangeCheck())
            {
                RequestRepaint();
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_enriching))
            {
                if (GrimoireEditorStyles.ToolbarButton("Refresh", false, GUILayout.Width(64)))
                {
                    GrimoireObjectKeyResolver.InvalidateCache(GrimoireSettings.GameId);
                    GrimoireStringKeyResolver.Invalidate(GrimoireSettings.GameId);
                    EnsureSummariesLoaded(force: true);
                }
            }

            EditorGUILayout.EndHorizontal();
            GrimoireEditorStyles.EndCollapsibleSection();
        }

        private void DrawRow(SceneRow row)
        {
            var target = row.TargetObject;
            if (target == null)
            {
                return;
            }

            var selected = Selection.activeGameObject != null &&
                           Selection.activeGameObject == target;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical();

            if (GUILayout.Button(row.GameObjectName, selected ? EditorStyles.boldLabel : EditorStyles.label))
            {
                Select(target);
            }

            var detailParts = new List<string>();
            if (!string.IsNullOrEmpty(row.Code))
            {
                detailParts.Add(row.Code);
            }

            if (!string.IsNullOrEmpty(row.TemplateName))
            {
                detailParts.Add(row.TemplateName);
            }

            if (!string.IsNullOrEmpty(row.SceneName))
            {
                detailParts.Add(row.SceneName);
            }

            if (detailParts.Count > 0)
            {
                EditorGUILayout.LabelField(
                    string.Join("  ·  ", detailParts),
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }

            if (row.Kind == SceneRowKind.Text)
            {
                EditorGUILayout.LabelField(row.TextPreview, EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();

            if (row.GameEngineDirty)
            {
                DrawBadge(
                    "Engine",
                    new Color(0.95f, 0.55f, 0.15f),
                    "Unsynced game-engine data (position, rotation, scale, or name).");
            }

            if (row.FieldsDirty)
            {
                DrawBadge(
                    "Fields",
                    GrimoireEditorStyles.Purple,
                    "Editable fields on the Object Link differ from Grimoire and need sync.");
            }

            if (row.TextDirty)
            {
                DrawBadge(
                    "Text",
                    TextBadgeColor,
                    "Source text or translations differ from Grimoire. Push them on the Sync tab.");
            }

            if (GUILayout.Button(
                    new GUIContent("Select", "Select this GameObject in the hierarchy."),
                    GUILayout.Width(56),
                    GUILayout.Height(24)))
            {
                Select(target);
            }

            if (row.Kind == SceneRowKind.Object &&
                GUILayout.Button(
                    new GUIContent("Open", "Open this object in the Object tab."),
                    GUILayout.Width(52),
                    GUILayout.Height(24)))
            {
                Select(target);
                OpenObjectRequested?.Invoke(row.Link);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private static void DrawBadge(string label, Color color, string tooltip)
        {
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                padding = new RectOffset(6, 6, 2, 2),
            };

            var content = new GUIContent(label, tooltip);
            var size = style.CalcSize(content);
            var rect = GUILayoutUtility.GetRect(size.x + 4f, 18f, GUILayout.Width(size.x + 8f));
            rect.y += 3f;
            EditorGUI.DrawRect(rect, color);
            GUI.Label(rect, content, style);
        }

        private List<SceneRow> BuildVisibleRows()
        {
            var gameId = GrimoireSettings.GameId;
            // Texts have no template, so the (disabled) template popup must not
            // silently filter every row away when only texts are shown.
            var templateFilter = _kindFilterIndex == KindTexts ? AllTemplatesKey : GetSelectedTemplateKey();
            var search = string.IsNullOrWhiteSpace(_search) ? null : _search.Trim();

            var rows = new List<SceneRow>(LinkBuffer.Count + TextLinkBuffer.Count);

            if (_kindFilterIndex != KindTexts)
            {
                foreach (var link in LinkBuffer)
                {
                    if (link == null || link.gameObject == null)
                    {
                        continue;
                    }

                    if (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId))
                    {
                        continue;
                    }

                    var row = BuildObjectRow(gameId, link);
                    if (!string.IsNullOrEmpty(templateFilter) &&
                        !string.Equals(row.TemplateName, templateFilter, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (PassesFilters(row, search))
                    {
                        rows.Add(row);
                    }
                }
            }

            // Text links carry no template, so a template filter excludes them.
            if (_kindFilterIndex != KindObjects && string.IsNullOrEmpty(templateFilter))
            {
                foreach (var link in TextLinkBuffer)
                {
                    if (link == null || link.gameObject == null)
                    {
                        continue;
                    }

                    var row = BuildTextRow(link);
                    if (PassesFilters(row, search))
                    {
                        rows.Add(row);
                    }
                }
            }

            return rows
                .OrderByDescending(r => r.IsDirty)
                .ThenBy(r => r.Kind)
                .ThenBy(r => r.SceneName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.GameObjectName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool PassesFilters(SceneRow row, string search)
        {
            if (_changedOnly && !row.IsDirty)
            {
                return false;
            }

            return search == null || RowMatchesSearch(row, search);
        }

        private static SceneRow BuildObjectRow(string gameId, GrimoireObjectLink link)
        {
            string templateName = null;
            string objectName = null;
            if (GrimoireObjectKeyResolver.TryGetSummary(
                    gameId, link.CachedObjectId, link.ObjectKey, out var summary))
            {
                templateName = summary.template?.name ?? summary.template?.id;
                objectName = summary.name;
            }

            return new SceneRow
            {
                Kind = SceneRowKind.Object,
                Link = link,
                GameObjectName = link.gameObject.name,
                Code = link.ObjectKey,
                ObjectName = objectName,
                TemplateName = templateName ?? "",
                SceneName = GrimoireGameEngineSync.GetSceneName(link.gameObject),
                GameEngineDirty = GrimoireGameEngineDirtyTracker.IsGameEngineDirty(link),
                FieldsDirty = GrimoireEditableFieldsRenderer.HasDirtyEditsForLink(link),
            };
        }

        private static SceneRow BuildTextRow(GrimoireTextLink link)
        {
            var locale = GrimoireTextLinkStore.ResolvePreviewLanguage(link);

            return new SceneRow
            {
                Kind = SceneRowKind.Text,
                TextLink = link,
                GameObjectName = link.gameObject.name,
                Code = link.TextCode,
                SceneName = GrimoireGameEngineSync.GetSceneName(link.gameObject),
                ResolvedText = link.GetResolvedText(locale),
                TextPreview = BuildTextPreview(link.GetPreviewText(locale), locale),
                TextDirty = GrimoireTextLinkStore.HasDeviations(link),
            };
        }

        private static string BuildTextPreview(string preview, string locale)
        {
            if (string.IsNullOrEmpty(preview))
            {
                return "(no copy loaded — refresh the Text Link inspector)";
            }

            var prefix = string.IsNullOrEmpty(locale) ? "" : $"[{locale}] ";
            if (string.Equals(preview, GrimoireTextLink.NotTranslatedPlaceholder, StringComparison.Ordinal))
            {
                return prefix + GrimoireTextLink.NotTranslatedPlaceholder;
            }

            var text = preview.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 90)
            {
                text = text.Substring(0, 87) + "…";
            }

            return $"{prefix}“{text}”";
        }

        private static bool RowMatchesSearch(SceneRow row, string search)
        {
            return Contains(row.GameObjectName, search) ||
                   Contains(row.Code, search) ||
                   Contains(row.ObjectName, search) ||
                   Contains(row.TemplateName, search) ||
                   Contains(row.SceneName, search) ||
                   Contains(row.ResolvedText, search);
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private string GetSelectedTemplateKey()
        {
            if (_templateFilterIndex < 0 || _templateFilterIndex >= _templateKeys.Length)
            {
                return AllTemplatesKey;
            }

            return _templateKeys[_templateFilterIndex] ?? AllTemplatesKey;
        }

        private void RefreshTemplateOptions()
        {
            var gameId = GrimoireSettings.GameId;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in LinkBuffer)
            {
                if (link == null)
                {
                    continue;
                }

                if (GrimoireObjectKeyResolver.TryGetSummary(
                        gameId, link.CachedObjectId, link.ObjectKey, out var summary))
                {
                    var name = summary.template?.name ?? summary.template?.id;
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
            }

            var ordered = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var keys = new string[ordered.Count + 1];
            var labels = new string[ordered.Count + 1];
            keys[0] = AllTemplatesKey;
            labels[0] = "All templates";
            for (var i = 0; i < ordered.Count; i++)
            {
                keys[i + 1] = ordered[i];
                labels[i + 1] = ordered[i];
            }

            var previous = GetSelectedTemplateKey();
            _templateKeys = keys;
            _templateLabels = labels;
            _templateFilterIndex = Array.FindIndex(_templateKeys, k => k == previous);
            if (_templateFilterIndex < 0)
            {
                _templateFilterIndex = 0;
            }
        }

        private void EnsureSummariesLoaded(bool force = false)
        {
            if (!GrimoireSettings.IsConfigured)
            {
                return;
            }

            if (_enriching && !force)
            {
                return;
            }

            LoadSummariesAsync(GrimoireSettings.GameId);
        }

        private async void LoadSummariesAsync(string gameId)
        {
            var generation = ++_enrichGeneration;
            _enriching = true;
            RequestRepaint();

            await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (generation != _enrichGeneration)
            {
                return;
            }

            await GrimoireObjectKeyResolver.EnsureLibraryCachedAsync(gameId);
            if (generation != _enrichGeneration)
            {
                return;
            }

            _enriching = false;
            CollectSceneLinks();
            RefreshTemplateOptions();
            RequestRepaint();
        }

        private static void Select(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            Selection.activeGameObject = gameObject;
            EditorGUIUtility.PingObject(gameObject);
        }

        private void OnDirtyChanged() => RequestRepaint();

        private void OnHierarchyChanged()
        {
            // The note counts links that existed when the language was applied.
            _clearedOverrides = 0;
            RequestRepaint();
        }

        private void OnSelectionChanged() => RequestRepaint();

        private void RequestRepaint() => RepaintNeeded?.Invoke();

        private enum SceneRowKind
        {
            Object = 0,
            Text = 1,
        }

        private sealed class SceneRow
        {
            public SceneRowKind Kind;
            public GrimoireObjectLink Link;
            public GrimoireTextLink TextLink;
            public string GameObjectName;
            public string Code;
            public string ObjectName;
            public string TemplateName;
            public string SceneName;
            public string ResolvedText;
            public string TextPreview;
            public bool GameEngineDirty;
            public bool FieldsDirty;
            public bool TextDirty;

            public bool IsDirty => GameEngineDirty || FieldsDirty || TextDirty;

            public GameObject TargetObject
            {
                get
                {
                    if (Kind == SceneRowKind.Object)
                    {
                        return Link != null ? Link.gameObject : null;
                    }

                    return TextLink != null ? TextLink.gameObject : null;
                }
            }
        }
    }
}
