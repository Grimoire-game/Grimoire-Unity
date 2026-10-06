using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Lists every field available on the selected object and offers a copyable
    /// C# snippet that reads it at runtime through <see cref="GrimoireObjectLink"/>
    /// or <see cref="GrimoireObjectData"/>.
    /// </summary>
    public static class GrimoireVariablesRenderer
    {
        private const float LabelWidth = 150f;
        private const float CopyButtonWidth = 52f;

        private enum AccessMode
        {
            ObjectLink = 0,
            ObjectKey = 1,
        }

        private static readonly GUIContent[] AccessModeLabels =
        {
            new GUIContent("Object Link", "Read through the GrimoireObjectLink component on the GameObject."),
            new GUIContent("Object key", "Read by object key through GrimoireObjectData, from any script."),
        };

        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
            "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
            "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
            "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
            "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
            "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct",
            "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
            "ushort", "using", "virtual", "void", "volatile", "while", "var", "link", "obj",
        };

        private static AccessMode _accessMode = AccessMode.ObjectLink;
        private static string _search = "";
        private static string _statusMessage;

        public static void Draw(ObjectViewDocument document, GrimoireObjectLink link)
        {
            var snapshot = ResolveSnapshot(document, link);
            if (snapshot == null || snapshot.Fields.Count == 0)
            {
                GrimoireEditorStyles.DrawInfoBox("No variables available on this object.");
                return;
            }

            var objectKey = !string.IsNullOrEmpty(link?.ObjectKey) ? link.ObjectKey : snapshot.ObjectKey;

            DrawToolbar(snapshot.Fields.Count);
            EditorGUILayout.Space(4);

            string currentSection = null;
            var sectionOpen = false;
            var shown = 0;
            foreach (var field in snapshot.Fields)
            {
                if (field == null || !MatchesSearch(field))
                {
                    continue;
                }

                var sectionTitle = string.IsNullOrEmpty(field.SectionTitle) ? "Fields" : field.SectionTitle;
                if (sectionTitle != currentSection)
                {
                    if (sectionOpen)
                    {
                        GrimoireEditorStyles.EndCollapsibleSection();
                    }

                    currentSection = sectionTitle;
                    sectionOpen = GrimoireEditorStyles.BeginCollapsibleSection(
                        $"variables-section:{sectionTitle}", sectionTitle, defaultExpanded: true);
                }

                shown++;
                if (sectionOpen)
                {
                    DrawField(snapshot, field, objectKey);
                }
            }

            if (sectionOpen)
            {
                GrimoireEditorStyles.EndCollapsibleSection();
            }

            if (shown == 0)
            {
                GrimoireEditorStyles.DrawInfoBox($"No variables match \"{_search}\".");
            }
        }

        private static GrimoireObjectSnapshot ResolveSnapshot(ObjectViewDocument document, GrimoireObjectLink link)
        {
            if (link != null && link.HasSnapshot)
            {
                return link.Snapshot;
            }

            return document != null ? GrimoireObjectSnapshotMapper.FromDocument(document) : null;
        }

        private static void DrawToolbar(int fieldCount)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"{fieldCount} variable{(fieldCount == 1 ? "" : "s")}",
                GrimoireEditorStyles.MiniSecondaryStyle,
                GUILayout.Width(90));

            _search = EditorGUILayout.TextField(_search ?? "", EditorStyles.toolbarSearchField);

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Access from", GrimoireEditorStyles.MiniSecondaryStyle, GUILayout.Width(90));
            _accessMode = (AccessMode)GUILayout.Toolbar((int)_accessMode, AccessModeLabels, EditorStyles.miniButton);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(_statusMessage, GrimoireEditorStyles.MiniSecondaryStyle);
            }
        }

        private static bool MatchesSearch(GrimoireCachedField field)
        {
            if (string.IsNullOrWhiteSpace(_search))
            {
                return true;
            }

            var query = _search.Trim();
            return Contains(field.DisplayLabel, query)
                   || Contains(field.FieldId, query)
                   || Contains(field.FieldType, query)
                   || Contains(field.Kind, query);
        }

        private static bool Contains(string source, string query) =>
            !string.IsNullOrEmpty(source) && source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void DrawField(GrimoireObjectSnapshot snapshot, GrimoireCachedField field, string objectKey)
        {
            var lookupName = LookupName(snapshot, field);
            var variableName = VariableName(field);
            var expression = BuildExpression(field, lookupName, variableName);
            var snippet = BuildSnippet(field, expression, variableName, objectKey);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                new GUIContent(field.DisplayLabel, Tooltip(field)),
                EditorStyles.boldLabel,
                GUILayout.Width(LabelWidth));
            EditorGUILayout.LabelField(
                new GUIContent(FormatPreview(ValuePreview(field)), ValuePreview(field)),
                EditorStyles.label);
            EditorGUILayout.LabelField(TypeLabel(field), GrimoireEditorStyles.MiniSecondaryStyle, GUILayout.Width(80));

            if (GUILayout.Button(new GUIContent("Copy", snippet), GUILayout.Width(CopyButtonWidth)))
            {
                EditorGUIUtility.systemCopyBuffer = snippet;
                _statusMessage = $"Copied code for \"{field.DisplayLabel}\" to the clipboard.";
                GUI.FocusControl(null);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(LabelWidth + 4);
            EditorGUILayout.SelectableLabel(
                expression,
                EditorStyles.miniLabel,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        /// <summary>
        /// Lookups match field id, label or field type; prefer the readable label
        /// when it resolves back to this exact field.
        /// </summary>
        private static string LookupName(GrimoireObjectSnapshot snapshot, GrimoireCachedField field)
        {
            if (!string.IsNullOrEmpty(field.Label)
                && snapshot.TryGetField(field.Label, out var match)
                && ReferenceEquals(match, field))
            {
                return field.Label;
            }

            return field.FieldId;
        }

        private static string BuildExpression(
            GrimoireCachedField field,
            string lookupName,
            string variableName)
        {
            var source = _accessMode == AccessMode.ObjectLink ? "link" : "obj";
            var name = Quote(lookupName);

            if (field.IsReference)
            {
                return field.Multiple
                    ? $"var {variableName} = {source}.GetReferences({name});"
                    : $"var {variableName} = await {ReferenceLoad(source, name)};";
            }

            if (field.Multiple)
            {
                var snapshotAccess = _accessMode == AccessMode.ObjectLink ? "link.Snapshot" : "obj";
                return $"{snapshotAccess}.TryGetField({name}, out var {variableName}Field)";
            }

            switch (field.Kind)
            {
                case ObjectViewKinds.Number:
                    return $"{source}.TryGetNumber({name}, out var {variableName})";
                case ObjectViewKinds.Boolean:
                    return $"{source}.TryGetBool({name}, out var {variableName})";
                case ObjectViewKinds.Vector:
                    return $"{source}.TryGetVector({name}, out var {variableName})";
                default:
                    return $"{source}.TryGetString({name}, out var {variableName})";
            }
        }

        private static string ReferenceLoad(string source, string name)
        {
            if (_accessMode == AccessMode.ObjectLink)
            {
                return $"link.GetReferencedObjectAsync({name}, 0)";
            }

            return $"GrimoireObjectCache.LoadAsync({source}.GetReferences({name})[0])";
        }

        private static string BuildSnippet(
            GrimoireCachedField field,
            string expression,
            string variableName,
            string objectKey)
        {
            var sb = new StringBuilder();
            var isTryGet = !field.IsReference;

            if (_accessMode == AccessMode.ObjectLink)
            {
                sb.AppendLine("var link = GetComponent<GrimoireObjectLink>();");
                if (isTryGet)
                {
                    sb.AppendLine($"if ({expression})");
                }
            }
            else
            {
                var condition = $"GrimoireObjectData.TryGet({Quote(objectKey ?? "")}, out var obj)";
                if (isTryGet)
                {
                    sb.AppendLine($"if ({condition}");
                    sb.AppendLine($"    && {expression})");
                }
                else
                {
                    sb.AppendLine($"if ({condition})");
                }
            }

            if (isTryGet)
            {
                sb.AppendLine("{");
                sb.AppendLine(field.Multiple
                    ? $"    var {variableName} = {variableName}Field.Values;"
                    : $"    // use {variableName}");
                sb.Append("}");
                return sb.ToString();
            }

            if (_accessMode == AccessMode.ObjectLink)
            {
                sb.Append(expression);
                return sb.ToString();
            }

            sb.AppendLine("{");
            sb.AppendLine($"    {expression}");
            sb.Append("}");
            return sb.ToString();
        }

        private static string TypeLabel(GrimoireCachedField field)
        {
            if (field.IsReference)
            {
                return field.Multiple ? "references" : "reference";
            }

            string type;
            switch (field.Kind)
            {
                case ObjectViewKinds.Number:
                    type = "double";
                    break;
                case ObjectViewKinds.Boolean:
                    type = "bool";
                    break;
                case ObjectViewKinds.Vector:
                    type = "Vector3";
                    break;
                default:
                    type = "string";
                    break;
            }

            return field.Multiple ? "List<string>" : type;
        }

        private static string ValuePreview(GrimoireCachedField field)
        {
            if (field.IsReference)
            {
                if (field.References.Count == 0)
                {
                    return "";
                }

                var names = new List<string>(field.References.Count);
                foreach (var reference in field.References)
                {
                    if (reference != null)
                    {
                        names.Add(reference.DisplayName);
                    }
                }

                return string.Join(", ", names);
            }

            if (field.Multiple && field.Values.Count > 0)
            {
                return string.Join(", ", field.Values);
            }

            return field.Value ?? "";
        }

        private static string FormatPreview(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(empty)";
            }

            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= 60 ? value : value.Substring(0, 57) + "…";
        }

        private static string Tooltip(GrimoireCachedField field)
        {
            var kind = string.IsNullOrEmpty(field.Kind) ? "field" : field.Kind;
            var type = string.IsNullOrEmpty(field.FieldType) ? kind : field.FieldType;
            var tooltip = $"{type} ({kind})\nField id: {field.FieldId}";
            if (field.GameEngineEditable)
            {
                tooltip += "\nGame engine editable — local edits are returned at runtime.";
            }

            return tooltip;
        }

        private static string VariableName(GrimoireCachedField field)
        {
            var source = string.IsNullOrEmpty(field.Label) ? field.FieldId : field.Label;
            var sb = new StringBuilder();
            var upperNext = false;
            foreach (var c in source ?? "")
            {
                if (!char.IsLetterOrDigit(c))
                {
                    upperNext = sb.Length > 0;
                    continue;
                }

                if (sb.Length == 0)
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
                }

                upperNext = false;
            }

            if (sb.Length == 0)
            {
                return "value";
            }

            if (char.IsDigit(sb[0]))
            {
                sb.Insert(0, '_');
            }

            var name = sb.ToString();
            return CSharpKeywords.Contains(name) ? name + "Value" : name;
        }

        private static string Quote(string value) =>
            "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
