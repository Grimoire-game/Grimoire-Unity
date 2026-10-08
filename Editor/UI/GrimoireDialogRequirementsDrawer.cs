using System;
using System.Collections.Generic;
using Grimoire.PluginV2.Internal;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Inspector section listing the Grimoire objects and fields a dialog reads,
    /// whether the open scene can supply them, and buttons to fix what is
    /// missing. Shared by the Dialog Player and dialog asset inspectors.
    /// </summary>
    public sealed class GrimoireDialogRequirementsDrawer
    {
        private GrimoireDialogAsset _dialog;
        private string _stamp;
        private bool _includeLinked = true;
        private List<GrimoireDialogObjectRequirement> _requirements = new List<GrimoireDialogObjectRequirement>();
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.Ordinal);

        private bool _busy;
        private string _status;
        private MessageType _statusType = MessageType.Info;

        /// <summary>Called when the owning inspector should repaint.</summary>
        public Action Repaint;

        /// <summary>Counts objects and how many of them the scene cannot fully supply (speakers excluded).</summary>
        public static void Summarize(IReadOnlyList<GrimoireDialogObjectRequirement> requirements, out int objects, out int missing)
        {
            objects = 0;
            missing = 0;
            if (requirements == null)
            {
                return;
            }

            foreach (var requirement in requirements)
            {
                if (requirement.speakerOnly)
                {
                    continue;
                }

                objects++;
                if (!IsSatisfied(requirement))
                {
                    missing++;
                }
            }
        }

        public static bool IsSatisfied(GrimoireDialogObjectRequirement requirement)
        {
            if (requirement == null)
            {
                return true;
            }

            if (!GrimoireDialogObjectRequirements.IsInScene(requirement, out _, out var snapshot))
            {
                return false;
            }

            foreach (var field in requirement.fields)
            {
                if (!GrimoireDialogObjectResolver.TryFindField(snapshot, field.reference, out _))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Forget cached requirements, e.g. after the asset was re-imported.</summary>
        public void Invalidate()
        {
            _stamp = null;
        }

        public void Draw(GrimoireDialogAsset dialog, bool showLinkedToggle = true)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Required Grimoire objects", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (showLinkedToggle && dialog != null && dialog.LinkedDialogs.Count > 0)
            {
                var include = GUILayout.Toggle(_includeLinked, new GUIContent("Include linked dialogs",
                    "Also list objects used by the dialogs this one jumps to."), EditorStyles.miniLabel);
                if (include != _includeLinked)
                {
                    _includeLinked = include;
                    Invalidate();
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (dialog == null)
            {
                EditorGUILayout.LabelField("Pick a dialog to see which Grimoire objects it reads.", GrimoireEditorStyles.MiniSecondaryStyle);
                EditorGUILayout.EndVertical();
                return;
            }

            EnsureCollected(dialog);

            if (_requirements.Count == 0)
            {
                EditorGUILayout.LabelField("This dialog does not read any Grimoire objects.", GrimoireEditorStyles.MiniSecondaryStyle);
                EditorGUILayout.EndVertical();
                return;
            }

            Summarize(_requirements, out var objects, out var missing);
            var summary = objects == 0
                ? "Only speakers are referenced."
                : missing == 0
                    ? $"{objects} object{(objects == 1 ? "" : "s")}, all available in the scene."
                    : $"{objects} object{(objects == 1 ? "" : "s")}, {missing} not available in the scene. " +
                      "Conditions on missing objects use the Grimoire library value.";
            EditorGUILayout.LabelField(summary, missing == 0 ? GrimoireEditorStyles.MiniSecondaryStyle : EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(2);

            foreach (var requirement in _requirements)
            {
                DrawObject(requirement);
            }

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox(_status, _statusType);
            }

            EditorGUILayout.EndVertical();
        }

        private void EnsureCollected(GrimoireDialogAsset dialog)
        {
            var stamp = dialog.GetInstanceID() + "|" + dialog.ImportedAt + "|" + _includeLinked;
            if (dialog == _dialog && stamp == _stamp)
            {
                return;
            }

            _dialog = dialog;
            _stamp = stamp;
            _requirements = GrimoireDialogObjectRequirements.Collect(dialog, _includeLinked);
        }

        private void DrawObject(GrimoireDialogObjectRequirement requirement)
        {
            var inScene = GrimoireDialogObjectRequirements.IsInScene(requirement, out var link, out _);
            var satisfied = requirement.speakerOnly || (inScene && IsSatisfied(requirement));
            var key = ObjectKey(requirement);
            var collapsed = _collapsed.Contains(key);

            EditorGUILayout.BeginHorizontal();

            var icon = requirement.speakerOnly
                ? Icon(inScene ? "TestPassed" : "console.infoicon.sml")
                : Icon(satisfied ? "TestPassed" : inScene ? "console.warnicon.sml" : "console.erroricon.sml");
            GUILayout.Label(icon, GUILayout.Width(18), GUILayout.Height(EditorGUIUtility.singleLineHeight));

            var title = new GUIContent(requirement.DisplayName, ObjectTooltip(requirement, inScene, link));
            if (requirement.fields.Count > 0)
            {
                var expanded = GUILayout.Toggle(!collapsed, title, EditorStyles.foldout, GUILayout.ExpandWidth(false));
                if (expanded)
                {
                    _collapsed.Remove(key);
                }
                else
                {
                    _collapsed.Add(key);
                }

                collapsed = !expanded;
            }
            else
            {
                GUILayout.Label(title, GUILayout.ExpandWidth(false));
            }

            GUILayout.FlexibleSpace();

            var keyLabel = requirement.HasKey ? requirement.objectKey : requirement.speakerOnly ? "speaker" : "no Code ID";
            GUILayout.Label(keyLabel, GrimoireEditorStyles.MiniSecondaryStyle);

            using (new EditorGUI.DisabledScope(_busy))
            {
                if (link != null)
                {
                    if (GUILayout.Button(new GUIContent("Select", "Select the Object Link in the hierarchy."), EditorStyles.miniButtonLeft, GUILayout.Width(52)))
                    {
                        Selection.activeGameObject = link.gameObject;
                        EditorGUIUtility.PingObject(link.gameObject);
                    }

                    if (GUILayout.Button(new GUIContent("Refresh", "Reload the Object Link's fields from Grimoire."), EditorStyles.miniButtonRight, GUILayout.Width(56)))
                    {
                        RefreshAsync(link);
                    }
                }
                else if (inScene)
                {
                    GUILayout.Label(new GUIContent("cached", "Known through a nested snapshot of another Object Link."), GrimoireEditorStyles.MiniSecondaryStyle);
                }
                else
                {
                    var disabled = Application.isPlaying;
                    using (new EditorGUI.DisabledScope(disabled))
                    {
                        if (GUILayout.Button(new GUIContent("Add to scene",
                                disabled ? "Exit Play Mode to add Object Links." : "Add a GameObject with an Object Link for this object and load its fields."),
                            EditorStyles.miniButton, GUILayout.Width(90)))
                        {
                            AddAsync(requirement);
                        }
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            if (requirement.speakerOnly || collapsed)
            {
                return;
            }

            EditorGUI.indentLevel += 2;
            foreach (var field in requirement.fields)
            {
                DrawField(field);
            }

            EditorGUI.indentLevel -= 2;
            EditorGUILayout.Space(2);
        }

        private static void DrawField(GrimoireDialogFieldRequirement field)
        {
            var status = GrimoireDialogObjectRequirements.Evaluate(field, out var value);
            var ok = status == GrimoireDialogFieldStatus.InScene;

            EditorGUILayout.BeginHorizontal();
            var label = new GUIContent(field.DisplayName, FieldTooltip(field));
            EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));

            var valueText = value == null ? "(empty)" : GrimoireDialogConditions.AsString(value);
            if (valueText.Length > 40)
            {
                valueText = valueText.Substring(0, 37) + "...";
            }

            var kind = string.IsNullOrEmpty(field.fieldType) ? "" : field.fieldType + " ";
            var access = field.isWritten ? (field.isRead ? "read/write" : "written") : "read";
            GUILayout.Label($"{kind}= {valueText}", ok ? EditorStyles.miniLabel : EditorStyles.miniBoldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(new GUIContent(StatusLabel(status) + " · " + access, StatusTooltip(status)), GrimoireEditorStyles.MiniSecondaryStyle);
            EditorGUILayout.EndHorizontal();
        }

        private async void AddAsync(GrimoireDialogObjectRequirement requirement)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            SetStatus($"Adding '{requirement.DisplayName}'...", MessageType.Info);
            try
            {
                var link = await GrimoireDialogSceneSpawner.AddObjectLinkAsync(requirement, message => SetStatus(message, MessageType.Info));
                if (link != null && !IsSatisfied(requirement))
                {
                    SetStatus(_status + " Some fields are still missing; check the field list.", MessageType.Warning);
                }
            }
            catch (Exception error)
            {
                SetStatus($"Could not add '{requirement.DisplayName}': {error.Message}", MessageType.Error);
            }
            finally
            {
                _busy = false;
                GrimoireDialogObjectResolver.InvalidateSceneScan();
                Repaint?.Invoke();
            }
        }

        private async void RefreshAsync(GrimoireObjectLink link)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            SetStatus($"Refreshing '{link.gameObject.name}'...", MessageType.Info);
            try
            {
                var error = await GrimoireDialogSceneSpawner.RefreshObjectLinkAsync(link);
                SetStatus(error ?? "Fields refreshed.", error == null ? MessageType.Info : MessageType.Warning);
            }
            catch (Exception error)
            {
                SetStatus($"Refresh failed: {error.Message}", MessageType.Error);
            }
            finally
            {
                _busy = false;
                Repaint?.Invoke();
            }
        }

        private void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            Repaint?.Invoke();
        }

        private static string ObjectKey(GrimoireDialogObjectRequirement requirement)
        {
            return requirement.HasKey ? requirement.objectKey : requirement.objectId;
        }

        private static string ObjectTooltip(GrimoireDialogObjectRequirement requirement, bool inScene, GrimoireObjectLink link)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(requirement.objectId))
            {
                lines.Add("UUID: " + requirement.objectId);
            }

            if (requirement.speakerOnly)
            {
                lines.Add("Used as a speaker only; no fields are read.");
            }

            lines.Add(link != null ? $"Object Link: {link.gameObject.name}"
                : inScene ? "Known from a nested snapshot."
                : "No Object Link in the scene.");

            if (requirement.dialogs.Count > 0)
            {
                var names = new List<string>();
                foreach (var dialog in requirement.dialogs)
                {
                    if (dialog != null)
                    {
                        names.Add(dialog.DisplayName);
                    }
                }

                lines.Add("Used in: " + string.Join(", ", names));
            }

            return string.Join("\n", lines);
        }

        private static string FieldTooltip(GrimoireDialogFieldRequirement field)
        {
            var lines = new List<string> { "Field id: " + field.fieldId };
            if (field.usedBy.Count > 0)
            {
                lines.Add("Used by: " + string.Join(", ", field.usedBy));
            }

            return string.Join("\n", lines);
        }

        private static string StatusLabel(GrimoireDialogFieldStatus status)
        {
            switch (status)
            {
                case GrimoireDialogFieldStatus.InScene: return "scene";
                case GrimoireDialogFieldStatus.FieldMissingInScene: return "not in snapshot";
                case GrimoireDialogFieldStatus.FromExport: return "export";
                case GrimoireDialogFieldStatus.AuthoredFallback: return "library value";
                default: return "no value";
            }
        }

        private static string StatusTooltip(GrimoireDialogFieldStatus status)
        {
            switch (status)
            {
                case GrimoireDialogFieldStatus.InScene:
                    return "Read from the Object Link in the scene.";
                case GrimoireDialogFieldStatus.FieldMissingInScene:
                    return "The Object Link is in the scene, but its snapshot has no such field. Click Refresh on the object.";
                case GrimoireDialogFieldStatus.FromExport:
                    return "No Object Link in the scene; the value comes from the imported export's ObjectRuntime.";
                case GrimoireDialogFieldStatus.AuthoredFallback:
                    return "No Object Link in the scene; the dialog uses the value from the Grimoire library at import time.";
                default:
                    return "No Object Link, export, or library value. The condition sees an empty value.";
            }
        }

        private static GUIContent Icon(string name)
        {
            var content = EditorGUIUtility.IconContent(name);
            return content != null && content.image != null ? new GUIContent(content.image) : new GUIContent("•");
        }
    }
}
