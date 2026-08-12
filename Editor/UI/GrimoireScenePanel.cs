using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Scene tab: lists every <see cref="GrimoireObjectLink"/> in loaded scenes,
    /// with change badges, search, and filters for changed / template.
    /// </summary>
    public class GrimoireScenePanel
    {
        private const string AllTemplatesKey = "";

        private static readonly List<GrimoireObjectLink> LinkBuffer = new List<GrimoireObjectLink>();

        private Vector2 _scroll;
        private string _search = "";
        private bool _changedOnly;
        private int _templateFilterIndex;
        private string[] _templateKeys = { AllTemplatesKey };
        private string[] _templateLabels = { "All templates" };
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
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Selection.selectionChanged -= OnSelectionChanged;
            Selection.selectionChanged += OnSelectionChanged;

            RefreshTemplateOptions();
            EnsureSummariesLoaded();
            RequestRepaint();
        }

        public void Deactivate()
        {
            GrimoireGameEngineDirtyTracker.Changed -= OnDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed -= OnDirtyChanged;
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

            DrawFilterBar();

            GrimoireGameEngineDirtyTracker.CollectSceneLinks(LinkBuffer);
            RefreshTemplateOptions();

            var rows = BuildVisibleRows(LinkBuffer);
            EditorGUILayout.LabelField(
                rows.Count == 0
                    ? "No linked objects"
                    : $"{rows.Count} linked object{(rows.Count == 1 ? "" : "s")}" +
                      (_enriching ? "  ·  loading templates…" : ""),
                GrimoireEditorStyles.MiniSecondaryStyle);
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (rows.Count == 0)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    LinkBuffer.Count == 0
                        ? "No GameObjects with a Grimoire Object Link in loaded scenes."
                        : "No linked objects match the current search or filters.");
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
            EditorGUILayout.LabelField("Template", GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(56));
            EditorGUI.BeginChangeCheck();
            _templateFilterIndex = EditorGUILayout.Popup(_templateFilterIndex, _templateLabels);
            if (EditorGUI.EndChangeCheck())
            {
                RequestRepaint();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _changedOnly = EditorGUILayout.ToggleLeft(
                new GUIContent("Changed only", "Show only objects with unsynced game-engine or editable field changes."),
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
                    EnsureSummariesLoaded(force: true);
                }
            }

            EditorGUILayout.EndHorizontal();
            GrimoireEditorStyles.EndCollapsibleSection();
        }

        private void DrawRow(SceneRow row)
        {
            var link = row.Link;
            var selected = Selection.activeGameObject != null &&
                           Selection.activeGameObject == link.gameObject;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical();

            var title = link.gameObject.name;
            if (GUILayout.Button(title, selected ? EditorStyles.boldLabel : EditorStyles.label))
            {
                SelectLink(link);
            }

            var detailParts = new List<string>();
            if (!string.IsNullOrEmpty(row.ObjectKey))
            {
                detailParts.Add(row.ObjectKey);
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
                    "Unsaved editable field changes in the Object tab.");
            }

            if (GUILayout.Button(
                    new GUIContent("Select", "Select this GameObject in the hierarchy."),
                    GUILayout.Width(56),
                    GUILayout.Height(24)))
            {
                SelectLink(link);
            }

            if (GUILayout.Button(
                    new GUIContent("Open", "Open this object in the Object tab."),
                    GUILayout.Width(52),
                    GUILayout.Height(24)))
            {
                SelectLink(link);
                OpenObjectRequested?.Invoke(link);
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

        private List<SceneRow> BuildVisibleRows(List<GrimoireObjectLink> links)
        {
            var gameId = GrimoireSettings.GameId;
            var templateFilter = GetSelectedTemplateKey();
            var search = string.IsNullOrWhiteSpace(_search) ? null : _search.Trim();

            var rows = new List<SceneRow>(links.Count);
            foreach (var link in links)
            {
                if (link == null || link.gameObject == null)
                {
                    continue;
                }

                if (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId))
                {
                    continue;
                }

                var row = BuildRow(gameId, link);
                if (_changedOnly && !row.GameEngineDirty && !row.FieldsDirty)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(templateFilter) &&
                    !string.Equals(row.TemplateName, templateFilter, StringComparison.Ordinal))
                {
                    continue;
                }

                if (search != null && !RowMatchesSearch(row, search))
                {
                    continue;
                }

                rows.Add(row);
            }

            return rows
                .OrderByDescending(r => r.GameEngineDirty || r.FieldsDirty)
                .ThenBy(r => r.SceneName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.GameObjectName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static SceneRow BuildRow(string gameId, GrimoireObjectLink link)
        {
            string templateName = null;
            string objectName = null;
            if (GrimoireObjectKeyResolver.TryGetSummary(
                    gameId, link.CachedObjectId, link.ObjectKey, out var summary))
            {
                templateName = summary.template?.name ?? summary.template?.id;
                objectName = summary.name;
            }

            var geDirty = GrimoireGameEngineDirtyTracker.IsGameEngineDirty(link);
            var fieldsDirty = GrimoireEditableFieldsRenderer.HasDirtyEditsForObject(link.CachedObjectId);

            return new SceneRow
            {
                Link = link,
                GameObjectName = link.gameObject.name,
                ObjectKey = link.ObjectKey,
                ObjectName = objectName,
                TemplateName = templateName ?? "",
                SceneName = GrimoireGameEngineSync.GetSceneName(link.gameObject),
                GameEngineDirty = geDirty,
                FieldsDirty = fieldsDirty,
            };
        }

        private static bool RowMatchesSearch(SceneRow row, string search)
        {
            return Contains(row.GameObjectName, search) ||
                   Contains(row.ObjectKey, search) ||
                   Contains(row.ObjectName, search) ||
                   Contains(row.TemplateName, search) ||
                   Contains(row.SceneName, search);
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
            GrimoireGameEngineDirtyTracker.CollectSceneLinks(LinkBuffer);
            RefreshTemplateOptions();
            RequestRepaint();
        }

        private static void SelectLink(GrimoireObjectLink link)
        {
            if (link == null || link.gameObject == null)
            {
                return;
            }

            Selection.activeGameObject = link.gameObject;
            EditorGUIUtility.PingObject(link.gameObject);
        }

        private void OnDirtyChanged() => RequestRepaint();

        private void OnHierarchyChanged() => RequestRepaint();

        private void OnSelectionChanged() => RequestRepaint();

        private void RequestRepaint() => RepaintNeeded?.Invoke();

        private struct SceneRow
        {
            public GrimoireObjectLink Link;
            public string GameObjectName;
            public string ObjectKey;
            public string ObjectName;
            public string TemplateName;
            public string SceneName;
            public bool GameEngineDirty;
            public bool FieldsDirty;
        }
    }
}
