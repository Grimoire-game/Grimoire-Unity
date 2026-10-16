using System;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Variables tab: syncs the game's variables (Grimoire types such as Mood) into
    /// the project and lists them with copyable code and element ids.
    /// </summary>
    public class GrimoireVariablesPanel
    {
        private const float CopyButtonWidth = 52f;
        private const float IdButtonWidth = 64f;

        private Vector2 _scroll;
        private string _search = "";
        private string _status;
        private string _error;
        private bool _syncing;

        public event Action RepaintNeeded;

        public void Activate()
        {
            if (GrimoireSettings.IsConfigured && !_syncing && GrimoireVariableSync.FindLibrary() == null)
            {
                Sync();
            }
        }

        /// <summary>Download the variables again.</summary>
        public void Sync()
        {
            if (GrimoireSettings.IsConfigured && !_syncing)
            {
                SyncAsync();
            }
        }

        public void Draw()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireEditorStyles.DrawInfoBox("Sign in and choose a workspace first to sync variables.");
                return;
            }

            GrimoireEditorStyles.DrawInfoBox(
                "Variables are the values on the Grimoire Variables page, such as the moods in \"Mood\". " +
                "Object fields and dialog setters store an element id; GrimoireVariables turns it into a name. " +
                "Synced to " + GrimoireVariableSync.AssetPath + " so builds read them without signing in.");

            var library = GrimoireVariableSync.FindLibrary();
            DrawToolbar(library);

            if (!string.IsNullOrEmpty(_error))
            {
                GrimoireEditorStyles.DrawErrorBox(_error);
            }
            else if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.LabelField(_status, GrimoireEditorStyles.MiniSecondaryStyle);
            }

            if (library == null)
            {
                if (!_syncing)
                {
                    GrimoireEditorStyles.DrawInfoBox("No variables synced yet. Click Sync variables.");
                }

                return;
            }

            if (!string.IsNullOrEmpty(library.GameId) && library.GameId != GrimoireSettings.GameId)
            {
                GrimoireEditorStyles.DrawErrorBox(
                    "These variables were synced for another game. Click Sync variables to replace them.");
            }

            DrawSearch();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawVariables(library);
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar(GrimoireVariableLibrary library)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(GrimoireSettings.GameName) ? "Variables" : $"Variables in {GrimoireSettings.GameName}",
                GrimoireEditorStyles.TitleStyle);
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_syncing))
            {
                var content = new GUIContent(_syncing ? "Syncing..." : "Sync variables",
                    "Download the game's variables from Grimoire to " + GrimoireVariableSync.AssetPath + ".");
                if (GUILayout.Button(content, GrimoireEditorStyles.PrimaryButtonStyle, GUILayout.Width(120)))
                {
                    Sync();
                }

                using (new EditorGUI.DisabledScope(library == null))
                {
                    if (GUILayout.Button("Select asset", GUILayout.Width(90)))
                    {
                        Selection.activeObject = library;
                        EditorGUIUtility.PingObject(library);
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            if (library != null)
            {
                var count = library.Variables.Count;
                var when = string.IsNullOrEmpty(library.SyncedAt)
                    ? ""
                    : " · synced " + GrimoireExportImporterService.FormatDate(library.SyncedAt);
                EditorGUILayout.LabelField($"{count} {(count == 1 ? "variable" : "variables")}{when}",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.Space(4);
        }

        private void DrawSearch()
        {
            EditorGUILayout.BeginHorizontal();
            _search = EditorGUILayout.TextField(_search ?? "", EditorStyles.toolbarSearchField);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void DrawVariables(GrimoireVariableLibrary library)
        {
            var query = (_search ?? "").Trim();
            var groups = library.Variables
                .Where(v => v != null && Matches(v, query))
                .GroupBy(v => string.IsNullOrEmpty(v.Group) ? "Variables" : v.Group)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            var shown = 0;
            foreach (var group in groups)
            {
                if (!GrimoireEditorStyles.BeginCollapsibleSection($"grimoire-variables:{group.Key}", group.Key))
                {
                    shown += group.Count();
                    continue;
                }

                foreach (var variable in group.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                {
                    DrawVariable(variable, query);
                    EditorGUILayout.Space(4);
                    shown++;
                }

                GrimoireEditorStyles.EndCollapsibleSection();
            }

            if (shown == 0)
            {
                GrimoireEditorStyles.DrawInfoBox(query.Length == 0
                    ? "This game has no variables yet. Create them on the Grimoire Variables page."
                    : $"No variables match \"{query}\".");
            }
        }

        private void DrawVariable(GrimoireVariable variable, string query)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(variable.Name, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(variable.ElementType, GrimoireEditorStyles.MiniSecondaryStyle);
            var findSnippet = $"var {Identifier(variable.Name)} = GrimoireVariables.Find({Quote(variable.Name)});";
            if (GUILayout.Button(new GUIContent("Copy", findSnippet), GUILayout.Width(CopyButtonWidth)))
            {
                Copy(findSnippet, $"Copied code for \"{variable.Name}\" to the clipboard.");
            }

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(variable.Description))
            {
                EditorGUILayout.LabelField(variable.Description, EditorStyles.wordWrappedMiniLabel);
            }

            var nameMatches = query.Length == 0 || Contains(variable.Name, query);
            foreach (var element in variable.Elements)
            {
                if (element != null && (nameMatches || Matches(element, query)))
                {
                    DrawElement(variable, element);
                }
            }

            if (variable.Elements.Count == 0)
            {
                EditorGUILayout.LabelField("No elements.", GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawElement(GrimoireVariable variable, GrimoireVariableElement element)
        {
            var snippet = ElementSnippet(variable, element);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            EditorGUILayout.LabelField(
                new GUIContent(element.DisplayName, string.IsNullOrEmpty(element.Description) ? element.Id : element.Description),
                GUILayout.Width(160));
            EditorGUILayout.LabelField(ValuePreview(element), GrimoireEditorStyles.MiniSecondaryStyle);

            if (GUILayout.Button(new GUIContent("Copy", snippet), GUILayout.Width(CopyButtonWidth)))
            {
                Copy(snippet, $"Copied code for \"{variable.Name}: {element.Name}\" to the clipboard.");
            }

            if (GUILayout.Button(new GUIContent("Copy ID", element.Id), GUILayout.Width(IdButtonWidth)))
            {
                Copy(element.Id, $"Copied the id of \"{variable.Name}: {element.Name}\" ({element.Id}).");
            }

            EditorGUILayout.EndHorizontal();
        }

        private static string ElementSnippet(GrimoireVariable variable, GrimoireVariableElement element)
        {
            var variableName = Quote(variable.Name);
            var elementName = Quote(element.Name);
            switch (variable.ElementType)
            {
                case "number":
                case "percentage":
                    return $"var {Identifier(element.Name)} = GrimoireVariables.GetValue<float>({variableName}, {elementName});";
                case "boolean":
                    return $"var {Identifier(element.Name)} = GrimoireVariables.GetValue<bool>({variableName}, {elementName});";
                default:
                    var local = Identifier(variable.Name);
                    return
                        $"// An object field of type \"{variable.Name}\" holds this element:\n" +
                        $"if (link.TryGetVariableElement({variableName}, out var {local}) && {local}.Id == {Quote(element.Id)})\n" +
                        "{\n" +
                        $"    // {local}.Name == {elementName}\n" +
                        "}";
            }
        }

        private static string ValuePreview(GrimoireVariableElement element)
        {
            if (element.HasRange)
            {
                return $"{Format(element.RangeMin)} – {Format(element.RangeMax)}";
            }

            var value = element.AuthoredValue;
            switch (value)
            {
                case null:
                    return "";
                case bool flag:
                    return flag ? "true" : "false";
                case double number:
                    return Format(number);
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private static string Format(double number)
        {
            return number.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static bool Matches(GrimoireVariable variable, string query)
        {
            return query.Length == 0
                   || Contains(variable.Name, query)
                   || Contains(variable.Group, query)
                   || variable.Elements.Any(e => e != null && Matches(e, query));
        }

        private static bool Matches(GrimoireVariableElement element, string query)
        {
            return Contains(element.Name, query) || Contains(element.Id, query);
        }

        private static bool Contains(string source, string query) =>
            !string.IsNullOrEmpty(source) && source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private void Copy(string text, string message)
        {
            EditorGUIUtility.systemCopyBuffer = text;
            _status = message;
            _error = null;
            GUI.FocusControl(null);
        }

        private async void SyncAsync()
        {
            _syncing = true;
            _error = null;
            RequestRepaint();

            var result = await GrimoireVariableSync.SyncAsync(GrimoireSettings.GameId, message =>
            {
                _status = message;
                RequestRepaint();
            });

            _syncing = false;
            if (result.Success)
            {
                var count = result.Data.Variables.Count;
                _status = $"Synced {count} {(count == 1 ? "variable" : "variables")}.";
            }
            else
            {
                _status = null;
                _error = "Could not sync variables: " + result.Error;
            }

            RequestRepaint();
        }

        private static string Identifier(string text)
        {
            var builder = new System.Text.StringBuilder();
            var upperNext = false;
            foreach (var c in text ?? "")
            {
                if (!char.IsLetterOrDigit(c))
                {
                    upperNext = builder.Length > 0;
                    continue;
                }

                builder.Append(builder.Length == 0 ? char.ToLowerInvariant(c) : upperNext ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }

            if (builder.Length == 0)
            {
                return "value";
            }

            if (char.IsDigit(builder[0]))
            {
                builder.Insert(0, '_');
            }

            return builder.ToString();
        }

        private static string Quote(string value) =>
            "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private void RequestRepaint()
        {
            RepaintNeeded?.Invoke();
        }
    }
}
