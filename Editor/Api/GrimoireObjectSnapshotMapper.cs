using System.Collections.Generic;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Converts an Object View document into a runtime
    /// <see cref="GrimoireObjectSnapshot"/> (all fields, reference stubs only).
    /// </summary>
    internal static class GrimoireObjectSnapshotMapper
    {
        public static GrimoireObjectSnapshot FromDocument(ObjectViewDocument document)
        {
            var snapshot = new GrimoireObjectSnapshot();
            if (document == null)
            {
                return snapshot;
            }

            var summary = document.@object;
            if (summary != null)
            {
                snapshot.ObjectId = summary.id ?? "";
                snapshot.ObjectKey = summary.code_id ?? "";
                snapshot.Name = summary.name ?? "";
            }

            if (document.sections == null)
            {
                return snapshot;
            }

            foreach (var section in document.sections)
            {
                if (section?.fields == null)
                {
                    continue;
                }

                var sectionTitle = string.IsNullOrEmpty(section.title) ? "Fields" : section.title;
                foreach (var field in section.fields)
                {
                    if (field == null)
                    {
                        continue;
                    }

                    snapshot.Fields.Add(FromViewField(field, sectionTitle));
                }
            }

            return snapshot;
        }

        /// <summary>
        /// Merge display copy from one localized Object View into a source
        /// snapshot. Raw values remain untouched so conditions and engine writes
        /// keep using language-neutral data.
        /// </summary>
        public static void MergeLocalizedDocument(
            GrimoireObjectSnapshot snapshot,
            ObjectViewDocument document,
            string languageCode)
        {
            if (snapshot == null || document == null || string.IsNullOrWhiteSpace(languageCode))
            {
                return;
            }

            var code = languageCode.Trim();
            if (!string.IsNullOrEmpty(document.@object?.name))
            {
                snapshot.SetLocalizedName(code, document.@object.name);
            }

            if (document.sections == null)
            {
                return;
            }

            foreach (var section in document.sections)
            {
                if (section?.fields == null)
                {
                    continue;
                }

                foreach (var viewField in section.fields)
                {
                    if (viewField == null ||
                        !snapshot.TryGetField(viewField.id, out var cached))
                    {
                        continue;
                    }

                    var localized = FromViewField(
                        viewField,
                        string.IsNullOrEmpty(section.title) ? "Fields" : section.title);
                    cached.SetLocalizedLabel(code, viewField.label);
                    if (cached.IsReference)
                    {
                        MergeReferenceNames(cached, localized, code);
                        continue;
                    }

                    cached.SetLocalizedValues(
                        code,
                        localized.Value,
                        new List<string>(localized.Values));
                }
            }
        }

        private static void MergeReferenceNames(
            GrimoireCachedField target,
            GrimoireCachedField localized,
            string languageCode)
        {
            for (var i = 0; i < localized.References.Count; i++)
            {
                var source = localized.References[i];
                if (source == null || string.IsNullOrEmpty(source.Name))
                {
                    continue;
                }

                GrimoireObjectRef destination = null;
                for (var r = 0; r < target.References.Count; r++)
                {
                    var candidate = target.References[r];
                    if (candidate != null &&
                        ((!string.IsNullOrEmpty(source.Id) && candidate.Id == source.Id) ||
                         (!string.IsNullOrEmpty(source.CodeId) && candidate.CodeId == source.CodeId)))
                    {
                        destination = candidate;
                        break;
                    }
                }

                if (destination == null && i < target.References.Count)
                {
                    destination = target.References[i];
                }

                destination?.SetLocalizedName(languageCode, source.Name);
            }
        }

        private static GrimoireCachedField FromViewField(ViewField field, string sectionTitle)
        {
            var kind = GrimoireFieldSync.ResolveEditKind(field);
            var cached = new GrimoireCachedField
            {
                FieldId = field.id ?? "",
                Label = field.label ?? "",
                Kind = kind,
                FieldType = field.hints?.field_type ?? "",
                SectionTitle = sectionTitle ?? "Fields",
                Multiple = field.multiple,
                GameEngineEditable = field.hints != null && field.hints.game_engine_editable,
            };

            if (kind == ObjectViewKinds.Reference)
            {
                CollectReferences(field, cached.References);
                return cached;
            }

            if (field.multiple && field.HasValues)
            {
                foreach (var value in field.values)
                {
                    cached.Values.Add(ScalarFromValue(kind, value));
                }

                cached.Value = cached.Values.Count > 0 ? cached.Values[0] : "";
                return cached;
            }

            cached.Value = ScalarFromField(kind, field);
            return cached;
        }

        private static void CollectReferences(ViewField field, List<GrimoireObjectRef> destination)
        {
            if (field?.values == null)
            {
                return;
            }

            foreach (var value in field.values)
            {
                var reference = value?.reference;
                if (reference == null)
                {
                    continue;
                }

                destination.Add(new GrimoireObjectRef
                {
                    Id = reference.id ?? "",
                    CodeId = reference.code_id ?? "",
                    Name = reference.name ?? "",
                    Kind = reference.ref_kind ?? "",
                });
            }
        }

        private static string ScalarFromField(string kind, ViewField field)
        {
            if (field == null || !field.HasValues)
            {
                return GrimoireFieldSync.BufferFromField(field);
            }

            return ScalarFromValue(kind, field.values[0]);
        }

        private static string ScalarFromValue(string kind, ViewValue value)
        {
            if (value == null)
            {
                return "";
            }

            if (kind == ObjectViewKinds.Media)
            {
                return value.media?.url ?? value.plain ?? "";
            }

            if (kind == ObjectViewKinds.Link)
            {
                return value.link?.href ?? value.plain ?? "";
            }

            if (kind == ObjectViewKinds.Text)
            {
                return value.plain ?? value.text?.content ?? "";
            }

            if (kind == ObjectViewKinds.Number)
            {
                return GrimoireFieldSync.BufferFromField(new ViewField
                {
                    kind = ObjectViewKinds.Number,
                    values = new[] { value },
                });
            }

            if (kind == ObjectViewKinds.Boolean)
            {
                return value.boolean != null && value.boolean.value ? "true" : "false";
            }

            if (kind == ObjectViewKinds.Vector)
            {
                return GrimoireFieldSync.BufferFromField(new ViewField
                {
                    kind = ObjectViewKinds.Vector,
                    values = new[] { value },
                });
            }

            return value.plain ?? "";
        }
    }
}
