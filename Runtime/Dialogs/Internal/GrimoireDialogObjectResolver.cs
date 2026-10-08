using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Grimoire.PluginV2.Internal
{
    /// <summary>
    /// Finds the Grimoire Object Link in the scene that an <c>object_field</c>
    /// reference points at and reads / writes the field on its snapshot. Used
    /// by the dialog runner at play time and by the inspectors to show status.
    /// </summary>
    internal static class GrimoireDialogObjectResolver
    {
        private static GrimoireObjectLink[] _sceneLinks = Array.Empty<GrimoireObjectLink>();
        private static int _scannedFrame = -1;
        private static DateTime _scannedAt = DateTime.MinValue;

        /// <summary>Forget the cached scene scan, e.g. after an Object Link was added in the editor.</summary>
        public static void InvalidateSceneScan()
        {
            _scannedFrame = -1;
            _scannedAt = DateTime.MinValue;
        }

        public static bool IsObjectField(GrimoireDialogValueRef reference)
        {
            return reference != null && reference.IsSet && reference.source == GrimoireDialogValueRef.SourceObjectField;
        }

        /// <summary>
        /// The Object Link (and its snapshot) for the object the reference points at.
        /// Falls back to nested snapshots in <see cref="GrimoireObjectCache"/>, in which
        /// case <paramref name="link"/> is null.
        /// </summary>
        public static bool TryFindLink(GrimoireDialogValueRef reference, out GrimoireObjectLink link, out GrimoireObjectSnapshot snapshot)
        {
            link = null;
            snapshot = null;
            if (!IsObjectField(reference))
            {
                return false;
            }

            return TryFindLink(reference.objectKey, reference.objectId, out link, out snapshot);
        }

        public static bool TryFindLink(string objectKey, string objectId, out GrimoireObjectLink link, out GrimoireObjectSnapshot snapshot)
        {
            link = null;
            snapshot = null;
            if (string.IsNullOrEmpty(objectKey) && string.IsNullOrEmpty(objectId))
            {
                return false;
            }

            foreach (var candidate in SceneLinks())
            {
                if (candidate == null || !LinkMatches(candidate, objectKey, objectId))
                {
                    continue;
                }

                link = candidate;
                snapshot = candidate.Snapshot;
                return true;
            }

            if ((!string.IsNullOrEmpty(objectKey) && GrimoireObjectCache.TryGet(objectKey, out snapshot)) ||
                (!string.IsNullOrEmpty(objectId) && GrimoireObjectCache.TryGet(objectId, out snapshot)))
            {
                return true;
            }

            snapshot = null;
            return false;
        }

        /// <summary>The snapshot field a reference points at, by field id, then export name, then label.</summary>
        public static bool TryFindField(GrimoireObjectSnapshot snapshot, GrimoireDialogValueRef reference, out GrimoireCachedField field)
        {
            field = null;
            if (snapshot == null || reference == null)
            {
                return false;
            }

            foreach (var name in FieldNames(reference))
            {
                if (snapshot.TryGetField(name, out field))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Reads the field value from the scene. False when the object or field is not in the scene.</summary>
        public static bool TryRead(GrimoireDialogValueRef reference, out object value)
        {
            value = null;
            if (!TryFindLink(reference, out _, out var snapshot) || !TryFindField(snapshot, reference, out var field))
            {
                return false;
            }

            value = ToValue(field);
            return true;
        }

        /// <summary>
        /// Writes the field value onto the scene Object Link and records the change
        /// (<paramref name="source"/> names who changed it, e.g. the dialog and node).
        /// False when the object or field is not in the scene.
        /// </summary>
        public static bool TryWrite(GrimoireDialogValueRef reference, object value, string source = null)
        {
            if (!TryFindLink(reference, out var link, out var snapshot) || !TryFindField(snapshot, reference, out var field))
            {
                return false;
            }

            var text = ToText(field, value);
            if (link != null)
            {
                return link.SetFieldValue(field.FieldId, text, source);
            }

            // Nested snapshot without its own Object Link: update the cached values only.
            if (field.Multiple)
            {
                field.Values.Clear();
                foreach (var part in text.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    field.Values.Add(part.Trim());
                }
            }

            field.Value = text;
            GrimoireObjectCache.NotifyUpdated();
            return true;
        }

        /// <summary>Converts a cached field to the value shape the dialog conditions compare with.</summary>
        public static object ToValue(GrimoireCachedField field)
        {
            if (field == null)
            {
                return null;
            }

            if (field.IsReference)
            {
                var names = new List<string>();
                foreach (var stub in field.References)
                {
                    if (stub == null)
                    {
                        continue;
                    }

                    names.Add(stub.HasKey ? stub.CodeId : stub.HasId ? stub.Id : stub.Name);
                }

                return names.Count == 1 ? (object)names[0] : names;
            }

            if (field.Multiple && field.Values.Count > 1)
            {
                return new List<string>(field.Values);
            }

            var raw = field.Value ?? "";
            var kind = field.Kind ?? "";
            if (kind.Equals("boolean", StringComparison.OrdinalIgnoreCase))
            {
                return ParseBool(raw);
            }

            if (kind.Equals("number", StringComparison.OrdinalIgnoreCase))
            {
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0d;
            }

            if (kind.Length == 0)
            {
                if (ParseBoolStrict(raw, out var flag))
                {
                    return flag;
                }

                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return number;
                }
            }

            return raw;
        }

        /// <summary>"character 'Hendrik' (characters/hendrik), field 'has beard'" for messages.</summary>
        public static string Describe(GrimoireDialogValueRef reference)
        {
            if (reference == null)
            {
                return "object field";
            }

            var objectPart = !string.IsNullOrEmpty(reference.objectName) ? $"'{reference.objectName}'" : "object";
            var key = !string.IsNullOrEmpty(reference.objectKey) ? reference.objectKey : reference.objectId;
            if (!string.IsNullOrEmpty(key))
            {
                objectPart += $" ({key})";
            }

            var fieldPart = FirstNonEmpty(reference.fieldName, LabelFieldPart(reference.label), reference.fieldId) ?? "?";
            return $"{objectPart}, field '{fieldPart}'";
        }

        /// <summary>Name shown for the object in inspectors and warnings.</summary>
        public static string ObjectDisplayName(GrimoireDialogValueRef reference)
        {
            if (reference == null)
            {
                return "?";
            }

            return FirstNonEmpty(reference.objectName, LabelObjectPart(reference.label), reference.objectKey, reference.objectId) ?? "?";
        }

        /// <summary>Name shown for the field in inspectors and warnings.</summary>
        public static string FieldDisplayName(GrimoireDialogValueRef reference)
        {
            if (reference == null)
            {
                return "?";
            }

            return FirstNonEmpty(reference.fieldName, LabelFieldPart(reference.label), reference.fieldId) ?? "?";
        }

        private static IEnumerable<GrimoireObjectLink> SceneLinks()
        {
            var now = DateTime.UtcNow;
            var stale = Application.isPlaying
                ? _scannedFrame != Time.frameCount
                : (now - _scannedAt).TotalSeconds > 0.5;
            if (stale)
            {
                _sceneLinks = UnityEngine.Object.FindObjectsOfType<GrimoireObjectLink>(true) ?? Array.Empty<GrimoireObjectLink>();
                _scannedFrame = Time.frameCount;
                _scannedAt = now;
            }

            return _sceneLinks;
        }

        private static bool LinkMatches(GrimoireObjectLink link, string objectKey, string objectId)
        {
            if (!string.IsNullOrEmpty(objectKey) && string.Equals(link.ObjectKey, objectKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(objectId) &&
                (string.Equals(link.CachedObjectId, objectId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(link.ObjectKey, objectId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var snapshot = link.Snapshot;
            return snapshot != null && (snapshot.MatchesIdOrKey(objectKey) || snapshot.MatchesIdOrKey(objectId));
        }

        private static IEnumerable<string> FieldNames(GrimoireDialogValueRef reference)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in new[] { reference.fieldId, reference.fieldName, LabelFieldPart(reference.label) })
            {
                if (!string.IsNullOrEmpty(raw) && seen.Add(raw))
                {
                    yield return raw;
                }
            }
        }

        private static string ToText(GrimoireCachedField field, object value)
        {
            switch (value)
            {
                case null:
                    return "";
                case bool flag:
                    return flag ? "true" : "false";
                case string text:
                    return text;
                case System.Collections.IEnumerable list when !(value is string):
                    var parts = new List<string>();
                    foreach (var item in list)
                    {
                        parts.Add(GrimoireDialogConditions.AsString(item));
                    }

                    return string.Join(",", parts);
                default:
                    if (field != null && string.Equals(field.Kind, "boolean", StringComparison.OrdinalIgnoreCase))
                    {
                        return GrimoireDialogConditions.ToBool(value) ? "true" : "false";
                    }

                    return GrimoireDialogConditions.AsString(value);
            }
        }

        private static bool ParseBool(string raw)
        {
            return ParseBoolStrict(raw, out var flag) && flag;
        }

        private static bool ParseBoolStrict(string raw, out bool flag)
        {
            raw = (raw ?? "").Trim();
            if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" || raw.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                flag = true;
                return true;
            }

            if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0" || raw.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                flag = false;
                return true;
            }

            flag = false;
            return false;
        }

        // Dialog editor labels look like "Hendrik: has beard".
        private static string LabelFieldPart(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return null;
            }

            var colon = label.LastIndexOf(':');
            return colon >= 0 ? label.Substring(colon + 1).Trim() : null;
        }

        private static string LabelObjectPart(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return null;
            }

            var colon = label.LastIndexOf(':');
            return colon > 0 ? label.Substring(0, colon).Trim() : null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }
    }
}
