using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Keeps Object Link editable fields and the API snapshot in sync with
    /// Grimoire object views: apply baselines, detect deviations, build commit
    /// payloads. Does not consult the exported C# database.
    /// </summary>
    public static class GrimoireLinkedFieldStore
    {
        public static event Action Changed;

        public sealed class DirtyFieldChange
        {
            public string FieldId;
            public string Label;
            public string Previous;
            public string Current;
        }

        /// <summary>
        /// Reconcile linked fields from an object view. Preserves local edits when
        /// <paramref name="preserveLocalEdits"/> is true; otherwise local values
        /// are reset to the Grimoire baselines.
        /// </summary>
        public static void ApplyFromDocument(
            GrimoireObjectLink link,
            ObjectViewDocument document,
            bool preserveLocalEdits = true)
        {
            if (link == null)
            {
                return;
            }

            if (document == null)
            {
                return;
            }

            var previousById = new Dictionary<string, GrimoireLinkedField>(StringComparer.Ordinal);
            foreach (var existing in link.LinkedFields)
            {
                if (existing == null || string.IsNullOrEmpty(existing.FieldId))
                {
                    continue;
                }

                previousById[existing.FieldId] = existing;
            }

            var next = new List<GrimoireLinkedField>();
            foreach (var entry in CollectEditableFields(document))
            {
                var field = entry.Field;
                var buffer = GrimoireFieldSync.BufferFromField(field);
                previousById.TryGetValue(field.id, out var prior);

                var linked = new GrimoireLinkedField
                {
                    FieldId = field.id,
                    Label = field.label ?? "",
                    Kind = GrimoireFieldSync.ResolveEditKind(field),
                    FieldType = field.hints?.field_type ?? "",
                    SectionId = entry.SectionId ?? "",
                    SectionTitle = entry.SectionTitle ?? "Fields",
                    Multiline = field.hints != null && field.hints.multiline,
                    ReadOnly = field.hints != null && field.hints.read_only,
                    CharacterLimit = field.hints?.character_limit ?? 0,
                    GrimoireValue = buffer,
                    LocalValue = preserveLocalEdits && prior != null
                        ? prior.LocalValue
                        : buffer,
                };

                next.Add(linked);
            }

            Undo.RecordObject(link, "Update Grimoire linked fields");
            link.ReplaceLinkedFields(next);
            var snapshot = GrimoireObjectSnapshotMapper.FromDocument(document);
            foreach (var linked in next)
            {
                if (linked == null || string.IsNullOrEmpty(linked.FieldId))
                {
                    continue;
                }

                snapshot.SetFieldValue(linked.FieldId, linked.LocalValue);
            }

            link.ReplaceSnapshot(snapshot);
            EditorUtility.SetDirty(link);
            NotifyChanged();
        }

        public static bool HasDeviations(GrimoireObjectLink link) =>
            link != null && link.HasFieldDeviations;

        public static int CountDeviations(GrimoireObjectLink link) =>
            link == null ? 0 : link.FieldDeviationCount;

        public static List<DirtyFieldChange> GetDeviations(GrimoireObjectLink link)
        {
            var list = new List<DirtyFieldChange>();
            if (link?.LinkedFields == null)
            {
                return list;
            }

            foreach (var field in link.LinkedFields)
            {
                if (field == null || !field.IsDeviated)
                {
                    continue;
                }

                list.Add(new DirtyFieldChange
                {
                    FieldId = field.FieldId,
                    Label = field.DisplayLabel,
                    Previous = field.GrimoireValue ?? "",
                    Current = field.LocalValue ?? "",
                });
            }

            return list;
        }

        public static bool SetLocalValue(GrimoireObjectLink link, string fieldId, string value)
        {
            if (link == null || string.IsNullOrEmpty(fieldId) ||
                !link.TryGetLinkedField(fieldId, out var field) ||
                field.ReadOnly)
            {
                return false;
            }

            var next = value ?? "";
            if (field.CharacterLimit > 0 && next.Length > field.CharacterLimit)
            {
                next = next.Substring(0, field.CharacterLimit);
            }

            if (string.Equals(field.LocalValue, next, StringComparison.Ordinal))
            {
                return false;
            }

            Undo.RecordObject(link, "Edit Grimoire linked field");
            field.LocalValue = next;
            link.Snapshot?.SetFieldValue(field.FieldId, next);
            EditorUtility.SetDirty(link);
            NotifyChanged();
            return true;
        }

        /// <summary>Discard local edits and restore every field to its Grimoire baseline.</summary>
        public static bool ResetToGrimoire(GrimoireObjectLink link)
        {
            if (link?.LinkedFields == null || !link.HasFieldDeviations)
            {
                return false;
            }

            Undo.RecordObject(link, "Reset Grimoire linked fields");
            foreach (var field in link.LinkedFields)
            {
                if (field == null || field.ReadOnly || !field.IsDeviated)
                {
                    continue;
                }

                field.LocalValue = field.GrimoireValue ?? "";
            }

            link.OverlayLinkedFieldValues();
            EditorUtility.SetDirty(link);
            NotifyChanged();
            return true;
        }

        /// <summary>
        /// After a commit is queued, treat local values as the new Grimoire baseline
        /// (object values are not live until review).
        /// </summary>
        public static void AcceptSubmitted(GrimoireObjectLink link)
        {
            if (link?.LinkedFields == null)
            {
                return;
            }

            Undo.RecordObject(link, "Accept Grimoire linked field submit");
            foreach (var field in link.LinkedFields)
            {
                if (field == null)
                {
                    continue;
                }

                field.GrimoireValue = field.LocalValue ?? "";
            }

            link.OverlayLinkedFieldValues();
            EditorUtility.SetDirty(link);
            NotifyChanged();
        }

        public static void Clear(GrimoireObjectLink link)
        {
            if (link == null)
            {
                return;
            }

            Undo.RecordObject(link, "Clear Grimoire linked fields");
            link.ClearLinkedFields();
            EditorUtility.SetDirty(link);
            NotifyChanged();
        }

        /// <summary>Raise <see cref="Changed"/> after external mutations to linked fields.</summary>
        public static void NotifyExternalChange() => NotifyChanged();

        /// <summary>
        /// Build stored glossary values for every deviant writable field, including
        /// <c>base_value</c> from the cached Grimoire baseline.
        /// </summary>
        public static bool TryBuildDirtyFieldChanges(
            GrimoireObjectLink link,
            out List<FieldValueUpdate> updates,
            out string error)
        {
            updates = new List<FieldValueUpdate>();
            error = null;

            if (link?.LinkedFields == null)
            {
                error = "No linked fields.";
                return false;
            }

            foreach (var linked in link.LinkedFields)
            {
                if (linked == null || !linked.IsDeviated)
                {
                    continue;
                }

                if (!GrimoireFieldSync.IsSupportedEditKind(linked.Kind))
                {
                    continue;
                }

                var viewField = ToViewField(linked);
                if (!GrimoireFieldSync.TryBuildStoredValue(
                        viewField, linked.LocalValue, out var value, out var buildError))
                {
                    error = $"{linked.DisplayLabel}: {buildError}";
                    updates = null;
                    return false;
                }

                object baseValue = null;
                if (!string.IsNullOrEmpty(linked.GrimoireValue))
                {
                    if (!GrimoireFieldSync.TryBuildStoredValue(
                            viewField, linked.GrimoireValue, out baseValue, out _))
                    {
                        baseValue = null;
                    }
                }

                updates.Add(new FieldValueUpdate
                {
                    id = linked.FieldId,
                    value = value,
                    base_value = baseValue,
                });
            }

            return true;
        }

        /// <summary>Fetch the object view and apply linked fields for a scene link.</summary>
        public static async System.Threading.Tasks.Task<ApiResult<ObjectViewDocument>> RefreshFromGrimoireAsync(
            GrimoireObjectLink link,
            bool preserveLocalEdits = true)
        {
            if (link == null)
            {
                return ApiResult<ObjectViewDocument>.Fail("Missing link.", "missing_parameter");
            }

            if (!GrimoireSettings.IsConfigured)
            {
                return ApiResult<ObjectViewDocument>.Fail(
                    "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).",
                    "not_configured");
            }

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var gameId = GrimoireSettings.GameId;
            var objectId = link.CachedObjectId;
            if (string.IsNullOrEmpty(objectId))
            {
                if (!link.HasKey)
                {
                    return ApiResult<ObjectViewDocument>.Fail(
                        "This Grimoire Object Link has no object key.",
                        "missing_parameter");
                }

                var resolved = await GrimoireObjectKeyResolver.ResolveAsync(gameId, link.ObjectKey);
                if (!resolved.Success)
                {
                    return ApiResult<ObjectViewDocument>.Fail(
                        resolved.Error, resolved.Code, resolved.HttpStatus);
                }

                objectId = resolved.Data;
                link.CachedObjectId = objectId;
                EditorUtility.SetDirty(link);
            }

            var view = await GrimoireApiClient.GetObjectViewAsync(
                gameId, objectId, GrimoireSettings.Locale);
            if (!view.Success)
            {
                return view;
            }

            ApplyFromDocument(link, view.Data, preserveLocalEdits);
            if (view.Data?.@object != null)
            {
                GrimoireObjectKeyResolver.RememberSummary(gameId, view.Data.@object);
            }

            return view;
        }

        public static int CountDeviationsInScene()
        {
            var links = new List<GrimoireObjectLink>();
            GrimoireGameEngineDirtyTracker.CollectSceneLinks(links);
            var count = 0;
            foreach (var link in links)
            {
                if (HasDeviations(link))
                {
                    count++;
                }
            }

            return count;
        }

        private static ViewField ToViewField(GrimoireLinkedField linked)
        {
            return new ViewField
            {
                id = linked.FieldId,
                label = linked.Label,
                kind = linked.Kind,
                hints = new FieldHints
                {
                    field_type = linked.FieldType,
                    multiline = linked.Multiline,
                    read_only = linked.ReadOnly,
                    character_limit = linked.CharacterLimit > 0 ? linked.CharacterLimit : (int?)null,
                    game_engine_editable = true,
                },
            };
        }

        private static List<EditableEntry> CollectEditableFields(ObjectViewDocument document)
        {
            var list = new List<EditableEntry>();
            if (document?.sections == null)
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

                    var kind = GrimoireFieldSync.ResolveEditKind(field);
                    if (!GrimoireFieldSync.IsSupportedEditKind(kind))
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

        private static void NotifyChanged() => Changed?.Invoke();

        private struct EditableEntry
        {
            public string SectionId;
            public string SectionTitle;
            public ViewField Field;
        }
    }
}
