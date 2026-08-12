using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Draws <c>hints.game_engine_editable</c> fields with editors and Reset.
    /// Values are stored on the bound <see cref="GrimoireObjectLink"/> so they
    /// survive scene saves; deviations from Grimoire are committed from Sync.
    /// </summary>
    public static class GrimoireEditableFieldsRenderer
    {
        private const float LabelWidth = 150f;

        private static GrimoireObjectLink _link;
        private static ObjectViewDocument _document;
        private static string _documentKey;
        private static string _statusMessage;
        private static string _error;

        /// <summary>Raised when editable field dirty state or buffers change.</summary>
        public static event Action Changed;

        public sealed class DirtyFieldChange
        {
            public string FieldId;
            public string Label;
            public string Previous;
            public string Current;
        }

        public static void Draw(ObjectViewDocument document, GrimoireObjectLink link = null)
        {
            if (document == null && link == null)
            {
                GrimoireEditorStyles.DrawInfoBox("No object loaded.");
                return;
            }

            if (link != null)
            {
                EnsureBound(link, document);
            }
            else if (document != null)
            {
                BindDocument(document);
            }

            if (_link != null)
            {
                DrawFromLink(_link);
                return;
            }

            GrimoireEditorStyles.DrawInfoBox(
                "No Grimoire Object Link selected. Select a linked GameObject to edit fields.");
        }

        public static void ClearBuffers()
        {
            _link = null;
            _document = null;
            _documentKey = null;
            _statusMessage = null;
            _error = null;
            NotifyDirtyChanged();
        }

        /// <summary>
        /// Bind a scene link and optional freshly loaded document. When a document
        /// is provided, field metadata and Grimoire baselines are reconciled onto
        /// the link while preserving local edits.
        /// </summary>
        public static void Bind(GrimoireObjectLink link, ObjectViewDocument document)
        {
            EnsureBound(link, document, forceApply: true);
        }

        /// <summary>
        /// Keep edit state aligned with the loaded object view when no link is
        /// available (rare field-only flows). Prefer <see cref="Bind"/>.
        /// </summary>
        public static void BindDocument(ObjectViewDocument document)
        {
            EnsureBound(_link, document, forceApply: document != null);
        }

        private static void EnsureBound(
            GrimoireObjectLink link,
            ObjectViewDocument document,
            bool forceApply = false)
        {
            _link = link;
            if (document == null)
            {
                _document = null;
                return;
            }

            var key = DocumentKey(document);
            var documentChanged = key != _documentKey || forceApply;
            _document = document;
            _documentKey = key;

            if (link != null && documentChanged)
            {
                GrimoireLinkedFieldStore.ApplyFromDocument(link, document, preserveLocalEdits: true);
                _statusMessage = null;
                _error = null;
                NotifyDirtyChanged();
            }
        }

        private static string DocumentKey(ObjectViewDocument document) =>
            $"{document.@object?.id}:{document.@object?.updated_at}:{document.schema_version}";

        /// <summary>Object id for the document/link currently backing edits, if any.</summary>
        public static string LoadedObjectId =>
            !string.IsNullOrEmpty(_link?.CachedObjectId)
                ? _link.CachedObjectId
                : _document?.@object?.id;

        /// <summary>True when any editable field on the bound link diverges from Grimoire.</summary>
        public static bool HasDirtyEdits =>
            _link != null && GrimoireLinkedFieldStore.HasDeviations(_link);

        /// <summary>
        /// True when the given object's linked fields diverge from Grimoire.
        /// Looks up the scene link by cached object id.
        /// </summary>
        public static bool HasDirtyEditsForObject(string objectId)
        {
            if (string.IsNullOrEmpty(objectId))
            {
                return false;
            }

            if (_link != null &&
                string.Equals(_link.CachedObjectId, objectId, StringComparison.Ordinal))
            {
                return GrimoireLinkedFieldStore.HasDeviations(_link);
            }

            var link = FindSceneLinkByObjectId(objectId);
            return link != null && GrimoireLinkedFieldStore.HasDeviations(link);
        }

        public static bool HasDirtyEditsForLink(GrimoireObjectLink link) =>
            GrimoireLinkedFieldStore.HasDeviations(link);

        public static List<DirtyFieldChange> GetDirtyFieldChanges()
        {
            return ToRendererChanges(GrimoireLinkedFieldStore.GetDeviations(_link));
        }

        public static List<DirtyFieldChange> GetDirtyFieldChanges(GrimoireObjectLink link)
        {
            return ToRendererChanges(GrimoireLinkedFieldStore.GetDeviations(link));
        }

        /// <summary>
        /// Discard local editable-field edits for the bound link.
        /// </summary>
        public static bool ResetDirtyFields()
        {
            if (_link == null || !GrimoireLinkedFieldStore.HasDeviations(_link))
            {
                return false;
            }

            if (!GrimoireLinkedFieldStore.ResetToGrimoire(_link))
            {
                return false;
            }

            _statusMessage = "Local edits discarded.";
            _error = null;
            GUI.FocusControl(null);
            NotifyDirtyChanged();
            return true;
        }

        public static bool ResetDirtyFields(GrimoireObjectLink link)
        {
            if (!GrimoireLinkedFieldStore.ResetToGrimoire(link))
            {
                return false;
            }

            if (_link == link)
            {
                _statusMessage = "Local edits discarded.";
                _error = null;
            }

            NotifyDirtyChanged();
            return true;
        }

        /// <summary>
        /// Build stored glossary values for every dirty editable field, including
        /// <c>base_value</c> from the cached Grimoire baseline on the link.
        /// </summary>
        public static bool TryBuildDirtyFieldChanges(
            out List<FieldValueUpdate> updates, out string error)
        {
            return GrimoireLinkedFieldStore.TryBuildDirtyFieldChanges(_link, out updates, out error);
        }

        public static bool TryBuildDirtyFieldChanges(
            GrimoireObjectLink link,
            out List<FieldValueUpdate> updates,
            out string error)
        {
            return GrimoireLinkedFieldStore.TryBuildDirtyFieldChanges(link, out updates, out error);
        }

        /// <summary>
        /// Treat the current local values as the new Grimoire baseline after a
        /// commit was queued (object values are not updated until review).
        /// </summary>
        public static void AcceptSubmittedEdits()
        {
            if (_link == null)
            {
                return;
            }

            GrimoireLinkedFieldStore.AcceptSubmitted(_link);
            _statusMessage = "Queued for review in Grimoire.";
            _error = null;
            NotifyDirtyChanged();
        }

        public static void AcceptSubmittedEdits(GrimoireObjectLink link)
        {
            GrimoireLinkedFieldStore.AcceptSubmitted(link);
            if (_link == link)
            {
                _statusMessage = "Queued for review in Grimoire.";
                _error = null;
            }

            NotifyDirtyChanged();
        }

        private static void DrawFromLink(GrimoireObjectLink link)
        {
            var fields = link.LinkedFields;
            if (fields == null || fields.Count == 0)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "No game-engine-editable fields on this object. " +
                    "Mark fields as Game engine on the template in Grimoire, then refresh.");
                return;
            }

            DrawToolbar(link);
            EditorGUILayout.Space(4);

            string currentSection = null;
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (field == null)
                {
                    continue;
                }

                var sectionTitle = string.IsNullOrEmpty(field.SectionTitle) ? "Fields" : field.SectionTitle;
                if (sectionTitle != currentSection)
                {
                    if (currentSection != null)
                    {
                        GrimoireEditorStyles.EndCollapsibleSection();
                    }

                    currentSection = sectionTitle;
                    var sectionId = $"editable-section:{field.SectionId ?? sectionTitle}";
                    if (!GrimoireEditorStyles.BeginCollapsibleSection(sectionId, sectionTitle, defaultExpanded: true))
                    {
                        currentSection = null;
                        continue;
                    }
                }

                if (currentSection == null)
                {
                    continue;
                }

                DrawLinkedField(link, field);
            }

            if (currentSection != null)
            {
                GrimoireEditorStyles.EndCollapsibleSection();
            }
        }

        private static void DrawToolbar(GrimoireObjectLink link)
        {
            var dirtyCount = GrimoireLinkedFieldStore.CountDeviations(link);
            var fieldCount = link.LinkedFields?.Count ?? 0;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"{fieldCount} editable field{(fieldCount == 1 ? "" : "s")}" +
                (dirtyCount > 0 ? $"  ·  {dirtyCount} changed" : ""),
                GrimoireEditorStyles.MiniSecondaryStyle);

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(dirtyCount == 0))
            {
                if (GUILayout.Button(
                        new GUIContent("Reset", "Discard local edits and restore values from the cached Grimoire baselines."),
                        GUILayout.Width(60)))
                {
                    ResetDirtyFields(link);
                    GUI.FocusControl(null);
                }
            }

            EditorGUILayout.EndHorizontal();

            if (dirtyCount > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(
                    "Local values are saved on the Object Link. Commit pending changes from the Sync tab.",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }

            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }
            else if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.LabelField(_statusMessage, GrimoireEditorStyles.MiniSecondaryStyle);
            }
        }

        private static void DrawLinkedField(GrimoireObjectLink link, GrimoireLinkedField field)
        {
            EditorGUILayout.BeginHorizontal();

            var label = field.ReadOnly ? field.DisplayLabel : field.DisplayLabel;
            if (!field.ReadOnly && field.IsDeviated)
            {
                label += " •";
            }

            EditorGUILayout.LabelField(
                new GUIContent(label, Tooltip(field)),
                GUILayout.Width(LabelWidth));

            EditorGUILayout.BeginVertical();

            var editKind = string.IsNullOrEmpty(field.Kind) ? ObjectViewKinds.Text : field.Kind;

            EditorGUI.BeginDisabledGroup(field.ReadOnly);

            if (!GrimoireFieldSync.IsSupportedEditKind(editKind))
            {
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(field.LocalValue) ? "Not set" : field.LocalValue,
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    "This kind cannot be edited from the plugin.",
                    EditorStyles.miniLabel);
            }
            else if (editKind == ObjectViewKinds.Boolean)
            {
                var current = string.Equals(field.LocalValue, "true", StringComparison.OrdinalIgnoreCase);
                var toggled = EditorGUILayout.Toggle(current);
                if (!field.ReadOnly && toggled != current)
                {
                    GrimoireLinkedFieldStore.SetLocalValue(link, field.FieldId, toggled ? "true" : "false");
                    _statusMessage = null;
                    _error = null;
                }
            }
            else
            {
                var buffer = field.LocalValue ?? "";
                string next;
                if (field.Multiline)
                {
                    next = EditorGUILayout.TextArea(buffer, GUILayout.MinHeight(48));
                }
                else
                {
                    next = EditorGUILayout.TextField(buffer);
                }

                if (!field.ReadOnly && next != buffer)
                {
                    GrimoireLinkedFieldStore.SetLocalValue(link, field.FieldId, next);
                    _statusMessage = null;
                    _error = null;
                }
            }

            EditorGUI.EndDisabledGroup();

            if (field.ReadOnly)
            {
                EditorGUILayout.LabelField("Read-only for your role", EditorStyles.miniLabel);
            }
            else if (field.IsDeviated)
            {
                EditorGUILayout.LabelField(
                    $"Grimoire: {FormatPreview(field.GrimoireValue)}",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        private static string FormatPreview(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(empty)";
            }

            return value.Length <= 80 ? value : value.Substring(0, 77) + "…";
        }

        private static List<DirtyFieldChange> ToRendererChanges(
            List<GrimoireLinkedFieldStore.DirtyFieldChange> source)
        {
            var list = new List<DirtyFieldChange>(source?.Count ?? 0);
            if (source == null)
            {
                return list;
            }

            foreach (var item in source)
            {
                list.Add(new DirtyFieldChange
                {
                    FieldId = item.FieldId,
                    Label = item.Label,
                    Previous = item.Previous,
                    Current = item.Current,
                });
            }

            return list;
        }

        private static GrimoireObjectLink FindSceneLinkByObjectId(string objectId)
        {
            var scratch = new List<GrimoireObjectLink>();
            GrimoireGameEngineDirtyTracker.CollectSceneLinks(scratch);
            foreach (var candidate in scratch)
            {
                if (candidate != null &&
                    string.Equals(candidate.CachedObjectId, objectId, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void NotifyDirtyChanged()
        {
            Changed?.Invoke();
        }

        private static string Tooltip(GrimoireLinkedField field)
        {
            var kind = string.IsNullOrEmpty(field.Kind) ? "field" : field.Kind;
            var type = string.IsNullOrEmpty(field.FieldType) ? kind : field.FieldType;
            var tooltip = $"{type} ({kind}) · game engine editable";
            if (field.IsDeviated)
            {
                tooltip += "\nChanged locally — pending sync to Grimoire.";
            }

            return tooltip;
        }

        static GrimoireEditableFieldsRenderer()
        {
            GrimoireLinkedFieldStore.Changed += () => Changed?.Invoke();
        }
    }
}
