using System.Collections.Generic;
using Grimoire.PluginV2.Internal;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    [CustomEditor(typeof(GrimoireDialogAsset))]
    public class GrimoireDialogAssetEditor : UnityEditor.Editor
    {
        private static bool _variablesExpanded;

        private readonly GrimoireDialogRequirementsDrawer _requirements = new GrimoireDialogRequirementsDrawer();

        private void OnEnable()
        {
            _requirements.Repaint = Repaint;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        }

        private void OnHierarchyChanged()
        {
            GrimoireDialogObjectResolver.InvalidateSceneScan();
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            var dialog = (GrimoireDialogAsset)target;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(dialog.DisplayName, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(dialog.Description))
            {
                EditorGUILayout.LabelField(dialog.Description, EditorStyles.wordWrappedMiniLabel);
            }

            Row("Key", string.IsNullOrEmpty(dialog.DialogKey) ? "-" : dialog.DialogKey);
            Row("Nodes", dialog.Nodes.Count.ToString());
            Row("Start node", string.IsNullOrEmpty(dialog.StartingNode) ? "-" : dialog.StartingNode);
            Row("Speakers", dialog.SpeakerMode);
            Row("Updated in Grimoire", GrimoireExportImporterService.FormatDate(dialog.UpdatedAt));
            Row("Imported", GrimoireExportImporterService.FormatDate(dialog.ImportedAt));
            if (dialog.LinkedDialogs.Count > 0)
            {
                var names = new List<string>();
                foreach (var linked in dialog.LinkedDialogs)
                {
                    if (linked != null)
                    {
                        names.Add(linked.DisplayName);
                    }
                }

                Row("Jumps to", string.Join(", ", names));
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Update from Grimoire", GUILayout.Width(150)))
            {
                GrimoireConnectWindow.OpenDialogs();
            }

            EditorGUILayout.LabelField("Play it with GrimoireDialogPlayer.Main.StartDialog(asset).", GrimoireEditorStyles.MiniSecondaryStyle);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);
            _variablesExpanded = EditorGUILayout.Foldout(_variablesExpanded, $"Variables ({dialog.Variables.Count})", true);
            if (_variablesExpanded)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                if (dialog.Variables.Count == 0)
                {
                    EditorGUILayout.LabelField("No dialog variables.", GrimoireEditorStyles.MiniSecondaryStyle);
                }

                foreach (var variable in dialog.Variables)
                {
                    if (variable == null)
                    {
                        continue;
                    }

                    var initial = variable.initialValue != null && variable.initialValue.HasValue ? variable.initialValue.ToString() : "(none)";
                    Row(variable.name, $"{variable.type} = {initial}");
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(6);
            _requirements.Draw(dialog);
        }

        private static void Row(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GrimoireEditorStyles.MiniSecondaryStyle, GUILayout.Width(130));
            EditorGUILayout.LabelField(value ?? "", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndHorizontal();
        }
    }
}
