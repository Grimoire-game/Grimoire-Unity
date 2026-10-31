using System;
using System.Collections.Generic;
using Grimoire.PluginV2.Internal;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Dialogs tab: adds the scene's single dialog player and imports the
    /// game's dialogs from the Public API as assets to pass to it.
    /// </summary>
    public class GrimoireDialogsPanel
    {
        private const int PageSize = 50;
        private const string SearchControlName = "GrimoireDialogSearch";

        private Vector2 _scroll;
        private Vector2 _detailsScroll;
        private string _selectedDialogId;
        private int _stateFilter;
        private static readonly string[] StateFilters = { "All dialogs", "Imported", "Update available", "Not imported" };
        private DialogSummary[] _dialogs;
        private Dictionary<string, GrimoireDialogAsset> _imported = new Dictionary<string, GrimoireDialogAsset>();
        private bool _loading;
        private string _error;
        private string _status;
        private string _search = "";
        private string _appliedSearch = "";
        private int _offset;
        private int _loadGeneration;
        private string _busyDialogId;
        private string _busyMessage;
        // Object requirements per imported asset, keyed by asset instance id + import stamp.
        private readonly Dictionary<string, List<GrimoireDialogObjectRequirement>> _requirementCache =
            new Dictionary<string, List<GrimoireDialogObjectRequirement>>(StringComparer.Ordinal);

        public event Action RepaintNeeded;

        public void Activate()
        {
            EditorApplication.projectChanged -= RefreshImported;
            EditorApplication.projectChanged += RefreshImported;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            RefreshImported();

            if (GrimoireSettings.IsConfigured && _dialogs == null && !_loading)
            {
                RefreshAsync();
            }
        }

        public void Deactivate()
        {
            EditorApplication.projectChanged -= RefreshImported;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        }

        private void OnHierarchyChanged()
        {
            GrimoireDialogObjectResolver.InvalidateSceneScan();
            RequestRepaint();
        }

        /// <summary>Re-fetch the dialog list from the server.</summary>
        public void Refresh()
        {
            if (GrimoireSettings.IsConfigured && !_loading)
            {
                RefreshAsync();
            }
        }

        /// <summary>Forget the list, e.g. after switching games.</summary>
        public void Reset()
        {
            _loadGeneration++;
            _loading = false;
            _dialogs = null;
            _offset = 0;
            _error = null;
            _status = null;
        }

        public void Draw(float width, float height)
        {
            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireEditorStyles.DrawInfoBox("Sign in and choose a workspace first to browse dialogs.");
                return;
            }

            GrimoireWorkflowGUI.Header("Dialogs", "Find a dialog, check its scene player and import state, then inspect or update the asset.");
            DrawSceneSetup();
            DrawWorkspaceFilters();

            if (!string.IsNullOrEmpty(_error))
            {
                GrimoireEditorStyles.DrawErrorBox(_error);
            }
            else if (!string.IsNullOrEmpty(_busyMessage))
            {
                EditorGUILayout.LabelField(_busyMessage, GrimoireEditorStyles.MiniSecondaryStyle);
            }
            else if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.LabelField(_status, GrimoireEditorStyles.MiniSecondaryStyle);
            }

            var paneHeight = Mathf.Max(220f, height - 234f);
            var narrow = GrimoireWorkflowGUI.IsNarrow(width);
            if (!narrow) EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(narrow ? GUILayout.ExpandWidth(true) : GUILayout.Width(Mathf.Max(230f, width * 0.57f)));
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(narrow ? 215f : paneHeight));
            DrawWorkspaceList();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandWidth(true));
            _detailsScroll = EditorGUILayout.BeginScrollView(_detailsScroll, GUILayout.Height(narrow ? Mathf.Max(220f, paneHeight - 215f) : paneHeight));
            DrawWorkspaceDetail();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            if (!narrow) EditorGUILayout.EndHorizontal();
        }

        private void DrawWorkspaceFilters()
        {
            var submit = Event.current.type == EventType.KeyDown &&
                         (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
                         GUI.GetNameOfFocusedControl() == SearchControlName;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Search", GUILayout.Width(44f));
            GUI.SetNextControlName(SearchControlName);
            _search = EditorGUILayout.TextField(_search ?? "", GUILayout.MinWidth(90f));
            using (new EditorGUI.DisabledScope(_loading))
            {
                if (GUILayout.Button("Search", GUILayout.Width(62f)) || submit) ApplySearch(_search);
            }
            _stateFilter = EditorGUILayout.Popup(_stateFilter, StateFilters, GUILayout.Width(125f));
            if (GUILayout.Button("Refresh", GUILayout.Width(65f))) Refresh();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(5f);
        }

        private void DrawWorkspaceList()
        {
            GrimoireWorkflowGUI.Section("Dialogs" + (_dialogs == null ? "" : $" · {_dialogs.Length}"));
            if (_loading && _dialogs == null)
                EditorGUILayout.LabelField("Loading dialogs…", GrimoireEditorStyles.MiniSecondaryStyle);
            else if (_dialogs == null || _dialogs.Length == 0)
                GrimoireEditorStyles.DrawInfoBox("No dialogs found. Try another search or refresh.");
            else
            {
                if (Array.Find(_dialogs, d => d.id == _selectedDialogId &&
                    (_stateFilter == 0 || DialogState(d, ImportedAsset(d)) == StateFilters[_stateFilter])) == null)
                    _selectedDialogId = Array.Find(_dialogs, d => _stateFilter == 0 ||
                        DialogState(d, ImportedAsset(d)) == StateFilters[_stateFilter])?.id;
                var shown = 0;
                foreach (var dialog in _dialogs)
                {
                    _imported.TryGetValue(dialog.id ?? "", out var asset);
                    var state = DialogState(dialog, asset);
                    if (_stateFilter > 0 && state != StateFilters[_stateFilter]) continue;
                    shown++;
                    if (GrimoireWorkflowGUI.Row(
                            string.IsNullOrEmpty(dialog.name) ? "(unnamed dialog)" : dialog.name,
                            (string.IsNullOrEmpty(dialog.folder) ? "Unfiled" : dialog.folder) + " · " + (dialog.dialog_key ?? ""),
                            asset == null ? "Not imported" : $"{asset.Nodes.Count} nodes",
                            state,
                            dialog.id == _selectedDialogId))
                        _selectedDialogId = dialog.id;
                }
                if (shown == 0) GrimoireEditorStyles.DrawInfoBox("No dialogs match this import filter.");
                DrawPaging();
            }
        }

        private void DrawWorkspaceDetail()
        {
            var visible = _dialogs == null ? Array.Empty<DialogSummary>() : Array.FindAll(_dialogs, d =>
            {
                _imported.TryGetValue(d.id ?? "", out var imported);
                return _stateFilter == 0 || DialogState(d, imported) == StateFilters[_stateFilter];
            });
            var dialog = Array.Find(visible, d => d.id == _selectedDialogId) ??
                         (visible.Length > 0 ? visible[0] : null);
            if (dialog == null)
            {
                GrimoireWorkflowGUI.DetailHeading("Dialog details", "Select a dialog", null);
                return;
            }
            _imported.TryGetValue(dialog.id ?? "", out var asset);
            GrimoireWorkflowGUI.DetailHeading("Dialog details", dialog.name, dialog.dialog_key);
            using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(_busyDialogId)))
            {
                if (GUILayout.Button(asset == null ? "Import dialog" : "Update dialog asset", GrimoireEditorStyles.PrimaryButtonStyle, GUILayout.Height(28f)))
                    ImportAsync(dialog);
            }
            if (asset != null && GUILayout.Button("Select imported asset"))
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }
            GrimoireWorkflowGUI.DetailLine("Folder", string.IsNullOrEmpty(dialog.folder) ? "Unfiled" : dialog.folder);
            GrimoireWorkflowGUI.DetailLine("Import", DialogState(dialog, asset));
            GrimoireWorkflowGUI.DetailLine("Status", dialog.status);
            GrimoireWorkflowGUI.DetailLine("Updated", GrimoireExportImporterService.FormatDate(dialog.updated_at));
            if (asset != null)
            {
                GrimoireWorkflowGUI.Section("Imported structure");
                GrimoireWorkflowGUI.DetailLine("Nodes", asset.Nodes.Count.ToString());
                GrimoireWorkflowGUI.DetailLine("Variables", asset.Variables.Count.ToString());
                GrimoireWorkflowGUI.DetailLine("Starting node", asset.StartingNode);
                var first = asset.FindNode(asset.StartingNode);
                if (first != null && !string.IsNullOrEmpty(first.text))
                    GrimoireWorkflowGUI.Note(first.text);
            }
            else GrimoireWorkflowGUI.Note("Import this dialog to inspect its nodes and variables in Unity.");
        }

        private static string DialogState(DialogSummary dialog, GrimoireDialogAsset asset)
        {
            if (asset == null) return "Not imported";
            return string.Equals(asset.UpdatedAt, dialog.updated_at ?? "", StringComparison.Ordinal)
                ? "Imported" : "Update available";
        }

        private GrimoireDialogAsset ImportedAsset(DialogSummary dialog)
        {
            _imported.TryGetValue(dialog.id ?? "", out var asset);
            return asset;
        }

        private void DrawSceneSetup()
        {
            var existing = GrimoireDialogSceneSpawner.FindExisting();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode && existing == null))
            {
                var content = existing == null
                    ? new GUIContent("Add Dialog to scene",
                        "Add one dialog player with a ready-made dialog box to the open scene. It plays every dialog you pass to it.")
                    : new GUIContent("Select Dialog in scene", "The scene already has a dialog player. Select it.");
                if (GUILayout.Button(content, GrimoireEditorStyles.PrimaryButtonStyle, GUILayout.Width(180)))
                {
                    var player = GrimoireDialogSceneSpawner.AddOrSelect();
                    _status = existing == null
                        ? $"Added '{player.name}' to the scene."
                        : $"Selected '{player.name}'.";
                }
            }

            EditorGUILayout.LabelField(
                existing == null ? "No dialog player in the scene yet." : $"In scene: {existing.name}",
                GrimoireEditorStyles.MiniSecondaryStyle);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(GrimoireSettings.GameName) ? "Dialogs" : $"Dialogs in {GrimoireSettings.GameName}",
                GrimoireEditorStyles.TitleStyle);
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_loading))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    RefreshAsync();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void DrawSearch()
        {
            var submit = Event.current.type == EventType.KeyDown &&
                         (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
                         GUI.GetNameOfFocusedControl() == SearchControlName;

            EditorGUILayout.BeginHorizontal();
            GUI.SetNextControlName(SearchControlName);
            _search = EditorGUILayout.TextField(_search ?? "");

            using (new EditorGUI.DisabledScope(_loading))
            {
                if (GUILayout.Button("Search", GUILayout.Width(70)) || submit)
                {
                    ApplySearch(_search);
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_appliedSearch) && string.IsNullOrEmpty(_search)))
                {
                    if (GUILayout.Button("Clear", GUILayout.Width(55)))
                    {
                        _search = "";
                        GUI.FocusControl(null);
                        ApplySearch("");
                    }
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);
        }

        private void ApplySearch(string search)
        {
            _appliedSearch = search?.Trim() ?? "";
            _offset = 0;
            RefreshAsync();
        }

        private void DrawDialogCard(DialogSummary dialog)
        {
            _imported.TryGetValue(dialog.id ?? "", out var asset);
            var isBusy = _busyDialogId == dialog.id;
            var anyBusy = !string.IsNullOrEmpty(_busyDialogId);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(string.IsNullOrEmpty(dialog.name) ? "(unnamed dialog)" : dialog.name, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (asset != null)
            {
                DrawObjectsBadge(asset);
                var outdated = !string.Equals(asset.UpdatedAt, dialog.updated_at ?? "", StringComparison.Ordinal);
                GUILayout.Label(outdated ? "Update available" : "Imported", GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.EndHorizontal();

            var meta = new List<string>();
            if (!string.IsNullOrEmpty(dialog.dialog_key))
            {
                meta.Add(dialog.dialog_key);
            }

            if (!string.IsNullOrEmpty(dialog.folder))
            {
                meta.Add(dialog.folder);
            }

            if (!string.IsNullOrEmpty(dialog.status))
            {
                meta.Add(dialog.status);
            }

            if (!string.IsNullOrEmpty(dialog.updated_at))
            {
                meta.Add("updated " + GrimoireExportImporterService.FormatDate(dialog.updated_at));
            }

            if (meta.Count > 0)
            {
                EditorGUILayout.LabelField(string.Join(" · ", meta), GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(anyBusy))
            {
                var label = isBusy ? "Working..." : asset == null ? "Import" : "Update";
                var tooltip = asset == null
                    ? "Save this dialog as an asset in " + GrimoireDialogImporter.AssetsFolder + "."
                    : "Download the latest version of this dialog from Grimoire.";
                if (GUILayout.Button(new GUIContent(label, tooltip),
                        asset == null ? GrimoireEditorStyles.PrimaryButtonStyle : GUI.skin.button,
                        GUILayout.Width(100)))
                {
                    ImportAsync(dialog);
                }
            }

            if (asset != null && GUILayout.Button("Select asset", GUILayout.Width(100)))
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // "2 objects, 1 not in scene" next to the import state; click to open the asset.
        private void DrawObjectsBadge(GrimoireDialogAsset asset)
        {
            var requirements = RequirementsFor(asset);
            GrimoireDialogRequirementsDrawer.Summarize(requirements, out var objects, out var missing);
            if (objects == 0)
            {
                return;
            }

            var text = missing == 0
                ? $"{objects} object{(objects == 1 ? "" : "s")} in scene"
                : $"{objects} object{(objects == 1 ? "" : "s")}, {missing} not in scene";
            var tooltip = missing == 0
                ? "Every Grimoire object this dialog reads has an Object Link in the open scene."
                : "Some Grimoire objects this dialog reads have no Object Link in the open scene. Click to see them.";
            var style = missing == 0 ? GrimoireEditorStyles.MiniSecondaryStyle : EditorStyles.miniBoldLabel;
            if (GUILayout.Button(new GUIContent(text, tooltip), style))
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }

            GUILayout.Space(8);
        }

        private List<GrimoireDialogObjectRequirement> RequirementsFor(GrimoireDialogAsset asset)
        {
            var key = asset.GetInstanceID() + "|" + asset.ImportedAt;
            if (!_requirementCache.TryGetValue(key, out var requirements))
            {
                requirements = GrimoireDialogObjectRequirements.Collect(asset);
                _requirementCache[key] = requirements;
            }

            return requirements;
        }

        private void DrawPaging()
        {
            var hasPrevious = _offset > 0;
            var hasNext = _dialogs != null && _dialogs.Length >= PageSize;
            if (!hasPrevious && !hasNext)
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!hasPrevious || _loading))
            {
                if (GUILayout.Button("Previous", GUILayout.Width(80)))
                {
                    _offset = Math.Max(0, _offset - PageSize);
                    RefreshAsync();
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"Page {_offset / PageSize + 1}", GrimoireEditorStyles.MiniSecondaryStyle, GUILayout.Width(60));
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!hasNext || _loading))
            {
                if (GUILayout.Button("Next", GUILayout.Width(80)))
                {
                    _offset += PageSize;
                    RefreshAsync();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private async void RefreshAsync()
        {
            var generation = ++_loadGeneration;
            var gameId = GrimoireSettings.GameId;
            _loading = true;
            _error = null;
            _status = "Loading dialogs...";
            RequestRepaint();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (generation != _loadGeneration || gameId != GrimoireSettings.GameId)
            {
                return;
            }

            if (!hasSession)
            {
                _loading = false;
                _status = null;
                _error = "Your Grimoire session has expired. Sign in again.";
                RequestRepaint();
                return;
            }

            var result = await GrimoireApiClient.ListDialogsAsync(gameId, _appliedSearch, PageSize, _offset);
            if (generation != _loadGeneration || gameId != GrimoireSettings.GameId)
            {
                return;
            }

            _loading = false;
            if (!result.Success)
            {
                _dialogs = null;
                _status = null;
                _error = result.HttpStatus > 0 ? $"{result.Error} (HTTP {result.HttpStatus})" : result.Error;
                RequestRepaint();
                return;
            }

            _dialogs = result.Data;
            _status = _dialogs.Length == 1 ? "1 dialog" : $"{_dialogs.Length} dialogs";
            RefreshImported();
            RequestRepaint();
        }

        private async void ImportAsync(DialogSummary dialog)
        {
            var name = string.IsNullOrEmpty(dialog.name) ? dialog.id : dialog.name;
            _busyDialogId = dialog.id;
            _busyMessage = $"{name}: starting...";
            _error = null;
            RequestRepaint();

            var result = await GrimoireDialogImporter.ImportAsync(
                GrimoireSettings.GameId,
                dialog.id,
                message =>
                {
                    _busyMessage = $"{name}: {message}";
                    RequestRepaint();
                });

            _busyDialogId = null;
            _busyMessage = null;

            if (!result.Success)
            {
                _error = $"Could not import '{name}': {result.Error}";
                RequestRepaint();
                return;
            }

            RefreshImported();
            _status = $"Imported '{name}' to {AssetDatabase.GetAssetPath(result.Data)}.";
            if (GrimoireDialogImporter.LastImportNotes.Count > 0)
            {
                _status += "\n" + string.Join("\n", GrimoireDialogImporter.LastImportNotes);
            }

            _requirementCache.Clear();
            EditorGUIUtility.PingObject(result.Data);
            RequestRepaint();
        }

        private void RefreshImported()
        {
            _imported = GrimoireDialogImporter.FindAllImported();
            RequestRepaint();
        }

        private void RequestRepaint()
        {
            RepaintNeeded?.Invoke();
        }
    }
}
