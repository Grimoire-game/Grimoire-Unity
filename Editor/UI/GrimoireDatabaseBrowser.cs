using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Browse exported Grimoire C# database files with expandable objects, grouped dialogs, and grouped variables.
    /// </summary>
    public class GrimoireDatabaseBrowser : EditorWindow
    {
        private enum BrowseTab
        {
            Objects,
            Variables,
            Documents,
            Dialogs
        }

        private const string GrimoireAssetsFolder = "Assets/Grimoire";

        private Vector2 _scroll;
        private string _filter = "";
        private BrowseTab _tab = BrowseTab.Objects;

        private string _status = "Click Refresh to scan Assets/Grimoire.";
        private MessageType _statusType = MessageType.Info;

        private string _databasePathDisplay = "";
        private string _rootClassName = "Database";

        private readonly HashSet<string> _expanded = new HashSet<string>();

        /// <summary>Runtime-style: one node per Library key with field list.</summary>
        private List<RuntimeObjectEntry> _runtimeObjects = new List<RuntimeObjectEntry>();

        /// <summary>Folder-style: tree under Library.</summary>
        private LibraryTreeNode _libraryRoot;

        private bool _objectsAreRuntime;

        private readonly List<DocumentRow> _documents = new List<DocumentRow>();

        /// <summary>Variables + dialog variables, grouped.</summary>
        private List<VariableFileGroup> _variableFileGroups = new List<VariableFileGroup>();

        private List<DialogGroup> _dialogGroups = new List<DialogGroup>();
        private List<DialogMetaRow> _dialogMeta = new List<DialogMetaRow>();

        [MenuItem("Window/Grimoire/Database Browser")]
        public static void ShowWindow()
        {
            var w = GetWindow<GrimoireDatabaseBrowser>("Grimoire Database");
            w.minSize = new Vector2(480, 520);
            w.Show();
        }

        private void OnEnable()
        {
            TryAutoRefresh();
        }

        private void TryAutoRefresh()
        {
            string dataPath = Path.Combine(Application.dataPath, "Grimoire");
            if (Directory.Exists(dataPath))
            {
                RefreshScan();
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Exported database browser", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Expand entries to inspect fields and structure. Dialogs list one row per dialog; expand to see sections. " +
                "Variables group by category; expand to see each element.",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh", GUILayout.Width(90)))
            {
                RefreshScan();
            }

            EditorGUILayout.LabelField("Folder:", GUILayout.Width(46));
            if (GUILayout.Button(GrimoireAssetsFolder, EditorStyles.linkLabel))
            {
                string rel = GrimoireAssetsFolder.Replace("\\", "/");
                var obj = AssetDatabase.LoadAssetAtPath<DefaultAsset>(rel);
                if (obj != null)
                {
                    EditorGUIUtility.PingObject(obj);
                    Selection.activeObject = obj;
                }
                else
                {
                    EditorUtility.RevealInFinder(Path.Combine(Application.dataPath, "Grimoire"));
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.HelpBox(_status, _statusType);
            }

            _filter = EditorGUILayout.TextField("Search", _filter);
            string filt = string.IsNullOrWhiteSpace(_filter) ? null : _filter.Trim();

            int objCount = _objectsAreRuntime ? _runtimeObjects.Count : CountLibraryLeaves(_libraryRoot);
            int varCount = _variableFileGroups.Sum(g => g.Categories.Sum(c => c.Elements.Count));

            _tab = (BrowseTab)GUILayout.Toolbar((int)_tab, new[]
            {
                $"Objects ({objCount})",
                $"Variables ({varCount})",
                $"Documents ({_documents.Count})",
                $"Dialogs ({_dialogGroups.Count + _dialogMeta.Count})"
            });

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            switch (_tab)
            {
                case BrowseTab.Objects:
                    DrawObjectsTab(filt);
                    break;
                case BrowseTab.Variables:
                    DrawVariablesTab(filt);
                    break;
                case BrowseTab.Documents:
                    DrawDocumentsTab(filt);
                    break;
                case BrowseTab.Dialogs:
                    DrawDialogsTab(filt);
                    break;
            }

            EditorGUILayout.EndScrollView();
        }

        private static int CountLibraryLeaves(LibraryTreeNode n)
        {
            if (n == null)
            {
                return 0;
            }

            if (n.Children.Count == 0)
            {
                return 1;
            }

            return n.Children.Sum(CountLibraryLeaves);
        }

        private void DrawObjectsTab(string filt)
        {
            if (_objectsAreRuntime)
            {
                foreach (var e in _runtimeObjects)
                {
                    if (!PassesFilter(filt, e.Key, e.CopyPath))
                    {
                        continue;
                    }

                    string id = "ro:" + e.Key;
                    bool exp = _expanded.Contains(id);
                    bool hasDetail = e.Fields.Count > 0;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.BeginVertical();
                    exp = EditorGUILayout.Foldout(exp, $"{e.Key}  →  {e.CopyPath}", true);
                    if (exp && hasDetail)
                    {
                        EditorGUI.indentLevel++;
                        foreach (var f in e.Fields)
                        {
                            DrawRuntimeFieldRow(f, e.Key, e.CopyPath);
                        }

                        EditorGUI.indentLevel--;
                    }
                    else if (exp && !hasDetail)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.LabelField("(No public fields parsed — open generated .cs to inspect.)", EditorStyles.miniLabel);
                        EditorGUI.indentLevel--;
                    }

                    EditorGUILayout.EndVertical();
                    if (GUILayout.Button("Copy", GUILayout.Width(44), GUILayout.MinHeight(20)))
                    {
                        CopyToClipboard(e.CopyPath);
                    }

                    EditorGUILayout.EndHorizontal();
                    ToggleExpanded(id, exp);
                }

                return;
            }

            if (_libraryRoot != null)
            {
                foreach (var c in _libraryRoot.Children)
                {
                    DrawLibraryTreeNode(c, _rootClassName + ".Library", filt, 0);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No object tree loaded.", MessageType.Info);
            }
        }

        private void DrawLibraryTreeNode(LibraryTreeNode node, string pathSoFar, string filt, int depth)
        {
            string path = pathSoFar + "." + node.Name;
            string copyPath = path;
            bool leaf = node.Children.Count == 0;
            bool hasFields = node.FieldSummaries.Count > 0;
            bool searchable = PassesFilter(filt, node.Name, path, copyPath);
            if (!leaf)
            {
                searchable |= node.Children.Any(ch => SubtreeMatchesFilter(ch, path + "." + ch.Name, filt));
            }

            if (filt != null && !searchable)
            {
                return;
            }

            string id = "lib:" + path;
            bool exp = _expanded.Contains(id);
            string label = leaf && hasFields
                ? $"{node.Name}  ({node.FieldSummaries.Count} fields)  →  {copyPath}"
                : $"{node.Name}  →  {copyPath}";

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical();
            exp = EditorGUILayout.Foldout(exp, label, true);
            if (exp)
            {
                EditorGUI.indentLevel++;
                if (hasFields)
                {
                    foreach (var f in node.FieldSummaries)
                    {
                        DrawDetailRow(f, copyPath);
                    }
                }

                foreach (var ch in node.Children)
                {
                    DrawLibraryTreeNode(ch, path, filt, depth + 1);
                }

                if (!hasFields && node.Children.Count == 0)
                {
                    EditorGUILayout.LabelField("(empty class)", EditorStyles.miniLabel);
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
            if (GUILayout.Button("Copy", GUILayout.Width(44), GUILayout.Height(16 + depth)))
            {
                CopyToClipboard(copyPath);
            }

            EditorGUILayout.EndHorizontal();

            ToggleExpanded(id, exp);
        }

        private static bool SubtreeMatchesFilter(LibraryTreeNode node, string path, string filt)
        {
            if (filt == null)
            {
                return true;
            }

            if (PassesFilter(filt, node.Name, path))
            {
                return true;
            }

            if (node.FieldSummaries.Any(f => f.IndexOf(filt, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return node.Children.Any(ch => SubtreeMatchesFilter(ch, path + "." + ch.Name, filt));
        }

        private static void DrawRuntimeFieldRow(RuntimeFieldEntry field, string itemKey, string dbTypedGetter)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"{field.Name}  =  {field.ValueDisplay}",
                EditorStyles.wordWrappedMiniLabel,
                GUILayout.MinHeight(EditorGUIUtility.singleLineHeight));

            // DB button — always shown; copies typed static property access
            if (GUILayout.Button("DB", GUILayout.Width(28), GUILayout.Height(14)))
                CopyToClipboard($"{dbTypedGetter}.{field.Name}");

            // RT button — supported types only; copies ObjectRuntime getter call
            if (field.Kind != FieldKind.Other)
            {
                string method = field.Kind == FieldKind.Boolean    ? "GetBoolean"
                              : field.Kind == FieldKind.String     ? "GetString"
                              : field.Kind == FieldKind.StringArray ? "GetStringArray"
                              : "GetNumber";
                if (GUILayout.Button("RT", GUILayout.Width(28), GUILayout.Height(14)))
                    CopyToClipboard($"ObjectRuntime.{method}(\"{itemKey}\", \"{field.Name}\")");
            }

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawDetailRow(string text, string contextForCopy)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.TextArea(text, EditorStyles.wordWrappedMiniLabel, GUILayout.MinHeight(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button("Copy", GUILayout.Width(40), GUILayout.Height(14)))
            {
                CopyToClipboard(text);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawVariablesTab(string filt)
        {
            foreach (var fileGrp in _variableFileGroups)
            {
                bool fileVisible = filt == null
                                   || fileGrp.SourceHint.IndexOf(filt, StringComparison.OrdinalIgnoreCase) >= 0
                                   || fileGrp.RootClassName.IndexOf(filt, StringComparison.OrdinalIgnoreCase) >= 0
                                   || fileGrp.Categories.Any(c => CategoryMatchesFilter(c, filt));

                if (!fileVisible)
                {
                    continue;
                }

                EditorGUILayout.LabelField(fileGrp.RootClassName + "  —  " + fileGrp.SourceHint, EditorStyles.boldLabel);

                foreach (var cat in fileGrp.Categories)
                {
                    if (filt != null && !CategoryMatchesFilter(cat, filt))
                    {
                        bool any = cat.Elements.Any(e => PassesFilter(filt, e.Name, e.CopyPath, e.DetailSummary));
                        if (!any)
                        {
                            continue;
                        }
                    }

                    string cid = "var:" + fileGrp.SourceHint + ":" + fileGrp.RootClassName + ":" + cat.CategoryName;
                    bool cexp = _expanded.Contains(cid);
                    cexp = EditorGUILayout.Foldout(cexp, $"{cat.CategoryName}  ({cat.Elements.Count} elements)", true);
                    if (cexp)
                    {
                        EditorGUI.indentLevel++;
                        foreach (var el in cat.Elements)
                        {
                            if (filt != null && !PassesFilter(filt, el.Name, el.CopyPath, el.DetailSummary))
                            {
                                continue;
                            }

                            string eid = cid + ":" + el.Name;
                            bool eexp = _expanded.Contains(eid);
                            EditorGUILayout.BeginHorizontal();
                            EditorGUILayout.BeginVertical();
                            eexp = EditorGUILayout.Foldout(eexp, $"{el.Name}  →  {el.CopyPath}", true);
                            if (eexp && !string.IsNullOrEmpty(el.DetailSummary))
                            {
                                EditorGUI.indentLevel++;
                                DrawDetailRow(el.DetailSummary, el.CopyPath);
                                EditorGUI.indentLevel--;
                            }

                            EditorGUILayout.EndVertical();
                            if (GUILayout.Button("Copy", GUILayout.Width(44), GUILayout.Height(18)))
                            {
                                CopyToClipboard(el.CopyPath);
                            }

                            EditorGUILayout.EndHorizontal();
                            ToggleExpanded(eid, eexp);
                        }

                        EditorGUI.indentLevel--;
                    }

                    ToggleExpanded(cid, cexp);
                }

                GUILayout.Space(6);
            }

            if (_variableFileGroups.Count == 0)
            {
                EditorGUILayout.HelpBox("No Variables.cs or DialogVariables.cs found.", MessageType.Info);
            }
        }

        private static bool CategoryMatchesFilter(VariableCategory cat, string filt)
        {
            return cat.CategoryName.IndexOf(filt, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawDocumentsTab(string filt)
        {
            foreach (var d in _documents)
            {
                if (!PassesFilter(filt, d.DisplayLabel, d.CopyPath, d.Detail))
                {
                    continue;
                }

                string id = "doc:" + d.CopyPath;
                bool exp = _expanded.Contains(id);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.BeginVertical();
                exp = EditorGUILayout.Foldout(exp, d.DisplayLabel, true);
                if (exp && !string.IsNullOrEmpty(d.Detail))
                {
                    EditorGUI.indentLevel++;
                    DrawDetailRow(d.Detail, d.CopyPath);
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
                if (GUILayout.Button("Copy", GUILayout.Width(44), GUILayout.Height(18)))
                {
                    CopyToClipboard(d.CopyPath);
                }

                EditorGUILayout.EndHorizontal();
                ToggleExpanded(id, exp);
            }

            if (_documents.Count == 0)
            {
                EditorGUILayout.HelpBox("No DocumentBlock[] fields found in the scanned database.", MessageType.Info);
            }
        }

        private void DrawDialogsTab(string filt)
        {
            if (_dialogMeta.Count > 0)
            {
                EditorGUILayout.LabelField("Dialog metadata", EditorStyles.boldLabel);
                foreach (var m in _dialogMeta)
                {
                    if (!PassesFilter(filt, m.DialogKey, m.CopyHint, m.StartingSection))
                    {
                        continue;
                    }

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.SelectableLabel($"{m.DialogKey}  ·  start: {m.StartingSection}\n{m.CopyHint}", EditorStyles.wordWrappedMiniLabel, GUILayout.Height(36));
                    if (GUILayout.Button("Copy", GUILayout.Width(44), GUILayout.Height(36)))
                    {
                        CopyToClipboard(m.CopyHint);
                    }

                    EditorGUILayout.EndHorizontal();
                }

                GUILayout.Space(8);
            }

            EditorGUILayout.LabelField("Dialogs & sections", EditorStyles.boldLabel);

            foreach (var g in _dialogGroups)
            {
                if (filt != null && !DialogGroupMatchesFilter(g, filt))
                {
                    continue;
                }

                string gid = "dlg:" + g.DialogKey;
                bool gexp = _expanded.Contains(gid);
                gexp = EditorGUILayout.Foldout(gexp, $"{g.DialogKey}  ({g.Sections.Count} sections)", true);
                if (gexp)
                {
                    EditorGUI.indentLevel++;
                    foreach (var s in g.Sections)
                    {
                        if (filt != null && !PassesFilter(filt, s.SectionId, s.FullKey, s.CopyPath))
                        {
                            continue;
                        }

                        string sid = gid + ":" + s.SectionId;
                        bool sexp = _expanded.Contains(sid);
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.BeginVertical();
                        sexp = EditorGUILayout.Foldout(sexp, $"{s.SectionId}  →  {s.CopyPath}", true);
                        if (sexp && !string.IsNullOrEmpty(s.Preview))
                        {
                            EditorGUI.indentLevel++;
                            DrawDetailRow(s.Preview, s.CopyPath);
                            EditorGUI.indentLevel--;
                        }

                        EditorGUILayout.EndVertical();
                        if (GUILayout.Button("Copy", GUILayout.Width(44), GUILayout.Height(18)))
                        {
                            CopyToClipboard(s.CopyPath);
                        }

                        EditorGUILayout.EndHorizontal();
                        ToggleExpanded(sid, sexp);
                    }

                    EditorGUI.indentLevel--;
                }

                ToggleExpanded(gid, gexp);
            }

            if (_dialogGroups.Count == 0 && _dialogMeta.Count == 0)
            {
                EditorGUILayout.HelpBox("No DialogDatabase.cs found.", MessageType.Info);
            }
        }

        private static bool DialogGroupMatchesFilter(DialogGroup g, string filt)
        {
            if (g.DialogKey.IndexOf(filt, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return g.Sections.Any(s => PassesFilter(filt, s.SectionId, s.FullKey, s.CopyPath, s.Preview));
        }

        private void ToggleExpanded(string id, bool exp)
        {
            if (exp)
            {
                _expanded.Add(id);
            }
            else
            {
                _expanded.Remove(id);
            }
        }

        private static bool PassesFilter(string filt, params string[] candidates)
        {
            if (filt == null)
            {
                return true;
            }

            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c))
                {
                    continue;
                }

                if (c.IndexOf(filt, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void CopyToClipboard(string text)
        {
            EditorGUIUtility.systemCopyBuffer = text ?? "";
        }

        private void RefreshScan()
        {
            string root = Path.Combine(Application.dataPath, "Grimoire");
            _runtimeObjects.Clear();
            _libraryRoot = null;
            _objectsAreRuntime = false;
            _documents.Clear();
            _variableFileGroups.Clear();
            _dialogGroups.Clear();
            _dialogMeta.Clear();
            _databasePathDisplay = "";

            if (!Directory.Exists(root))
            {
                _status = "No Assets/Grimoire folder found. Import an export from Grimoire Importer first.";
                _statusType = MessageType.Warning;
                return;
            }

            var csFiles = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            string databaseText = null;
            string databasePath = null;

            foreach (var fi in csFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(fi);
                if (name.Equals("GrimoireJson.cs", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("GrimoireLog.cs", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("UnityGrimoireBootstrap.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(fi, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Grimoire Browser] Could not read {fi}: {ex.Message}");
                    continue;
                }

                if (databaseText == null
                    && !name.Contains("DialogDatabase", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("DialogVariables", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Variables", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Types", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Tags", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("TranslationKeys", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("VariableRuntime", StringComparison.OrdinalIgnoreCase))
                {
                    if (text.Contains("public static class Library") || text.Contains("IReadOnlyDictionary<string, Template> Library"))
                    {
                        databaseText = text;
                        databasePath = fi;
                    }
                }

                ParseDialogDatabase(text, fi);
                CollectVariableGroups(text, fi, isDialogVariables: false);
                CollectVariableGroups(text, fi, isDialogVariables: true);
            }

            if (!string.IsNullOrEmpty(databaseText))
            {
                _rootClassName = FindOuterStaticClassName(databaseText) ?? "Database";
                _databasePathDisplay = RelativeProjectPath(databasePath);

                if (databaseText.IndexOf("IReadOnlyDictionary<string, Template> Library", StringComparison.Ordinal) >= 0
                    || databaseText.IndexOf("Dictionary<string, Template>", StringComparison.Ordinal) >= 0)
                {
                    _objectsAreRuntime = true;
                    _runtimeObjects = BuildRuntimeObjectList(databaseText, _rootClassName);
                    foreach (var d in BuildRuntimeDocumentRows(databaseText, _rootClassName))
                    {
                        _documents.Add(d);
                    }
                }
                else if (databaseText.IndexOf("public static class Library", StringComparison.Ordinal) >= 0)
                {
                    _objectsAreRuntime = false;
                    _libraryRoot = BuildLibraryTree(databaseText, "Library");
                    foreach (var d in BuildFolderDocumentRows(databaseText, _rootClassName))
                    {
                        _documents.Add(d);
                    }
                }
            }
            else
            {
                _status = "Could not find a main database file (expected nested Library or runtime Dictionary). " +
                          "Place your primary Database .cs at Assets/Grimoire or under a subfolder.";
                _statusType = MessageType.Warning;
                return;
            }

            _documents.Sort((a, b) => string.Compare(a.CopyPath, b.CopyPath, StringComparison.OrdinalIgnoreCase));

            if (databaseText != null)
            {
                _status = $"Scan complete. Main database: {_databasePathDisplay}";
                _statusType = MessageType.Info;
            }
        }

        #region Parsing & models

        private enum FieldKind { Number, String, Boolean, StringArray, Other }

        private sealed class RuntimeFieldEntry
        {
            public string Name;
            public string ValueDisplay;
            public FieldKind Kind;
        }

        private sealed class RuntimeObjectEntry
        {
            public string Key;
            public string TypeName;
            public string CopyPath;
            public List<RuntimeFieldEntry> Fields = new List<RuntimeFieldEntry>();
        }

        private sealed class LibraryTreeNode
        {
            public string Name;
            public List<LibraryTreeNode> Children = new List<LibraryTreeNode>();
            public List<string> FieldSummaries = new List<string>();
        }

        private sealed class DocumentRow
        {
            public string DisplayLabel;
            public string CopyPath;
            public string Detail;
        }

        private sealed class VariableFileGroup
        {
            public string RootClassName;
            public string SourceHint;
            public List<VariableCategory> Categories = new List<VariableCategory>();
        }

        private sealed class VariableCategory
        {
            public string CategoryName;
            public List<VariableElementRow> Elements = new List<VariableElementRow>();
        }

        private sealed class VariableElementRow
        {
            public string Name;
            public string CopyPath;
            public string DetailSummary;
        }

        private sealed class DialogGroup
        {
            public string DialogKey;
            public List<DialogSectionRow> Sections = new List<DialogSectionRow>();
        }

        private sealed class DialogSectionRow
        {
            public string SectionId;
            public string FullKey;
            public string CopyPath;
            public string Preview;
        }

        private sealed class DialogMetaRow
        {
            public string DialogKey;
            public string StartingSection;
            public string CopyHint;
        }

        private static List<RuntimeObjectEntry> BuildRuntimeObjectList(string text, string rootClass)
        {
            var result = new List<RuntimeObjectEntry>();
            foreach (var le in ExtractLibraryDictionaryBodies(text))
            {
                var entry = new RuntimeObjectEntry
                {
                    Key      = le.Key,
                    TypeName = le.TypeName,
                    CopyPath = $"{rootClass}.Get<{rootClass}.{le.TypeName}>(\"{le.Key}\")"
                };
                entry.Fields.AddRange(ExtractObjectInitializerFieldEntries(le.Body));
                result.Add(entry);
            }

            result.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private readonly struct LibraryEntry
        {
            public readonly string Key;
            public readonly string TypeName;
            public readonly string Body;
            public LibraryEntry(string key, string typeName, string body)
            { Key = key; TypeName = typeName; Body = body; }
        }

        private static IEnumerable<LibraryEntry> ExtractLibraryDictionaryBodies(string fullText)
        {
            var anchor = Regex.Match(fullText, @"\bLibrary\s*=\s*new\s+Dictionary\s*<string\s*,\s*Template\s*>");
            int idx = anchor.Success ? anchor.Index : fullText.IndexOf("Library", StringComparison.Ordinal);
            if (idx < 0)
            {
                yield break;
            }

            int dictStart = fullText.IndexOf('{', fullText.IndexOf('=', idx));
            if (dictStart < 0)
            {
                yield break;
            }

            if (!TryGetBraceRange(fullText, dictStart, out int end))
            {
                yield break;
            }

            string blob = fullText.Substring(dictStart, end - dictStart + 1);
            foreach (Match m in Regex.Matches(blob, @"\[\s*""([^""]+)""\s*\]\s*=\s*new\s+(\w+)\s*\{"))
            {
                string key      = m.Groups[1].Value;
                string typeName = m.Groups[2].Value;
                int brace = blob.IndexOf('{', m.Index + m.Length - 1);
                if (brace < 0 || !TryGetBraceRange(blob, brace, out int entryEnd))
                {
                    continue;
                }

                string body = blob.Substring(brace + 1, entryEnd - brace - 1);
                yield return new LibraryEntry(key, typeName, body);
            }
        }

        /// <summary>Runtime Library entries use object-initializer lines: fieldName = value,</summary>
        private static List<RuntimeFieldEntry> ExtractObjectInitializerFieldEntries(string body)
        {
            var result = new List<RuntimeFieldEntry>();
            var lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], @"^\s*(\w+)\s*=\s*(.*)$");
                if (!m.Success)
                {
                    continue;
                }

                string name = m.Groups[1].Value;
                string rest = m.Groups[2].Value.TrimEnd();
                int depth = CountBraceDelta(rest);
                int li = i;
                while (depth > 0 && li + 1 < lines.Length)
                {
                    li++;
                    rest += " " + lines[li].TrimEnd();
                    depth += CountBraceDelta(lines[li]);
                }

                rest = rest.TrimEnd();
                while (rest.EndsWith(",", StringComparison.Ordinal))
                {
                    rest = rest.Substring(0, rest.Length - 1).TrimEnd();
                }

                string show = rest.Length > 160 ? rest.Substring(0, 157) + "..." : rest;
                result.Add(new RuntimeFieldEntry
                {
                    Name         = name,
                    ValueDisplay = show,
                    Kind         = InferFieldKind(show)
                });
                i = li;
            }

            return result;
        }

        private static FieldKind InferFieldKind(string value)
        {
            if (string.IsNullOrEmpty(value)) return FieldKind.Other;

            string v = value.Trim();

            // Boolean literals
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("false", StringComparison.OrdinalIgnoreCase))
                return FieldKind.Boolean;

            // Quoted string literals
            if (v.StartsWith("\"", StringComparison.Ordinal))
                return FieldKind.String;

            // Numeric literals — optional sign, digits, optional decimal, optional suffix
            if (Regex.IsMatch(v, @"^-?\d+(\.\d+)?[fFdDlLuU]?$"))
                return FieldKind.Number;

            // String array initializers: new string[] { ... } or new string[0]
            if (v.StartsWith("new string[", StringComparison.Ordinal))
                return FieldKind.StringArray;

            return FieldKind.Other;
        }

        private static int CountBraceDelta(string s)
        {
            int n = 0;
            foreach (char c in s)
            {
                if (c == '{')
                {
                    n++;
                }
                else if (c == '}')
                {
                    n--;
                }
            }

            return n;
        }

        private static LibraryTreeNode BuildLibraryTree(string text, string entryClassName)
        {
            int libIdx = text.IndexOf($"public static class {entryClassName}", StringComparison.Ordinal);
            if (libIdx < 0)
            {
                return null;
            }

            int braceOpen = text.IndexOf('{', libIdx);
            if (braceOpen < 0 || !TryGetBraceRange(text, braceOpen, out int libEnd))
            {
                return null;
            }

            string libBody = text.Substring(braceOpen + 1, libEnd - braceOpen - 1);
            var lines = libBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var classLine = new Regex(@"^(\t*)\s*public\s+static\s+class\s+(\w+)\s*");
            var readonlyLine = new Regex(@"^\s*public\s+static\s+readonly\s+(.+)$");

            var root = new LibraryTreeNode { Name = entryClassName };
            var stack = new List<(int indent, LibraryTreeNode node)>();
            stack.Add((-1, root));

            foreach (var raw in lines)
            {
                var cm = classLine.Match(raw);
                if (cm.Success)
                {
                    int indent = cm.Groups[1].Value.Length;
                    string name = cm.Groups[2].Value;
                    while (stack.Count > 0 && stack[stack.Count - 1].indent >= indent)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    var parent = stack[stack.Count - 1].node;
                    var node = new LibraryTreeNode { Name = name };
                    parent.Children.Add(node);
                    stack.Add((indent, node));
                    continue;
                }

                var ro = readonlyLine.Match(raw);
                if (ro.Success && stack.Count > 0)
                {
                    string s = ro.Groups[1].Value.Trim();
                    if (s.Length > 200)
                    {
                        s = s.Substring(0, 197) + "...";
                    }

                    stack[stack.Count - 1].node.FieldSummaries.Add(s);
                }
            }

            return root;
        }

        private static List<DocumentRow> BuildRuntimeDocumentRows(string text, string rootClass)
        {
            var list = new List<DocumentRow>();
            foreach (var le in ExtractLibraryDictionaryBodies(text))
            {
                foreach (Match fm in Regex.Matches(le.Body, @"(\w+)\s*=\s*(new\s+DocumentBlock\[\]\s*\{)"))
                {
                    string field = fm.Groups[1].Value;
                    int open = fm.Index + fm.Length - 1;
                    if (open < 0 || open >= le.Body.Length || le.Body[open] != '{' || !TryGetBraceRange(le.Body, open, out int end))
                    {
                        continue;
                    }

                    string arrInit = le.Body.Substring(open, end - open + 1);
                    string copy = $"{rootClass}.Library[\"{le.Key}\"].{field}";
                    list.Add(new DocumentRow
                    {
                        DisplayLabel = $"{copy}  (DocumentBlock[])",
                        CopyPath = copy,
                        Detail = SummarizeDocumentBlockInitializer(arrInit)
                    });
                }
            }

            return list;
        }

        private static List<DocumentRow> BuildFolderDocumentRows(string text, string rootClass)
        {
            var list = new List<DocumentRow>();
            int libIdx = text.IndexOf("public static class Library", StringComparison.Ordinal);
            if (libIdx < 0)
            {
                return list;
            }

            int braceOpen = text.IndexOf('{', libIdx);
            if (braceOpen < 0 || !TryGetBraceRange(text, braceOpen, out int libEnd))
            {
                return list;
            }

            string libBody = text.Substring(braceOpen + 1, libEnd - braceOpen - 1);
            var lines = libBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var stack = new List<(int indent, string name)>();
            var classLine = new Regex(@"^(\t*)\s*public\s+static\s+class\s+(\w+)\s*");
            var docField = new Regex(@"^\s*public\s+static\s+readonly\s+DocumentBlock\[\]\s+(\w+)\s*=\s*(.+)$");

            foreach (var line in lines)
            {
                var cm = classLine.Match(line);
                if (cm.Success)
                {
                    int indent = cm.Groups[1].Value.Length;
                    string name = cm.Groups[2].Value;
                    while (stack.Count > 0 && stack[stack.Count - 1].indent >= indent)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    stack.Add((indent, name));
                    continue;
                }

                var dm = docField.Match(line);
                if (dm.Success)
                {
                    string field = dm.Groups[1].Value;
                    string rhs = dm.Groups[2].Value.Trim();
                    if (rhs.EndsWith(";", StringComparison.Ordinal))
                    {
                        rhs = rhs.Substring(0, rhs.Length - 1).Trim();
                    }

                    string path = rootClass + ".Library." + string.Join(".", stack.Select(s => s.name)) + "." + field;
                    list.Add(new DocumentRow
                    {
                        DisplayLabel = $"{path}  (DocumentBlock[])",
                        CopyPath = path,
                        Detail = SummarizeDocumentBlockInitializer(rhs)
                    });
                }
            }

            return list;
        }

        private static string SummarizeDocumentBlockInitializer(string rhs)
        {
            int count = Regex.Matches(rhs, @"new\s+DocumentBlock\b").Count;
            var types = new List<string>();
            foreach (Match tm in Regex.Matches(rhs, @"type\s*=\s*""([^""]*)"""))
            {
                types.Add(tm.Groups[1].Value);
            }

            var previews = new List<string>();
            foreach (Match cm in Regex.Matches(rhs, @"content\s*=\s*""([\s\S]*?)""(?=\s*,|\s*\})"))
            {
                string c = cm.Groups[1].Value.Replace("\\n", " ").Trim();
                if (c.Length > 80)
                {
                    c = c.Substring(0, 77) + "...";
                }

                if (c.Length > 0)
                {
                    previews.Add(c);
                }
            }

            var sb = new StringBuilder();
            sb.Append($"{count} block(s)");
            if (types.Count > 0)
            {
                sb.Append(" · types: ").Append(string.Join(", ", types.Distinct()));
            }

            if (previews.Count > 0)
            {
                sb.AppendLine().AppendLine("Text previews:");
                foreach (var p in previews.Take(8))
                {
                    sb.AppendLine("• " + p);
                }

                if (previews.Count > 8)
                {
                    sb.Append("… (").Append(previews.Count - 8).Append(" more)");
                }
            }

            return sb.ToString();
        }

        private void CollectVariableGroups(string text, string path, bool isDialogVariables)
        {
            if (isDialogVariables)
            {
                if (text.IndexOf("public static class DialogVariables", StringComparison.Ordinal) < 0
                    && !Regex.IsMatch(text, @"public\s+static\s+class\s+\w*DialogVariables\w*\b"))
                {
                    return;
                }
            }
            else
            {
                if (text.IndexOf("public abstract class VariableElement", StringComparison.Ordinal) < 0
                    && text.IndexOf("public class StringVariableElement", StringComparison.Ordinal) < 0)
                {
                    return;
                }
            }

            string rootClass = FindOuterStaticClassName(text);
            if (string.IsNullOrEmpty(rootClass))
            {
                return;
            }

            var grp = new VariableFileGroup
            {
                RootClassName = rootClass,
                SourceHint = RelativeProjectPath(path)
            };

            var byCat = new Dictionary<string, VariableCategory>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ExtractVariableDetailRows(text, rootClass))
            {
                if (!byCat.TryGetValue(item.Category, out var cat))
                {
                    cat = new VariableCategory { CategoryName = item.Category };
                    byCat[item.Category] = cat;
                }

                cat.Elements.Add(new VariableElementRow
                {
                    Name = item.ElementName,
                    CopyPath = item.CopyPath,
                    DetailSummary = item.Detail
                });
            }

            foreach (var c in byCat.Values)
            {
                c.Elements.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                grp.Categories.Add(c);
            }

            grp.Categories.Sort((a, b) => string.Compare(a.CategoryName, b.CategoryName, StringComparison.OrdinalIgnoreCase));
            _variableFileGroups.Add(grp);
        }

        private sealed class VarParseItem
        {
            public string Category;
            public string ElementName;
            public string CopyPath;
            public string Detail;
        }

        private static IEnumerable<VarParseItem> ExtractVariableDetailRows(string text, string rootClass)
        {
            var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var pathStack = new List<(int tabs, string name)>();
            var classRegex = new Regex(@"^(\t*)public\s+static\s+class\s+(\w+)\s*$");
            var elementStart = new Regex(@"^\s*public\s+static\s+readonly\s+(\w+)\s+(\w+)\s*=\s*new\s*\(\s*\)\s*$");

            bool insideRoot = false;
            for (int li = 0; li < lines.Length; li++)
            {
                string raw = lines[li];
                var cm = classRegex.Match(raw);
                if (cm.Success)
                {
                    int tabs = cm.Groups[1].Value.Length;
                    string name = cm.Groups[2].Value;
                    if (!insideRoot)
                    {
                        if (name == rootClass)
                        {
                            insideRoot = true;
                            pathStack.Clear();
                            pathStack.Add((tabs, name));
                        }

                        continue;
                    }

                    while (pathStack.Count > 0 && pathStack[pathStack.Count - 1].tabs >= tabs)
                    {
                        pathStack.RemoveAt(pathStack.Count - 1);
                    }

                    pathStack.Add((tabs, name));
                    continue;
                }

                if (!insideRoot)
                {
                    continue;
                }

                var es = elementStart.Match(raw);
                if (es.Success)
                {
                    string elType = es.Groups[1].Value;
                    string elName = es.Groups[2].Value;
                    var names = pathStack.Select(p => p.name).ToList();
                    if (names.Count < 2)
                    {
                        continue;
                    }

                    string category = names[names.Count - 1];
                    string copy = string.Join(".", names) + "." + elName;

                    int braceLine = FindFollowingLineWithContent(lines, li + 1, "{");
                    string block = braceLine >= 0 ? ExtractBraceBlockFromLines(lines, braceLine) : "";
                    string detail = SummarizeVariableObjectInitializer(block, elType);
                    yield return new VarParseItem
                    {
                        Category = category,
                        ElementName = elName,
                        CopyPath = copy,
                        Detail = detail
                    };
                }
            }
        }

        private static int FindFollowingLineWithContent(string[] lines, int start, string contains)
        {
            for (int i = start; i < lines.Length && i < start + 6; i++)
            {
                if (lines[i].IndexOf(contains, StringComparison.Ordinal) >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string ExtractBraceBlockFromLines(string[] lines, int lineWithOpenBrace)
        {
            var sb = new StringBuilder();
            int depth = 0;
            bool started = false;
            for (int i = lineWithOpenBrace; i < lines.Length; i++)
            {
                string L = lines[i];
                foreach (char c in L)
                {
                    if (c == '{')
                    {
                        depth++;
                        started = true;
                    }
                    else if (c == '}')
                    {
                        depth--;
                    }
                }

                sb.AppendLine(L);
                if (started && depth <= 0)
                {
                    break;
                }
            }

            return sb.ToString();
        }

        private static string SummarizeVariableObjectInitializer(string block, string elType)
        {
            if (string.IsNullOrEmpty(block))
            {
                return "";
            }

            var sb = new StringBuilder();
            sb.AppendLine("Type: " + elType);
            foreach (Match m in Regex.Matches(block, @"^\s*(\w+)\s*=\s*(.+?),?\s*$", RegexOptions.Multiline))
            {
                string prop = m.Groups[1].Value;
                if (prop == "LinkedValueGetter")
                {
                    continue;
                }

                string val = m.Groups[2].Value.Trim();
                if (val.EndsWith(",", StringComparison.Ordinal))
                {
                    val = val.Substring(0, val.Length - 1).Trim();
                }

                if (val.Length > 100)
                {
                    val = val.Substring(0, 97) + "...";
                }

                sb.AppendLine($"{prop}: {val}");
            }

            return sb.ToString().TrimEnd();
        }

        private void ParseDialogDatabase(string text, string path)
        {
            if (text.IndexOf("public static class DialogDatabase", StringComparison.Ordinal) < 0)
            {
                return;
            }

            string rel = RelativeProjectPath(path);
            var byDialog = new Dictionary<string, DialogGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in Regex.Matches(text, @"\[\s*""([^""]+)""\s*\]\s*=\s*new\s+(?!DialogInfo)(\w+)\s*\{"))
            {
                string fullKey = m.Groups[1].Value;
                string sectionType = m.Groups[2].Value;
                int brace = text.IndexOf('{', m.Index + m.Length - 1);
                if (brace < 0 || !TryGetBraceRange(text, brace, out int secEnd))
                {
                    continue;
                }

                string body = text.Substring(brace + 1, secEnd - brace - 1);
                int slash = fullKey.IndexOf('/');
                string dkey = slash >= 0 ? fullKey.Substring(0, slash) : fullKey;
                string secId = slash >= 0 ? fullKey.Substring(slash + 1) : fullKey;

                if (!byDialog.TryGetValue(dkey, out var g))
                {
                    g = new DialogGroup { DialogKey = dkey };
                    byDialog[dkey] = g;
                }

                g.Sections.Add(new DialogSectionRow
                {
                    SectionId = secId,
                    FullKey = fullKey,
                    CopyPath = $"DialogDatabase.Dialogs[\"{fullKey}\"]",
                    Preview = SummarizeDialogSectionBody(body, sectionType)
                });
            }

            foreach (var g in byDialog.Values)
            {
                g.Sections.Sort((a, b) => string.Compare(a.SectionId, b.SectionId, StringComparison.OrdinalIgnoreCase));
                _dialogGroups.Add(g);
            }

            _dialogGroups.Sort((a, b) => string.Compare(a.DialogKey, b.DialogKey, StringComparison.OrdinalIgnoreCase));

            foreach (Match m in Regex.Matches(text, @"\[\s*""([^""]+)""\s*\]\s*=\s*new\s+DialogInfo\s*\{([\s\S]*?)\}\s*,?\s*"))
            {
                string dk = m.Groups[1].Value;
                string inner = m.Groups[2].Value;
                string start = "";
                Match sm = Regex.Match(inner, @"StartingSection\s*=\s*""([^""]*)""");
                if (sm.Success)
                {
                    start = sm.Groups[1].Value;
                }

                _dialogMeta.Add(new DialogMetaRow
                {
                    DialogKey = dk,
                    StartingSection = start,
                    CopyHint = $"DialogDatabase.GetStartingSection(\"{dk}\")"
                });
            }

            _dialogMeta.Sort((a, b) => string.Compare(a.DialogKey, b.DialogKey, StringComparison.OrdinalIgnoreCase));
        }

        private static string SummarizeDialogSectionBody(string body, string sectionType)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Section type: " + sectionType);
            foreach (Match m in Regex.Matches(body, @"^\s*(\w+)\s*=\s*(.+?),?\s*$", RegexOptions.Multiline))
            {
                string name = m.Groups[1].Value;
                string val = m.Groups[2].Value.Trim();
                if (val.EndsWith(",", StringComparison.Ordinal))
                {
                    val = val.Substring(0, val.Length - 1).Trim();
                }

                if (name == "NextSection" || name == "NextDialog" || name == "NextDialogSection" || name == "Type"
                    || name == "SectionId" || name == "Id")
                {
                    // always somewhat useful
                }

                if (val.Length > 180)
                {
                    val = val.Substring(0, 177) + "...";
                }

                sb.AppendLine($"{name}: {val}");
            }

            return sb.ToString().TrimEnd();
        }

        private static bool TryGetBraceRange(string s, int openBrace, out int endIndex)
        {
            endIndex = -1;
            if (openBrace < 0 || openBrace >= s.Length || s[openBrace] != '{')
            {
                return false;
            }

            int depth = 0;
            for (int i = openBrace; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        endIndex = i;
                        return true;
                    }
                }
            }

            return false;
        }

        private static string FindOuterStaticClassName(string text)
        {
            var ms = Regex.Matches(text, @"^(\t*)public\s+static\s+class\s+(\w+)\s*$", RegexOptions.Multiline);
            if (ms.Count == 0)
            {
                return null;
            }

            int minTabs = int.MaxValue;
            string name = null;
            foreach (Match m in ms)
            {
                int tabs = m.Groups[1].Value.Length;
                if (tabs < minTabs)
                {
                    minTabs = tabs;
                    name = m.Groups[2].Value;
                }
            }

            return name;
        }

        private static string RelativeProjectPath(string absolutePath)
        {
            string data = Application.dataPath.Replace("\\", "/");
            string abs = absolutePath.Replace("\\", "/");
            if (abs.StartsWith(data, StringComparison.OrdinalIgnoreCase))
            {
                return "Assets" + abs.Substring(data.Length);
            }

            return absolutePath;
        }

        #endregion
    }
}
