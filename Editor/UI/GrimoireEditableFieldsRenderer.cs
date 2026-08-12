using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Draws <c>hints.game_engine_editable</c> fields with editors and Sync /
    /// Reset controls so Unity ↔ Grimoire values stay aligned.
    /// </summary>
    public static class GrimoireEditableFieldsRenderer
    {
        private const float LabelWidth = 150f;

        private static string _documentKey;
        private static readonly Dictionary<string, string> EditBuffers = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> BaselineBuffers = new Dictionary<string, string>();
        private static string _statusMessage;
        private static string _error;
        private static bool _syncing;

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
            _statusMessage = null;
            _error = null;
            _syncing = false;
        }

        /// <summary>
        /// True when the currently loaded object's editable field buffers diverge
        /// from the last loaded Grimoire values.
        /// </summary>
        public static bool HasDirtyEditsForObject(string objectId)
        {
            if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(_documentKey))
            {
                return false;
            }

            if (!_documentKey.StartsWith(objectId + ":", System.StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var pair in EditBuffers)
            {
                BaselineBuffers.TryGetValue(pair.Key, out var baseline);
                if (!string.Equals(pair.Value ?? "", baseline ?? "", System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void DrawToolbar(ObjectViewDocument document, List<EditableEntry> editable)
        {
            var dirtyCount = CountDirty(editable);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"{editable.Count} editable field{(editable.Count == 1 ? "" : "s")}" +
                (dirtyCount > 0 ? $"  ·  {dirtyCount} changed" : ""),
                GrimoireEditorStyles.MiniSecondaryStyle);

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_syncing || dirtyCount == 0))
            {
                if (GUILayout.Button(
                        new GUIContent("Reset", "Discard local edits and restore values from the loaded Grimoire document."),
                        GUILayout.Width(60)))
                {
                    ResetBuffers(editable);
                    _statusMessage = "Local edits discarded.";
                    _error = null;
                    GUI.FocusControl(null);
                }

                if (GUILayout.Button(
                        new GUIContent("Sync to Grimoire", "Push edited field values to Grimoire so the web app stays in sync."),
                        GUILayout.Width(120)))
                {
                    SyncAsync(document, editable);
                }
            }

            EditorGUILayout.EndHorizontal();

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

        private static async void SyncAsync(ObjectViewDocument document, List<EditableEntry> editable)
        {
            var objectId = document.@object?.id;
            var gameId = GrimoireSettings.GameId;
            if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(gameId))
            {
                _error = "Missing object or game id.";
                return;
            }

            var updates = new List<FieldValueUpdate>();
            foreach (var entry in editable)
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

                if (!GrimoireFieldSync.TryBuildStoredValue(field, GetBuffer(field.id), out var value, out var error))
                {
                    _error = $"{field.label}: {error}";
                    return;
                }

                updates.Add(new FieldValueUpdate { id = field.id, value = value });
            }

            if (updates.Count == 0)
            {
                _statusMessage = "Nothing to sync.";
                return;
            }

            _syncing = true;
            _error = null;
            _statusMessage = "Syncing...";

            var result = await GrimoireFieldSync.PushFieldUpdatesAsync(gameId, objectId, updates);
            _syncing = false;

            if (!result.Success)
            {
                _error = result.Error ?? "Sync failed.";
                _statusMessage = null;
                return;
            }

            EnsureBuffers(result.Data);
            _statusMessage = $"Synced {updates.Count} field{(updates.Count == 1 ? "" : "s")} to Grimoire.";
            _error = null;
            GUI.FocusControl(null);
        }

        private static void EnsureBuffers(ObjectViewDocument document)
        {
            var key = $"{document.@object?.id}:{document.@object?.updated_at}:{document.schema_version}";
            if (key == _documentKey)
            {
                return;
            }

            EditBuffers.Clear();
            BaselineBuffers.Clear();
            _documentKey = key;
            _statusMessage = null;
            _error = null;

            foreach (var entry in CollectEditableFields(document))
            {
                var buffer = GrimoireFieldSync.BufferFromField(entry.Field);
                EditBuffers[entry.Field.id] = buffer;
                BaselineBuffers[entry.Field.id] = buffer;
            }
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

        private static bool IsDirty(string fieldId)
        {
            if (!EditBuffers.TryGetValue(fieldId, out var current))
            {
                return false;
            }

            BaselineBuffers.TryGetValue(fieldId, out var baseline);
            return !string.Equals(current ?? "", baseline ?? "", System.StringComparison.Ordinal);
        }

        private static string GetBuffer(string fieldId) =>
            EditBuffers.TryGetValue(fieldId, out var value) ? value ?? "" : "";

        private static void SetBuffer(string fieldId, string value)
        {
            EditBuffers[fieldId] = value ?? "";
            _statusMessage = null;
            _error = null;
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
