using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Draws the <c>game_engine_data</c> array from an Object View Document —
    /// linked engine scene instances, with live Unity diffs and reset-to-Grimoire.
    /// </summary>
    public static class GrimoireGameEngineDataRenderer
    {
        private const float LabelWidth = 120f;

        public static void Draw(ObjectViewDocument document, GrimoireObjectLink sceneLink = null)
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

            var pending = sceneLink != null
                ? GrimoireGameEngineDirtyTracker.FindPending(sceneLink)
                : null;
            var anyLocalDirty = pending != null && pending.IsDirty;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"{instances.Length} linked instance{(instances.Length == 1 ? "" : "s")}" +
                (anyLocalDirty ? "  ·  local changes" : ""),
                GrimoireEditorStyles.MiniSecondaryStyle);

            GUILayout.FlexibleSpace();

            if (sceneLink != null)
            {
                using (new EditorGUI.DisabledScope(!anyLocalDirty && !HasMatchingLocalDiff(sceneLink, instances)))
                {
                    if (GUILayout.Button(
                            new GUIContent("Reset to Grimoire", "Restore this Unity object's transform from the values saved on Grimoire."),
                            GUILayout.Width(120)))
                    {
                        ResetMatching(sceneLink, instances);
                    }
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);

            for (var i = 0; i < instances.Length; i++)
            {
                DrawInstance(instances[i], i, sceneLink);
            }
        }

        private static bool HasMatchingLocalDiff(GrimoireObjectLink sceneLink, GameEngineInstance[] instances)
        {
            if (sceneLink == null || instances == null)
            {
                return false;
            }

            var engineId = GrimoireGameEngineSync.GetEngineInstanceId(sceneLink.gameObject);
            var scene = GrimoireGameEngineSync.GetSceneName(sceneLink.gameObject);
            foreach (var instance in instances)
            {
                if (!MatchesLink(instance, engineId, scene))
                {
                    continue;
                }

                return HasLiveDiff(sceneLink, instance);
            }

            return false;
        }

        private static void ResetMatching(GrimoireObjectLink sceneLink, GameEngineInstance[] instances)
        {
            var engineId = GrimoireGameEngineSync.GetEngineInstanceId(sceneLink.gameObject);
            var scene = GrimoireGameEngineSync.GetSceneName(sceneLink.gameObject);
            foreach (var instance in instances)
            {
                if (!MatchesLink(instance, engineId, scene))
                {
                    continue;
                }

                if (GrimoireGameEngineSync.ResetTransformFromInstance(sceneLink, instance))
                {
                    return;
                }
            }

            // Fallback: fetch latest from Grimoire in case the loaded document is stale.
            ResetAsync(sceneLink);
        }

        private static async void ResetAsync(GrimoireObjectLink link)
        {
            var result = await GrimoireGameEngineSync.ResetTransformFromGrimoireAsync(link);
            if (!result.Success)
            {
                Debug.LogWarning($"[Grimoire] Reset failed: {result.Error}");
            }
        }

        private static void DrawInstance(GameEngineInstance instance, int index, GrimoireObjectLink preferredLink)
        {
            var link = ResolveLink(instance, preferredLink);
            var liveDiff = link != null && HasLiveDiff(link, instance);
            var title = BuildInstanceTitle(instance, index);
            if (liveDiff)
            {
                title += "  ·  changed";
            }

            var sectionId = $"game-engine:{instance.id ?? index.ToString()}";

            if (!GrimoireEditorStyles.BeginCollapsibleSection(sectionId, title, defaultExpanded: index == 0 || liveDiff))
            {
                return;
            }

            DrawRow("ID", instance.id);
            DrawRow("Engine instance", instance.engine_instance_id);
            DrawRow("Scene", instance.scene);

            if (link != null)
            {
                var t = link.transform;
                DrawVectorCompare(
                    "Location",
                    instance.location,
                    t.position,
                    link.SyncPosition,
                    compareEuler: false);
                DrawVectorCompare(
                    "Rotation",
                    instance.rotation,
                    t.eulerAngles,
                    link.SyncRotation,
                    compareEuler: true);
                DrawVectorCompare(
                    "Scale",
                    instance.scale,
                    t.localScale,
                    link.SyncScale,
                    compareEuler: false);

                if (link.SyncIdName)
                {
                    var savedName = ExtractName(instance.engine_instance_id) ?? link.gameObject.name;
                    var dirty = !string.Equals(savedName, link.gameObject.name, System.StringComparison.Ordinal);
                    DrawCompareRow("Id / Name", savedName, link.gameObject.name, dirty);
                }
                else
                {
                    DrawRow("Id / Name", instance.engine_instance_id);
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(!liveDiff))
                {
                    if (GUILayout.Button(
                            new GUIContent("Reset to Grimoire", "Discard local transform changes and restore values saved on Grimoire."),
                            GUILayout.Width(130)))
                    {
                        GrimoireGameEngineSync.ResetTransformFromInstance(link, instance);
                    }
                }

                if (GUILayout.Button("Select", GUILayout.Width(70)))
                {
                    Selection.activeGameObject = link.gameObject;
                    EditorGUIUtility.PingObject(link.gameObject);
                }

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                DrawVectorRow("Location", instance.location);
                DrawVectorRow("Rotation", instance.rotation);
                DrawVectorRow("Scale", instance.scale);
                EditorGUILayout.HelpBox(
                    "No matching GameObject with a Grimoire Object Link was found in open scenes for this instance.",
                    MessageType.Info);
            }

            GrimoireEditorStyles.EndCollapsibleSection();
        }

        private static GrimoireObjectLink ResolveLink(GameEngineInstance instance, GrimoireObjectLink preferredLink)
        {
            if (preferredLink != null)
            {
                var engineId = GrimoireGameEngineSync.GetEngineInstanceId(preferredLink.gameObject);
                var scene = GrimoireGameEngineSync.GetSceneName(preferredLink.gameObject);
                if (MatchesLink(instance, engineId, scene))
                {
                    return preferredLink;
                }
            }

            return GrimoireGameEngineSync.TryFindSceneLink(instance, out var found) ? found : null;
        }

        private static bool MatchesLink(GameEngineInstance instance, string engineInstanceId, string scene)
        {
            if (instance == null)
            {
                return false;
            }

            if (!string.Equals(instance.scene ?? "", scene ?? "", System.StringComparison.Ordinal))
            {
                return false;
            }

            var left = GrimoireGameEngineSync.NormalizeEngineInstanceId(instance.engine_instance_id);
            var right = GrimoireGameEngineSync.NormalizeEngineInstanceId(engineInstanceId);
            return string.Equals(left, right, System.StringComparison.Ordinal);
        }

        private static bool HasLiveDiff(GrimoireObjectLink link, GameEngineInstance instance)
        {
            var t = link.transform;
            if (link.SyncPosition && GrimoireGameEngineSync.IsVectorDirty(instance.location, t.position))
            {
                return true;
            }

            if (link.SyncRotation && GrimoireGameEngineSync.IsVectorDirty(instance.rotation, t.eulerAngles, compareEuler: true))
            {
                return true;
            }

            if (link.SyncScale && GrimoireGameEngineSync.IsVectorDirty(instance.scale, t.localScale))
            {
                return true;
            }

            if (link.SyncIdName)
            {
                var savedName = ExtractName(instance.engine_instance_id);
                if (!string.IsNullOrEmpty(savedName) &&
                    !string.Equals(savedName, link.gameObject.name, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void DrawVectorCompare(
            string label,
            GameEngineVector3 saved,
            Vector3 live,
            bool syncEnabled,
            bool compareEuler)
        {
            if (!syncEnabled)
            {
                DrawVectorRow(label, saved);
                return;
            }

            var dirty = GrimoireGameEngineSync.IsVectorDirty(saved, live, compareEuler);
            var savedText = saved != null ? saved.ToString() : "—";
            var liveText = FormatVector(live);
            DrawCompareRow(label, savedText, liveText, dirty);
        }

        private static void DrawCompareRow(string label, string saved, string live, bool dirty)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(LabelWidth));
            if (dirty)
            {
                EditorGUILayout.LabelField(
                    $"{saved}  →  {live}",
                    EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                EditorGUILayout.SelectableLabel(saved, EditorStyles.miniLabel, GUILayout.Height(16));
            }

            EditorGUILayout.EndHorizontal();
        }

        private static string FormatVector(Vector3 value) =>
            $"({FormatComponent(value.x)}, {FormatComponent(value.y)}, {FormatComponent(value.z)})";

        private static string FormatComponent(float value) =>
            Mathf.Abs(value % 1f) < 0.0001f ? value.ToString("0") : value.ToString("0.###");

        private static string ExtractName(string engineInstanceId)
        {
            if (string.IsNullOrEmpty(engineInstanceId))
            {
                return null;
            }

            var separator = engineInstanceId.IndexOf('|');
            return separator >= 0 && separator < engineInstanceId.Length - 1
                ? engineInstanceId.Substring(separator + 1)
                : null;
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
