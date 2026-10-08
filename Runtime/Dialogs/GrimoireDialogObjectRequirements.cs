using System;
using System.Collections.Generic;
using System.Text;
using Grimoire.PluginV2.Internal;

namespace Grimoire.PluginV2
{
    /// <summary>Where a dialog can read an object field from right now.</summary>
    public enum GrimoireDialogFieldStatus
    {
        /// <summary>A Grimoire Object Link in the scene has the field.</summary>
        InScene,

        /// <summary>The Object Link is in the scene, but its snapshot does not contain the field. Refresh the link.</summary>
        FieldMissingInScene,

        /// <summary>No Object Link in the scene; the imported export's ObjectRuntime has a value.</summary>
        FromExport,

        /// <summary>No Object Link in the scene; the dialog uses the library value baked in at import.</summary>
        AuthoredFallback,

        /// <summary>No value anywhere. The condition sees an empty value.</summary>
        Unavailable,
    }

    /// <summary>One object field a dialog reads or writes.</summary>
    public sealed class GrimoireDialogFieldRequirement
    {
        public string fieldId = "";
        public string fieldName = "";
        public string fieldType = "";

        /// <summary>A representative reference (used to read the live value).</summary>
        public GrimoireDialogValueRef reference;

        /// <summary>"Dialog / node" labels of every place that uses the field.</summary>
        public List<string> usedBy = new List<string>();

        public bool isRead;
        public bool isWritten;

        public string DisplayName => !string.IsNullOrEmpty(fieldName) ? fieldName : fieldId;
    }

    /// <summary>One Grimoire object a dialog depends on, with the fields it touches.</summary>
    public sealed class GrimoireDialogObjectRequirement
    {
        public string objectId = "";
        public string objectKey = "";
        public string objectName = "";

        /// <summary>True when the object is only used as a speaker, never for a field.</summary>
        public bool speakerOnly;

        public List<GrimoireDialogFieldRequirement> fields = new List<GrimoireDialogFieldRequirement>();
        public List<GrimoireDialogAsset> dialogs = new List<GrimoireDialogAsset>();

        public bool HasKey => !string.IsNullOrEmpty(objectKey);
        public string DisplayName => !string.IsNullOrEmpty(objectName) ? objectName : HasKey ? objectKey : objectId;

        public GrimoireDialogFieldRequirement FindField(string fieldId)
        {
            foreach (var field in fields)
            {
                if (string.Equals(field.fieldId, fieldId, StringComparison.Ordinal))
                {
                    return field;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Lists the Grimoire objects and fields a dialog depends on and checks
    /// whether the open scene can supply them. Used by the Dialog Player at
    /// start and by the inspectors.
    /// </summary>
    public static class GrimoireDialogObjectRequirements
    {
        /// <summary>Every object the dialog (and, optionally, the dialogs it jumps to) reads or writes.</summary>
        public static List<GrimoireDialogObjectRequirement> Collect(GrimoireDialogAsset dialog, bool includeLinkedDialogs = true)
        {
            var result = new List<GrimoireDialogObjectRequirement>();
            if (dialog == null)
            {
                return result;
            }

            var byObject = new Dictionary<string, GrimoireDialogObjectRequirement>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<GrimoireDialogAsset>();
            var queue = new Queue<GrimoireDialogAsset>();
            queue.Enqueue(dialog);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == null || !visited.Add(current))
                {
                    continue;
                }

                CollectFrom(current, byObject, result);

                if (!includeLinkedDialogs)
                {
                    continue;
                }

                foreach (var linked in current.LinkedDialogs)
                {
                    if (linked != null)
                    {
                        queue.Enqueue(linked);
                    }
                }
            }

            result.Sort((a, b) =>
            {
                var bySpeaker = a.speakerOnly.CompareTo(b.speakerOnly);
                return bySpeaker != 0 ? bySpeaker : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>Whether an Object Link for the object is in the scene.</summary>
        public static bool IsInScene(GrimoireDialogObjectRequirement requirement, out GrimoireObjectLink link, out GrimoireObjectSnapshot snapshot)
        {
            link = null;
            snapshot = null;
            return requirement != null &&
                   GrimoireDialogObjectResolver.TryFindLink(requirement.objectKey, requirement.objectId, out link, out snapshot);
        }

        /// <summary>Where the field would be read from, and the value the dialog would see.</summary>
        public static GrimoireDialogFieldStatus Evaluate(GrimoireDialogFieldRequirement field, out object value)
        {
            value = null;
            var reference = field?.reference;
            if (reference == null)
            {
                return GrimoireDialogFieldStatus.Unavailable;
            }

            if (GrimoireDialogObjectResolver.TryFindLink(reference, out _, out var snapshot))
            {
                if (GrimoireDialogObjectResolver.TryFindField(snapshot, reference, out var cached))
                {
                    value = GrimoireDialogObjectResolver.ToValue(cached);
                    return GrimoireDialogFieldStatus.InScene;
                }

                FallbackValue(reference, out value);
                return GrimoireDialogFieldStatus.FieldMissingInScene;
            }

            if (GrimoireDialogExportBridge.TryReadObjectField(reference, out value))
            {
                return GrimoireDialogFieldStatus.FromExport;
            }

            if (reference.authoredValue != null && reference.authoredValue.HasValue)
            {
                value = reference.authoredValue.ToObject();
                return GrimoireDialogFieldStatus.AuthoredFallback;
            }

            return GrimoireDialogFieldStatus.Unavailable;
        }

        /// <summary>
        /// One message listing every object that is not in the scene, or null
        /// when everything resolves. Speaker-only objects are not reported.
        /// </summary>
        public static string BuildMissingReport(GrimoireDialogAsset dialog, IReadOnlyList<GrimoireDialogObjectRequirement> requirements)
        {
            if (dialog == null || requirements == null || requirements.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            var missing = 0;
            foreach (var requirement in requirements)
            {
                if (requirement.speakerOnly || requirement.fields.Count == 0)
                {
                    continue;
                }

                var inScene = IsInScene(requirement, out _, out var snapshot);
                var problems = new List<string>();
                foreach (var field in requirement.fields)
                {
                    if (inScene && GrimoireDialogObjectResolver.TryFindField(snapshot, field.reference, out _))
                    {
                        continue;
                    }

                    var status = Evaluate(field, out var value);
                    var detail = status == GrimoireDialogFieldStatus.FieldMissingInScene ? "not in the Object Link snapshot, refresh the link"
                        : status == GrimoireDialogFieldStatus.FromExport ? "uses the export value"
                        : status == GrimoireDialogFieldStatus.AuthoredFallback ? $"uses the library value {GrimoireDialogConditions.AsString(value)}"
                        : "no value, counts as empty";
                    problems.Add($"    - {field.DisplayName}: {detail} (used by {string.Join(", ", field.usedBy)})");
                }

                if (problems.Count == 0)
                {
                    continue;
                }

                missing++;
                var key = requirement.HasKey ? requirement.objectKey : requirement.objectId;
                builder.AppendLine(inScene
                    ? $"  {requirement.DisplayName} ({key}) is in the scene, but some fields are missing from its snapshot:"
                    : $"  {requirement.DisplayName} ({key}) has no Object Link in the scene:");
                foreach (var problem in problems)
                {
                    builder.AppendLine(problem);
                }
            }

            if (missing == 0)
            {
                return null;
            }

            return $"'{dialog.DisplayName}' reads Grimoire objects that the scene cannot supply. " +
                   "Select the dialog asset and use Required Grimoire objects > Add to scene to fix this.\n" + builder.ToString().TrimEnd();
        }

        private static void CollectFrom(
            GrimoireDialogAsset dialog,
            Dictionary<string, GrimoireDialogObjectRequirement> byObject,
            List<GrimoireDialogObjectRequirement> result)
        {
            var speakerIsObject = string.Equals(dialog.SpeakerMode, "object", StringComparison.OrdinalIgnoreCase);
            foreach (var node in dialog.Nodes)
            {
                if (node == null)
                {
                    continue;
                }

                var where = $"{dialog.DisplayName} / {node.identifier}";
                Add(byObject, result, dialog, node.conditionSubjectRef, where, false);
                Add(byObject, result, dialog, node.conditionCheckRef, where, false);
                if (node.setter != null)
                {
                    Add(byObject, result, dialog, node.setter.targetRef, where, true);
                    Add(byObject, result, dialog, node.setter.valueRef, where, false);
                }

                if (node.conditionBranches != null)
                {
                    foreach (var branch in node.conditionBranches)
                    {
                        Add(byObject, result, dialog, branch?.valueRef, where, false);
                    }
                }

                if (node.options != null)
                {
                    foreach (var option in node.options)
                    {
                        if (option == null)
                        {
                            continue;
                        }

                        Add(byObject, result, dialog, option.action?.targetRef, where, true);
                        Add(byObject, result, dialog, option.action?.valueRef, where, false);
                        Add(byObject, result, dialog, option.visibleWhen?.subjectRef, where, false);
                        Add(byObject, result, dialog, option.visibleWhen?.valueRef, where, false);
                    }
                }

                if (speakerIsObject && !string.IsNullOrEmpty(node.speakerId))
                {
                    var speaker = GetOrAdd(byObject, result, node.speakerId, "", node.speakerName, true);
                    AddDialog(speaker, dialog);
                }
            }
        }

        private static void Add(
            Dictionary<string, GrimoireDialogObjectRequirement> byObject,
            List<GrimoireDialogObjectRequirement> result,
            GrimoireDialogAsset dialog,
            GrimoireDialogValueRef reference,
            string where,
            bool written)
        {
            if (!GrimoireDialogObjectResolver.IsObjectField(reference))
            {
                return;
            }

            if (string.IsNullOrEmpty(reference.objectId) && string.IsNullOrEmpty(reference.objectKey))
            {
                return;
            }

            var requirement = GetOrAdd(byObject, result, reference.objectId, reference.objectKey,
                GrimoireDialogObjectResolver.ObjectDisplayName(reference), false);
            requirement.speakerOnly = false;
            AddDialog(requirement, dialog);

            var fieldId = !string.IsNullOrEmpty(reference.fieldId) ? reference.fieldId : GrimoireDialogObjectResolver.FieldDisplayName(reference);
            var field = requirement.FindField(fieldId);
            if (field == null)
            {
                field = new GrimoireDialogFieldRequirement
                {
                    fieldId = fieldId,
                    fieldName = GrimoireDialogObjectResolver.FieldDisplayName(reference),
                    fieldType = reference.fieldType ?? "",
                    reference = reference,
                };
                requirement.fields.Add(field);
            }

            if (string.IsNullOrEmpty(field.fieldType) && !string.IsNullOrEmpty(reference.fieldType))
            {
                field.fieldType = reference.fieldType;
            }

            field.isRead |= !written;
            field.isWritten |= written;
            if (!field.usedBy.Contains(where))
            {
                field.usedBy.Add(where);
            }
        }

        private static GrimoireDialogObjectRequirement GetOrAdd(
            Dictionary<string, GrimoireDialogObjectRequirement> byObject,
            List<GrimoireDialogObjectRequirement> result,
            string objectId,
            string objectKey,
            string objectName,
            bool speakerOnly)
        {
            GrimoireDialogObjectRequirement requirement = null;
            if (!string.IsNullOrEmpty(objectId))
            {
                byObject.TryGetValue(objectId, out requirement);
            }

            if (requirement == null && !string.IsNullOrEmpty(objectKey))
            {
                byObject.TryGetValue(objectKey, out requirement);
            }

            if (requirement == null)
            {
                requirement = new GrimoireDialogObjectRequirement
                {
                    objectId = objectId ?? "",
                    objectKey = objectKey ?? "",
                    objectName = objectName ?? "",
                    speakerOnly = speakerOnly,
                };
                result.Add(requirement);
            }

            if (string.IsNullOrEmpty(requirement.objectKey) && !string.IsNullOrEmpty(objectKey))
            {
                requirement.objectKey = objectKey;
            }

            if (string.IsNullOrEmpty(requirement.objectId) && !string.IsNullOrEmpty(objectId))
            {
                requirement.objectId = objectId;
            }

            if (string.IsNullOrEmpty(requirement.objectName) && !string.IsNullOrEmpty(objectName))
            {
                requirement.objectName = objectName;
            }

            if (!string.IsNullOrEmpty(requirement.objectId))
            {
                byObject[requirement.objectId] = requirement;
            }

            if (!string.IsNullOrEmpty(requirement.objectKey))
            {
                byObject[requirement.objectKey] = requirement;
            }

            return requirement;
        }

        private static void AddDialog(GrimoireDialogObjectRequirement requirement, GrimoireDialogAsset dialog)
        {
            if (dialog != null && !requirement.dialogs.Contains(dialog))
            {
                requirement.dialogs.Add(dialog);
            }
        }

        private static void FallbackValue(GrimoireDialogValueRef reference, out object value)
        {
            if (GrimoireDialogExportBridge.TryReadObjectField(reference, out value))
            {
                return;
            }

            value = reference.authoredValue != null && reference.authoredValue.HasValue ? reference.authoredValue.ToObject() : null;
        }
    }
}
