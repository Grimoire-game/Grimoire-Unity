using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Sync tab: lists pending game_engine_data and editable-field changes and
    /// commits them through <c>POST /api/v1/engine-commits</c> with a title and
    /// optional description for review in Grimoire.
    /// </summary>
    public class GrimoireSyncPanel
    {
        private static readonly List<GrimoireObjectLink> LinkScratch = new List<GrimoireObjectLink>();

        private Vector2 _scroll;
        private bool _busy;
        private string _status;
        private string _error;
        private string _commitTitle = "";
        private string _commitDescription = "";
        private int _opGeneration;

        public event Action RepaintNeeded;

        public void Activate()
        {
            GrimoireGameEngineDirtyTracker.Changed -= OnDirtyChanged;
            GrimoireGameEngineDirtyTracker.Changed += OnDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed -= OnDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed += OnDirtyChanged;
            RequestRepaint();
        }

        public void Deactivate()
        {
            GrimoireGameEngineDirtyTracker.Changed -= OnDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed -= OnDirtyChanged;
        }

        public void Draw()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Sign in and choose a workspace first to sync game engine data.");
                return;
            }

            var entries = BuildEntries(out var selectedEntry, out var others);
            var dirtyTotal = CountDirtyEntries(entries);

            DrawCommitForm(selectedEntry, dirtyTotal);

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
            if (selectedEntry == null)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Select a GameObject with a Grimoire Object Link to see its live game engine data.");
            }
            else
            {
                DrawChangeCard(selectedEntry, isSelected: true);
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
                    "No other linked objects have unsynced transform or field changes.",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }
            else
            {
                foreach (var entry in others)
                {
                    DrawChangeCard(entry, isSelected: false);
                    EditorGUILayout.Space(4);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawCommitForm(SyncEntry selectedEntry, int dirtyTotal)
        {
            var selectedDirty = selectedEntry != null && selectedEntry.IsDirty;
            var canCommit = dirtyTotal > 0 || selectedDirty;

            EditorGUILayout.LabelField("Commit", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Pushes a titled batch to Grimoire for review (POST /engine-commits). " +
                "Values are not applied until accepted on the Engine Sync tab.",
                GrimoireEditorStyles.MiniSecondaryStyle);

            using (new EditorGUI.DisabledScope(_busy || !canCommit))
            {
                _commitTitle = EditorGUILayout.TextField(
                    new GUIContent("Title", "Required. Shown to the reviewer."),
                    _commitTitle ?? "");
                EditorGUILayout.LabelField("Description (optional)", GrimoireEditorStyles.MiniSecondaryStyle);
                _commitDescription = EditorGUILayout.TextArea(
                    _commitDescription ?? "",
                    GUILayout.MinHeight(48));
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            var titleReady = !string.IsNullOrWhiteSpace(_commitTitle);
            using (new EditorGUI.DisabledScope(_busy || !selectedDirty || !titleReady))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Commit selected",
                            "Queue the selected object's pending changes for review."),
                        GrimoireEditorStyles.PrimaryButtonStyle,
                        GUILayout.Height(28)))
                {
                    CommitAsync(new[] { selectedEntry });
                }
            }

            using (new EditorGUI.DisabledScope(_busy || dirtyTotal == 0 || !titleReady))
            {
                var label = dirtyTotal == 0 ? "Commit all" : $"Commit all ({dirtyTotal})";
                if (GUILayout.Button(
                        new GUIContent(label, "Queue every pending change as one commit."),
                        GUILayout.Height(28)))
                {
                    CommitAllDirty();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(_busy || !selectedDirty))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Reset selected",
                            "Restore the selected object's transform and discard unsaved field edits."),
                        GUILayout.Height(28)))
                {
                    ResetAsync(new[] { selectedEntry });
                }
            }

            using (new EditorGUI.DisabledScope(_busy || dirtyTotal == 0))
            {
                var label = dirtyTotal == 0 ? "Reset all" : $"Reset all ({dirtyTotal})";
                if (GUILayout.Button(
                        new GUIContent(
                            label,
                            "Discard local transform and field changes for every pending object."),
                        GUILayout.Height(28)))
                {
                    ResetAllDirty();
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

        private void DrawChangeCard(SyncEntry entry, bool isSelected)
        {
            var change = entry.Engine;
            var title = change.GameObjectName;
            if (!string.IsNullOrEmpty(change.ObjectKey))
            {
                title += $"  ·  {change.ObjectKey}";
            }

            if (entry.IsDirty)
            {
                title += "  ·  pending";
            }
            else if (isSelected)
            {
                title += "  ·  up to date";
            }

            var sectionId = $"sync:{change.Link.GetInstanceID()}";
            if (!GrimoireEditorStyles.BeginCollapsibleSection(
                    sectionId, title, defaultExpanded: isSelected || entry.IsDirty))
            {
                return;
            }

            EditorGUILayout.LabelField("Scene", change.Scene, GrimoireEditorStyles.MiniSecondaryStyle);

            if (entry.EngineDirty)
            {
                EditorGUILayout.LabelField("Game engine data", EditorStyles.miniBoldLabel);
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
            }
            else if (isSelected)
            {
                EditorGUILayout.LabelField("Game engine data", EditorStyles.miniBoldLabel);
                DrawField("Position", FormatVector(change.Position), null, false, change.Link.SyncPosition);
                DrawField("Rotation", FormatVector(change.Euler), null, false, change.Link.SyncRotation);
                DrawField("Scale", FormatVector(change.Scale), null, false, change.Link.SyncScale);
                DrawField("Id / Name", change.EngineName, null, false, change.Link.SyncIdName);
            }

            if (entry.FieldsDirty)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(
                    entry.FieldChanges.Count == 1
                        ? "Editable fields (1 changed)"
                        : $"Editable fields ({entry.FieldChanges.Count} changed)",
                    EditorStyles.miniBoldLabel);

                foreach (var field in entry.FieldChanges)
                {
                    DrawField(field.Label, field.Current, field.Previous, dirty: true, enabled: true);
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Select", GUILayout.Width(70)))
            {
                Selection.activeGameObject = change.Link.gameObject;
                EditorGUIUtility.PingObject(change.Link.gameObject);
            }

            var titleReady = !string.IsNullOrWhiteSpace(_commitTitle);
            using (new EditorGUI.DisabledScope(_busy || !entry.IsDirty || !titleReady))
            {
                if (GUILayout.Button(
                        new GUIContent("Commit", "Queue this object's pending changes for review."),
                        GUILayout.Width(70)))
                {
                    CommitAsync(new[] { entry });
                }
            }

            using (new EditorGUI.DisabledScope(_busy || !entry.IsDirty))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Reset",
                            "Restore transform from Grimoire and discard unsaved field edits."),
                        GUILayout.Width(70)))
                {
                    ResetAsync(new[] { entry });
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

        private List<SyncEntry> BuildEntries(out SyncEntry selectedEntry, out List<SyncEntry> others)
        {
            selectedEntry = null;
            others = new List<SyncEntry>();
            var byLink = new Dictionary<int, SyncEntry>();
            var ordered = new List<SyncEntry>();

            var selectedLink = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<GrimoireObjectLink>()
                : null;

            foreach (var change in GrimoireGameEngineDirtyTracker.GetPendingChanges())
            {
                if (change.Link == null)
                {
                    continue;
                }

                var entry = UpsertEntry(byLink, ordered, change);
                AttachFieldState(entry);
            }

            if (selectedLink != null &&
                (selectedLink.HasKey || !string.IsNullOrEmpty(selectedLink.CachedObjectId)))
            {
                var id = selectedLink.GetInstanceID();
                if (!byLink.TryGetValue(id, out selectedEntry))
                {
                    selectedEntry = UpsertEntry(byLink, ordered, BuildLiveOnly(selectedLink));
                    AttachFieldState(selectedEntry);
                }
            }

            MaybeAddFieldOnlyEntry(byLink, ordered, selectedLink);

            foreach (var entry in ordered)
            {
                if (selectedLink != null && entry.Link == selectedLink)
                {
                    selectedEntry = entry;
                    continue;
                }

                if (entry.IsDirty)
                {
                    others.Add(entry);
                }
            }

            return ordered;
        }

        private static void MaybeAddFieldOnlyEntry(
            Dictionary<int, SyncEntry> byLink,
            List<SyncEntry> ordered,
            GrimoireObjectLink selectedLink)
        {
            if (!GrimoireEditableFieldsRenderer.HasDirtyEdits)
            {
                return;
            }

            var objectId = GrimoireEditableFieldsRenderer.LoadedObjectId;
            if (string.IsNullOrEmpty(objectId))
            {
                return;
            }

            foreach (var existing in ordered)
            {
                if (string.Equals(existing.Engine.ObjectId, objectId, StringComparison.Ordinal))
                {
                    AttachFieldState(existing);
                    return;
                }
            }

            var link = FindLinkByObjectId(objectId, selectedLink);
            if (link == null)
            {
                return;
            }

            var entry = UpsertEntry(byLink, ordered, BuildLiveOnly(link));
            AttachFieldState(entry);
        }

        private static GrimoireObjectLink FindLinkByObjectId(
            string objectId, GrimoireObjectLink preferred)
        {
            if (preferred != null &&
                string.Equals(preferred.CachedObjectId, objectId, StringComparison.Ordinal))
            {
                return preferred;
            }

            GrimoireGameEngineDirtyTracker.CollectSceneLinks(LinkScratch);
            foreach (var link in LinkScratch)
            {
                if (link != null &&
                    string.Equals(link.CachedObjectId, objectId, StringComparison.Ordinal))
                {
                    return link;
                }
            }

            return null;
        }

        private static SyncEntry UpsertEntry(
            Dictionary<int, SyncEntry> byLink,
            List<SyncEntry> ordered,
            GrimoireGameEngineDirtyTracker.PendingChange change)
        {
            var id = change.Link.GetInstanceID();
            if (byLink.TryGetValue(id, out var existing))
            {
                existing.Engine = change;
                return existing;
            }

            var entry = new SyncEntry { Engine = change };
            byLink[id] = entry;
            ordered.Add(entry);
            return entry;
        }

        private static void AttachFieldState(SyncEntry entry)
        {
            if (entry?.Link == null)
            {
                return;
            }

            entry.FieldsDirty = GrimoireEditableFieldsRenderer.HasDirtyEditsForObject(entry.Engine.ObjectId);
            entry.FieldChanges = entry.FieldsDirty
                ? GrimoireEditableFieldsRenderer.GetDirtyFieldChanges()
                : new List<GrimoireEditableFieldsRenderer.DirtyFieldChange>();
        }

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

        private static int CountDirtyEntries(List<SyncEntry> entries)
        {
            var count = 0;
            foreach (var entry in entries)
            {
                if (entry.IsDirty)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Pending objects across game-engine transforms and editable fields.</summary>
        public static int TotalPendingCount()
        {
            var count = GrimoireGameEngineDirtyTracker.DirtyCount;
            if (!GrimoireEditableFieldsRenderer.HasDirtyEdits)
            {
                return count;
            }

            var objectId = GrimoireEditableFieldsRenderer.LoadedObjectId;
            if (string.IsNullOrEmpty(objectId))
            {
                return count;
            }

            foreach (var change in GrimoireGameEngineDirtyTracker.GetPendingChanges())
            {
                if (change.IsDirty &&
                    string.Equals(change.ObjectId, objectId, StringComparison.Ordinal))
                {
                    return count;
                }
            }

            return count + 1;
        }

        private void CommitAllDirty()
        {
            CommitAsync(CollectDirtyEntries());
        }

        private void ResetAllDirty()
        {
            ResetAsync(CollectDirtyEntries());
        }

        private List<SyncEntry> CollectDirtyEntries()
        {
            var entries = BuildEntries(out var selected, out var others);
            var dirty = new List<SyncEntry>();
            if (selected != null && selected.IsDirty)
            {
                dirty.Add(selected);
            }

            dirty.AddRange(others);
            return dirty;
        }

        private async void CommitAsync(IReadOnlyList<SyncEntry> entries)
        {
            if (_busy || entries == null || entries.Count == 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_commitTitle))
            {
                _error = "Enter a commit title before pushing changes.";
                _status = null;
                RequestRepaint();
                return;
            }

            var generation = ++_opGeneration;
            _busy = true;
            _error = null;
            _status = "Building commit…";
            RequestRepaint();

            var links = new List<GrimoireObjectLink>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry?.Link != null && entry.IsDirty)
                {
                    links.Add(entry.Link);
                }
            }

            var result = await GrimoireEngineCommit.CommitAsync(
                _commitTitle,
                _commitDescription,
                links,
                includeEngineData: true,
                includeEditableFields: true);

            if (generation != _opGeneration)
            {
                return;
            }

            _busy = false;
            if (!result.Success)
            {
                _status = null;
                _error = result.Error ?? "Commit failed.";
                RequestRepaint();
                return;
            }

            var changeCount = result.Data?.changes?.Length ?? 0;
            var conflict = GrimoireEngineCommit.FormatConflictSummary(result.Data?.changes);
            _status = changeCount == 1
                ? "Queued 1 change for review in Grimoire."
                : $"Queued {changeCount} changes for review in Grimoire.";
            if (!string.IsNullOrEmpty(conflict))
            {
                _status += " " + conflict;
            }

            _error = null;
            _commitTitle = "";
            _commitDescription = "";
            RequestRepaint();
        }

        private async void ResetAsync(IReadOnlyList<SyncEntry> entries)
        {
            if (_busy || entries == null || entries.Count == 0)
            {
                return;
            }

            var generation = ++_opGeneration;
            _busy = true;
            _error = null;
            _status = $"Resetting 0/{entries.Count}…";
            RequestRepaint();

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var ok = 0;
            var failed = 0;
            string lastError = null;

            for (var i = 0; i < entries.Count; i++)
            {
                if (generation != _opGeneration)
                {
                    return;
                }

                var entry = entries[i];
                if (entry?.Link == null)
                {
                    continue;
                }

                _status = $"Resetting {i + 1}/{entries.Count}: {entry.Link.gameObject.name}…";
                RequestRepaint();

                var objectOk = true;

                if (entry.EngineDirty)
                {
                    var result = await GrimoireGameEngineSync.ResetTransformFromGrimoireAsync(entry.Link);
                    if (!result.Success)
                    {
                        objectOk = false;
                        lastError = result.Error;
                    }
                }

                if (entry.FieldsDirty)
                {
                    if (!GrimoireEditableFieldsRenderer.ResetDirtyFields())
                    {
                        objectOk = false;
                        lastError = "Could not reset editable field edits.";
                    }
                }

                if (objectOk)
                {
                    ok++;
                }
                else
                {
                    failed++;
                }
            }

            if (generation != _opGeneration)
            {
                return;
            }

            FinishBatch(ok, failed, lastError, "Reset");
        }

        private void FinishBatch(int ok, int failed, string lastError, string verb)
        {
            _busy = false;
            if (failed == 0)
            {
                _status = ok == 1 ? $"{verb} 1 object." : $"{verb} {ok} objects.";
                _error = null;
            }
            else
            {
                _status = $"{verb} {ok}, failed {failed}.";
                _error = lastError;
            }

            RequestRepaint();
        }

        private void OnDirtyChanged() => RequestRepaint();

        private void RequestRepaint() => RepaintNeeded?.Invoke();

        private sealed class SyncEntry
        {
            public GrimoireGameEngineDirtyTracker.PendingChange Engine;
            public bool FieldsDirty;
            public List<GrimoireEditableFieldsRenderer.DirtyFieldChange> FieldChanges =
                new List<GrimoireEditableFieldsRenderer.DirtyFieldChange>();

            public GrimoireObjectLink Link => Engine?.Link;
            public bool EngineDirty => Engine != null && Engine.IsDirty;
            public bool IsDirty => EngineDirty || FieldsDirty;
        }
    }
}
