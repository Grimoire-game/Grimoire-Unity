using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Draws <c>hints.game_engine_editable</c> fields with editors and Reset.
    /// Pending edits are committed from the Sync tab.
    /// </summary>
    public static class GrimoireEditableFieldsRenderer
    {
        private const float LabelWidth = 150f;

        private static string _documentKey;
        private static ObjectViewDocument _document;
        private static readonly Dictionary<string, string> EditBuffers = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> BaselineBuffers = new Dictionary<string, string>();
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

        public static void Draw(ObjectViewDocument document)
        {
            if (document == null)
            {
                GrimoireEditorStyles.DrawInfoBox("No object loaded.");
                return;
            }

            EnsureBuffers(document);

            var editable = CollectEditableFields(document);
            if (editable.Count == 0)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "No game-engine-editable fields on this object. " +
                    "Mark fields as Game engine on the template in Grimoire to edit them here.");
                return;
            }

            DrawToolbar(document, editable);
            EditorGUILayout.Space(4);

            string currentSection = null;
            foreach (var entry in editable)
            {
                if (entry.SectionTitle != currentSection)
                {
                    if (currentSection != null)
                    {
                        GrimoireEditorStyles.EndCollapsibleSection();
                    }

                    currentSection = entry.SectionTitle;
                    var sectionId = $"editable-section:{entry.SectionId ?? entry.SectionTitle}";
                    if (!GrimoireEditorStyles.BeginCollapsibleSection(sectionId, entry.SectionTitle, defaultExpanded: true))
                    {
                        currentSection = null;
                        continue;
                    }
                }

                if (currentSection == null)
                {
                    continue;
                }

                DrawEditableField(entry.Field);
            }

            if (currentSection != null)
            {
                GrimoireEditorStyles.EndCollapsibleSection();
            }
        }

        public static void ClearBuffers()
        {
            EditBuffers.Clear();
            BaselineBuffers.Clear();
            _documentKey = null;
            _document = null;
            _statusMessage = null;
            _error = null;
            NotifyDirtyChanged();
        }

        /// <summary>
        /// Keep edit buffers aligned with the loaded object view. Resets buffers
        /// when the document identity changes; no-ops when already bound.
        /// </summary>
        public static void BindDocument(ObjectViewDocument document)
        {
            if (document == null)
            {
                ClearBuffers();
                return;
            }

            EnsureBuffers(document);
        }

        /// <summary>Object id for the document currently backing edit buffers, if any.</summary>
        public static string LoadedObjectId => _document?.@object?.id;

        /// <summary>True when any editable field buffer diverges from its Grimoire baseline.</summary>
        public static bool HasDirtyEdits => CountAllDirty() > 0;

        /// <summary>
        /// True when the currently loaded object's editable field buffers diverge
        /// from the last loaded Grimoire values.
        /// </summary>
        public static bool HasDirtyEditsForObject(string objectId)
        {
            if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(LoadedObjectId))
            {
                return false;
            }

            if (!string.Equals(LoadedObjectId, objectId, StringComparison.Ordinal))
            {
                return false;
            }

            return HasDirtyEdits;
        }

        public static List<DirtyFieldChange> GetDirtyFieldChanges()
        {
            var list = new List<DirtyFieldChange>();
            if (_document == null)
            {
                return list;
            }

            foreach (var entry in CollectEditableFields(_document))
            {
                var field = entry.Field;
                if (field?.hints != null && field.hints.read_only)
                {
                    continue;
                }

                if (!IsDirty(field.id))
                {
                    continue;
                }

                BaselineBuffers.TryGetValue(field.id, out var baseline);
                list.Add(new DirtyFieldChange
                {
                    FieldId = field.id,
                    Label = string.IsNullOrEmpty(field.label) ? field.id : field.label,
                    Previous = baseline ?? "",
                    Current = GetBuffer(field.id),
                });
            }

            return list;
        }

        /// <summary>
        /// Discard local editable-field edits for the loaded object.
        /// </summary>
        public static bool ResetDirtyFields()
        {
            if (_document == null || !HasDirtyEdits)
            {
                return false;
            }

            ResetBuffers(CollectEditableFields(_document));
            _statusMessage = "Local edits discarded.";
            _error = null;
            GUI.FocusControl(null);
            NotifyDirtyChanged();
            return true;
        }

        /// <summary>
        /// Build stored glossary values for every dirty editable field, including
        /// <c>base_value</c> from the last loaded Grimoire baseline.
        /// </summary>
        public static bool TryBuildDirtyFieldChanges(
            out List<FieldValueUpdate> updates, out string error)
        {
            updates = new List<FieldValueUpdate>();
            error = null;

            if (_document == null)
            {
                error = "No object loaded.";
                return false;
            }

            foreach (var entry in CollectEditableFields(_document))
            {
                var field = entry.Field;
                if (!IsDirty(field.id))
                {
                    continue;
                }

                if (field.hints != null && field.hints.read_only)
                {
                    continue;
                }

                if (!GrimoireFieldSync.IsSupportedEditKind(GrimoireFieldSync.ResolveEditKind(field)))
                {
                    continue;
                }

                if (!GrimoireFieldSync.TryBuildStoredValue(field, GetBuffer(field.id), out var value, out var buildError))
                {
                    error = $"{field.label}: {buildError}";
                    updates = null;
                    return false;
                }

                object baseValue = null;
                if (BaselineBuffers.TryGetValue(field.id, out var baseline) &&
                    !string.IsNullOrEmpty(baseline))
                {
                    if (!GrimoireFieldSync.TryBuildStoredValue(field, baseline, out baseValue, out _))
                    {
                        baseValue = null;
                    }
                }

                updates.Add(new FieldValueUpdate
                {
                    id = field.id,
                    value = value,
                    base_value = baseValue,
                });
            }

            return true;
        }

        /// <summary>
        /// Treat the current edit buffers as the new local baseline after a
        /// commit was queued (object values are not updated until review).
        /// </summary>
        public static void AcceptSubmittedEdits()
        {
            if (_document == null)
            {
                return;
            }

            foreach (var entry in CollectEditableFields(_document))
            {
                var id = entry.Field?.id;
                if (string.IsNullOrEmpty(id) || !EditBuffers.TryGetValue(id, out var current))
                {
                    continue;
                }

                BaselineBuffers[id] = current;
            }

            _statusMessage = "Queued for review in Grimoire.";
            _error = null;
            NotifyDirtyChanged();
        }

        private static void DrawToolbar(ObjectViewDocument document, List<EditableEntry> editable)
        {
            _ = document;
            var dirtyCount = CountDirty(editable);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"{editable.Count} editable field{(editable.Count == 1 ? "" : "s")}" +
                (dirtyCount > 0 ? $"  ·  {dirtyCount} changed" : ""),
                GrimoireEditorStyles.MiniSecondaryStyle);

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(dirtyCount == 0))
            {
                if (GUILayout.Button(
                        new GUIContent("Reset", "Discard local edits and restore values from the loaded Grimoire document."),
                        GUILayout.Width(60)))
                {
                    ResetBuffers(editable);
                    _statusMessage = "Local edits discarded.";
                    _error = null;
                    GUI.FocusControl(null);
                    NotifyDirtyChanged();
                }
            }

            EditorGUILayout.EndHorizontal();

            if (dirtyCount > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(
                    "Commit pending changes from the Sync tab (title + description required).",
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

        private static void DrawEditableField(ViewField field)
        {
            EditorGUILayout.BeginHorizontal();

            var label = field.hints != null && field.hints.required ? $"{field.label} *" : field.label;
            EditorGUILayout.LabelField(
                new GUIContent(label, Tooltip(field)),
                GUILayout.Width(LabelWidth));

            EditorGUILayout.BeginVertical();

            var readOnly = field.hints != null && field.hints.read_only;
            var editKind = GrimoireFieldSync.ResolveEditKind(field);

            EditorGUI.BeginDisabledGroup(readOnly);

            if (!GrimoireFieldSync.IsSupportedEditKind(editKind))
            {
                EditorGUILayout.LabelField(field.plain ?? "Not set", EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    "This kind cannot be edited from the plugin.",
                    EditorStyles.miniLabel);
            }
            else if (editKind == ObjectViewKinds.Boolean)
            {
                var current = string.Equals(GetBuffer(field.id), "true", System.StringComparison.OrdinalIgnoreCase);
                var toggled = EditorGUILayout.Toggle(current);
                if (!readOnly && toggled != current)
                {
                    SetBuffer(field.id, toggled ? "true" : "false");
                }
            }
            else
            {
                var multiline = field.hints != null && field.hints.multiline;
                var buffer = GetBuffer(field.id);
                string next;
                if (multiline)
                {
                    next = EditorGUILayout.TextArea(buffer, GUILayout.MinHeight(48));
                }
                else
                {
                    next = EditorGUILayout.TextField(buffer);
                }

                if (!readOnly && next != buffer)
                {
                    if (field.hints?.character_limit is int limit && limit > 0 && next.Length > limit)
                    {
                        next = next.Substring(0, limit);
                    }

                    SetBuffer(field.id, next);
                }
            }

            EditorGUI.EndDisabledGroup();

            if (readOnly)
            {
                EditorGUILayout.LabelField("Read-only for your role", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        private static void EnsureBuffers(ObjectViewDocument document)
        {
            var key = $"{document.@object?.id}:{document.@object?.updated_at}:{document.schema_version}";
            if (key == _documentKey)
            {
                _document = document;
                return;
            }

            EditBuffers.Clear();
            BaselineBuffers.Clear();
            _documentKey = key;
            _document = document;
            _statusMessage = null;
            _error = null;

            foreach (var entry in CollectEditableFields(document))
            {
                var buffer = GrimoireFieldSync.BufferFromField(entry.Field);
                EditBuffers[entry.Field.id] = buffer;
                BaselineBuffers[entry.Field.id] = buffer;
            }

            NotifyDirtyChanged();
        }

        private static void ResetBuffers(List<EditableEntry> editable)
        {
            foreach (var entry in editable)
            {
                if (BaselineBuffers.TryGetValue(entry.Field.id, out var baseline))
                {
                    EditBuffers[entry.Field.id] = baseline;
                }
            }
        }

        private static List<EditableEntry> CollectEditableFields(ObjectViewDocument document)
        {
            var list = new List<EditableEntry>();
            if (document.sections == null)
            {
                return list;
            }

            foreach (var section in document.sections)
            {
                if (section?.fields == null)
                {
                    continue;
                }

                foreach (var field in section.fields)
                {
                    if (field?.hints == null || !field.hints.game_engine_editable)
                    {
                        continue;
                    }

                    list.Add(new EditableEntry
                    {
                        SectionId = section.id,
                        SectionTitle = string.IsNullOrEmpty(section.title) ? "Fields" : section.title,
                        Field = field,
                    });
                }
            }

            return list;
        }

        private static int CountDirty(List<EditableEntry> editable)
        {
            var count = 0;
            foreach (var entry in editable)
            {
                if (entry.Field.hints != null && entry.Field.hints.read_only)
                {
                    continue;
                }

                if (IsDirty(entry.Field.id))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountAllDirty()
        {
            if (_document == null)
            {
                return 0;
            }

            return CountDirty(CollectEditableFields(_document));
        }

        private static bool IsDirty(string fieldId)
        {
            if (!EditBuffers.TryGetValue(fieldId, out var current))
            {
                return false;
            }

            BaselineBuffers.TryGetValue(fieldId, out var baseline);
            return !string.Equals(current ?? "", baseline ?? "", StringComparison.Ordinal);
        }

        private static string GetBuffer(string fieldId) =>
            EditBuffers.TryGetValue(fieldId, out var value) ? value ?? "" : "";

        private static void SetBuffer(string fieldId, string value)
        {
            EditBuffers[fieldId] = value ?? "";
            _statusMessage = null;
            _error = null;
            NotifyDirtyChanged();
        }

        private static void NotifyDirtyChanged()
        {
            Changed?.Invoke();
        }

        private static string Tooltip(ViewField field)
        {
            if (field.hints == null)
            {
                return field.kind;
            }

            var tooltip = $"{field.hints.field_type} ({field.kind}) · game engine editable";
            if (!string.IsNullOrEmpty(field.hints.documentation))
            {
                tooltip += $"\n{field.hints.documentation}";
            }

            return tooltip;
        }

        private struct EditableEntry
        {
            public string SectionId;
            public string SectionTitle;
            public ViewField Field;
        }
    }
}
