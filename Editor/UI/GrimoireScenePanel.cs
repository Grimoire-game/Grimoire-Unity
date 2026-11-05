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

        private static readonly Color TextBadgeColor = new Color(0.20f, 0.55f, 0.95f);

        private static readonly List<GrimoireObjectLink> LinkBuffer = new List<GrimoireObjectLink>();
        private static readonly List<GrimoireTextLink> TextLinkBuffer = new List<GrimoireTextLink>();

        private Vector2 _scroll;
        private string _search = "";
        private bool _changedOnly;
        private readonly HashSet<string> _selectedKinds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _selectedTemplates = new HashSet<string>(StringComparer.Ordinal);
        private string[] _templateKeys = { AllTemplatesKey };
        private bool _enriching;
        private int _enrichGeneration;

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
            GrimoireGameLanguagePreview.Changed -= OnDirtyChanged;
            GrimoireGameLanguagePreview.Changed += OnDirtyChanged;
            GrimoireSettings.LocaleChanged -= OnDirtyChanged;
            GrimoireSettings.LocaleChanged += OnDirtyChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Selection.selectionChanged -= OnSelectionChanged;
            Selection.selectionChanged += OnSelectionChanged;

            GrimoireGameLanguages.EnsureLoaded();
            GrimoireGameLanguagePreview.EnsureLoaded();
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
            GrimoireGameLanguagePreview.Changed -= OnDirtyChanged;
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

            GrimoireWorkflowGUI.Header("Scene", "Find Grimoire-linked objects and text in the loaded Unity scenes.");
            DrawGameLanguageLabel();
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

        private static void DrawGameLanguageLabel()
        {
            var locale = GrimoireSettings.Locale;
            var shown = string.IsNullOrEmpty(locale)
                ? GrimoireGameLanguages.SourceLanguageLabel
                : GrimoireGameLanguages.DisplayName(locale);
            EditorGUILayout.LabelField("Game language: " + shown + "  ·  Change in Settings",
                GrimoireEditorStyles.MiniSecondaryStyle);
            if (GrimoireGameLanguagePreview.IsLoading)
                EditorGUILayout.LabelField("Refreshing translated scene content from Grimoire…",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            else if (!string.IsNullOrEmpty(GrimoireGameLanguagePreview.Error))
                GrimoireEditorStyles.DrawErrorBox(GrimoireGameLanguagePreview.Error);
            EditorGUILayout.Space(4);
        }

        private void DrawFilterBar()
        {
            _search = GrimoireWorkflowGUI.SearchAndFilters(_search, ShowFilterMenu,
                _selectedKinds.Count + _selectedTemplates.Count + (_changedOnly ? 1 : 0), () =>
                {
                    GrimoireObjectKeyResolver.InvalidateCache(GrimoireSettings.GameId);
                    GrimoireStringKeyResolver.Invalidate(GrimoireSettings.GameId);
                    EnsureSummariesLoaded(force: true);
                }, _enriching);
        }

        private void ShowFilterMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("All scene content"),
                _selectedKinds.Count == 0 && _selectedTemplates.Count == 0 && !_changedOnly, () =>
            {
                _selectedKinds.Clear();
                _selectedTemplates.Clear();
                _changedOnly = false;
                RequestRepaint();
            });
            menu.AddSeparator("");
            GrimoireWorkflowGUI.AddMultiChoice(menu, "Type/Objects", _selectedKinds, "Objects", RequestRepaint);
            GrimoireWorkflowGUI.AddMultiChoice(menu, "Type/Text links", _selectedKinds, "Texts", RequestRepaint);
            if (_templateKeys.Length > 1)
            {
                menu.AddSeparator("");
                foreach (var template in _templateKeys.Where(key => !string.IsNullOrEmpty(key)))
                    GrimoireWorkflowGUI.AddMultiChoice(menu, "Template/" + template.Replace('/', '⁄'),
                        _selectedTemplates, template, RequestRepaint);
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Changed only"), _changedOnly, () =>
            {
                _changedOnly = !_changedOnly;
                RequestRepaint();
            });
            menu.ShowAsContext();
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
            var search = string.IsNullOrWhiteSpace(_search) ? null : _search.Trim();

            var rows = new List<SceneRow>(LinkBuffer.Count + TextLinkBuffer.Count);

            if (_selectedKinds.Count == 0 || _selectedKinds.Contains("Objects"))
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
                    if (_selectedTemplates.Count > 0 && !_selectedTemplates.Contains(row.TemplateName))
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
            if ((_selectedKinds.Count == 0 || _selectedKinds.Contains("Texts")) &&
                _selectedTemplates.Count == 0)
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

            if (link.Snapshot != null)
                objectName = link.Snapshot.GetDisplayName(GrimoireSettings.Locale);

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
            keys[0] = AllTemplatesKey;
            for (var i = 0; i < ordered.Count; i++)
            {
                keys[i + 1] = ordered[i];
            }
            _templateKeys = keys;
            _selectedTemplates.RemoveWhere(template => !ordered.Contains(template));
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
