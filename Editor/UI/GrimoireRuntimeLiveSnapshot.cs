using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    public partial class GrimoireRuntimePanel
    {
        private void CaptureLiveSnapshot()
        {
            if (!EditorApplication.isPlaying) return;
            var observed = BuildObservedValues();
            var now = DateTime.Now;

            foreach (var pair in observed)
            {
                var next = pair.Value;
                if (_liveValues.TryGetValue(pair.Key, out var previous))
                {
                    next.InitialValue = previous.InitialValue;
                    next.LastChanged = previous.LastChanged;
                    if (!string.Equals(previous.Value, next.Value, StringComparison.Ordinal))
                    {
                        next.LastChanged = now;
                        if (_hasLiveBaseline && !_capturePaused)
                            RecordLiveChange(next, previous.Value, next.Value, next.Source, now);
                    }
                }
                else
                {
                    next.InitialValue = next.Value;
                    if (_hasLiveBaseline && !_capturePaused)
                    {
                        next.LastChanged = now;
                        RecordLiveChange(next, "(not linked)", next.Value, next.Source, now);
                    }
                }
                _liveValues[pair.Key] = next;
            }

            foreach (var id in _liveValues.Keys.Where(id => !observed.ContainsKey(id)).ToArray())
            {
                var removed = _liveValues[id];
                // Logic events are momentary, rather than a value exposed by a live source.
                if (id.StartsWith("logic|", StringComparison.Ordinal)) continue;
                if (_hasLiveBaseline && !_capturePaused)
                    RecordLiveChange(removed, removed.Value, "(unlinked)", removed.Source, now);
                _liveValues.Remove(id);
            }
            _hasLiveBaseline = true;
        }

        private Dictionary<string, LiveValue> BuildObservedValues()
        {
            var values = new Dictionary<string, LiveValue>(StringComparer.Ordinal);

            foreach (var obj in _objects)
            {
                if (obj.SceneGameObject == null) continue;
                var link = obj.SceneGameObject.GetComponent<GrimoireObjectLink>();
                var source = link == null ? "Object Runtime" : link.ActiveSource.ToString();
                var display = string.IsNullOrEmpty(obj.TypeName) ? obj.SceneObjectName : obj.TypeName;
                foreach (var field in obj.EditableFields)
                {
                    AddObserved(values, ObjectFieldId(obj.SceneGameObject, field.Name), "Objects",
                        display + " · " + field.Name, obj.Key + "." + field.Name,
                        field.EditBuffer, source, obj.SceneGameObject);
                }
            }

            foreach (var variable in _variables)
                AddObserved(values, "export-variable|" + variable.Path, "Variables",
                    variable.FieldName, variable.Path, variable.CurrentValue, "VariableRuntime", null);

            foreach (var variable in GrimoireVariables.All)
            {
                if (variable == null) continue;
                foreach (var element in variable.Elements)
                {
                    if (element == null) continue;
                    AddObserved(values, "variable|" + element.Id, "Variables",
                        variable.Name + " · " + element.DisplayName,
                        variable.Name + "." + element.Name,
                        FormatLiveValue(GrimoireVariables.GetValue(element.Id)), "GrimoireVariables", null);
                }
            }

            foreach (var player in UnityEngine.Object.FindObjectsOfType<GrimoireDialogPlayer>(true))
            {
                var prefix = "dialog|" + player.GetInstanceID() + "|";
                var dialogName = player.CurrentDialog?.DisplayName ?? player.LastStartedDialog?.DisplayName ?? player.name;
                var dialogKey = player.CurrentDialog?.DialogKey ?? player.LastStartedDialog?.DialogKey ?? player.name;
                AddObserved(values, prefix + "state", "Dialogs", dialogName + " · State",
                    dialogKey + ".state", player.IsPlaying ? "Playing" : "Idle", "Dialog Player", player.gameObject);
                AddObserved(values, prefix + "node", "Dialogs", dialogName + " · Current node",
                    dialogKey + ".current_node", player.CurrentLine?.NodeId ?? "(none)", "Dialog Player", player.gameObject);
                AddObserved(values, prefix + "text", "Dialogs", dialogName + " · Displayed line",
                    dialogKey + ".text", player.CurrentLine?.Text ?? "", "Dialog Player", player.gameObject);
                AddObserved(values, prefix + "choices", "Dialogs", dialogName + " · Choices",
                    dialogKey + ".choices", (player.CurrentLine?.Choices.Count ?? 0).ToString(), "Dialog Player", player.gameObject);
            }

            for (var i = 0; i < _dialogs.Count; i++)
            {
                var dialog = _dialogs[i];
                AddObserved(values, "export-dialog|" + i + "|section", "Dialogs", dialog.DialogKey + " · Section",
                    dialog.DialogKey + ".section", dialog.CurrentSection, "Dialog Runtime", null);
                AddObserved(values, "export-dialog|" + i + "|state", "Dialogs", dialog.DialogKey + " · State",
                    dialog.DialogKey + ".state", dialog.State, "Dialog Runtime", null);
                foreach (var member in dialog.Properties)
                    if (member.Value != null && member.Value.Length <= 256)
                        AddObserved(values, "export-dialog|" + i + "|" + member.Key, "Dialogs",
                            dialog.DialogKey + " · " + member.Key, dialog.DialogKey + "." + member.Key,
                            member.Value, "Dialog Runtime", null);
            }

            foreach (var link in UnityEngine.Object.FindObjectsOfType<GrimoireTextLink>(true))
            {
                if (!link.HasCode) continue;
                var prefix = "text|" + link.GetInstanceID();
                AddObserved(values, prefix, "Text links", link.gameObject.name + " · Text",
                    link.TextCode, link.GetResolvedText(), "Text Link", link.gameObject);
                AddObserved(values, prefix + "|language", "Text links", link.gameObject.name + " · Language",
                    link.TextCode + ".language", link.ResolveActiveLanguage(), "Text Link", link.gameObject);
            }

            foreach (var logic in _logicEntries)
                AddObserved(values, "logic-registry|" + logic.ObjectKey, "Logic",
                    logic.ObjectKey + " · Sections", logic.ObjectKey + ".sections",
                    logic.Sections.Count.ToString(), "Logic Runtime", null);

            var linkedObjects = new HashSet<GameObject>();
            foreach (var link in UnityEngine.Object.FindObjectsOfType<GrimoireObjectLink>(true))
                if (link.HasKey) linkedObjects.Add(link.gameObject);
            foreach (var link in UnityEngine.Object.FindObjectsOfType<GrimoireTextLink>(true))
                if (link.HasCode) linkedObjects.Add(link.gameObject);
            foreach (var player in UnityEngine.Object.FindObjectsOfType<GrimoireDialogPlayer>(true))
                linkedObjects.Add(player.gameObject);
            foreach (var gameObject in linkedObjects)
            {
                if (gameObject == null) continue;
                var prefix = "scene|" + gameObject.GetInstanceID() + "|";
                var path = gameObject.scene.name + "/" + gameObject.name;
                AddObserved(values, prefix + "active", "Scene links", gameObject.name + " · Active",
                    path + ".active", gameObject.activeSelf.ToString(), "Unity scene", gameObject);
                AddObserved(values, prefix + "position", "Scene links", gameObject.name + " · Position",
                    path + ".position", gameObject.transform.position.ToString("F2"), "Unity scene", gameObject);
                AddObserved(values, prefix + "rotation", "Scene links", gameObject.name + " · Rotation",
                    path + ".rotation", gameObject.transform.eulerAngles.ToString("F2"), "Unity scene", gameObject);
                AddObserved(values, prefix + "scale", "Scene links", gameObject.name + " · Scale",
                    path + ".scale", gameObject.transform.localScale.ToString("F2"), "Unity scene", gameObject);
            }

            foreach (var component in _sceneComponents)
            {
                if (component.Component == null || component.Component is GrimoireObjectLink ||
                    component.Component is GrimoireTextLink || component.Component is GrimoireDialogPlayer) continue;
                foreach (var member in component.PublicMembers)
                {
                    if (member.Value == null || member.Value.Length > 256 ||
                        member.Key == "name" || member.Key == "tag" || member.Key == "gameObject" ||
                        member.Key == "transform" || member.Key == "hideFlags") continue;
                    AddObserved(values, "component|" + component.Component.GetInstanceID() + "|" + member.Key,
                        "Scene links", component.GameObjectName + " · " + member.Key,
                        component.GameObjectName + "." + component.TypeName + "." + member.Key,
                        member.Value, component.TypeName, component.Component.gameObject);
                }
            }
            return values;
        }

        private static void AddObserved(Dictionary<string, LiveValue> values, string id, string category,
            string name, string path, string value, string source, GameObject sceneObject)
        {
            if (string.IsNullOrEmpty(id)) return;
            values[id] = new LiveValue
            {
                Id = id, Category = category, Name = name ?? "", Path = path ?? "",
                Value = value ?? "", Source = source ?? "", SceneObject = sceneObject
            };
        }
    }
}
