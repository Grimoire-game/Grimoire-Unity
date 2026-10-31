using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;

namespace Grimoire.PluginV2.Editor
{
    public partial class GrimoireRuntimePanel
    {
        private static GrimoireRuntimePanel _shared;

        static GrimoireRuntimePanel()
        {
            // Capture from Play Mode entry even when the Grimoire window is closed.
            EditorApplication.delayCall += () => Shared.Activate();
        }

        internal static GrimoireRuntimePanel Shared =>
            _shared ?? (_shared = new GrimoireRuntimePanel());

        private const int MaxLiveChanges = 500;
        private const double LivePollIntervalSeconds = 0.75;
        private static readonly string[] LiveCategories =
            { "All linked data", "Objects", "Variables", "Dialogs", "Logic", "Text links", "Scene links" };

        private sealed class LiveValue
        {
            public string Id;
            public string Category;
            public string Name;
            public string Path;
            public string Value;
            public string InitialValue;
            public string Source;
            public DateTime LastChanged;
            public GameObject SceneObject;
        }

        private sealed class LiveChange
        {
            public string Id;
            public string Category;
            public string Name;
            public string Path;
            public string Before;
            public string After;
            public string Source;
            public DateTime Time;
            public GameObject SceneObject;
        }

        private readonly Dictionary<string, LiveValue> _liveValues =
            new Dictionary<string, LiveValue>(StringComparer.Ordinal);
        private readonly List<LiveChange> _liveChanges = new List<LiveChange>();
        private readonly Dictionary<GrimoireDialogPlayer, UnityAction<DialogLine>> _dialogListeners =
            new Dictionary<GrimoireDialogPlayer, UnityAction<DialogLine>>();
        private LiveChange _selectedLiveChange;
        private string _selectedLiveId;
        private Vector2 _liveListScroll;
        private Vector2 _liveDetailScroll;
        private string _liveSearch = "";
        private int _liveCategoryIndex;
        private bool _showLinkedNow;
        private bool _capturePaused;
        private bool _hasLiveBaseline;
        private bool _showAdvancedInspector;
        private double _nextLivePoll;
        private List<Type> _cachedGrimoireTypes;

        private void ClearLiveState()
        {
            _liveValues.Clear();
            _liveChanges.Clear();
            _selectedLiveChange = null;
            _selectedLiveId = null;
            _capturePaused = false;
            _hasLiveBaseline = false;
            _nextLivePoll = 0;
        }

        private void BindRuntimeEvents()
        {
            GrimoireObjectLink.RuntimeFieldChanged -= OnObjectFieldChanged;
            GrimoireObjectLink.RuntimeFieldChanged += OnObjectFieldChanged;
            GrimoireObjectLink.FunctionTriggered -= OnFunctionTriggered;
            GrimoireObjectLink.FunctionTriggered += OnFunctionTriggered;
            GrimoireVariables.ValueChanged -= OnVariableValueChanged;
            GrimoireVariables.ValueChanged += OnVariableValueChanged;
            GrimoireTextLink.TextChanged -= OnTextChanged;
            GrimoireTextLink.TextChanged += OnTextChanged;
            SyncDialogListeners();
        }

        private void UnbindRuntimeEvents()
        {
            GrimoireObjectLink.RuntimeFieldChanged -= OnObjectFieldChanged;
            GrimoireObjectLink.FunctionTriggered -= OnFunctionTriggered;
            GrimoireVariables.ValueChanged -= OnVariableValueChanged;
            GrimoireTextLink.TextChanged -= OnTextChanged;
            foreach (var pair in _dialogListeners)
                if (pair.Key != null) pair.Key.OnLine.RemoveListener(pair.Value);
            _dialogListeners.Clear();
        }

        private void SyncDialogListeners()
        {
            if (!EditorApplication.isPlaying) return;
            foreach (var player in UnityEngine.Object.FindObjectsOfType<GrimoireDialogPlayer>(true))
            {
                if (player == null || _dialogListeners.ContainsKey(player)) continue;
                var capturedPlayer = player;
                UnityAction<DialogLine> callback = line => OnDialogLine(capturedPlayer, line);
                player.OnLine.AddListener(callback);
                _dialogListeners.Add(player, callback);
            }
            foreach (var player in _dialogListeners.Keys.Where(player => player == null).ToArray())
                _dialogListeners.Remove(player);
        }

        private void PollLiveValues()
        {
            if (!_active || !EditorApplication.isPlaying || _snapshotPending ||
                EditorApplication.timeSinceStartup < _nextLivePoll) return;
            _nextLivePoll = EditorApplication.timeSinceStartup + LivePollIntervalSeconds;
            var bootstrap = FindSceneComponent<GrimoireBootstrap>();
            if (bootstrap != null && !bootstrap.IsInitialized) return;
            SyncDialogListeners();
            RefreshData();
            CaptureLiveSnapshot();
            RequestRepaint();
        }

        private void OnObjectFieldChanged(GrimoireObjectLink link, GrimoireRuntimeFieldChange change)
        {
            if (!EditorApplication.isPlaying || link == null || change == null) return;
            var name = string.IsNullOrEmpty(link.Snapshot?.Name) ? link.gameObject.name : link.Snapshot.Name;
            RecordExternal(ObjectFieldId(link.gameObject, change.Label), "Objects", name + " · " + change.Label,
                link.ObjectKey + "." + change.Label, change.PreviousValue, change.Value,
                string.IsNullOrEmpty(change.Source) ? "Object Link" : change.Source, link.gameObject);
        }

        private void OnVariableValueChanged(GrimoireVariableElement element, object value)
        {
            if (!EditorApplication.isPlaying || element == null) return;
            var id = "variable|" + element.Id;
            var before = _liveValues.TryGetValue(id, out var old)
                ? old.Value : FormatLiveValue(element.AuthoredValue);
            var name = (element.Variable?.Name ?? "Variable") + " · " + element.DisplayName;
            RecordExternal(id, "Variables", name, name, before, FormatLiveValue(value), "GrimoireVariables", null);
        }

        private void OnTextChanged(GrimoireTextLink link)
        {
            if (!EditorApplication.isPlaying || link == null || !link.HasCode) return;
            var id = "text|" + link.GetInstanceID();
            var before = _liveValues.TryGetValue(id, out var old) ? old.Value : link.SourceText;
            RecordExternal(id, "Text links", link.gameObject.name + " · Text", link.TextCode,
                before, link.GetResolvedText(), "Text Link", link.gameObject);
        }

        private void OnFunctionTriggered(GrimoireObjectLink link, string function)
        {
            if (!EditorApplication.isPlaying || link == null) return;
            var id = "logic|" + link.GetInstanceID() + "|" + function;
            RecordExternal(id, "Logic", link.gameObject.name + " · " + function,
                link.ObjectKey + "." + function, "Idle", "Triggered", "Object Link", link.gameObject, true);
        }

        private void OnDialogLine(GrimoireDialogPlayer player, DialogLine line)
        {
            if (!EditorApplication.isPlaying || player == null || line == null) return;
            var id = "dialog|" + player.GetInstanceID() + "|node";
            var before = _liveValues.TryGetValue(id, out var old) ? old.Value : "(start)";
            RecordExternal(id, "Dialogs", (line.Dialog?.DisplayName ?? player.name) + " · Current node",
                (line.Dialog?.DialogKey ?? player.name) + ".current_node", before, line.NodeId,
                "Dialog Player", player.gameObject);
        }

        private static string ObjectFieldId(GameObject gameObject, string fieldName) =>
            "object|" + gameObject.GetInstanceID() + "|" + (fieldName ?? "").Trim().ToLowerInvariant();

        private static string FormatLiveValue(object value) =>
            value == null ? "(none)" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        private void RecordExternal(string id, string category, string name, string path,
            string before, string after, string source, GameObject sceneObject, bool force = false)
        {
            if (string.IsNullOrEmpty(id)) return;
            before = before ?? "";
            after = after ?? "";
            var now = DateTime.Now;
            if (!_liveValues.TryGetValue(id, out var live))
                _liveValues[id] = live = new LiveValue { Id = id, InitialValue = before };
            live.Category = category;
            live.Name = name;
            live.Path = path;
            live.Value = after;
            live.Source = source;
            live.SceneObject = sceneObject;
            if (!force && string.Equals(before, after, StringComparison.Ordinal)) return;
            live.LastChanged = now;
            if (!_capturePaused) RecordLiveChange(live, before, after, source, now);
            RequestRepaint();
        }

        private void RecordLiveChange(LiveValue live, string before, string after, string source, DateTime time)
        {
            var change = new LiveChange
            {
                Id = live.Id, Category = live.Category, Name = live.Name, Path = live.Path,
                Before = before, After = after, Source = source, Time = time, SceneObject = live.SceneObject
            };
            _liveChanges.Insert(0, change);
            if (_liveChanges.Count > MaxLiveChanges) _liveChanges.RemoveAt(_liveChanges.Count - 1);
            if (_selectedLiveChange == null) _selectedLiveChange = change;
        }
    }
}
