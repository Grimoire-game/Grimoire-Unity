using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Runtime tab: guided setup for Bootstrap / Session Tracker, then a live
    /// inspector for Grimoire engine values while the scene is playing.
    /// </summary>
    public class GrimoireRuntimePanel
    {
        private enum Tab { Objects, Variables, Dialogs, Logic, Scene }

        /// <summary>Detected primitive kind of a variable — used to pick the right input widget.</summary>
        private enum VarKind { String, Int, Float, Bool, Other }

        // ── UI state ──────────────────────────────────────────────────────────
        private Tab _tab = Tab.Objects;
        private Vector2 _scroll;
        private string _filter = "";
        private bool _showDefaults = true;
        private bool _highlightChanged = true;
        private bool _active;
        private bool _setupExpanded = true;
        private bool _wasSetupReady;
        private bool _snapshotPending;
        private string _sessionIdDraft = "";

        private string _status = "";
        private MessageType _statusType = MessageType.Info;
        private bool _grimoireTypesFound;

        // ── Edit state (keyed by variable Path, survives manual refresh) ──────
        /// <summary>Text currently typed into an edit field but not yet applied.</summary>
        private readonly Dictionary<string, string> _editBuffers = new Dictionary<string, string>();
        /// <summary>Last setter error message per variable path.</summary>
        private readonly Dictionary<string, string> _setErrors   = new Dictionary<string, string>();
        /// <summary>Path of a variable whose set just succeeded (shown briefly in green).</summary>
        private string _lastSetOkPath;

        private readonly HashSet<string> _expanded = new HashSet<string>();

        // ── Cached ObjectRuntime reflection ───────────────────────────────────
        private Type       _objectRuntimeType;
        private MethodInfo _objRtGetNumber, _objRtGetString, _objRtGetBool, _objRtGetStringArray;
        private MethodInfo _objRtSetNumber, _objRtSetString, _objRtSetBool, _objRtSetStringArray;
        private MethodInfo _objRtHasOverride;
        private MethodInfo _objRtSavePrefs, _objRtResetItem;

        // ── Cached data lists ─────────────────────────────────────────────────
        private readonly List<RuntimeVariableEntry> _variables = new List<RuntimeVariableEntry>();
        private readonly List<RuntimeDialogEntry> _dialogs = new List<RuntimeDialogEntry>();
        private readonly List<RuntimeObjectEntry> _objects = new List<RuntimeObjectEntry>();
        private readonly List<SceneComponentEntry> _sceneComponents = new List<SceneComponentEntry>();
        private readonly List<LogicRegistryEntry> _logicEntries = new List<LogicRegistryEntry>();

        public event Action RepaintNeeded;
        /// <summary>Raised when the user should open the Versions tab (e.g. to import an export).</summary>
        public event Action OpenVersionsRequested;

        public void Activate()
        {
            if (_active)
            {
                return;
            }

            _active = true;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            SyncSessionDraftFromScene();
            if (EditorApplication.isPlaying)
            {
                ScheduleSnapshot();
            }

            RequestRepaint();
        }

        public void Deactivate()
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            _snapshotPending = false;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.delayCall -= TakeSnapshotIfPlaying;
        }

        private void RequestRepaint() => RepaintNeeded?.Invoke();

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                ClearData();
                SyncSessionDraftFromScene();
                ScheduleSnapshot();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _snapshotPending = false;
                ClearData();
            }

            RequestRepaint();
        }

        private void ClearData()
        {
            _variables.Clear();
            _dialogs.Clear();
            _objects.Clear();
            _sceneComponents.Clear();
            _logicEntries.Clear();
            _editBuffers.Clear();
            _setErrors.Clear();
            _lastSetOkPath = null;
            _status = "";
            _grimoireTypesFound = false;
        }

        /// <summary>
        /// Capture values once after Bootstrap has had a chance to initialize.
        /// No continuous polling — Refresh is manual after that.
        /// </summary>
        private void ScheduleSnapshot()
        {
            if (!EditorApplication.isPlaying || _snapshotPending)
            {
                return;
            }

            _snapshotPending = true;
            _status = "Loading scene snapshot…";
            _statusType = MessageType.Info;
            EditorApplication.delayCall -= TakeSnapshotIfPlaying;
            EditorApplication.delayCall += TakeSnapshotIfPlaying;
        }

        private void TakeSnapshotIfPlaying()
        {
            if (!_active || !EditorApplication.isPlaying)
            {
                _snapshotPending = false;
                return;
            }

            // Wait until Bootstrap has finished (or isn't in the scene).
            var bootstrap = FindSceneComponent<GrimoireBootstrap>();
            if (bootstrap != null && !bootstrap.IsInitialized)
            {
                EditorApplication.delayCall += TakeSnapshotIfPlaying;
                return;
            }

            _snapshotPending = false;
            RefreshData();
            RequestRepaint();
        }

        // ─────────────────────────────────────────────────── Draw ────────────

        public void Draw()
        {
            var ready = EvaluateSetup(out var hasExport, out var bootstrap, out var tracker);
            if (ready && !_wasSetupReady)
            {
                // Collapse the checklist once setup is complete so live values get the space.
                _setupExpanded = false;
            }

            _wasSetupReady = ready;

            DrawSetupChecklist(ready, hasExport, bootstrap, tracker);

            if (!ready)
            {
                return;
            }

            if (!EditorApplication.isPlaying)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Press Play to inspect live variables, objects, dialogs, and logic.");
                EditorGUILayout.Space(6);
                if (GUILayout.Button("Enter Play Mode", GUILayout.Height(32)))
                {
                    EditorApplication.isPlaying = true;
                }

                return;
            }

            DrawInspectorHeader();

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, _statusType);

            _tab = (Tab)GUILayout.Toolbar((int)_tab, new[]
            {
                $"Objects ({_objects.Count})",
                $"Variables ({_variables.Count})",
                $"Dialogs ({_dialogs.Count})",
                $"Logic ({_logicEntries.Count})",
                $"Scene ({_sceneComponents.Count})"
            });

            _filter = EditorGUILayout.TextField("Filter", _filter);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            switch (_tab)
            {
                case Tab.Objects:   DrawObjectsTab();   break;
                case Tab.Variables: DrawVariablesTab(); break;
                case Tab.Dialogs:   DrawDialogsTab();   break;
                case Tab.Logic:     DrawLogicTab();     break;
                case Tab.Scene:     DrawSceneTab();     break;
            }

            EditorGUILayout.EndScrollView();
        }

        // ─────────────────────────────────────────────── Setup ───────────────

        private bool EvaluateSetup(
            out bool hasExport,
            out GrimoireBootstrap bootstrap,
            out GrimoireSessionTracker tracker)
        {
            hasExport = HasImportedExport();
            bootstrap = FindSceneComponent<GrimoireBootstrap>();
            tracker = FindSceneComponent<GrimoireSessionTracker>();
            // Export + Bootstrap are required; Session Tracker is optional.
            return hasExport && bootstrap != null;
        }

        /// <summary>
        /// True when Assets/Grimoire contains a usable export (database + runtime),
        /// or when those types are already loaded in the AppDomain.
        /// </summary>
        private static bool HasImportedExport()
        {
            if (HasGrimoireExportOnDisk())
            {
                return true;
            }

            return HasGrimoireExportTypes();
        }

        private static bool HasGrimoireExportOnDisk()
        {
            var root = GrimoireExportImporterService.ExtractPath;
            if (!Directory.Exists(root))
            {
                return false;
            }

            string[] csFiles;
            try
            {
                csFiles = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            }
            catch
            {
                return false;
            }

            if (csFiles.Length == 0)
            {
                return false;
            }

            var hasDatabase = false;
            var hasVariableRuntime = false;
            var hasObjectRuntime = false;

            foreach (var path in csFiles)
            {
                var name = Path.GetFileName(path);
                if (name.Equals("VariableRuntime.cs", StringComparison.OrdinalIgnoreCase))
                {
                    hasVariableRuntime = true;
                }
                else if (name.Equals("ObjectRuntime.cs", StringComparison.OrdinalIgnoreCase))
                {
                    hasObjectRuntime = true;
                }

                // Main database: nested Library or runtime Dictionary Library.
                if (!hasDatabase
                    && !name.Contains("DialogDatabase", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("DialogVariables", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Variables", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Types", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Tags", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("TranslationKeys", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("VariableRuntime", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("ObjectRuntime", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Only read a prefix — enough to spot Library markers.
                        var text = File.ReadAllText(path);
                        if (text.IndexOf("public static class Library", StringComparison.Ordinal) >= 0
                            || text.IndexOf("IReadOnlyDictionary<string, Template> Library", StringComparison.Ordinal) >= 0
                            || text.IndexOf("Dictionary<string, Template>", StringComparison.Ordinal) >= 0)
                        {
                            hasDatabase = true;
                        }
                    }
                    catch
                    {
                        // Unreadable file — skip.
                    }
                }

                if (hasDatabase && hasVariableRuntime && hasObjectRuntime)
                {
                    return true;
                }
            }

            // Database is the required signal; runtimes are strongly preferred but
            // older exports may name them differently — accept database alone.
            return hasDatabase;
        }

        private static bool HasGrimoireExportTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        var ns = t.Namespace ?? "";
                        if (!(ns == "Grimoire" || ns.StartsWith("Grimoire.", StringComparison.Ordinal)))
                        {
                            continue;
                        }

                        if (t.Name == "Database"
                            || t.Name.EndsWith("Database", StringComparison.Ordinal)
                            || t.Name == "VariableRuntime"
                            || t.Name == "ObjectRuntime")
                        {
                            // Prefer Database / *Database that isn't DialogDatabase.
                            if (t.Name.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                continue;
                            }

                            return true;
                        }
                    }
                }
                catch
                {
                    // Dynamic / reflection-only assemblies can throw — skip them.
                }
            }

            return false;
        }

        private static T FindSceneComponent<T>() where T : Component
        {
            return UnityEngine.Object.FindObjectOfType<T>(true);
        }

        private void DrawSetupChecklist(
            bool ready,
            bool hasExport,
            GrimoireBootstrap bootstrap,
            GrimoireSessionTracker tracker)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Runtime", GrimoireEditorStyles.TitleStyle);
            GUILayout.FlexibleSpace();
            if (ready && EditorApplication.isPlaying)
            {
                var label = tracker != null && tracker.IsTracking
                    ? "Tracking playthrough"
                    : "Ready";
                EditorGUILayout.LabelField(label, GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.EndHorizontal();

            _setupExpanded = EditorGUILayout.Foldout(
                _setupExpanded || !ready,
                ready ? "Setup" : "Setup (finish these first)",
                true);
            if (!_setupExpanded && ready)
            {
                EditorGUILayout.Space(4);
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            DrawSetupStep(
                done: hasExport,
                title: "1. Import a Unity export",
                detail: hasExport
                    ? "Found in Assets/Grimoire/."
                    : "Download an export from the Versions tab into Assets/Grimoire/.",
                actionLabel: hasExport ? null : "Open Versions",
                onAction: () => OpenVersionsRequested?.Invoke());

            DrawSetupStep(
                done: bootstrap != null,
                title: "2. Add Grimoire Bootstrap",
                detail: bootstrap != null
                    ? $"Found on '{bootstrap.gameObject.name}'."
                    : "Initializes language, variables, objects, and logic in Play Mode.",
                actionLabel: bootstrap != null ? "Select" : "Add to scene",
                onAction: () =>
                {
                    if (bootstrap != null)
                    {
                        Selection.activeGameObject = bootstrap.gameObject;
                        EditorGUIUtility.PingObject(bootstrap.gameObject);
                    }
                    else
                    {
                        EnsureBootstrapInScene();
                    }
                });

            DrawSessionTrackerStep(tracker);

            if (!EditorApplication.isPlaying && ready)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(
                    "Then press Play — live values appear below.",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(6);
        }

        private void DrawSessionTrackerStep(GrimoireSessionTracker tracker)
        {
            var hasSession = tracker != null && !string.IsNullOrWhiteSpace(tracker.SessionId);
            var done = tracker != null; // optional component; presence is enough for the checkmark

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                done ? "✓  3. Session Tracker (optional)" : "○  3. Session Tracker (optional)",
                EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            if (tracker == null)
            {
                if (GUILayout.Button("Add to scene", GUILayout.Width(100)))
                {
                    EnsureSessionTrackerInScene();
                }
            }
            else if (GUILayout.Button("Select", GUILayout.Width(70)))
            {
                Selection.activeGameObject = tracker.gameObject;
                EditorGUIUtility.PingObject(tracker.gameObject);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                "Streams variable/object changes to a Grimoire playthrough.",
                GrimoireEditorStyles.MiniSecondaryStyle);

            if (tracker == null)
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Session ID", GUILayout.Width(70));
            var newId = EditorGUILayout.TextField(_sessionIdDraft);
            if (newId != _sessionIdDraft)
            {
                _sessionIdDraft = newId;
            }

            using (new EditorGUI.DisabledScope(
                       string.IsNullOrWhiteSpace(_sessionIdDraft) ||
                       string.Equals(_sessionIdDraft.Trim(), tracker.SessionId, StringComparison.Ordinal)))
            {
                if (GUILayout.Button("Save", GUILayout.Width(50)))
                {
                    tracker.SessionId = _sessionIdDraft.Trim();
                    EditorUtility.SetDirty(tracker);
                }
            }

            EditorGUILayout.EndHorizontal();

            if (EditorApplication.isPlaying && tracker != null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    tracker.StatusText,
                    GrimoireEditorStyles.MiniSecondaryStyle);
                GUILayout.FlexibleSpace();

                if (tracker.IsTracking)
                {
                    if (GUILayout.Button("Stop", GUILayout.Width(60)))
                    {
                        tracker.StopSession();
                    }
                }
                else if (!string.IsNullOrWhiteSpace(tracker.SessionId))
                {
                    if (GUILayout.Button("Start", GUILayout.Width(60)))
                    {
                        tracker.StartSession(tracker.SessionId);
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
            else if (!hasSession)
            {
                EditorGUILayout.LabelField(
                    "Paste a playthrough session ID from the Grimoire platform.",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.Space(4);
        }

        private static void DrawSetupStep(
            bool done,
            string title,
            string detail,
            string actionLabel,
            Action onAction)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                (done ? "✓  " : "○  ") + title,
                EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(actionLabel) && onAction != null)
            {
                if (GUILayout.Button(actionLabel, GUILayout.Width(100)))
                {
                    onAction();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(detail, GrimoireEditorStyles.MiniSecondaryStyle);
            EditorGUILayout.Space(4);
        }

        private void EnsureBootstrapInScene()
        {
            var existing = FindSceneComponent<GrimoireBootstrap>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                return;
            }

            var host = FindOrCreateGrimoireHost();
            Undo.AddComponent<GrimoireBootstrap>(host);
            Selection.activeGameObject = host;
            EditorGUIUtility.PingObject(host);
            RequestRepaint();
        }

        private void EnsureSessionTrackerInScene()
        {
            var existing = FindSceneComponent<GrimoireSessionTracker>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                SyncSessionDraftFromScene();
                EditorGUIUtility.PingObject(existing.gameObject);
                return;
            }

            var host = FindOrCreateGrimoireHost();
            Undo.AddComponent<GrimoireSessionTracker>(host);

            Selection.activeGameObject = host;
            SyncSessionDraftFromScene();
            EditorGUIUtility.PingObject(host);
            RequestRepaint();
        }

        private static GameObject FindOrCreateGrimoireHost()
        {
            var bootstrap = FindSceneComponent<GrimoireBootstrap>();
            if (bootstrap != null)
            {
                return bootstrap.gameObject;
            }

            var tracker = FindSceneComponent<GrimoireSessionTracker>();
            if (tracker != null)
            {
                return tracker.gameObject;
            }

            var go = new GameObject("Grimoire");
            Undo.RegisterCreatedObjectUndo(go, "Create Grimoire");
            return go;
        }

        private void SyncSessionDraftFromScene()
        {
            var tracker = FindSceneComponent<GrimoireSessionTracker>();
            _sessionIdDraft = tracker != null ? tracker.SessionId ?? "" : "";
        }

        // ─────────────────────────────────────────────── Header ──────────────

        private void DrawInspectorHeader()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Scene snapshot", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_snapshotPending))
            {
                if (GUILayout.Button(
                        new GUIContent("Refresh", "Re-read current values from the running scene (manual)."),
                        EditorStyles.miniButton,
                        GUILayout.Width(70)))
                {
                    RefreshData();
                    RequestRepaint();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                "Values are captured when Play starts. Press Refresh to update.",
                GrimoireEditorStyles.MiniSecondaryStyle);
            GUILayout.Space(4);
        }

        // ──────────────────────────────────────────── Variables Tab ──────────

        private void DrawVariablesTab()
        {
            if (_variables.Count == 0)
            {
                string msg = _grimoireTypesFound
                    ? "No variable elements found in the loaded Grimoire types.\n" +
                      "Make sure your export contains a Variables class with VariableElement fields."
                    : "No Grimoire types found in the loaded assemblies.\n" +
                      "Import a Grimoire export to Assets/Grimoire/ first.";
                EditorGUILayout.HelpBox(msg, MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            _showDefaults    = GUILayout.Toggle(_showDefaults,    "Show defaults",    EditorStyles.miniButton);
            _highlightChanged = GUILayout.Toggle(_highlightChanged, "Highlight changed", EditorStyles.miniButton);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);

            string filt = NormaliseFilter();

            var groups = _variables
                .Where(v => MatchesFilter(filt, v.FieldName, v.Key, v.Category, v.CurrentValue))
                .GroupBy(v => v.Category)
                .OrderBy(g => g.Key);

            foreach (var grp in groups)
            {
                string collapseKey = "_collapsed:var_cat:" + grp.Key;
                bool expanded = !_expanded.Contains(collapseKey);

                GUIStyle catFold = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
                bool newExp = EditorGUILayout.Foldout(expanded, $"{grp.Key}  ({grp.Count()} variables)", true, catFold);
                if (newExp != expanded)
                {
                    if (newExp) _expanded.Remove(collapseKey);
                    else        _expanded.Add(collapseKey);
                }

                if (!newExp) continue;

                EditorGUI.indentLevel++;
                foreach (var v in grp.OrderBy(x => x.FieldName))
                    DrawVariableEntry(v);
                EditorGUI.indentLevel--;
                GUILayout.Space(4);
            }
        }

        private void DrawVariableEntry(RuntimeVariableEntry v)
        {
            bool changed = v.CurrentValue != v.DefaultValue
                           && !string.IsNullOrEmpty(v.CurrentValue)
                           && v.CurrentValue != "(no getter)"
                           && v.CurrentValue != "null";

            bool justSet = _lastSetOkPath == v.Path;

            // Card background
            Color origBg = GUI.backgroundColor;
            if (justSet)
                GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
            else if (_highlightChanged && changed)
                GUI.backgroundColor = new Color(0.7f, 1f, 0.7f);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = origBg;

            // ── Header row: type badge + name + copy ────────────────────────
            EditorGUILayout.BeginHorizontal();
            GUIStyle badge = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = new Color(0.5f, 0.75f, 1f) } };
            EditorGUILayout.LabelField($"[{v.ShortTypeName}]", badge, GUILayout.Width(80));
            EditorGUILayout.LabelField(v.FieldName, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(40)))
                EditorGUIUtility.systemCopyBuffer = v.CurrentValue ?? "";
            EditorGUILayout.EndHorizontal();

            // ── Live value display ───────────────────────────────────────────
            GUIStyle valueStyle = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Bold,
                normal    = { textColor = justSet    ? new Color(0.1f, 0.85f, 0.3f) :
                                          changed     ? new Color(0.2f, 0.9f, 0.4f) :
                                                        Color.white }
            };
            EditorGUILayout.LabelField("Value: " + v.CurrentValue, valueStyle);

            if (_showDefaults && v.DefaultValue != v.CurrentValue)
                EditorGUILayout.LabelField("Default: " + v.DefaultValue, EditorStyles.miniLabel);

            if (v.Key != v.FieldName)
                EditorGUILayout.LabelField("Key: " + v.Key, EditorStyles.miniLabel);

            // ── Edit row ─────────────────────────────────────────────────────
            if (v.HasSetter)
            {
                GUILayout.Space(3);

                // Ensure the edit buffer has a starting value when user first focuses
                if (!_editBuffers.ContainsKey(v.Path))
                    _editBuffers[v.Path] = v.CurrentValue ?? "";

                if (v.ValueKind == VarKind.Bool)
                {
                    // Bool: toggle applies immediately
                    bool current = v.CurrentValue == "true" || v.CurrentValue == "True" || v.CurrentValue == "1";
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Set:", EditorStyles.miniLabel, GUILayout.Width(28));
                    bool toggled = EditorGUILayout.Toggle(current, GUILayout.Width(20));
                    EditorGUILayout.LabelField(toggled ? "true" : "false", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();

                    if (toggled != current)
                        ApplyValue(v, toggled ? "true" : "false");
                }
                else
                {
                    // String / Int / Float / Other: text field + Apply button
                    string fieldControlName = "varfield_" + v.Path;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Set:", EditorStyles.miniLabel, GUILayout.Width(28));

                    GUI.SetNextControlName(fieldControlName);
                    string newBuf = EditorGUILayout.TextField(_editBuffers[v.Path]);
                    if (newBuf != _editBuffers[v.Path])
                        _editBuffers[v.Path] = newBuf;

                    bool applyPressed = GUILayout.Button("Apply", EditorStyles.miniButton, GUILayout.Width(46));

                    // Also allow Enter key while the field is focused
                    bool enterPressed = Event.current.type == EventType.KeyDown
                                        && Event.current.keyCode == KeyCode.Return
                                        && GUI.GetNameOfFocusedControl() == fieldControlName;

                    if (applyPressed || enterPressed)
                    {
                        ApplyValue(v, _editBuffers[v.Path]);
                        if (enterPressed) Event.current.Use();
                    }

                    // Reset to default (↺ button enabled only when value differs from default)
                    GUI.enabled = v.DefaultValue != "-" && v.DefaultValue != v.CurrentValue;
                    if (GUILayout.Button("↺", EditorStyles.miniButton, GUILayout.Width(22)))
                        ApplyValue(v, v.DefaultValue);
                    GUI.enabled = true;

                    EditorGUILayout.EndHorizontal();
                }

                // Error message
                if (_setErrors.TryGetValue(v.Path, out string err))
                {
                    GUIStyle errStyle = new GUIStyle(EditorStyles.miniLabel)
                        { normal = { textColor = new Color(1f, 0.4f, 0.4f) }, wordWrap = true };
                    EditorGUILayout.LabelField("⚠ " + err, errStyle);
                }
            }
            else
            {
                // Read-only indicator
                GUIStyle roStyle = new GUIStyle(EditorStyles.miniLabel)
                    { normal = { textColor = new Color(0.6f, 0.6f, 0.6f) } };
                EditorGUILayout.LabelField("read-only (no setter)", roStyle);
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>Calls the setter with the given text value and updates error/ok state.</summary>
        private void ApplyValue(RuntimeVariableEntry v, string text)
        {
            var (ok, error) = InvokeLinkedValueSetter(v.ElementRef, text);
            if (ok)
            {
                _setErrors.Remove(v.Path);
                _editBuffers[v.Path] = text;
                _lastSetOkPath = v.Path;
                // Force an immediate value refresh so the live label updates
                v.CurrentValue = InvokeLinkedValueGetter(v.ElementRef);
                Debug.Log($"[Grimoire Runtime Inspector] Set '{v.FieldName}' = {text}");
            }
            else
            {
                _setErrors[v.Path] = error;
                _lastSetOkPath = null;
                Debug.LogWarning($"[Grimoire Runtime Inspector] Could not set '{v.FieldName}': {error}");
            }
            RequestRepaint();
        }

        // ──────────────────────────────────────────── Dialogs Tab ────────────

        private void DrawDialogsTab()
        {
            if (_dialogs.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No active dialog state detected.\n\n" +
                    (_grimoireTypesFound
                        ? "Grimoire types are loaded. A dialog runtime entry will appear here once a " +
                          "DialogRuntime / DialogManager MonoBehaviour is active in the scene and a dialog is running."
                        : "No Grimoire types found. Import a Grimoire export first."),
                    MessageType.Info);
                return;
            }

            string filt = NormaliseFilter();

            foreach (var d in _dialogs)
            {
                if (!MatchesFilter(filt, d.DialogKey, d.CurrentSection, d.State)) continue;

                DrawDialogEntry(d);
                GUILayout.Space(4);
            }
        }

        private void DrawDialogEntry(RuntimeDialogEntry d)
        {
            string id = "dlg:" + d.DialogKey;
            bool exp = _expanded.Contains(id);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            bool newExp = EditorGUILayout.Foldout(exp, d.DialogKey, true, titleStyle);
            ToggleExpanded(id, exp, newExp);

            EditorGUILayout.LabelField("Current Section: " + d.CurrentSection);
            EditorGUILayout.LabelField("State: " + d.State, EditorStyles.miniLabel);

            if (newExp)
            {
                EditorGUI.indentLevel++;
                foreach (var kv in d.Properties)
                    DrawKeyValue(kv.Key, kv.Value);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        // ──────────────────────────────────────────── Objects Tab ────────────

        private void DrawObjectsTab()
        {
            if (_objects.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No Grimoire Object Links found in the loaded scene.\n\n" +
                    "Add Object Links to GameObjects you use in Play Mode, then press Refresh.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(
                $"In play ({_objects.Count})",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Scene Object Links with live values from the API snapshot (export Database is optional).",
                GrimoireEditorStyles.MiniSecondaryStyle);
            EditorGUILayout.Space(2);

            string filt = NormaliseFilter();
            foreach (var obj in _objects
                         .Where(o => MatchesFilter(filt, o.Key, o.TypeName, o.SceneObjectName))
                         .OrderBy(o => o.SceneObjectName ?? o.Key))
            {
                DrawObjectEntry(obj);
                GUILayout.Space(3);
            }
        }

        private void DrawObjectEntry(RuntimeObjectEntry obj)
        {
            string id  = "obj:" + obj.Key;
            bool   exp = _expanded.Contains(id);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var title = !string.IsNullOrEmpty(obj.SceneObjectName)
                ? $"{obj.SceneObjectName}  ·  {obj.Key}"
                : obj.Key;
            if (!string.IsNullOrEmpty(obj.TypeName) && obj.TypeName != "ObjectLink")
            {
                title += $"  [{obj.TypeName}]";
            }

            bool newExp = EditorGUILayout.Foldout(exp, title, true);
            ToggleExpanded(id, exp, newExp);

            if (newExp)
            {
                if (obj.EditableFields.Count > 0)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (obj.ObjectRuntimeAvailable)
                    {
                        if (GUILayout.Button("Save", GUILayout.Width(44), GUILayout.Height(16)))
                        {
                            try { _objRtSavePrefs?.Invoke(null, null); } catch { }
                        }

                        if (GUILayout.Button("Reset", GUILayout.Width(44), GUILayout.Height(16)))
                        {
                            try
                            {
                                _objRtResetItem?.Invoke(null, new object[] { obj.RuntimeKey });
                                PopulateObjectFields(obj, obj.SceneGameObject != null
                                    ? obj.SceneGameObject.GetComponent<GrimoireObjectLink>()
                                    : null);
                                GUI.FocusControl(null);
                                RequestRepaint();
                            }
                            catch { }
                        }
                    }

                    EditorGUILayout.EndHorizontal();

                    foreach (var field in obj.EditableFields)
                        DrawObjectFieldEntry(obj, field);
                }
                else if (obj.Properties.Count > 0)
                {
                    foreach (var kv in obj.Properties)
                        DrawKeyValue(kv.Key, kv.Value);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        "No adaptable fields found for this object.",
                        GrimoireEditorStyles.MiniSecondaryStyle);
                }
            }

            EditorGUILayout.EndVertical();

            if (obj.SceneGameObject != null)
            {
                if (GUILayout.Button("Ping", GUILayout.Width(44), GUILayout.Height(18)))
                {
                    EditorGUIUtility.PingObject(obj.SceneGameObject);
                    Selection.activeGameObject = obj.SceneGameObject;
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawObjectFieldEntry(RuntimeObjectEntry obj, RuntimeObjectField field)
        {
            Color origBg = GUI.backgroundColor;
            var differsFromDb = !string.IsNullOrEmpty(field.DefaultValue)
                                && !ValuesEqual(field.EditBuffer, field.DefaultValue, field.Kind);
            if (field.HasOverride || differsFromDb)
            {
                GUI.backgroundColor = new Color(0.75f, 0.9f, 1f); // live / object value differs from DB
            }

            EditorGUILayout.BeginVertical();
            EditorGUILayout.BeginHorizontal();

            if (field.Kind == FieldVarKind.Boolean)
            {
                bool current = IsTruthy(field.EditBuffer);
                bool toggled = EditorGUILayout.Toggle(new GUIContent(field.Name), current);
                if (toggled != current)
                {
                    field.EditBuffer = toggled ? "true" : "false";
                    ApplyObjectFieldValue(obj, field);
                }
            }
            else
            {
                EditorGUILayout.PrefixLabel(field.Name);
                string newBuf = EditorGUILayout.TextField(field.EditBuffer);
                if (newBuf != field.EditBuffer)
                {
                    field.EditBuffer = newBuf;
                    if (field.Kind == FieldVarKind.String)
                        ApplyObjectFieldValue(obj, field);
                }

                if ((field.Kind == FieldVarKind.Number || field.Kind == FieldVarKind.StringArray) &&
                    GUILayout.Button("Set", GUILayout.Width(36), GUILayout.Height(16)))
                {
                    ApplyObjectFieldValue(obj, field);
                }
            }

            GUI.backgroundColor = origBg;
            EditorGUILayout.EndHorizontal();

            if (differsFromDb)
            {
                EditorGUILayout.LabelField(
                    $"Database: {field.DefaultValue}",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.EndVertical();
        }

        private void ApplyObjectFieldValue(RuntimeObjectEntry obj, RuntimeObjectField field)
        {
            if (!obj.ObjectRuntimeAvailable) return;
            var runtimeName = string.IsNullOrEmpty(field.RuntimeName) ? field.Name : field.RuntimeName;
            try
            {
                switch (field.Kind)
                {
                    case FieldVarKind.Number:
                        if (double.TryParse(field.EditBuffer,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double num))
                        {
                            _objRtSetNumber?.Invoke(null, new object[] { obj.RuntimeKey, runtimeName, num });
                            field.HasOverride = true;
                        }
                        break;

                    case FieldVarKind.String:
                        _objRtSetString?.Invoke(null, new object[] { obj.RuntimeKey, runtimeName, field.EditBuffer });
                        field.HasOverride = true;
                        break;

                    case FieldVarKind.Boolean:
                        bool bv = IsTruthy(field.EditBuffer);
                        field.EditBuffer = bv ? "true" : "false";
                        _objRtSetBool?.Invoke(null, new object[] { obj.RuntimeKey, runtimeName, bv });
                        field.HasOverride = true;
                        break;

                    case FieldVarKind.StringArray:
                        string[] arr = ParseStringArray(field.EditBuffer);
                        _objRtSetStringArray?.Invoke(null, new object[] { obj.RuntimeKey, runtimeName, arr });
                        field.HasOverride = true;
                        break;
                }
                RequestRepaint();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GrimoireRuntimeInspector] ApplyObjectFieldValue failed: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────── Scene Tab ──────────────

        private void DrawSceneTab()
        {
            if (_sceneComponents.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No Grimoire MonoBehaviours found in the active scene.\n\n" +
                    "Add a GrimoireBootstrap (or any Grimoire MonoBehaviour) to the scene, then enter Play Mode.",
                    MessageType.Info);
                return;
            }

            string filt = NormaliseFilter();

            foreach (var comp in _sceneComponents)
            {
                if (!MatchesFilter(filt, comp.TypeName, comp.GameObjectName)) continue;

                DrawSceneEntry(comp);
                GUILayout.Space(3);
            }
        }

        private void DrawSceneEntry(SceneComponentEntry comp)
        {
            string id = "scene:" + comp.InstanceId;
            bool exp = _expanded.Contains(id);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical();

            bool newExp = EditorGUILayout.Foldout(exp, $"[{comp.TypeName}]  on  {comp.GameObjectName}", true);
            ToggleExpanded(id, exp, newExp);

            if (newExp)
            {
                EditorGUI.indentLevel++;
                foreach (var kv in comp.PublicMembers)
                    DrawKeyValue(kv.Key, kv.Value);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();

            if (GUILayout.Button("Ping", GUILayout.Width(40), GUILayout.Height(18)) && comp.Component != null)
            {
                EditorGUIUtility.PingObject(comp.Component);
                Selection.activeGameObject = comp.Component.gameObject;
            }

            EditorGUILayout.EndHorizontal();
        }

        // ─────────────────────────────────────── Shared draw helpers ─────────

        private static void DrawKeyValue(string key, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(key, EditorStyles.miniLabel, GUILayout.Width(150));
            string display = value ?? "null";
            if (display.Length > 240) display = display.Substring(0, 237) + "...";
            EditorGUILayout.LabelField(display, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void ToggleExpanded(string id, bool was, bool now)
        {
            if (now && !was) _expanded.Add(id);
            else if (!now && was) _expanded.Remove(id);
        }

        private string NormaliseFilter() =>
            string.IsNullOrWhiteSpace(_filter) ? null : _filter.Trim();

        private static bool MatchesFilter(string filt, params string[] candidates)
        {
            if (filt == null) return true;
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && c.IndexOf(filt, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        // ─────────────────────────────────────────── Data Refresh ────────────

        private void RefreshData()
        {
            _variables.Clear();
            _dialogs.Clear();
            _objects.Clear();
            _sceneComponents.Clear();

            try
            {
                var grimoireTypes = GetGrimoireTypes();
                _grimoireTypesFound = grimoireTypes.Count > 0;
                CacheObjectRuntimeMethods(grimoireTypes);

                if (!_grimoireTypesFound)
                {
                    _status = "No Grimoire types found. Import a Grimoire export to Assets/Grimoire/ first.";
                    _statusType = MessageType.Warning;
                    return;
                }

                ScanSceneComponents(grimoireTypes);
                ScanVariables(grimoireTypes);
                ScanDialogs(grimoireTypes);

                var library = new List<RuntimeObjectEntry>();
                ScanObjectsInto(grimoireTypes, library);
                BuildSceneObjects(library);
                ScanLogic(grimoireTypes);
                FilterLogicToSceneObjects();

                _status = $"Snapshot  ·  {_objects.Count} scene objects  ·  " +
                          $"{_variables.Count} vars  ·  {_dialogs.Count} dialogs  ·  {_logicEntries.Count} logic";
                _statusType = MessageType.Info;
            }
            catch (Exception ex)
            {
                _status = $"Refresh error: {ex.Message}";
                _statusType = MessageType.Error;
                Debug.LogError($"[Grimoire Runtime Inspector] Refresh failed:\n{ex}");
            }
        }

        // ── Cache ObjectRuntime reflection methods ─────────────────────────

        private void CacheObjectRuntimeMethods(List<Type> grimoireTypes)
        {
            _objectRuntimeType = grimoireTypes.FirstOrDefault(t => t.Name == "ObjectRuntime");
            if (_objectRuntimeType == null)
            {
                _objRtGetNumber = _objRtGetString = _objRtGetBool = _objRtGetStringArray = null;
                _objRtSetNumber = _objRtSetString = _objRtSetBool = _objRtSetStringArray = null;
                _objRtHasOverride = _objRtSavePrefs = _objRtResetItem = null;
                return;
            }

            _objRtGetNumber      = _objectRuntimeType.GetMethod("GetNumber",       new[] { typeof(string), typeof(string), typeof(double) });
            _objRtGetString      = _objectRuntimeType.GetMethod("GetString",       new[] { typeof(string), typeof(string), typeof(string) });
            _objRtGetBool        = _objectRuntimeType.GetMethod("GetBoolean",      new[] { typeof(string), typeof(string), typeof(bool) });
            _objRtGetStringArray = _objectRuntimeType.GetMethod("GetStringArray",  new[] { typeof(string), typeof(string) });
            _objRtSetNumber      = _objectRuntimeType.GetMethod("SetNumber",       new[] { typeof(string), typeof(string), typeof(double) });
            _objRtSetString      = _objectRuntimeType.GetMethod("SetString",       new[] { typeof(string), typeof(string), typeof(string) });
            _objRtSetBool        = _objectRuntimeType.GetMethod("SetBoolean",      new[] { typeof(string), typeof(string), typeof(bool) });
            _objRtSetStringArray = _objectRuntimeType.GetMethod("SetStringArray",  new[] { typeof(string), typeof(string), typeof(string[]) });
            _objRtHasOverride    = _objectRuntimeType.GetMethod("HasOverride",     new[] { typeof(string), typeof(string) });
            _objRtSavePrefs      = _objectRuntimeType.GetMethod("SaveToPlayerPrefs", Type.EmptyTypes);
            _objRtResetItem      = _objectRuntimeType.GetMethod("ResetItem",       new[] { typeof(string) });
        }

        // ── Collect all types in the Grimoire namespace ────────────────────

        private static List<Type> GetGrimoireTypes()
        {
            var result = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Namespace == "Grimoire" ||
                            (t.FullName != null && t.FullName.StartsWith("Grimoire.", StringComparison.Ordinal)))
                            result.Add(t);
                    }
                }
                catch { }
            }
            return result;
        }

        // ── Scene components ───────────────────────────────────────────────

        private void ScanSceneComponents(List<Type> grimoireTypes)
        {
            var mbBase = typeof(MonoBehaviour);
            foreach (var t in grimoireTypes)
            {
                if (!mbBase.IsAssignableFrom(t) || t.IsAbstract) continue;

                try
                {
                    var found = UnityEngine.Object.FindObjectsOfType(t);
                    foreach (var obj in found)
                    {
                        if (!(obj is MonoBehaviour mb)) continue;
                        AddSceneComponentEntry(t, mb);
                    }
                }
                catch { }
            }

            // Plugin components live outside the export namespace.
            foreach (var bootstrap in UnityEngine.Object.FindObjectsOfType<GrimoireBootstrap>(true))
            {
                AddSceneComponentEntry(typeof(GrimoireBootstrap), bootstrap);
            }

            foreach (var tracker in UnityEngine.Object.FindObjectsOfType<GrimoireSessionTracker>(true))
            {
                AddSceneComponentEntry(typeof(GrimoireSessionTracker), tracker);
            }

            foreach (var link in UnityEngine.Object.FindObjectsOfType<GrimoireObjectLink>(true))
            {
                if (!link.HasKey)
                {
                    continue;
                }

                var entry = new SceneComponentEntry
                {
                    TypeName       = "GrimoireObjectLink",
                    GameObjectName = link.gameObject.name,
                    Component      = link,
                    InstanceId     = UnityObjectId.Of(link)
                };
                entry.PublicMembers["ObjectKey"] = link.ObjectKey;
                if (!string.IsNullOrEmpty(link.CachedObjectId))
                {
                    entry.PublicMembers["CachedObjectId"] = link.CachedObjectId;
                }

                _sceneComponents.Add(entry);
            }
        }

        private void AddSceneComponentEntry(Type t, MonoBehaviour mb)
        {
            var entry = new SceneComponentEntry
            {
                TypeName       = t.Name,
                GameObjectName = mb.gameObject.name,
                Component      = mb,
                InstanceId     = UnityObjectId.Of(mb)
            };

            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                try { entry.PublicMembers[prop.Name] = FormatValue(prop.GetValue(mb)); } catch { }
            }

            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try { entry.PublicMembers[field.Name] = FormatValue(field.GetValue(mb)); } catch { }
            }

            _sceneComponents.Add(entry);
        }

        /// <summary>
        /// Build the Objects list from scene <see cref="GrimoireObjectLink"/>s only,
        /// resolving fields from the API snapshot (export Library is optional).
        /// </summary>
        private void BuildSceneObjects(List<RuntimeObjectEntry> library)
        {
            _objects.Clear();

            var links = UnityEngine.Object.FindObjectsOfType<GrimoireObjectLink>(true);
            if (links == null || links.Length == 0)
            {
                return;
            }

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var link in links)
            {
                if (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId))
                {
                    continue;
                }

                var objectKey = link.HasKey ? link.ObjectKey.Trim() : (link.CachedObjectId ?? "");
                var libraryMatch = FindLibraryEntry(library, objectKey);
                seenKeys.Add(objectKey);
                if (!string.IsNullOrEmpty(link.CachedObjectId))
                {
                    seenKeys.Add(link.CachedObjectId);
                }

                var typeName = libraryMatch?.TypeName;
                if (string.IsNullOrEmpty(typeName))
                {
                    typeName = !string.IsNullOrEmpty(link.Snapshot?.Name)
                        ? link.Snapshot.Name
                        : "Object";
                }

                var entry = new RuntimeObjectEntry
                {
                    Key = objectKey,
                    RuntimeKey = objectKey,
                    TypeName = typeName,
                    LibraryInstance = libraryMatch?.LibraryInstance,
                    ObjectRuntimeAvailable = _objectRuntimeType != null,
                    InScene = true,
                    SceneObjectName = link.gameObject.name,
                    SceneGameObject = link.gameObject,
                };

                if (libraryMatch != null)
                {
                    foreach (var kv in libraryMatch.Properties)
                    {
                        if (kv.Key.StartsWith("__", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        entry.Properties[kv.Key] = kv.Value;
                    }
                }

                PopulateObjectFields(entry, link);
                _objects.Add(entry);

                AddNestedSnapshotRows(link, library, seenKeys);
            }

            _objects.Sort((a, b) => string.Compare(
                a.SceneObjectName ?? a.Key,
                b.SceneObjectName ?? b.Key,
                StringComparison.OrdinalIgnoreCase));
        }

        private void AddNestedSnapshotRows(
            GrimoireObjectLink link,
            List<RuntimeObjectEntry> library,
            HashSet<string> seenKeys)
        {
            var nested = link?.NestedSnapshots;
            if (nested == null)
            {
                return;
            }

            for (var i = 0; i < nested.Count; i++)
            {
                var snapshot = nested[i];
                if (snapshot == null || snapshot.IsEmpty)
                {
                    continue;
                }

                var nestedKey = !string.IsNullOrEmpty(snapshot.ObjectKey)
                    ? snapshot.ObjectKey
                    : snapshot.ObjectId;
                if (string.IsNullOrEmpty(nestedKey) || !seenKeys.Add(nestedKey))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(snapshot.ObjectId))
                {
                    seenKeys.Add(snapshot.ObjectId);
                }

                var libraryMatch = FindLibraryEntry(library, nestedKey);
                var nestedEntry = new RuntimeObjectEntry
                {
                    Key = nestedKey,
                    RuntimeKey = nestedKey,
                    TypeName = libraryMatch?.TypeName
                               ?? (!string.IsNullOrEmpty(snapshot.Name) ? snapshot.Name : "Object"),
                    LibraryInstance = libraryMatch?.LibraryInstance,
                    ObjectRuntimeAvailable = _objectRuntimeType != null,
                    InScene = false,
                    SceneObjectName = $"{link.gameObject.name} / {snapshot.Name}",
                    SceneGameObject = link.gameObject,
                };

                PopulateObjectFieldsFromSnapshot(nestedEntry, snapshot);
                _objects.Add(nestedEntry);
            }
        }

        private static RuntimeObjectEntry FindLibraryEntry(
            List<RuntimeObjectEntry> library,
            string objectKey)
        {
            if (library == null || library.Count == 0 || string.IsNullOrEmpty(objectKey))
            {
                return null;
            }

            foreach (var candidate in library)
            {
                if (KeysMatch(objectKey, candidate.Key))
                {
                    return candidate;
                }

                if (candidate.Properties != null
                    && candidate.Properties.TryGetValue("__libraryPath", out var path)
                    && KeysMatch(objectKey, path))
                {
                    return candidate;
                }
            }

            // Last-segment match (playerhouse/interactables_bed ↔ …interactables_bed).
            var needle = LastKeySegment(objectKey);
            RuntimeObjectEntry segmentMatch = null;
            var ambiguous = false;
            foreach (var candidate in library)
            {
                if (!string.Equals(LastKeySegment(candidate.Key), needle, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (segmentMatch != null)
                {
                    ambiguous = true;
                    break;
                }

                segmentMatch = candidate;
            }

            return ambiguous ? null : segmentMatch;
        }

        private static bool KeysMatch(string objectKey, string libraryKey)
        {
            if (string.IsNullOrEmpty(objectKey) || string.IsNullOrEmpty(libraryKey))
            {
                return false;
            }

            if (string.Equals(objectKey, libraryKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var a = NormalizeKey(objectKey);
            var b = NormalizeKey(libraryKey);
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase)
                   || a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeKey(string key) =>
            (key ?? "").Replace('\\', '/').Replace('.', '/').Trim('/');

        private static string LastKeySegment(string key)
        {
            var normalized = NormalizeKey(key);
            var slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }

        private void FilterLogicToSceneObjects()
        {
            if (_logicEntries.Count == 0 || _objects.Count == 0)
            {
                _logicEntries.Clear();
                return;
            }

            _logicEntries.RemoveAll(entry =>
            {
                foreach (var obj in _objects)
                {
                    if (KeysMatch(obj.Key, entry.ObjectKey)
                        || string.Equals(
                            LastKeySegment(obj.Key),
                            LastKeySegment(entry.ObjectKey),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            });
        }

        // ── Variables ─────────────────────────────────────────────────────

        private void ScanVariables(List<Type> grimoireTypes)
        {
            // Find VariableElement abstract base (or the most general base that has subclasses)
            Type varBase = grimoireTypes.FirstOrDefault(t =>
                t.Name == "VariableElement" && t.IsAbstract)
                ?? grimoireTypes.FirstOrDefault(t =>
                    t.Name.Contains("VariableElement") && !t.Name.Contains("Runtime"));

            if (varBase == null) return;

            // Static classes whose name ends with "Variables"
            var roots = grimoireTypes.Where(t =>
                t.IsAbstract && t.IsSealed && t.IsClass &&
                t.Name.EndsWith("Variables", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var root in roots)
                ScanTypeForVariables(root, root.Name, "", varBase);
        }

        private void ScanTypeForVariables(Type type, string rootName, string categoryPath, Type varBase)
        {
            string category = string.IsNullOrEmpty(categoryPath) ? rootName : categoryPath;

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral) continue;

                if (!IsVariableElementType(field.FieldType, varBase)) continue;

                object element = null;
                try { element = field.GetValue(null); } catch { }
                if (element == null) continue;

                string key        = ReadStringMember(element, "Key") ?? field.Name;
                string defaultVal = ReadDefaultValue(element);
                string currentVal = InvokeLinkedValueGetter(element);
                string shortType  = field.FieldType.Name
                    .Replace("VariableElement", "")
                    .Replace("Variable", "");
                string shortTypeFinal = string.IsNullOrEmpty(shortType) ? field.FieldType.Name : shortType;

                _variables.Add(new RuntimeVariableEntry
                {
                    FieldName     = field.Name,
                    Key           = key,
                    Category      = category,
                    TypeName      = field.FieldType.Name,
                    ShortTypeName = shortTypeFinal,
                    CurrentValue  = currentVal,
                    DefaultValue  = defaultVal,
                    Path          = type.FullName + "." + field.Name,
                    ElementRef    = element,
                    HasSetter     = CheckHasSetter(element),
                    ValueKind     = DetectVarKind(shortTypeFinal)
                });
            }

            // Recurse into nested static classes (categories)
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!nested.IsAbstract || !nested.IsSealed || !nested.IsClass) continue;

                string newCat = string.IsNullOrEmpty(categoryPath)
                    ? nested.Name
                    : categoryPath + " / " + nested.Name;

                ScanTypeForVariables(nested, rootName, newCat, varBase);
            }
        }

        /// <summary>
        /// Returns true if the element has a writable storage property
        /// (NumericValue, PercentageValue, BooleanValue, or Name for strings).
        /// Falls back to checking for a LinkedValueSetter delegate for custom types.
        /// </summary>
        private static bool CheckHasSetter(object element)
        {
            Type t = element.GetType();

            // Exported typed elements use typed storage properties.
            foreach (var propName in new[] { "NumericValue", "PercentageValue", "BooleanValue" })
            {
                var prop = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite) return true;
            }

            // StringVariableElement: Value is derived from Name which is writable on the base.
            var valueProp = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (valueProp != null)
            {
                var nameProp = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                if (nameProp != null && nameProp.CanWrite) return true;
            }

            // Legacy fallback: LinkedValueSetter delegate.
            object setter = FindLinkedValueMember(element, "LinkedValueSetter");
            return setter != null;
        }

        /// <summary>Detects the primitive kind from the ShortTypeName string.</summary>
        private static VarKind DetectVarKind(string shortType)
        {
            if (string.IsNullOrEmpty(shortType)) return VarKind.Other;
            string s = shortType.Trim().ToLowerInvariant();
            if (s == "string" || s == "text") return VarKind.String;
            if (s == "int" || s == "integer" || s == "long") return VarKind.Int;
            if (s == "float" || s == "double" || s == "single" || s == "number" || s == "percentage") return VarKind.Float;
            if (s == "bool" || s == "boolean") return VarKind.Bool;
            return VarKind.Other;
        }

        /// <summary>
        /// Sets the element's storage property from the given text value.
        /// Handles NumericValue, PercentageValue, BooleanValue, and Name (for string elements).
        /// Falls back to invoking a LinkedValueSetter delegate for custom types.
        /// Returns (true, null) on success or (false, errorMessage).
        /// </summary>
        private static (bool ok, string error) InvokeLinkedValueSetter(object element, string textValue)
        {
            try
            {
                Type t = element.GetType();

                // NumberVariableElement: set NumericValue (double?)
                var numProp = t.GetProperty("NumericValue", BindingFlags.Public | BindingFlags.Instance);
                if (numProp != null && numProp.CanWrite)
                {
                    if (double.TryParse(textValue, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d))
                    {
                        numProp.SetValue(element, (double?)d);
                        return (true, null);
                    }
                    return (false, $"'{textValue}' is not a valid number.");
                }

                // PercentageVariableElement: set PercentageValue (double?)
                var pctProp = t.GetProperty("PercentageValue", BindingFlags.Public | BindingFlags.Instance);
                if (pctProp != null && pctProp.CanWrite)
                {
                    if (double.TryParse(textValue, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d))
                    {
                        pctProp.SetValue(element, (double?)d);
                        return (true, null);
                    }
                    return (false, $"'{textValue}' is not a valid percentage.");
                }

                // BooleanVariableElement: set BooleanValue (bool?)
                var boolProp = t.GetProperty("BooleanValue", BindingFlags.Public | BindingFlags.Instance);
                if (boolProp != null && boolProp.CanWrite)
                {
                    string lower = (textValue ?? "").Trim().ToLowerInvariant();
                    if (lower == "true"  || lower == "1" || lower == "yes") { boolProp.SetValue(element, (bool?)true);  return (true, null); }
                    if (lower == "false" || lower == "0" || lower == "no")  { boolProp.SetValue(element, (bool?)false); return (true, null); }
                    return (false, "Expected true / false / 1 / 0.");
                }

                // StringVariableElement: Value is derived from Name — set Name directly.
                var valueProp = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (valueProp != null)
                {
                    var nameProp = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null && nameProp.CanWrite)
                    {
                        nameProp.SetValue(element, textValue);
                        return (true, null);
                    }
                }

                // Legacy fallback: LinkedValueSetter delegate.
                object setter = FindLinkedValueMember(element, "LinkedValueSetter");
                if (setter == null) return (false, "No writable storage property or setter found on this variable element.");

                var invokeMethod = setter.GetType().GetMethod("Invoke");
                if (invokeMethod == null) return (false, "Setter delegate has no Invoke method.");

                var parameters = invokeMethod.GetParameters();
                if (parameters.Length != 1)
                    return (false, $"Setter has {parameters.Length} parameters (expected 1).");

                Type paramType = parameters[0].ParameterType;

                object parsedValue;
                try { parsedValue = ParseValue(textValue, paramType); }
                catch (Exception ex) { return (false, ex.Message); }

                invokeMethod.Invoke(setter, new[] { parsedValue });
                return (true, null);
            }
            catch (TargetInvocationException tie)
            {
                return (false, tie.InnerException?.Message ?? tie.Message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>Parses a string representation into the requested target type.</summary>
        private static object ParseValue(string text, Type target)
        {
            if (target == typeof(string))  return text;

            if (target == typeof(bool) || target == typeof(bool?))
            {
                string lower = (text ?? "").Trim().ToLowerInvariant();
                if (lower == "true"  || lower == "1" || lower == "yes") return true;
                if (lower == "false" || lower == "0" || lower == "no")  return false;
                throw new FormatException("Expected true / false / 1 / 0.");
            }

            if (target == typeof(int) || target == typeof(int?))
            {
                if (int.TryParse(text, out int i)) return i;
                throw new FormatException($"'{text}' is not a valid integer.");
            }

            if (target == typeof(float) || target == typeof(float?))
            {
                if (float.TryParse(text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float f)) return f;
                throw new FormatException($"'{text}' is not a valid float.");
            }

            if (target == typeof(double) || target == typeof(double?))
            {
                if (double.TryParse(text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
                throw new FormatException($"'{text}' is not a valid double.");
            }

            // Generic fallback
            return Convert.ChangeType(text, target, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Walks the type hierarchy to find LinkedValueGetter or LinkedValueSetter by name.</summary>
        private static object FindLinkedValueMember(object element, string memberName)
        {
            Type cur = element.GetType();
            while (cur != null && cur != typeof(object))
            {
                var prop = cur.GetProperty(memberName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (prop != null) { try { return prop.GetValue(element); } catch { return null; } }

                var field = cur.GetField(memberName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) { try { return field.GetValue(element); } catch { return null; } }

                cur = cur.BaseType;
            }
            return null;
        }

        private static bool IsVariableElementType(Type t, Type varBase)
        {
            if (varBase.IsAssignableFrom(t)) return true;

            // Walk up the hierarchy for generic base types
            Type cur = t;
            while (cur != null && cur != typeof(object))
            {
                if (cur.Name.Contains("VariableElement")) return true;
                cur = cur.BaseType;
            }
            return false;
        }

        /// <summary>
        /// Reads the live value from a variable element.
        /// Calls GetValue() / GetMin()+GetMax() / the Value property as appropriate for each
        /// exported VariableElement subclass so that linked-variable chains are resolved correctly.
        /// Falls back to invoking the LinkedValueGetter delegate for custom types.
        /// </summary>
        private static string InvokeLinkedValueGetter(object element)
        {
            try
            {
                Type t = element.GetType();

                // NumberVariableElement / BooleanVariableElement / PercentageVariableElement
                // all expose a no-arg GetValue() that follows linked chains automatically.
                var getValueMethod = t.GetMethod("GetValue",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getValueMethod != null)
                {
                    object result = getValueMethod.Invoke(element, null);
                    return FormatValue(result);
                }

                // StringVariableElement: value is exposed as the read-only Value property.
                var valueProp = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (valueProp != null && valueProp.CanRead)
                {
                    object result = valueProp.GetValue(element);
                    return FormatValue(result);
                }

                // RangeVariableElement: expose as "min..max (step incr)"
                var getMinMethod  = t.GetMethod("GetMin",       BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var getMaxMethod  = t.GetMethod("GetMax",       BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var getIncrMethod = t.GetMethod("GetIncrement", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getMinMethod != null && getMaxMethod != null)
                {
                    string min  = FormatValue(getMinMethod.Invoke(element, null));
                    string max  = FormatValue(getMaxMethod.Invoke(element, null));
                    string incr = getIncrMethod != null ? FormatValue(getIncrMethod.Invoke(element, null)) : "1";
                    return $"{min}..{max} (step {incr})";
                }

                // TypeVariableElement: resolve the linked element and return its Name.
                var getLinkedMethod = t.GetMethod("GetLinkedValue",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getLinkedMethod != null)
                {
                    object linked = getLinkedMethod.Invoke(element, null);
                    if (linked == null) return "null";
                    return ReadStringMember(linked, "Name") ?? FormatValue(linked);
                }

                // Legacy fallback: invoke the LinkedValueGetter delegate directly.
                object getter = FindLinkedValueMember(element, "LinkedValueGetter");
                if (getter == null) return "(no getter)";

                var invoke = getter.GetType().GetMethod("Invoke");
                if (invoke == null) return "(not invokable)";

                object legacyResult = invoke.Invoke(getter, null);
                return FormatValue(legacyResult);
            }
            catch (TargetInvocationException tie)
            {
                return $"(err: {TruncateMessage(tie.InnerException?.Message ?? tie.Message)})";
            }
            catch (Exception ex)
            {
                return $"(err: {TruncateMessage(ex.Message)})";
            }
        }

        // ── Dialogs ───────────────────────────────────────────────────────

        private void ScanDialogs(List<Type> grimoireTypes)
        {
            var mbBase = typeof(MonoBehaviour);

            // Look for a runtime dialog manager / controller MonoBehaviour
            foreach (var t in grimoireTypes)
            {
                if (!mbBase.IsAssignableFrom(t) || t.IsAbstract) continue;

                bool isDialogType =
                    t.Name.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (t.Name.IndexOf("Runtime", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     t.Name.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     t.Name.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     t.Name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0);

                if (!isDialogType) continue;

                try
                {
                    var found = UnityEngine.Object.FindObjectsOfType(t);
                    foreach (var obj in found)
                    {
                        if (!(obj is MonoBehaviour mb)) continue;
                        _dialogs.Add(BuildDialogEntry(t, mb));
                    }
                }
                catch { }
            }

            // Also check static singletons / classes with "Dialog" and active state properties
            foreach (var t in grimoireTypes)
            {
                if (!t.IsAbstract || !t.IsSealed || !t.IsClass) continue;
                if (t.Name.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) < 0) continue;

                object singleton = TryGetStaticSingleton(t);
                if (singleton == null) continue;

                _dialogs.Add(BuildDialogEntry(t, singleton));
            }
        }

        private static RuntimeDialogEntry BuildDialogEntry(Type t, object instance)
        {
            var entry = new RuntimeDialogEntry
            {
                DialogKey      = t.Name,
                CurrentSection = "-",
                State          = "active"
            };

            var bindFlags = BindingFlags.Public | BindingFlags.Instance;
            if (instance == null || (instance is MonoBehaviour))
                bindFlags = BindingFlags.Public | BindingFlags.Instance;

            foreach (var prop in t.GetProperties(bindFlags))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                try
                {
                    object val = prop.GetValue(instance);
                    string str = FormatValue(val);

                    if (ContainsAny(prop.Name, "DialogKey", "CurrentDialog", "ActiveDialog"))
                        entry.DialogKey = str;
                    else if (ContainsAny(prop.Name, "Section", "CurrentSection", "ActiveSection"))
                        entry.CurrentSection = str;
                    else if (ContainsAny(prop.Name, "State", "Status", "Phase"))
                        entry.State = str;

                    entry.Properties[prop.Name] = str;
                }
                catch { }
            }

            foreach (var field in t.GetFields(bindFlags))
            {
                try
                {
                    object val = field.GetValue(instance);
                    entry.Properties[field.Name] = FormatValue(val);
                }
                catch { }
            }

            return entry;
        }

        // ── Objects ───────────────────────────────────────────────────────

        private void ScanObjectsInto(List<Type> grimoireTypes, List<RuntimeObjectEntry> destination)
        {
            // Find the main Database static class
            var dbType = grimoireTypes.FirstOrDefault(t =>
                t.IsAbstract && t.IsSealed && t.IsClass &&
                (t.Name == "Database" || (t.Name.EndsWith("Database") &&
                 t.Name.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) < 0)));

            if (dbType == null) return;

            // Case 1: nested static class called Library
            var libraryNested = dbType.GetNestedType("Library", BindingFlags.Public | BindingFlags.NonPublic);
            if (libraryNested != null)
            {
                ScanLibraryType(libraryNested, dbType.Name + ".Library", destination);
                return;
            }

            // Case 2: Dictionary-based Library property/field
            object libraryObj = ReadStaticMember(dbType, "Library");
            if (libraryObj is System.Collections.IDictionary dict)
            {
                foreach (System.Collections.DictionaryEntry kvp in dict)
                {
                    string key = kvp.Key?.ToString() ?? "(null)";
                    var entry  = new RuntimeObjectEntry
                    {
                        Key      = key,
                        RuntimeKey = key,
                        TypeName = kvp.Value?.GetType().Name ?? "unknown"
                    };

                    if (kvp.Value != null)
                        CollectPublicMembers(kvp.Value, entry.Properties);

                    entry.LibraryInstance        = kvp.Value;
                    entry.ObjectRuntimeAvailable = _objectRuntimeType != null;
                    destination.Add(entry);
                }
            }
        }

        /// <summary>
        /// Build adaptable fields for a scene object.
        /// Live value priority: ObjectRuntime override → Object Link local/snapshot → Database default.
        /// When the Object Link local value differs from the database and ObjectRuntime has no
        /// override yet, the local value is written into ObjectRuntime so Play uses it.
        /// </summary>
        private void PopulateObjectFields(RuntimeObjectEntry entry, GrimoireObjectLink link)
        {
            entry.EditableFields.Clear();
            var byNorm = new Dictionary<string, RuntimeObjectField>(StringComparer.Ordinal);

            void Register(RuntimeObjectField field)
            {
                if (field == null || string.IsNullOrEmpty(field.RuntimeName))
                {
                    return;
                }

                var norm = NormalizeFieldName(field.RuntimeName);
                if (byNorm.TryGetValue(norm, out var existing))
                {
                    // Prefer richer data: keep runtime name from template, labels from link.
                    if (string.IsNullOrEmpty(existing.DefaultValue) && !string.IsNullOrEmpty(field.DefaultValue))
                    {
                        existing.DefaultValue = field.DefaultValue;
                    }

                    if (string.IsNullOrEmpty(existing.ObjectLocalValue) && !string.IsNullOrEmpty(field.ObjectLocalValue))
                    {
                        existing.ObjectLocalValue = field.ObjectLocalValue;
                    }

                    if (!string.IsNullOrEmpty(field.Name) && field.Name.IndexOf(' ') < 0)
                    {
                        existing.RuntimeName = field.RuntimeName;
                    }

                    return;
                }

                byNorm[norm] = field;
                entry.EditableFields.Add(field);
            }

            // 1) API snapshot on the Object Link (does not require an export)
            RegisterSnapshotFields(Register, link?.Snapshot);

            // 2) Database / Library template fields (optional)
            if (entry.LibraryInstance != null)
            {
                var instance = entry.LibraryInstance;
                var type = instance.GetType();

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!TryGetFieldVarKind(field.FieldType, out var kind)) continue;
                    object raw;
                    try { raw = field.GetValue(instance); }
                    catch { continue; }

                    Register(new RuntimeObjectField
                    {
                        Name = field.Name,
                        RuntimeName = field.Name,
                        Kind = kind,
                        DefaultValue = FormatFieldDefault(raw, kind),
                    });
                }

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    if (!TryGetFieldVarKind(prop.PropertyType, out var kind)) continue;
                    object raw;
                    try { raw = prop.GetValue(instance); }
                    catch { continue; }

                    Register(new RuntimeObjectField
                    {
                        Name = prop.Name,
                        RuntimeName = prop.Name,
                        Kind = kind,
                        DefaultValue = FormatFieldDefault(raw, kind),
                    });
                }
            }

            // 3) Object Link adaptable fields (values authored on the component)
            if (link?.LinkedFields != null)
            {
                foreach (var linked in link.LinkedFields)
                {
                    if (linked == null || linked.ReadOnly)
                    {
                        continue;
                    }

                    var label = !string.IsNullOrEmpty(linked.Label) ? linked.Label : linked.FieldId;
                    if (string.IsNullOrEmpty(label))
                    {
                        continue;
                    }

                    var kind = MapLinkedFieldKind(linked);
                    var norm = NormalizeFieldName(label);
                    if (byNorm.TryGetValue(norm, out var existing))
                    {
                        existing.ObjectLocalValue = linked.LocalValue ?? "";
                        if (string.IsNullOrEmpty(existing.DefaultValue))
                        {
                            existing.DefaultValue = linked.GrimoireValue ?? "";
                        }

                        continue;
                    }

                    // Try matching a known template field by normalized label.
                    RuntimeObjectField matched = null;
                    foreach (var candidate in entry.EditableFields)
                    {
                        if (NormalizeFieldName(candidate.RuntimeName) == norm
                            || NormalizeFieldName(candidate.Name) == norm)
                        {
                            matched = candidate;
                            break;
                        }
                    }

                    if (matched != null)
                    {
                        matched.ObjectLocalValue = linked.LocalValue ?? "";
                        if (string.IsNullOrEmpty(matched.DefaultValue))
                        {
                            matched.DefaultValue = linked.GrimoireValue ?? "";
                        }

                        continue;
                    }

                    Register(new RuntimeObjectField
                    {
                        Name = label,
                        RuntimeName = label,
                        Kind = kind,
                        DefaultValue = linked.GrimoireValue ?? "",
                        ObjectLocalValue = linked.LocalValue ?? "",
                    });
                }
            }

            // 4) Resolve live values and seed ObjectRuntime from object locals when needed
            foreach (var field in entry.EditableFields)
            {
                ResolveLiveFieldValue(entry, field);
            }
        }

        private void PopulateObjectFieldsFromSnapshot(
            RuntimeObjectEntry entry,
            GrimoireObjectSnapshot snapshot)
        {
            entry.EditableFields.Clear();
            var byNorm = new Dictionary<string, RuntimeObjectField>(StringComparer.Ordinal);

            void Register(RuntimeObjectField field)
            {
                if (field == null || string.IsNullOrEmpty(field.RuntimeName))
                {
                    return;
                }

                var norm = NormalizeFieldName(field.RuntimeName);
                if (byNorm.ContainsKey(norm))
                {
                    return;
                }

                byNorm[norm] = field;
                entry.EditableFields.Add(field);
            }

            RegisterSnapshotFields(Register, snapshot);

            foreach (var field in entry.EditableFields)
            {
                ResolveLiveFieldValue(entry, field);
            }
        }

        private static void RegisterSnapshotFields(
            Action<RuntimeObjectField> register,
            GrimoireObjectSnapshot snapshot)
        {
            if (register == null || snapshot?.Fields == null)
            {
                return;
            }

            foreach (var cached in snapshot.Fields)
            {
                if (cached == null)
                {
                    continue;
                }

                var label = cached.DisplayLabel;
                if (string.IsNullOrEmpty(label))
                {
                    continue;
                }

                if (cached.IsReference)
                {
                    var names = new List<string>();
                    var refs = cached.References;
                    if (refs != null)
                    {
                        for (var i = 0; i < refs.Count; i++)
                        {
                            if (refs[i] != null && !string.IsNullOrEmpty(refs[i].DisplayName))
                            {
                                names.Add(refs[i].DisplayName);
                            }
                        }
                    }

                    register(new RuntimeObjectField
                    {
                        Name = label,
                        RuntimeName = label,
                        Kind = FieldVarKind.StringArray,
                        DefaultValue = FormatStringArray(names.ToArray()),
                        ObjectLocalValue = FormatStringArray(names.ToArray()),
                    });
                    continue;
                }

                register(new RuntimeObjectField
                {
                    Name = label,
                    RuntimeName = label,
                    Kind = MapCachedFieldKind(cached),
                    DefaultValue = cached.Value ?? "",
                    ObjectLocalValue = cached.Value ?? "",
                });
            }
        }

        private void ResolveLiveFieldValue(RuntimeObjectEntry entry, RuntimeObjectField field)
        {
            var runtimeName = string.IsNullOrEmpty(field.RuntimeName) ? field.Name : field.RuntimeName;
            var databaseDefault = field.DefaultValue ?? "";
            var objectLocal = field.ObjectLocalValue;

            // Read ObjectRuntime with the database default as fallback (not 0 / empty).
            var live = ReadObjectRuntimeValue(entry.RuntimeKey, runtimeName, field.Kind, databaseDefault);
            var hasRtOverride = false;
            if (entry.ObjectRuntimeAvailable && _objRtHasOverride != null)
            {
                try
                {
                    hasRtOverride = (bool)_objRtHasOverride.Invoke(
                        null, new object[] { entry.RuntimeKey, runtimeName });
                }
                catch
                {
                    hasRtOverride = false;
                }
            }

            // Object Link local differs from database → that value is what Play should use.
            var objectDiffers = !string.IsNullOrEmpty(objectLocal)
                                && !ValuesEqual(objectLocal, databaseDefault, field.Kind);

            if (hasRtOverride)
            {
                field.EditBuffer = live ?? databaseDefault;
                field.HasOverride = true;
                return;
            }

            if (objectDiffers)
            {
                field.EditBuffer = FormatLiveBuffer(objectLocal, field.Kind);
                field.HasOverride = true;
                // Push onto ObjectRuntime so gameplay uses the object value.
                if (entry.ObjectRuntimeAvailable)
                {
                    WriteObjectRuntimeValue(entry.RuntimeKey, runtimeName, field.Kind, field.EditBuffer);
                }

                return;
            }

            // Fall back to live read (already defaulted to database) or database.
            field.EditBuffer = string.IsNullOrEmpty(live) ? databaseDefault : live;
            field.HasOverride = !ValuesEqual(field.EditBuffer, databaseDefault, field.Kind);
        }

        private string ReadObjectRuntimeValue(
            string runtimeKey,
            string fieldName,
            FieldVarKind kind,
            string databaseDefault)
        {
            if (_objectRuntimeType == null || string.IsNullOrEmpty(runtimeKey) || string.IsNullOrEmpty(fieldName))
            {
                return databaseDefault;
            }

            try
            {
                switch (kind)
                {
                    case FieldVarKind.Number:
                    {
                        if (_objRtGetNumber == null) return databaseDefault;
                        double fallback = 0;
                        double.TryParse(
                            databaseDefault,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out fallback);
                        var value = (double)_objRtGetNumber.Invoke(
                            null, new object[] { runtimeKey, fieldName, fallback });
                        return value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    case FieldVarKind.Boolean:
                    {
                        if (_objRtGetBool == null) return databaseDefault;
                        var fallback = IsTruthy(databaseDefault);
                        var value = (bool)_objRtGetBool.Invoke(
                            null, new object[] { runtimeKey, fieldName, fallback });
                        return value ? "true" : "false";
                    }
                    case FieldVarKind.StringArray:
                    {
                        if (_objRtGetStringArray == null) return databaseDefault;
                        var arr = (string[])_objRtGetStringArray.Invoke(
                            null, new object[] { runtimeKey, fieldName });
                        return FormatStringArray(arr);
                    }
                    default:
                    {
                        if (_objRtGetString == null) return databaseDefault;
                        var value = (string)_objRtGetString.Invoke(
                            null, new object[] { runtimeKey, fieldName, databaseDefault ?? "" });
                        return value ?? databaseDefault;
                    }
                }
            }
            catch
            {
                return databaseDefault;
            }
        }

        private void WriteObjectRuntimeValue(
            string runtimeKey,
            string fieldName,
            FieldVarKind kind,
            string text)
        {
            try
            {
                switch (kind)
                {
                    case FieldVarKind.Number:
                        if (double.TryParse(
                                text,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var num))
                        {
                            _objRtSetNumber?.Invoke(null, new object[] { runtimeKey, fieldName, num });
                        }
                        break;
                    case FieldVarKind.Boolean:
                        _objRtSetBool?.Invoke(null, new object[] { runtimeKey, fieldName, IsTruthy(text) });
                        break;
                    case FieldVarKind.StringArray:
                        _objRtSetStringArray?.Invoke(null, new object[] { runtimeKey, fieldName, ParseStringArray(text) });
                        break;
                    default:
                        _objRtSetString?.Invoke(null, new object[] { runtimeKey, fieldName, text ?? "" });
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Grimoire Runtime] Could not set {runtimeKey}.{fieldName}: {ex.Message}");
            }
        }

        private static string FormatFieldDefault(object raw, FieldVarKind kind)
        {
            if (kind == FieldVarKind.StringArray)
            {
                return FormatStringArray(raw as string[]);
            }

            if (raw is bool b)
            {
                return b ? "true" : "false";
            }

            if (raw is float f)
            {
                return f.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (raw is double d)
            {
                return d.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
            }

            return raw?.ToString() ?? "";
        }

        private static string FormatLiveBuffer(string value, FieldVarKind kind)
        {
            if (kind == FieldVarKind.Boolean)
            {
                return IsTruthy(value) ? "true" : "false";
            }

            return value ?? "";
        }

        private static bool ValuesEqual(string a, string b, FieldVarKind kind)
        {
            if (kind == FieldVarKind.Boolean)
            {
                return IsTruthy(a) == IsTruthy(b);
            }

            if (kind == FieldVarKind.Number)
            {
                if (double.TryParse(a, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var da)
                    && double.TryParse(b, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var db))
                {
                    return Math.Abs(da - db) < 0.000001;
                }
            }

            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim().ToLowerInvariant();
            return v == "true" || v == "1" || v == "yes";
        }

        private static string NormalizeFieldName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var chars = name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant);
            return new string(chars.ToArray());
        }

        private static FieldVarKind MapLinkedFieldKind(GrimoireLinkedField linked)
        {
            var kind = (linked.Kind ?? "").Trim().ToLowerInvariant();
            var type = (linked.FieldType ?? "").Trim().ToLowerInvariant();
            if (kind.Contains("bool") || type.Contains("bool")) return FieldVarKind.Boolean;
            if (kind.Contains("number") || kind.Contains("int") || kind.Contains("float")
                || type.Contains("number") || type.Contains("int") || type.Contains("float"))
            {
                return FieldVarKind.Number;
            }

            if (type.Contains("array") || kind.Contains("array")) return FieldVarKind.StringArray;
            return FieldVarKind.String;
        }

        private static FieldVarKind MapCachedFieldKind(GrimoireCachedField cached)
        {
            var kind = (cached.Kind ?? "").Trim().ToLowerInvariant();
            var type = (cached.FieldType ?? "").Trim().ToLowerInvariant();
            if (kind == "boolean" || type.Contains("bool")) return FieldVarKind.Boolean;
            if (kind == "number" || type.Contains("number") || type.Contains("int") || type.Contains("float"))
            {
                return FieldVarKind.Number;
            }

            if (cached.Multiple || kind == "reference") return FieldVarKind.StringArray;
            return FieldVarKind.String;
        }

        private static bool TryGetFieldVarKind(Type t, out FieldVarKind kind)
        {
            if (t == typeof(double) || t == typeof(float) || t == typeof(int) ||
                t == typeof(long)   || t == typeof(short) || t == typeof(byte))
            { kind = FieldVarKind.Number;  return true; }

            if (t == typeof(bool))
            { kind = FieldVarKind.Boolean; return true; }

            if (t == typeof(string))
            { kind = FieldVarKind.String;  return true; }

            if (t == typeof(string[]))
            { kind = FieldVarKind.StringArray; return true; }

            kind = default;
            return false;
        }

        private static string FormatStringArray(string[] arr)
        {
            if (arr == null || arr.Length == 0) return "";
            return string.Join(", ", arr);
        }

        private static string[] ParseStringArray(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            var parts = text.Split(',');
            var result = new List<string>();
            foreach (var p in parts)
            {
                string trimmed = p.Trim();
                if (trimmed.Length > 0) result.Add(trimmed);
            }
            return result.ToArray();
        }

        private void ScanLibraryType(Type type, string pathPrefix, List<RuntimeObjectEntry> destination)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral) continue;
                try
                {
                    object val = field.GetValue(null);
                    if (val == null) continue;

                    var entry = new RuntimeObjectEntry
                    {
                        Key      = field.Name,
                        RuntimeKey = field.Name,
                        TypeName = field.FieldType.Name
                    };

                    // Prefer Template.Key / Id when present — matches ObjectLink object keys.
                    var templateKey = ReadStringMember(val, "Key")
                                      ?? ReadStringMember(val, "Id")
                                      ?? ReadStringMember(val, "CodeId")
                                      ?? field.Name;
                    entry.Key = templateKey;
                    entry.RuntimeKey = templateKey;

                    CollectPublicMembers(val, entry.Properties);
                    entry.LibraryInstance        = val;
                    entry.ObjectRuntimeAvailable = _objectRuntimeType != null;
                    // Also index under the static path for fuzzy matching.
                    if (!string.Equals(pathPrefix + "." + field.Name, templateKey, StringComparison.Ordinal))
                    {
                        entry.Properties["__libraryPath"] = pathPrefix + "." + field.Name;
                    }

                    destination.Add(entry);
                }
                catch { }
            }

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
            {
                if (nested.IsAbstract && nested.IsSealed)
                    ScanLibraryType(nested, pathPrefix + "." + nested.Name, destination);
            }
        }

        private static void CollectPublicMembers(object obj, Dictionary<string, string> dict)
        {
            Type t = obj.GetType();
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                try { dict[prop.Name] = FormatValue(prop.GetValue(obj)); } catch { }
            }

            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try { dict[field.Name] = FormatValue(field.GetValue(obj)); } catch { }
            }
        }

        // ─────────────────────────────────── Reflection helpers ──────────────

        /// <summary>
        /// Reads the initial / default value from a variable element by inspecting its typed
        /// storage property (NumericValue, PercentageValue, BooleanValue, RangeMin, or Value).
        /// This is the value the element was constructed with, before any runtime edits.
        /// </summary>
        private static string ReadDefaultValue(object element)
        {
            Type t = element.GetType();

            // NumberVariableElement
            var numProp = t.GetProperty("NumericValue", BindingFlags.Public | BindingFlags.Instance);
            if (numProp != null) { object v = numProp.GetValue(element); return v != null ? FormatValue(v) : "-"; }

            // PercentageVariableElement
            var pctProp = t.GetProperty("PercentageValue", BindingFlags.Public | BindingFlags.Instance);
            if (pctProp != null) { object v = pctProp.GetValue(element); return v != null ? FormatValue(v) : "-"; }

            // BooleanVariableElement
            var boolProp = t.GetProperty("BooleanValue", BindingFlags.Public | BindingFlags.Instance);
            if (boolProp != null) { object v = boolProp.GetValue(element); return v != null ? FormatValue(v) : "-"; }

            // RangeVariableElement
            var rangeMinProp = t.GetProperty("RangeMin", BindingFlags.Public | BindingFlags.Instance);
            if (rangeMinProp != null)
            {
                var rangeMaxProp = t.GetProperty("RangeMax",  BindingFlags.Public | BindingFlags.Instance);
                var incrProp     = t.GetProperty("Increment", BindingFlags.Public | BindingFlags.Instance);
                string min  = FormatValue(rangeMinProp.GetValue(element));
                string max  = rangeMaxProp != null ? FormatValue(rangeMaxProp.GetValue(element)) : "?";
                string incr = incrProp     != null ? FormatValue(incrProp.GetValue(element))     : "1";
                return $"{min}..{max} (step {incr})";
            }

            // StringVariableElement: Value = Name
            var valueProp = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (valueProp != null) { object v = valueProp.GetValue(element); return v != null ? FormatValue(v) : "-"; }

            // Fallback for custom types
            return ReadMember(element, "DefaultValue")?.ToString() ?? "-";
        }

        private static string ReadStringMember(object obj, string name) =>
            ReadMember(obj, name)?.ToString();

        private static object ReadMember(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();

            Type cur = t;
            while (cur != null && cur != typeof(object))
            {
                var prop = cur.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (prop != null) { try { return prop.GetValue(obj); } catch { return null; } }

                var field = cur.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) { try { return field.GetValue(obj); } catch { return null; } }

                cur = cur.BaseType;
            }
            return null;
        }

        private static object ReadStaticMember(Type type, string name)
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)
                    ?? type.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (prop != null) { try { return prop.GetValue(null); } catch { } }

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static)
                     ?? type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (field != null) { try { return field.GetValue(null); } catch { } }

            return null;
        }

        private static object TryGetStaticSingleton(Type type)
        {
            foreach (var name in new[] { "Instance", "instance", "_instance", "Current", "Singleton" })
            {
                object val = ReadStaticMember(type, name);
                if (val != null) return val;
            }
            return null;
        }

        private static string FormatValue(object val)
        {
            if (val == null) return "null";
            if (val is string s)  return s.Length > 220 ? s.Substring(0, 217) + "..." : s;
            if (val is bool b)    return b ? "true" : "false";
            if (val is float f)   return f.ToString("G4");
            if (val is double d)  return d.ToString("G6");
            if (val is System.Collections.ICollection coll) return $"[{coll.Count} items]";

            string str = val.ToString();
            return str.Length > 220 ? str.Substring(0, 217) + "..." : str;
        }

        private static string TruncateMessage(string msg) =>
            msg.Length > 80 ? msg.Substring(0, 77) + "..." : msg;

        private static bool ContainsAny(string source, params string[] tokens)
        {
            foreach (var t in tokens)
                if (source.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ─────────────────────────────────────────── Data models ─────────────

        private sealed class RuntimeVariableEntry
        {
            public string  FieldName;
            public string  Key;
            public string  Category;
            public string  TypeName;
            public string  ShortTypeName;
            public string  CurrentValue;
            public string  DefaultValue;
            public string  Path;
            // ── setter support ────────────────────────────────
            public object  ElementRef;   // the live VariableElement instance
            public bool    HasSetter;    // LinkedValueSetter was found
            public VarKind ValueKind;    // detected primitive kind
        }

        private sealed class RuntimeDialogEntry
        {
            public string DialogKey;
            public string CurrentSection;
            public string State;
            public readonly Dictionary<string, string> Properties = new Dictionary<string, string>();
        }

        private enum FieldVarKind { Number, String, Boolean, StringArray }

        private sealed class RuntimeObjectField
        {
            public string        Name;
            /// <summary>Name used for ObjectRuntime get/set (C# template field name when known).</summary>
            public string        RuntimeName;
            public FieldVarKind  Kind;
            public string        EditBuffer;       // live value shown / edited
            public string        DefaultValue;     // Database / Library baseline
            public string        ObjectLocalValue; // value stored on the Object Link
            public bool          HasOverride;      // live/object value differs from database
        }

        private sealed class RuntimeObjectEntry
        {
            public string Key;
            /// <summary>Key passed to ObjectRuntime getters/setters (ObjectLink object key).</summary>
            public string RuntimeKey;
            public string TypeName;
            public object LibraryInstance;    // the Template object from Database.Library
            public bool   ObjectRuntimeAvailable;
            public bool   InScene;
            public string SceneObjectName;
            public GameObject SceneGameObject;
            public readonly Dictionary<string, string>      Properties    = new Dictionary<string, string>();
            public readonly List<RuntimeObjectField>        EditableFields = new List<RuntimeObjectField>();
        }

        private sealed class SceneComponentEntry
        {
            public string       TypeName;
            public string       GameObjectName;
            public MonoBehaviour Component;
            public UnityObjectId InstanceId;
            public readonly Dictionary<string, string> PublicMembers = new Dictionary<string, string>();
        }

        private sealed class LogicSectionEntry
        {
            public string Title;
            public string FunctionName;
            public int    ConditionCount;
            public int    ConsequenceCount;
            public string[] LinkedSectionIds;
        }

        private sealed class LogicRegistryEntry
        {
            public string ObjectKey;
            public readonly List<LogicSectionEntry> Sections = new List<LogicSectionEntry>();
        }

        // ── Logic scan ───────────────────────────────────────────────────

        private void ScanLogic(List<Type> grimoireTypes)
        {
            _logicEntries.Clear();

            var logicDataType = grimoireTypes.FirstOrDefault(t => t.Name == "LogicData");
            if (logicDataType == null) return;

            var registryField = logicDataType.GetField("Registry",
                BindingFlags.Public | BindingFlags.Static);
            if (registryField == null) return;

            var registry = registryField.GetValue(null);
            if (registry == null) return;

            // Registry is Dictionary<string, LogicSection[]>
            var dictType = registry.GetType();
            var enumerator = dictType.GetMethod("GetEnumerator")?.Invoke(registry, null);
            if (enumerator == null) return;

            var moveNext = enumerator.GetType().GetMethod("MoveNext");
            var current  = enumerator.GetType().GetProperty("Current");

            while (moveNext != null && (bool)moveNext.Invoke(enumerator, null))
            {
                var kvp = current?.GetValue(enumerator);
                if (kvp == null) continue;

                var keyProp = kvp.GetType().GetProperty("Key");
                var valProp = kvp.GetType().GetProperty("Value");
                string objectKey = keyProp?.GetValue(kvp)?.ToString() ?? "";
                var sectionsArray = valProp?.GetValue(kvp) as Array;

                var entry = new LogicRegistryEntry { ObjectKey = objectKey };

                if (sectionsArray != null)
                {
                    foreach (var section in sectionsArray)
                    {
                        var se = new LogicSectionEntry();
                        se.Title = section.GetType().GetField("Title")?.GetValue(section)?.ToString() ?? "";
                        se.FunctionName = section.GetType().GetField("FunctionName")?.GetValue(section)?.ToString() ?? "";

                        var conditions = section.GetType().GetField("Conditions")?.GetValue(section) as Array;
                        se.ConditionCount = conditions?.Length ?? 0;

                        var consequences = section.GetType().GetField("Consequences")?.GetValue(section) as Array;
                        se.ConsequenceCount = consequences?.Length ?? 0;

                        var linked = section.GetType().GetField("LinkedSectionIds")?.GetValue(section) as string[];
                        se.LinkedSectionIds = linked ?? Array.Empty<string>();

                        entry.Sections.Add(se);
                    }
                }

                _logicEntries.Add(entry);
            }
        }

        // ── Logic tab drawing ────────────────────────────────────────────

        private void DrawLogicTab()
        {
            if (_logicEntries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No logic data found. Export your project with logic sections to populate this tab.",
                    MessageType.Info);
                return;
            }

            bool hasFilter = !string.IsNullOrEmpty(_filter);

            foreach (var entry in _logicEntries)
            {
                if (hasFilter &&
                    entry.ObjectKey.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string foldKey = $"logic:{entry.ObjectKey}";
                bool isExpanded = _expanded.Contains(foldKey);
                bool newExpanded = EditorGUILayout.Foldout(isExpanded,
                    $"{entry.ObjectKey}  ({entry.Sections.Count} section{(entry.Sections.Count != 1 ? "s" : "")})",
                    true);

                if (newExpanded != isExpanded)
                {
                    if (newExpanded) _expanded.Add(foldKey);
                    else _expanded.Remove(foldKey);
                }

                if (!newExpanded) continue;

                EditorGUI.indentLevel++;
                for (int i = 0; i < entry.Sections.Count; i++)
                {
                    var s = entry.Sections[i];
                    string label = string.IsNullOrEmpty(s.Title) ? $"Section {i}" : s.Title;
                    if (!string.IsNullOrEmpty(s.FunctionName))
                        label += $"  [fn: {s.FunctionName}]";

                    EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField("Conditions", s.ConditionCount.ToString());
                    EditorGUILayout.LabelField("Consequences", s.ConsequenceCount.ToString());
                    if (s.LinkedSectionIds.Length > 0)
                        EditorGUILayout.LabelField("Linked", string.Join(", ", s.LinkedSectionIds));

                    // Trigger button (only for named functions in play mode)
                    if (!string.IsNullOrEmpty(s.FunctionName) && Application.isPlaying)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(EditorGUI.indentLevel * 15f);
                        if (GUILayout.Button($"Trigger {s.FunctionName}", GUILayout.Width(200)))
                        {
                            var logicRuntimeType = AppDomain.CurrentDomain.GetAssemblies()
                                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                                .FirstOrDefault(t => t.Name == "LogicRuntime");
                            if (logicRuntimeType != null)
                            {
                                var m = logicRuntimeType.GetMethod("TriggerFunction",
                                    BindingFlags.Public | BindingFlags.Static,
                                    null, new[] { typeof(string), typeof(string) }, null);
                                m?.Invoke(null, new object[] { entry.ObjectKey, s.FunctionName });
                            }
                        }

                        if (GUILayout.Button("Evaluate", GUILayout.Width(80)))
                        {
                            var logicRuntimeType = AppDomain.CurrentDomain.GetAssemblies()
                                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                                .FirstOrDefault(t => t.Name == "LogicRuntime");
                            if (logicRuntimeType != null)
                            {
                                var m = logicRuntimeType.GetMethod("EvaluateLogic",
                                    BindingFlags.Public | BindingFlags.Static,
                                    null, new[] { typeof(string), typeof(int) }, null);
                                if (m != null)
                                {
                                    bool passed = (bool)m.Invoke(null, new object[] { entry.ObjectKey, i });
                                    Debug.Log($"[Logic Inspector] {entry.ObjectKey}[{i}] → {(passed ? "PASS" : "FAIL")}");
                                }
                            }
                        }
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUI.indentLevel--;
                }
                EditorGUI.indentLevel--;
            }
        }
    }
}
