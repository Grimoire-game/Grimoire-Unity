using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    public partial class GrimoireRuntimePanel
    {
        private void DrawLiveOverview(float width, float height)
        {
            GrimoireWorkflowGUI.Header("Runtime  ·  LIVE",
                "All Grimoire-linked values and their observed changes in this Play Mode session.");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(_capturePaused ? "● Paused · Play Mode" : "● Capturing · Play Mode",
                GrimoireEditorStyles.MiniSecondaryStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_capturePaused ? "Resume capture" : "Pause capture", GUILayout.Width(105f)))
                _capturePaused = !_capturePaused;
            if (GUILayout.Button("Clear history", GUILayout.Width(90f)))
            {
                _liveChanges.Clear();
                _selectedLiveChange = null;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                $"{_liveChanges.Count} changes   ·   " +
                $"{CountLiveCategory("Objects")} objects   ·   {CountLiveCategory("Variables")} variables   ·   " +
                $"{CountLiveCategory("Dialogs")} dialogs   ·   {CountLiveCategory("Logic")} logic   ·   " +
                $"{CountLiveCategory("Text links")} text links   ·   {CountLiveCategory("Scene links")} scene links",
                GrimoireEditorStyles.MiniSecondaryStyle);
            GUILayout.Space(7f);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(!_showLinkedNow && !_showAdvancedInspector, "Changes", EditorStyles.toolbarButton, GUILayout.Width(72f)))
            {
                _showLinkedNow = false;
                _showAdvancedInspector = false;
            }
            if (GUILayout.Toggle(_showLinkedNow && !_showAdvancedInspector, "Linked now", EditorStyles.toolbarButton, GUILayout.Width(82f)))
            {
                _showLinkedNow = true;
                _showAdvancedInspector = false;
            }
            if (GUILayout.Toggle(_showAdvancedInspector, "Inspector", EditorStyles.toolbarButton, GUILayout.Width(75f)))
                _showAdvancedInspector = true;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh now", GUILayout.Width(85f)))
            {
                RefreshData();
                CaptureLiveSnapshot();
            }
            EditorGUILayout.EndHorizontal();

            if (_showAdvancedInspector) return;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Search", GUILayout.Width(44f));
            _liveSearch = EditorGUILayout.TextField(_liveSearch ?? "", GUILayout.MinWidth(90f));
            _liveCategoryIndex = EditorGUILayout.Popup(_liveCategoryIndex, LiveCategories, GUILayout.Width(130f));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(_showLinkedNow
                ? "Current values include linked data that has not changed this session."
                : "Newest change first · previous and new values are consecutive live observations.",
                GrimoireEditorStyles.MiniSecondaryStyle);
            GUILayout.Space(7f);

            var paneHeight = Mathf.Max(230f, height - 245f);
            var narrow = GrimoireWorkflowGUI.IsNarrow(width);
            if (!narrow) EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(narrow ? GUILayout.ExpandWidth(true) : GUILayout.Width(Mathf.Max(230f, width * 0.57f)));
            _liveListScroll = EditorGUILayout.BeginScrollView(_liveListScroll, GUILayout.Height(narrow ? 220f : paneHeight));
            if (_showLinkedNow) DrawLiveInventoryRows();
            else DrawLiveChangeRows();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandWidth(true));
            _liveDetailScroll = EditorGUILayout.BeginScrollView(_liveDetailScroll,
                GUILayout.Height(narrow ? Mathf.Max(220f, paneHeight - 220f) : paneHeight));
            if (_showLinkedNow) DrawLiveValueDetail();
            else DrawLiveChangeDetail();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            if (!narrow) EditorGUILayout.EndHorizontal();
        }

        private int CountLiveCategory(string category) =>
            _liveValues.Values.Count(value => value.Category == category);

        private bool MatchesLiveFilter(string category, string name, string path, string value, string source)
        {
            if (_liveCategoryIndex > 0 && category != LiveCategories[_liveCategoryIndex]) return false;
            if (string.IsNullOrWhiteSpace(_liveSearch)) return true;
            var query = _liveSearch.Trim();
            return (name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (path ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (value ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (source ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawLiveChangeRows()
        {
            var changes = _liveChanges.Where(change => MatchesLiveFilter(change.Category,
                change.Name, change.Path, change.Before + " " + change.After, change.Source)).ToList();
            GrimoireWorkflowGUI.Section($"Live change feed · {changes.Count}");
            if (changes.Count == 0)
            {
                GrimoireEditorStyles.DrawInfoBox(_snapshotPending
                    ? "Waiting for the Grimoire runtime to initialize…"
                    : "No changes recorded yet. Linked values remain visible under Linked now.");
                return;
            }
            if (_selectedLiveChange == null || !changes.Contains(_selectedLiveChange))
                _selectedLiveChange = changes[0];
            foreach (var change in changes)
            {
                if (GrimoireWorkflowGUI.Row(change.Name,
                        change.Path + " · " + change.Source,
                        change.After,
                        change.Time.ToString("HH:mm:ss.fff"),
                        change == _selectedLiveChange))
                    _selectedLiveChange = change;
            }
        }

        private void DrawLiveInventoryRows()
        {
            var values = _liveValues.Values.Where(value => MatchesLiveFilter(value.Category,
                    value.Name, value.Path, value.Value, value.Source))
                .OrderBy(value => Array.IndexOf(LiveCategories, value.Category))
                .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToList();
            GrimoireWorkflowGUI.Section($"Current linked values · {values.Count}");
            if (values.Count == 0)
            {
                GrimoireEditorStyles.DrawInfoBox(_snapshotPending
                    ? "Waiting for the Grimoire runtime to initialize…"
                    : "No linked values detected. Add an Object Link, Text Link, dialog player, or import variables.");
                return;
            }
            if (!values.Any(value => value.Id == _selectedLiveId))
                _selectedLiveId = values[0].Id;
            string previousCategory = null;
            foreach (var value in values)
            {
                if (value.Category != previousCategory)
                {
                    GrimoireWorkflowGUI.Section(value.Category);
                    previousCategory = value.Category;
                }
                if (GrimoireWorkflowGUI.Row(value.Name, value.Path + " · " + value.Source,
                        value.Value, value.LastChanged == default ? "Unchanged this session" :
                            "Changed " + value.LastChanged.ToString("HH:mm:ss"),
                        value.Id == _selectedLiveId))
                    _selectedLiveId = value.Id;
            }
        }

        private void DrawLiveChangeDetail()
        {
            var visible = _liveChanges.Where(item => MatchesLiveFilter(item.Category,
                item.Name, item.Path, item.Before + " " + item.After, item.Source)).ToList();
            var change = _selectedLiveChange != null && visible.Contains(_selectedLiveChange)
                ? _selectedLiveChange : visible.FirstOrDefault();
            if (change == null)
            {
                GrimoireWorkflowGUI.DetailHeading("Selected change", "No change selected", null);
                return;
            }
            GrimoireWorkflowGUI.DetailHeading("Selected change", change.Name, change.Path);
            GrimoireWorkflowGUI.DetailLine("Category", change.Category);
            GrimoireWorkflowGUI.DetailLine("Observed at", change.Time.ToString("HH:mm:ss.fff"));
            GrimoireWorkflowGUI.DetailLine("Observed from", change.Source);
            GrimoireWorkflowGUI.Section("Change");
            GrimoireWorkflowGUI.DetailLine("Previous live value", change.Before);
            GrimoireWorkflowGUI.DetailLine("New live value", change.After);
            GrimoireWorkflowGUI.DetailLine("Current live value",
                _liveValues.TryGetValue(change.Id, out var current) ? current.Value : "(unlinked)");
            DrawLiveActions(change.SceneObject, change.Path);
            GrimoireWorkflowGUI.Note("These are Play Mode observations. They do not edit authored API or version values.");
        }

        private void DrawLiveValueDetail()
        {
            var visible = _liveValues.Values.Where(item => MatchesLiveFilter(item.Category,
                item.Name, item.Path, item.Value, item.Source)).ToList();
            if (!_liveValues.TryGetValue(_selectedLiveId ?? "", out var value) || !visible.Contains(value))
                value = visible.OrderBy(v => Array.IndexOf(LiveCategories, v.Category))
                    .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (value == null)
            {
                GrimoireWorkflowGUI.DetailHeading("Live value", "No linked value selected", null);
                return;
            }
            GrimoireWorkflowGUI.DetailHeading("Live value", value.Name, value.Path);
            GrimoireWorkflowGUI.DetailLine("Category", value.Category);
            GrimoireWorkflowGUI.DetailLine("Observed from", value.Source);
            GrimoireWorkflowGUI.DetailLine("Last change", value.LastChanged == default
                ? "Unchanged this session" : value.LastChanged.ToString("HH:mm:ss.fff"));
            GrimoireWorkflowGUI.Section("Current state");
            GrimoireWorkflowGUI.DetailLine("Current live value", value.Value);
            GrimoireWorkflowGUI.DetailLine("At Play Mode start", value.InitialValue);
            DrawLiveActions(value.SceneObject, value.Path);
            GrimoireWorkflowGUI.Note("This inventory includes linked values even when they have not changed during this session.");
        }

        private static void DrawLiveActions(GameObject sceneObject, string path)
        {
            GUILayout.Space(8f);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(sceneObject == null))
            {
                if (GUILayout.Button("Ping in Hierarchy"))
                {
                    Selection.activeGameObject = sceneObject;
                    EditorGUIUtility.PingObject(sceneObject);
                }
            }
            if (GUILayout.Button("Copy path")) EditorGUIUtility.systemCopyBuffer = path ?? "";
            EditorGUILayout.EndHorizontal();
        }
    }
}
