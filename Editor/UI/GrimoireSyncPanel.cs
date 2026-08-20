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
        private static readonly List<GrimoireTextLink> TextLinkScratch = new List<GrimoireTextLink>();

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
            GrimoireLinkedFieldStore.Changed -= OnDirtyChanged;
            GrimoireLinkedFieldStore.Changed += OnDirtyChanged;
            GrimoireTextLinkStore.Changed -= OnDirtyChanged;
            GrimoireTextLinkStore.Changed += OnDirtyChanged;
            RequestRepaint();
        }

        public void Deactivate()
        {
            GrimoireGameEngineDirtyTracker.Changed -= OnDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed -= OnDirtyChanged;
            GrimoireLinkedFieldStore.Changed -= OnDirtyChanged;
            GrimoireTextLinkStore.Changed -= OnDirtyChanged;
        }

        public void Draw()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Sign in and choose a workspace first to sync game engine data.");
                return;
            }

            if (GrimoireGameEngineSync.IsPlayModeBlocked)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Game engine data sync is editor-only. Exit Play Mode to queue transform changes. " +
                    "Editable field commits can still be prepared after Play.");
            }

            var entries = BuildEntries(out var selectedEntry, out var others);
            BuildTextEntries(out var selectedTextEntry, out var otherTextEntries);
            var dirtyTotal = CountDirtyEntries(entries) + CountDirtyTextEntries(selectedTextEntry, otherTextEntries);

            DrawCommitForm(selectedEntry, selectedTextEntry, dirtyTotal);

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

            DrawTextLinksSection(selectedTextEntry, otherTextEntries);
        }

        private void DrawTextLinksSection(TextSyncEntry selectedEntry, List<TextSyncEntry> others)
        {
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Text links", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Translation edits push immediately via PATCH /strings/{id}/translations/{locale}. " +
                "Source text edits stay local until changed in Grimoire.",
                GrimoireEditorStyles.MiniSecondaryStyle);

            if (selectedEntry == null)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Select a GameObject with a Grimoire Text Link to review its pending translation edits.");
            }
            else
            {
                DrawTextChangeCard(selectedEntry, isSelected: true);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(
                others.Count == 0
                    ? "Other pending text links"
                    : $"Other pending text links ({others.Count})",
                EditorStyles.boldLabel);

            if (others.Count == 0)
            {
                EditorGUILayout.LabelField(
                    "No other text links have unsynced translation changes.",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }
            else
            {
                foreach (var entry in others)
                {
                    DrawTextChangeCard(entry, isSelected: false);
                    EditorGUILayout.Space(4);
                }
            }
        }

        private void DrawTextChangeCard(TextSyncEntry entry, bool isSelected)
        {
            var link = entry.Link;
            var title = link.gameObject.name;
            if (!string.IsNullOrEmpty(link.TextCode))
            {
                title += $"  ·  {link.TextCode}";
            }

            if (entry.IsDirty)
            {
                title += "  ·  pending";
            }
            else if (isSelected)
            {
                title += "  ·  up to date";
            }

            var sectionId = $"text-sync:{UnityObjectId.Of(link)}";
            if (!GrimoireEditorStyles.BeginCollapsibleSection(
                    sectionId, title, defaultExpanded: isSelected || entry.IsDirty))
            {
                return;
            }

            if (entry.SourceDirty)
            {
                DrawField("Source", entry.SourceCurrent, entry.SourcePrevious, dirty: true, enabled: true);
                EditorGUILayout.LabelField(
                    "Source text is not writable via the Public API.",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }

            if (entry.TranslationChanges.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(
                    entry.TranslationChanges.Count == 1
                        ? "Translations (1 changed)"
                        : $"Translations ({entry.TranslationChanges.Count} changed)",
                    EditorStyles.miniBoldLabel);

                foreach (var change in entry.TranslationChanges)
                {
                    DrawField(change.Label, change.Current, change.Previous, dirty: true, enabled: true);
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Select", GUILayout.Width(70)))
            {
                Selection.activeGameObject = link.gameObject;
                EditorGUIUtility.PingObject(link.gameObject);
            }

            using (new EditorGUI.DisabledScope(_busy || !entry.PushableDirty))
            {
                if (GUILayout.Button(
                        new GUIContent("Push", "PATCH deviated translations to Grimoire."),
                        GUILayout.Width(70)))
                {
                    PushTextAsync(new[] { entry });
                }
            }

            using (new EditorGUI.DisabledScope(_busy || !entry.IsDirty))
            {
                if (GUILayout.Button("Reset", GUILayout.Width(70)))
                {
                    GrimoireTextLinkStore.ResetToGrimoire(link);
                }
            }

            EditorGUILayout.EndHorizontal();
            GrimoireEditorStyles.EndCollapsibleSection();
        }

        private void DrawCommitForm(SyncEntry selectedEntry, TextSyncEntry selectedTextEntry, int dirtyTotal)
        {
            var selectedDirty = (selectedEntry != null && selectedEntry.IsDirty) ||
                                (selectedTextEntry != null && selectedTextEntry.PushableDirty);
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
            EditorGUILayout.Space(2);

            var selectedTextDirty = selectedTextEntry != null && selectedTextEntry.PushableDirty;
            var textDirtyTotal = CountPushableTextEntries(selectedTextEntry, otherTextEntries);
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(_busy || !selectedTextDirty))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Push selected translations",
                            "PATCH deviated translations on the selected text link."),
                        GUILayout.Height(28)))
                {
                    PushTextAsync(new[] { selectedTextEntry });
                }
            }

            using (new EditorGUI.DisabledScope(_busy || textDirtyTotal == 0))
            {
                var label = textDirtyTotal == 0
                    ? "Push all translations"
                    : $"Push all translations ({textDirtyTotal})";
                if (GUILayout.Button(new GUIContent(label), GUILayout.Height(28)))
                {
                    PushAllTextDirty(selectedTextEntry, otherTextEntries);
                }
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

            var sectionId = $"sync:{UnityObjectId.Of(change.Link)}";
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
            var byLink = new Dictionary<UnityObjectId, SyncEntry>();
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
                var id = UnityObjectId.Of(selectedLink);
                if (!byLink.TryGetValue(id, out selectedEntry))
                {
                    selectedEntry = UpsertEntry(byLink, ordered, BuildLiveOnly(selectedLink));
                    AttachFieldState(selectedEntry);
                }
            }

            MaybeAddFieldOnlyEntries(byLink, ordered, selectedLink);

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

        private static void MaybeAddFieldOnlyEntries(
            Dictionary<UnityObjectId, SyncEntry> byLink,
            List<SyncEntry> ordered,
            GrimoireObjectLink selectedLink)
        {
            _ = selectedLink;
            GrimoireGameEngineDirtyTracker.CollectSceneLinks(LinkScratch);
            foreach (var link in LinkScratch)
            {
                if (link == null || !GrimoireLinkedFieldStore.HasDeviations(link))
                {
                    continue;
                }

                var id = UnityObjectId.Of(link);
                if (byLink.TryGetValue(id, out var existing))
                {
                    AttachFieldState(existing);
                    continue;
                }

                var entry = UpsertEntry(byLink, ordered, BuildLiveOnly(link));
                AttachFieldState(entry);
            }
        }

        private static SyncEntry UpsertEntry(
            Dictionary<UnityObjectId, SyncEntry> byLink,
            List<SyncEntry> ordered,
            GrimoireGameEngineDirtyTracker.PendingChange change)
        {
            var id = UnityObjectId.Of(change.Link);
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

            entry.FieldsDirty = GrimoireEditableFieldsRenderer.HasDirtyEditsForLink(entry.Link);
            entry.FieldChanges = entry.FieldsDirty
                ? GrimoireEditableFieldsRenderer.GetDirtyFieldChanges(entry.Link)
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

        private static int CountDirtyTextEntries(TextSyncEntry selectedEntry, List<TextSyncEntry> others)
        {
            var count = 0;
            if (selectedEntry != null && selectedEntry.IsDirty)
            {
                count++;
            }

            foreach (var entry in others)
            {
                if (entry != null && entry.IsDirty)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountPushableTextEntries(TextSyncEntry selectedEntry, List<TextSyncEntry> others)
        {
            var count = 0;
            if (selectedEntry != null && selectedEntry.PushableDirty)
            {
                count++;
            }

            foreach (var entry in others)
            {
                if (entry != null && entry.PushableDirty)
                {
                    count++;
                }
            }

            return count;
        }

        private List<TextSyncEntry> BuildTextEntries(out TextSyncEntry selectedEntry, out List<TextSyncEntry> others)
        {
            selectedEntry = null;
            others = new List<TextSyncEntry>();
            var ordered = new List<TextSyncEntry>();

            var selectedLink = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<GrimoireTextLink>()
                : null;

            TextLinkScratch.Clear();
            GrimoireTextLinkStore.CollectSceneLinks(TextLinkScratch);
            foreach (var link in TextLinkScratch)
            {
                if (link == null)
                {
                    continue;
                }

                var entry = BuildTextEntry(link);
                ordered.Add(entry);
                if (selectedLink != null && link == selectedLink)
                {
                    selectedEntry = entry;
                }
                else if (entry.IsDirty)
                {
                    others.Add(entry);
                }
            }

            if (selectedEntry == null && selectedLink != null &&
                (selectedLink.HasCode || !string.IsNullOrEmpty(selectedLink.CachedStringId)))
            {
                selectedEntry = BuildTextEntry(selectedLink);
            }

            return ordered;
        }

        private static TextSyncEntry BuildTextEntry(GrimoireTextLink link)
        {
            var changes = GrimoireTextLinkStore.GetDeviations(link);
            var translationChanges = new List<GrimoireTextLinkStore.DirtyTranslationChange>();
            var sourceDirty = false;
            string sourcePrevious = null;
            string sourceCurrent = null;

            foreach (var change in changes)
            {
                if (string.Equals(change.LanguageCode, "(source)", StringComparison.Ordinal))
                {
                    sourceDirty = true;
                    sourcePrevious = change.Previous;
                    sourceCurrent = change.Current;
                    continue;
                }

                translationChanges.Add(change);
            }

            return new TextSyncEntry
            {
                Link = link,
                SourceDirty = sourceDirty,
                SourcePrevious = sourcePrevious,
                SourceCurrent = sourceCurrent,
                TranslationChanges = translationChanges,
            };
        }

        private void PushAllTextDirty(TextSyncEntry selectedEntry, List<TextSyncEntry> others)
        {
            var entries = new List<TextSyncEntry>();
            if (selectedEntry != null && selectedEntry.PushableDirty)
            {
                entries.Add(selectedEntry);
            }

            foreach (var entry in others)
            {
                if (entry != null && entry.PushableDirty)
                {
                    entries.Add(entry);
                }
            }

            PushTextAsync(entries);
        }

        private async void PushTextAsync(IReadOnlyList<TextSyncEntry> entries)
        {
            if (_busy || entries == null || entries.Count == 0)
            {
                return;
            }

            var generation = ++_opGeneration;
            _busy = true;
            _error = null;
            _status = "Pushing translations…";
            RequestRepaint();
            var links = new List<GrimoireTextLink>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry?.Link != null && entry.PushableDirty)
                {
                    links.Add(entry.Link);
                }
            }

            var result = await GrimoireTextSync.PushLinksAsync(links);
            if (generation != _opGeneration)
            {
                return;
            }

            _busy = false;
            if (!result.Success)
            {
                _status = null;
                _error = result.Error ?? "Push failed.";
            }
            else
            {
                _status = result.Data == 1
                    ? "Pushed 1 translation to Grimoire."
                    : $"Pushed {result.Data} translations to Grimoire.";
                _error = null;
            }

            RequestRepaint();
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
            var engineDirty = GrimoireGameEngineDirtyTracker.DirtyCount;
            var fieldOnly = 0;

            GrimoireGameEngineDirtyTracker.CollectSceneLinks(LinkScratch);
            foreach (var link in LinkScratch)
            {
                if (link == null || !GrimoireLinkedFieldStore.HasDeviations(link))
                {
                    continue;
                }

                if (!GrimoireGameEngineDirtyTracker.IsGameEngineDirty(link))
                {
                    fieldOnly++;
                }
            }

            var textDirty = 0;
            TextLinkScratch.Clear();
            GrimoireTextLinkStore.CollectSceneLinks(TextLinkScratch);
            foreach (var link in TextLinkScratch)
            {
                if (link != null && GrimoireTextLinkStore.HasPushableDeviations(link))
                {
                    textDirty++;
                }
            }

            return engineDirty + fieldOnly + textDirty;
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
                includeEngineData: !GrimoireGameEngineSync.IsPlayModeBlocked,
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
                    if (!GrimoireEditableFieldsRenderer.ResetDirtyFields(entry.Link))
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

        private sealed class TextSyncEntry
        {
            public GrimoireTextLink Link;
            public bool SourceDirty;
            public string SourcePrevious;
            public string SourceCurrent;
            public List<GrimoireTextLinkStore.DirtyTranslationChange> TranslationChanges =
                new List<GrimoireTextLinkStore.DirtyTranslationChange>();

            public bool PushableDirty => Link != null && Link.HasTranslationDeviations;
            public bool IsDirty => SourceDirty || PushableDirty;
        }
    }
}
