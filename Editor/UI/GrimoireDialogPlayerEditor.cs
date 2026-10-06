using Grimoire.PluginV2.Internal;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    [CustomEditor(typeof(GrimoireDialogPlayer))]
    public class GrimoireDialogPlayerEditor : UnityEditor.Editor
    {
        private static bool _eventsExpanded;
        private static bool _variablesExpanded = true;

        private GrimoireDialogAsset _testDialog;

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            var player = (GrimoireDialogPlayer)target;
            serializedObject.Update();

            DrawUsage();

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GrimoireDialogPlayer.language)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GrimoireDialogPlayer.keepVariablesBetweenRuns)));

            EditorGUILayout.Space(4);
            _eventsExpanded = EditorGUILayout.Foldout(_eventsExpanded, "Events", true);
            if (_eventsExpanded)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GrimoireDialogPlayer.OnLine)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GrimoireDialogPlayer.OnChoices)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GrimoireDialogPlayer.OnDialogStarted)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GrimoireDialogPlayer.OnDialogEnded)));
            }

            serializedObject.ApplyModifiedProperties();

            if (Application.isPlaying)
            {
                EditorGUILayout.Space(8);
                DrawPlayback(player);
            }
        }

        private static void DrawUsage()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                "This player plays every dialog in the scene. Start one from your code:",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.SelectableLabel(
                "GrimoireDialogPlayer.Main.StartDialog(dialogAsset);",
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button("Import dialogs (Dialogs tab)"))
            {
                GrimoireConnectWindow.OpenDialogs();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawPlayback(GrimoireDialogPlayer player)
        {
            EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var runner = player.Runner;
            if (!player.IsPlaying)
            {
                EditorGUILayout.LabelField($"Not playing (last end: {player.LastEndReason}).", GrimoireEditorStyles.MiniSecondaryStyle);
                if (_testDialog == null)
                {
                    _testDialog = player.LastStartedDialog;
                }

                EditorGUILayout.BeginHorizontal();
                _testDialog = (GrimoireDialogAsset)EditorGUILayout.ObjectField(_testDialog, typeof(GrimoireDialogAsset), false);
                using (new EditorGUI.DisabledScope(_testDialog == null))
                {
                    if (GUILayout.Button("Start", GUILayout.Width(60)))
                    {
                        player.StartDialog(_testDialog);
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                var node = runner.CurrentNode;
                var line = player.CurrentLine;
                EditorGUILayout.LabelField("Dialog", runner.CurrentDialog != null ? runner.CurrentDialog.DisplayName : "-");
                EditorGUILayout.LabelField("Node", $"{node.identifier} ({node.type})");
                if (line != null)
                {
                    if (line.HasSpeaker)
                    {
                        EditorGUILayout.LabelField("Speaker", line.Speaker);
                    }

                    EditorGUILayout.LabelField(line.Text, EditorStyles.wordWrappedLabel);

                    if (line.IsChoice)
                    {
                        foreach (var choice in line.Choices)
                        {
                            if (GUILayout.Button($"{choice.Index + 1}. {choice.Text}"))
                            {
                                player.Choose(choice.Index);
                                break;
                            }
                        }
                    }
                }

                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(line != null && line.IsChoice))
                {
                    if (GUILayout.Button("Next"))
                    {
                        player.Next();
                    }
                }

                if (GUILayout.Button("Restart"))
                {
                    player.StartDialog(player.LastStartedDialog);
                }

                if (GUILayout.Button("Stop"))
                {
                    player.Stop();
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();

            _variablesExpanded = EditorGUILayout.Foldout(_variablesExpanded, "Variables", true);
            if (!_variablesExpanded)
            {
                return;
            }

            var any = false;
            foreach (var entry in runner.Variables.All())
            {
                any = true;
                var owner = entry.Dialog != null ? entry.Dialog.DisplayName : "?";
                EditorGUILayout.LabelField($"{owner} / {entry.Name}", GrimoireDialogConditions.AsString(entry.Value));
            }

            if (!any)
            {
                EditorGUILayout.LabelField("No variables used yet.", GrimoireEditorStyles.MiniSecondaryStyle);
            }
        }
    }
}
