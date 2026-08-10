using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Draws the <c>game_engine_data</c> array from an Object View Document —
    /// linked engine scene instances (transforms) synced from game engine plugins.
    /// </summary>
    public static class GrimoireGameEngineDataRenderer
    {
        private const float LabelWidth = 120f;

        public static void Draw(ObjectViewDocument document)
        {
            if (document == null)
            {
                GrimoireEditorStyles.DrawInfoBox("No object loaded.");
                return;
            }

            var instances = document.game_engine_data;
            if (instances == null || instances.Length == 0)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "No game engine instances are linked to this object. " +
                    "Engine plugins can sync scene instances via PATCH /api/v1/objects/{id}.");
                return;
            }

            EditorGUILayout.LabelField(
                $"{instances.Length} linked instance{(instances.Length == 1 ? "" : "s")}",
                GrimoireEditorStyles.MiniSecondaryStyle);
            EditorGUILayout.Space(4);

            for (var i = 0; i < instances.Length; i++)
            {
                DrawInstance(instances[i], i);
            }
        }

        private static void DrawInstance(GameEngineInstance instance, int index)
        {
            var title = BuildInstanceTitle(instance, index);
            var sectionId = $"game-engine:{instance.id ?? index.ToString()}";

            if (!GrimoireEditorStyles.BeginCollapsibleSection(sectionId, title, defaultExpanded: index == 0))
            {
                return;
            }

            DrawRow("ID", instance.id);
            DrawRow("Engine instance", instance.engine_instance_id);
            DrawRow("Scene", instance.scene);
            DrawVectorRow("Location", instance.location);
            DrawVectorRow("Rotation", instance.rotation);
            DrawVectorRow("Scale", instance.scale);

            GrimoireEditorStyles.EndCollapsibleSection();
        }

        private static string BuildInstanceTitle(GameEngineInstance instance, int index)
        {
            if (!string.IsNullOrEmpty(instance.scene))
            {
                return instance.scene;
            }

            if (!string.IsNullOrEmpty(instance.engine_instance_id))
            {
                return instance.engine_instance_id;
            }

            return $"Instance {index + 1}";
        }

        private static void DrawRow(string label, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(LabelWidth));
            EditorGUILayout.SelectableLabel(value, EditorStyles.miniLabel, GUILayout.Height(16));
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawVectorRow(string label, GameEngineVector3 vector)
        {
            if (vector == null)
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(LabelWidth));
            EditorGUILayout.SelectableLabel(vector.ToString(), EditorStyles.miniLabel, GUILayout.Height(16));
            EditorGUILayout.EndHorizontal();
        }
    }
}
