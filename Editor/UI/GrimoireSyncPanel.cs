using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Sync tab: lists pending game_engine_data changes for the selected
    /// linked object and for every other dirty linked object in open scenes.
    /// </summary>
    public class GrimoireSyncPanel
    {
        private Vector2 _scroll;
        private bool _syncing;
        private string _status;
        private string _error;
        private int _syncGeneration;

        public event Action RepaintNeeded;

        public void Activate()
        {
            GrimoireGameEngineDirtyTracker.Changed -= OnDirtyChanged;
            GrimoireGameEngineDirtyTracker.Changed += OnDirtyChanged;
            RequestRepaint();
        }

        public void Deactivate()
        {
            GrimoireGameEngineDirtyTracker.Changed -= OnDirtyChanged;
        }

        public void Draw()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Sign in and choose a workspace first to sync game engine data.");
                return;
            }

            var pending = GrimoireGameEngineDirtyTracker.GetPendingChanges();
            var selectedLink = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<GrimoireObjectLink>()
                : null;

            GrimoireGameEngineDirtyTracker.PendingChange selectedChange = null;
            var others = new List<GrimoireGameEngineDirtyTracker.PendingChange>();

            foreach (var change in pending)
            {
                if (change.Link == null)
                {
                    continue;
                }

                if (selectedLink != null && change.Link == selectedLink)
                {
                    selectedChange = change;
                }
                else if (change.IsDirty)
                {
                    others.Add(change);
                }
            }

            // Selected linked object with no entry yet (clean) — still show live values.
            if (selectedChange == null && selectedLink != null &&
                (selectedLink.HasKey || !string.IsNullOrEmpty(selectedLink.CachedObjectId)))
            {
                selectedChange = GrimoireGameEngineDirtyTracker.FindPending(selectedLink)
                                 ?? BuildLiveOnly(selectedLink);
            }

            DrawToolbar(selectedChange);

            if (!string.IsNullOrEmpty(_error))
            {
                GrimoireEditorStyles.DrawErrorBox(_error);
            }

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.LabelField(_status, GrimoireEditorStyles.MiniSecondaryStyle);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Selected object", EditorStyles.boldLabel);
            if (selectedChange == null)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Select a GameObject with a Grimoire Object Link to see its live game engine data.");
            }
            else
            {
                DrawChangeCard(selectedChange, isSelected: true);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField(
                others.Count == 0
                    ? "Other pending changes"
                    : $"Other pending changes ({others.Count})",
                EditorStyles.boldLabel);

            if (others.Count == 0)
            {
                EditorGUILayout.LabelField(
                    "No other linked objects have unsynced transform changes.",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }
            else
            {
                foreach (var change in others)
                {
                    DrawChangeCard(change, isSelected: false);
                    EditorGUILayout.Space(4);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar(GrimoireGameEngineDirtyTracker.PendingChange selectedChange)
        {
            EditorGUILayout.BeginHorizontal();

            var selectedDirty = selectedChange != null && selectedChange.IsDirty;
            var dirtyTotal = GrimoireGameEngineDirtyTracker.DirtyCount;

            using (new EditorGUI.DisabledScope(_syncing || !selectedDirty))
            {
                if (GUILayout.Button("Sync selected", GrimoireEditorStyles.PrimaryButtonStyle, GUILayout.Height(28)))
                {
                    SyncAsync(new[] { selectedChange.Link });
                }
            }

            using (new EditorGUI.DisabledScope(_syncing || dirtyTotal == 0))
            {
                var label = dirtyTotal == 0 ? "Sync all" : $"Sync all ({dirtyTotal})";
                if (GUILayout.Button(label, GUILayout.Height(28)))
                {
                    SyncAllDirty();
                }
            }

            GUILayout.FlexibleSpace();

            if (dirtyTotal > 0)
            {
                EditorGUILayout.LabelField(
                    $"{dirtyTotal} pending",
                    GrimoireEditorStyles.MiniSecondaryStyle,
                    GUILayout.Width(80));
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);
        }

        private void DrawChangeCard(GrimoireGameEngineDirtyTracker.PendingChange change, bool isSelected)
        {
            var title = change.GameObjectName;
            if (!string.IsNullOrEmpty(change.ObjectKey))
            {
                title += $"  ·  {change.ObjectKey}";
            }

            if (change.IsDirty)
            {
                title += "  ·  pending";
            }
            else if (isSelected)
            {
                title += "  ·  up to date";
            }

            var sectionId = $"sync:{change.Link.GetInstanceID()}";
            if (!GrimoireEditorStyles.BeginCollapsibleSection(sectionId, title, defaultExpanded: isSelected || change.IsDirty))
            {
                return;
            }

            EditorGUILayout.LabelField("Scene", change.Scene, GrimoireEditorStyles.MiniSecondaryStyle);
            DrawField(
                "Position",
                FormatVector(change.Position),
                change.PositionDirty ? FormatVector(change.LastPosition) : null,
                change.PositionDirty,
                change.Link.SyncPosition);
            DrawField(
                "Rotation",
                FormatVector(change.Euler),
                change.RotationDirty ? FormatVector(change.LastEuler) : null,
                change.RotationDirty,
                change.Link.SyncRotation);
            DrawField(
                "Scale",
                FormatVector(change.Scale),
                change.ScaleDirty ? FormatVector(change.LastScale) : null,
                change.ScaleDirty,
                change.Link.SyncScale);
            DrawField(
                "Id / Name",
                change.EngineName,
                change.IdNameDirty ? change.LastName : null,
                change.IdNameDirty,
                change.Link.SyncIdName);

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Select", GUILayout.Width(70)))
            {
                Selection.activeGameObject = change.Link.gameObject;
                EditorGUIUtility.PingObject(change.Link.gameObject);
            }

            using (new EditorGUI.DisabledScope(_syncing || !change.IsDirty))
            {
                if (GUILayout.Button("Sync", GUILayout.Width(70)))
                {
                    SyncAsync(new[] { change.Link });
                }
            }

            EditorGUILayout.EndHorizontal();
            GrimoireEditorStyles.EndCollapsibleSection();
        }

        private static void DrawField(
            string label, string current, string previous, bool dirty, bool enabled)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(90));

            if (!enabled)
            {
                EditorGUILayout.LabelField("not synced", GrimoireEditorStyles.MiniSecondaryStyle);
            }
            else if (dirty)
            {
                EditorGUILayout.LabelField($"{previous}  →  {current}", EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(current, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndHorizontal();
        }

        private static string FormatVector(Vector3 value) =>
            $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";

        private static GrimoireGameEngineDirtyTracker.PendingChange BuildLiveOnly(GrimoireObjectLink link)
        {
            var t = link.transform;
            return new GrimoireGameEngineDirtyTracker.PendingChange
            {
                Link = link,
                GameObjectName = link.gameObject.name,
                ObjectKey = link.ObjectKey,
                ObjectId = link.CachedObjectId,
                Scene = GrimoireGameEngineSync.GetSceneName(link.gameObject),
                Position = t.position,
                Euler = t.eulerAngles,
                Scale = t.localScale,
                EngineName = link.gameObject.name,
                LastPosition = t.position,
                LastEuler = t.eulerAngles,
                LastScale = t.localScale,
                LastName = link.gameObject.name,
            };
        }

        private void SyncAllDirty()
        {
            var links = new List<GrimoireObjectLink>();
            foreach (var change in GrimoireGameEngineDirtyTracker.GetPendingChanges())
            {
                if (change.IsDirty && change.Link != null)
                {
                    links.Add(change.Link);
                }
            }

            SyncAsync(links);
        }

        private async void SyncAsync(IReadOnlyList<GrimoireObjectLink> links)
        {
            if (_syncing || links == null || links.Count == 0)
            {
                return;
            }

            var generation = ++_syncGeneration;
            _syncing = true;
            _error = null;
            _status = $"Syncing 0/{links.Count}…";
            RequestRepaint();

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var ok = 0;
            var failed = 0;
            string lastError = null;

            for (var i = 0; i < links.Count; i++)
            {
                if (generation != _syncGeneration)
                {
                    return;
                }

                var link = links[i];
                if (link == null)
                {
                    continue;
                }

                _status = $"Syncing {i + 1}/{links.Count}: {link.gameObject.name}…";
                RequestRepaint();

                var result = await GrimoireGameEngineSync.UpsertAsync(link);
                if (result.Success)
                {
                    ok++;
                }
                else
                {
                    failed++;
                    lastError = result.Error;
                }
            }

            if (generation != _syncGeneration)
            {
                return;
            }

            _syncing = false;
            if (failed == 0)
            {
                _status = ok == 1 ? "Synced 1 object." : $"Synced {ok} objects.";
                _error = null;
            }
            else
            {
                _status = $"Synced {ok}, failed {failed}.";
                _error = lastError;
            }

            RequestRepaint();
        }

        private void OnDirtyChanged() => RequestRepaint();

        private void RequestRepaint() => RepaintNeeded?.Invoke();
    }
}
