using System;
using System.Collections.Generic;
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
        private enum Tab { Variables, Dialogs, Objects, Logic, Scene }

        /// <summary>Detected primitive kind of a variable — used to pick the right input widget.</summary>
        private enum VarKind { String, Int, Float, Bool, Other }

        // ── UI state ──────────────────────────────────────────────────────────
        private Tab _tab = Tab.Variables;
        private Vector2 _scroll;
        private string _filter = "";
        private bool _autoRefresh = true;
        private float _refreshInterval = 0.5f;
        private double _lastRefreshTime;
        private bool _showDefaults = true;
        private bool _highlightChanged = true;
        private bool _active;
        private bool _setupExpanded = true;
        private bool _wasSetupReady;
        private string _sessionIdDraft = "";

        private string _status = "";
        private MessageType _statusType = MessageType.Info;
        private bool _grimoireTypesFound;

        // ── Edit state (keyed by variable Path, survives auto-refresh) ────────
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
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            SyncSessionDraftFromScene();
            if (EditorApplication.isPlaying)
            {
                RefreshData();
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
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private void RequestRepaint() => RepaintNeeded?.Invoke();

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                ClearData();
                _lastRefreshTime = 0;
                SyncSessionDraftFromScene();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
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

        private void OnEditorUpdate()
        {
            if (!_active || !_autoRefresh || !EditorApplication.isPlaying) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRefreshTime >= _refreshInterval)
            {
                _lastRefreshTime = now;
                RefreshData();
                RequestRepaint();
            }
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
                $"Variables ({_variables.Count})",
                $"Dialogs ({_dialogs.Count})",
                $"Objects ({_objects.Count})",
                $"Logic ({_logicEntries.Count})",
                $"Scene ({_sceneComponents.Count})"
            });

            _filter = EditorGUILayout.TextField("Filter", _filter);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            switch (_tab)
            {
                case Tab.Variables: DrawVariablesTab(); break;
                case Tab.Dialogs:   DrawDialogsTab();   break;
                case Tab.Objects:   DrawObjectsTab();   break;
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
            hasExport = HasGrimoireExportTypes();
            bootstrap = FindSceneComponent<GrimoireBootstrap>();
            tracker = FindSceneComponent<GrimoireSessionTracker>();
            // Export + Bootstrap are required; Session Tracker is optional.
            return hasExport && bootstrap != null;
        }

        private static bool HasGrimoireExportTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Namespace != "Grimoire")
                        {
                            continue;
                        }

                        if (t.Name == "Database" || t.Name == "VariableRuntime" || t.Name == "ObjectRuntime")
                        {
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
                    ? "Grimoire types found in this project."
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
            EditorGUILayout.LabelField("Live values", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto", EditorStyles.miniButton);

            GUI.enabled = !_autoRefresh;
            if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.Width(55)))
            {
                RefreshData();
                RequestRepaint();
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Refresh rate (s):", EditorStyles.miniLabel, GUILayout.Width(100));
            _refreshInterval = EditorGUILayout.Slider(_refreshInterval, 0.1f, 5f);
            EditorGUILayout.EndHorizontal();

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
                    "No Library object entries found.\n\n" +
                    "This tab reflects static Database.Library data at runtime. " +
                    "Ensure your Grimoire export is imported and contains a Database class with a Library.",
                    MessageType.Info);
                return;
            }

            string filt = NormaliseFilter();

            foreach (var obj in _objects)
            {
                if (!MatchesFilter(filt, obj.Key, obj.TypeName)) continue;

                DrawObjectEntry(obj);
                GUILayout.Space(3);
            }
        }

        private void DrawObjectEntry(RuntimeObjectEntry obj)
        {
            string id  = "obj:" + obj.Key;
            bool   exp = _expanded.Contains(id);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical();

            bool newExp = EditorGUILayout.Foldout(exp, $"{obj.Key}  [{obj.TypeName}]", true);
            ToggleExpanded(id, exp, newExp);

            if (newExp)
            {
                EditorGUI.indentLevel++;

                if (obj.ObjectRuntimeAvailable && obj.EditableFields.Count > 0)
                {
                    // ── Header row ──────────────────────────────────────────
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Save", GUILayout.Width(44), GUILayout.Height(16)))
                    {
                        try { _objRtSavePrefs?.Invoke(null, null); } catch { }
                    }
                    if (GUILayout.Button("Reset", GUILayout.Width(44), GUILayout.Height(16)))
                    {
                        try
                        {
                            _objRtResetItem?.Invoke(null, new object[] { obj.Key });
                            PopulateEditableFields(obj);
                            GUI.FocusControl(null);
                            RequestRepaint();
                        }
                        catch { }
                    }
                    EditorGUILayout.EndHorizontal();

                    // ── Editable fields ─────────────────────────────────────
                    foreach (var field in obj.EditableFields)
                        DrawObjectFieldEntry(obj, field);
                }
                else
                {
                    // Fallback: read-only property list
                    foreach (var kv in obj.Properties)
                        DrawKeyValue(kv.Key, kv.Value);
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();

            if (GUILayout.Button("Copy", GUILayout.Width(44), GUILayout.Height(18)))
                EditorGUIUtility.systemCopyBuffer = obj.Key;

            EditorGUILayout.EndHorizontal();
        }

        private void DrawObjectFieldEntry(RuntimeObjectEntry obj, RuntimeObjectField field)
        {
            Color origBg = GUI.backgroundColor;
            if (field.HasOverride)
                GUI.backgroundColor = new Color(0.75f, 0.9f, 1f);   // tinted blue = overridden

            EditorGUILayout.BeginHorizontal();

            if (field.Kind == FieldVarKind.Boolean)
            {
                bool current = field.EditBuffer == "True";
                bool toggled = EditorGUILayout.Toggle(new GUIContent(field.Name), current);
                if (toggled != current)
                {
                    field.EditBuffer = toggled.ToString();
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
        }

        private void ApplyObjectFieldValue(RuntimeObjectEntry obj, RuntimeObjectField field)
        {
            if (!obj.ObjectRuntimeAvailable) return;
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
                            _objRtSetNumber?.Invoke(null, new object[] { obj.Key, field.Name, num });
                            field.HasOverride = true;
                        }
                        break;

                    case FieldVarKind.String:
                        _objRtSetString?.Invoke(null, new object[] { obj.Key, field.Name, field.EditBuffer });
                        field.HasOverride = true;
                        break;

                    case FieldVarKind.Boolean:
                        bool bv = field.EditBuffer == "True";
                        _objRtSetBool?.Invoke(null, new object[] { obj.Key, field.Name, bv });
                        field.HasOverride = true;
                        break;

                    case FieldVarKind.StringArray:
                        string[] arr = ParseStringArray(field.EditBuffer);
                        _objRtSetStringArray?.Invoke(null, new object[] { obj.Key, field.Name, arr });
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
                ScanObjects(grimoireTypes);
                ScanLogic(grimoireTypes);

                _status = $"Refreshed  ·  {_variables.Count} vars  ·  {_dialogs.Count} dialogs  ·  " +
                          $"{_objects.Count} objects  ·  {_logicEntries.Count} logic  ·  {_sceneComponents.Count} scene";
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

                        var entry = new SceneComponentEntry
                        {
                            TypeName       = t.Name,
                            GameObjectName = mb.gameObject.name,
                            Component      = mb,
                            InstanceId     = mb.GetInstanceID()
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
                }
                catch { }
            }
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

        private void ScanObjects(List<Type> grimoireTypes)
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
                ScanLibraryType(libraryNested, dbType.Name + ".Library");
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
                        TypeName = kvp.Value?.GetType().Name ?? "unknown"
                    };

                    if (kvp.Value != null)
                        CollectPublicMembers(kvp.Value, entry.Properties);

                    entry.LibraryInstance        = kvp.Value;
                    entry.ObjectRuntimeAvailable = _objectRuntimeType != null;
                    PopulateEditableFields(entry);
                    _objects.Add(entry);
                }

                _objects.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void PopulateEditableFields(RuntimeObjectEntry entry)
        {
            entry.EditableFields.Clear();
            if (entry.LibraryInstance == null) return;

            var instance = entry.LibraryInstance;
            var type     = instance.GetType();

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!TryGetFieldVarKind(field.FieldType, out var kind)) continue;

                string rawValue = "";
                bool   hasOverride = false;

                if (entry.ObjectRuntimeAvailable && _objRtGetNumber != null)
                {
                    try
                    {
                        rawValue = kind switch
                        {
                            FieldVarKind.Number      => ((double)_objRtGetNumber.Invoke(null, new object[] { entry.Key, field.Name, 0.0 })).ToString("G"),
                            FieldVarKind.Boolean     => ((bool)_objRtGetBool.Invoke(null, new object[] { entry.Key, field.Name, false })).ToString(),
                            FieldVarKind.StringArray => _objRtGetStringArray != null
                                ? string.Join(", ", (string[])_objRtGetStringArray.Invoke(null, new object[] { entry.Key, field.Name }))
                                : FormatStringArray((string[])field.GetValue(instance)),
                            _                        => (string)_objRtGetString.Invoke(null, new object[] { entry.Key, field.Name, "" })
                        };
                        if (_objRtHasOverride != null)
                            hasOverride = (bool)_objRtHasOverride.Invoke(null, new object[] { entry.Key, field.Name });
                    }
                    catch { rawValue = field.GetValue(instance)?.ToString() ?? ""; }
                }
                else
                {
                    if (kind == FieldVarKind.StringArray)
                        rawValue = FormatStringArray((string[])field.GetValue(instance));
                    else
                        rawValue = field.GetValue(instance)?.ToString() ?? "";
                }

                entry.EditableFields.Add(new RuntimeObjectField
                {
                    Name        = field.Name,
                    Kind        = kind,
                    EditBuffer  = rawValue,
                    HasOverride = hasOverride
                });
            }
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

        private void ScanLibraryType(Type type, string pathPrefix)
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
                        Key      = pathPrefix + "." + field.Name,
                        TypeName = field.FieldType.Name
                    };

                    CollectPublicMembers(val, entry.Properties);
                    entry.LibraryInstance        = val;
                    entry.ObjectRuntimeAvailable = _objectRuntimeType != null;
                    PopulateEditableFields(entry);
                    _objects.Add(entry);
                }
                catch { }
            }

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
            {
                if (nested.IsAbstract && nested.IsSealed)
                    ScanLibraryType(nested, pathPrefix + "." + nested.Name);
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
            public FieldVarKind  Kind;
            public string        EditBuffer;   // current editor text / "True"/"False"
            public bool          HasOverride;  // true when ObjectRuntime has a live override
        }

        private sealed class RuntimeObjectEntry
        {
            public string Key;
            public string TypeName;
            public object LibraryInstance;    // the Template object from Database.Library
            public bool   ObjectRuntimeAvailable;
            public readonly Dictionary<string, string>      Properties    = new Dictionary<string, string>();
            public readonly List<RuntimeObjectField>        EditableFields = new List<RuntimeObjectField>();
        }

        private sealed class SceneComponentEntry
        {
            public string       TypeName;
            public string       GameObjectName;
            public MonoBehaviour Component;
            public int          InstanceId;
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
