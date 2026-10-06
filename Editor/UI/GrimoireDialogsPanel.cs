using System;
using System.Collections.Generic;
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

        public event Action RepaintNeeded;

        public void Activate()
        {
            EditorApplication.projectChanged -= RefreshImported;
            EditorApplication.projectChanged += RefreshImported;
            RefreshImported();

            if (GrimoireSettings.IsConfigured && _dialogs == null && !_loading)
            {
                RefreshAsync();
            }
        }

        public void Deactivate()
        {
            EditorApplication.projectChanged -= RefreshImported;
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
            _dialogs = null;
            _offset = 0;
            _error = null;
            _status = null;
        }

        public void Draw()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireEditorStyles.DrawInfoBox("Sign in and choose a workspace first to browse dialogs.");
                return;
            }

            GrimoireEditorStyles.DrawInfoBox(
                "1. Add the dialog player to your scene once.\n" +
                "2. Import the dialogs you need. They are saved to " + GrimoireDialogImporter.AssetsFolder +
                " and play in builds without signing in.\n" +
                "3. Play one from code: GrimoireDialogPlayer.Main.StartDialog(dialogAsset).");

            DrawSceneSetup();
            DrawToolbar();
            DrawSearch();

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

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_loading && _dialogs == null)
            {
                EditorGUILayout.LabelField("Loading dialogs...", GrimoireEditorStyles.MiniSecondaryStyle);
            }
            else if (_dialogs == null || _dialogs.Length == 0)
            {
                if (!_loading)
                {
                    GrimoireEditorStyles.DrawInfoBox(string.IsNullOrEmpty(_appliedSearch)
                        ? "This game has no dialogs yet. Create one in the Grimoire dialog editor."
                        : $"No dialogs match '{_appliedSearch}'.");
                }
            }
            else
            {
                foreach (var dialog in _dialogs)
                {
                    DrawDialogCard(dialog);
                    EditorGUILayout.Space(4);
                }

                DrawPaging();
            }

            EditorGUILayout.EndScrollView();
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
            _loading = true;
            _error = null;
            _status = "Loading dialogs...";
            RequestRepaint();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (generation != _loadGeneration)
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

            var result = await GrimoireApiClient.ListDialogsAsync(GrimoireSettings.GameId, _appliedSearch, PageSize, _offset);
            if (generation != _loadGeneration)
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
