using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Grimoire Connect: sign in, pick company and game, then browse tasks,
    /// scene links, inspect objects, sync game engine data, or download exports.
    /// </summary>
    public class GrimoireConnectWindow : EditorWindow
    {
        private const int TabTasks = 0;
        private const int TabScene = 1;
        private const int TabObject = 2;
        private const int TabSync = 3;
        private const int TabExport = 4;

        private const int ObjectTabInfo = 0;
        private const int ObjectTabEditable = 1;
        private const int ObjectTabGameEngine = 2;

        private static readonly string[] ObjectTabLabels = { "Info", "Editable", "Game Engine Data" };

        private GrimoireObjectLink _link;
        private ObjectViewDocument _document;
        private string _statusMessage;
        private string _error;
        private bool _loading;
        private bool _settingsOpen;
        private Vector2 _objectScroll;
        private Vector2 _editableScroll;
        private Vector2 _gameEngineScroll;
        private int _selectedTab;
        private int _selectedObjectTab;

        private int _loadGeneration;

        private GrimoireTasksPanel _objectTasksPanel;
        private GrimoireUserTasksPanel _userTasksPanel;
        private GrimoireSetupPanel _setupQueue;
        private GrimoireScenePanel _scenePanel;
        private GrimoireSyncPanel _syncPanel;
        private GrimoireExportPanel _exportPanel;

        [MenuItem("Window/Grimoire/Grimoire Connect")]
        public static void Open()
        {
            var window = GetWindow<GrimoireConnectWindow>("Grimoire Connect");
            window.minSize = new Vector2(420, 520);
            window.Show();
        }

        [MenuItem("Window/Grimoire/Export Importer")]
        public static void OpenExportImporter()
        {
            Open();
            var window = GetWindow<GrimoireConnectWindow>();
            window._selectedTab = TabExport;
            window._exportPanel?.Activate();
            window.Repaint();
        }

        public static void ShowAndLoad(GrimoireObjectLink link)
        {
            Open();
            var window = GetWindow<GrimoireConnectWindow>();
            window._selectedTab = TabObject;
            window.LoadLink(link);
        }

        private void OnEnable()
        {
            _objectTasksPanel = new GrimoireTasksPanel();
            _objectTasksPanel.RepaintNeeded += Repaint;
            _objectTasksPanel.TaskStatusChanged += OnTaskStatusChanged;

            _userTasksPanel = new GrimoireUserTasksPanel();
            _userTasksPanel.RepaintNeeded += ScheduleRepaint;

            _setupQueue = new GrimoireSetupPanel();
            _setupQueue.RepaintNeeded += Repaint;
            _setupQueue.SetupCompleted += OnSetupCompleted;

            _scenePanel = new GrimoireScenePanel();
            _scenePanel.RepaintNeeded += ScheduleRepaint;
            _scenePanel.OpenObjectRequested += OnSceneOpenObjectRequested;

            _syncPanel = new GrimoireSyncPanel();
            _syncPanel.RepaintNeeded += ScheduleRepaint;

            _exportPanel = new GrimoireExportPanel();
            _exportPanel.RepaintNeeded += ScheduleRepaint;

            Selection.selectionChanged += OnSelectionChanged;
            GrimoireAuthSession.Changed += OnAuthChanged;
            GrimoireSettings.Changed += OnSettingsChanged;
            GrimoireObjectViewRenderer.RepaintNeeded += Repaint;
            GrimoireGameEngineSync.DocumentUpdated += OnGameEngineDocumentUpdated;
            GrimoireFieldSync.DocumentUpdated += OnGameEngineDocumentUpdated;
            GrimoireGameEngineDirtyTracker.Changed += OnGameEngineDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed += OnGameEngineDirtyChanged;

            _userTasksPanel.Activate();
            if (_selectedTab == TabScene)
            {
                _scenePanel.Activate();
            }
            else if (_selectedTab == TabSync)
            {
                _syncPanel.Activate();
            }
            else if (_selectedTab == TabExport)
            {
                _exportPanel.Activate();
            }

            OnSelectionChanged();
        }

        private void ScheduleRepaint()
        {
            EditorApplication.delayCall -= Repaint;
            EditorApplication.delayCall += Repaint;
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            GrimoireAuthSession.Changed -= OnAuthChanged;
            GrimoireSettings.Changed -= OnSettingsChanged;
            GrimoireObjectViewRenderer.RepaintNeeded -= Repaint;
            GrimoireGameEngineSync.DocumentUpdated -= OnGameEngineDocumentUpdated;
            GrimoireFieldSync.DocumentUpdated -= OnGameEngineDocumentUpdated;
            GrimoireGameEngineDirtyTracker.Changed -= OnGameEngineDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed -= OnGameEngineDirtyChanged;
            _scenePanel?.Deactivate();
            _syncPanel?.Deactivate();
        }

        private void OnGameEngineDirtyChanged()
        {
            ScheduleRepaint();
        }

        private void OnSceneOpenObjectRequested(GrimoireObjectLink link)
        {
            if (link == null)
            {
                return;
            }

            _selectedTab = TabObject;
            LoadLink(link);
        }

        private void OnGameEngineDocumentUpdated(ObjectViewDocument document)
        {
            if (document == null || _link == null)
            {
                return;
            }

            var linkedId = _link.CachedObjectId;
            var updatedId = document.@object?.id;
            if (string.IsNullOrEmpty(linkedId) || linkedId != updatedId)
            {
                return;
            }

            _document = document;
            _error = null;
            _loading = false;
            _statusMessage = null;
            if (_document.@object != null && GrimoireSettings.HasGameId)
            {
                GrimoireObjectKeyResolver.RememberSummary(GrimoireSettings.GameId, _document.@object);
            }

            GrimoireEditableFieldsRenderer.BindDocument(_document);
            Repaint();
        }

        private void OnAuthChanged()
        {
            _objectTasksPanel.ResetStatusFetchState();
            _userTasksPanel.Reset();

            if (!GrimoireAuthSession.IsSignedIn)
            {
                _setupQueue.OnSignedOut();
                _document = null;
            }

            Repaint();
        }

        private void OnSettingsChanged()
        {
            _objectTasksPanel.ResetStatusFetchState();
            _userTasksPanel.Reset();
            _userTasksPanel.Activate();
            if (_selectedTab == TabScene)
            {
                _scenePanel?.Activate();
            }
            else if (_selectedTab == TabExport)
            {
                _exportPanel?.Activate();
            }

            Repaint();
        }

        private void OnSetupCompleted()
        {
            _error = null;
            _userTasksPanel.Reset();
            _userTasksPanel.Activate();
            if (_selectedTab == TabScene)
            {
                _scenePanel?.Activate();
            }
            else if (_selectedTab == TabExport)
            {
                _exportPanel?.Activate();
            }

            ReloadCurrent();
        }

        private void OnSelectionChanged()
        {
            if (_selectedTab != TabObject)
            {
                Repaint();
                return;
            }

            var active = Selection.activeGameObject;
            var link = active != null ? active.GetComponent<GrimoireObjectLink>() : null;

            if (link == null)
            {
                Repaint();
                return;
            }

            if (link == _link && _document != null)
            {
                return;
            }

            LoadLink(link);
        }

        private void OnTaskStatusChanged(string taskId, string statusKey)
        {
            if (_document?.tasks != null)
            {
                foreach (var task in _document.tasks)
                {
                    if (task.id == taskId)
                    {
                        task.status = statusKey;
                        break;
                    }
                }
            }

            Repaint();
            ReloadCurrent();
        }

        private void ReloadCurrent()
        {
            if (_link != null)
            {
                LoadLink(_link, force: true);
            }
        }

        /// <summary>Re-fetch everything from the server: tasks, the current object, and exports.</summary>
        private void RefreshAll()
        {
            _objectTasksPanel.ResetStatusFetchState();
            _userTasksPanel.Refresh();
            _exportPanel.Refresh();
            RefreshObject();
            Repaint();
        }

        /// <summary>Re-fetch only the currently linked object from the server.</summary>
        private void RefreshObject()
        {
            GrimoireObjectKeyResolver.InvalidateCache(GrimoireSettings.GameId);
            ReloadCurrent();
        }

        private async void LoadLink(GrimoireObjectLink link, bool force = false)
        {
            _link = link;
            _error = null;
            _document = force ? _document : null;

            if (!GrimoireSettings.IsConfigured)
            {
                Repaint();
                return;
            }

            if (!link.HasKey)
            {
                _error = "This GrimoireObjectLink has no object key set.";
                Repaint();
                return;
            }

            var generation = ++_loadGeneration;
            var gameId = GrimoireSettings.GameId;

            _loading = true;
            _statusMessage = $"Resolving '{link.ObjectKey}'...";
            Repaint();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (generation != _loadGeneration)
            {
                return;
            }

            if (!hasSession)
            {
                FinishWithError("Your Grimoire session has expired. Sign in again.");
                return;
            }

            var objectId = link.CachedObjectId;
            var resolvedFromCache = !string.IsNullOrEmpty(objectId);

            if (!resolvedFromCache)
            {
                var resolved = await GrimoireObjectKeyResolver.ResolveAsync(gameId, link.ObjectKey);
                if (generation != _loadGeneration)
                {
                    return;
                }

                if (!resolved.Success)
                {
                    FinishWithError(resolved.Error);
                    return;
                }

                objectId = resolved.Data;
                RememberResolvedId(link, objectId);
            }

            _statusMessage = "Loading object...";
            Repaint();

            var view = await GrimoireApiClient.GetObjectViewAsync(gameId, objectId, GrimoireSettings.Locale);
            if (generation != _loadGeneration)
            {
                return;
            }

            if (!view.Success && resolvedFromCache && view.HttpStatus == 404)
            {
                link.CachedObjectId = "";
                GrimoireObjectKeyResolver.InvalidateCache(gameId);

                var resolved = await GrimoireObjectKeyResolver.ResolveAsync(gameId, link.ObjectKey);
                if (generation != _loadGeneration)
                {
                    return;
                }

                if (!resolved.Success)
                {
                    FinishWithError(resolved.Error);
                    return;
                }

                RememberResolvedId(link, resolved.Data);
                view = await GrimoireApiClient.GetObjectViewAsync(gameId, resolved.Data, GrimoireSettings.Locale);
                if (generation != _loadGeneration)
                {
                    return;
                }
            }

            if (!view.Success)
            {
                var detail = view.HttpStatus > 0 ? $"{view.Error} (HTTP {view.HttpStatus})" : view.Error;
                FinishWithError(detail);
                return;
            }

            _document = view.Data;
            _loading = false;
            _statusMessage = null;
            if (_document?.@object != null)
            {
                GrimoireObjectKeyResolver.RememberSummary(gameId, _document.@object);
            }

            GrimoireEditableFieldsRenderer.BindDocument(_document);
            Repaint();
        }

        private void FinishWithError(string error)
        {
            _loading = false;
            _statusMessage = null;
            _error = error;
            Repaint();
        }

        private static void RememberResolvedId(GrimoireObjectLink link, string objectId)
        {
            if (link.CachedObjectId == objectId)
            {
                return;
            }

            link.CachedObjectId = objectId;
            EditorUtility.SetDirty(link);
        }

        private void OnGUI()
        {
            if (_setupQueue.NeedsSetup)
            {
                _setupQueue.Draw(new Rect(0f, 0f, position.width, position.height));
                return;
            }

            DrawToolbar();

            if (_settingsOpen)
            {
                DrawSettings();
            }

            DrawTabBar();

            GrimoireEditorStyles.BeginContentArea();
            switch (_selectedTab)
            {
                case TabTasks:
                    DrawTasksTab();
                    break;
                case TabScene:
                    DrawSceneTab();
                    break;
                case TabObject:
                    DrawObjectTab();
                    break;
                case TabSync:
                    DrawSyncTab();
                    break;
                case TabExport:
                    DrawExportTab();
                    break;
            }

            GrimoireEditorStyles.EndContentArea();
        }

        private void DrawTabBar()
        {
            var dirty = GrimoireSyncPanel.TotalPendingCount();
            var syncLabel = dirty > 0 ? $"Sync ({dirty})" : "Sync";
            var labels = new[] { "Tasks", "Scene", "Object", syncLabel, "Versions" };

            var picked = GrimoireEditorStyles.DrawTabBar(_selectedTab, labels);
            if (picked != _selectedTab)
            {
                if (_selectedTab == TabSync)
                {
                    _syncPanel?.Deactivate();
                }
                else if (_selectedTab == TabScene)
                {
                    _scenePanel?.Deactivate();
                }

                _selectedTab = picked;
                if (_selectedTab == TabObject)
                {
                    OnSelectionChanged();
                }
                else if (_selectedTab == TabTasks)
                {
                    _userTasksPanel.Activate();
                }
                else if (_selectedTab == TabScene)
                {
                    _scenePanel?.Activate();
                }
                else if (_selectedTab == TabSync)
                {
                    _syncPanel?.Activate();
                }
                else if (_selectedTab == TabExport)
                {
                    _exportPanel?.Activate();
                }
            }
        }

        private void DrawTasksTab()
        {
            _userTasksPanel.Draw();
        }

        private void DrawSceneTab()
        {
            _scenePanel?.Draw();
        }

        private void DrawSyncTab()
        {
            _syncPanel?.Draw();
        }

        private void DrawExportTab()
        {
            _exportPanel?.Draw();
        }

        private void DrawObjectTab()
        {
            if (!string.IsNullOrEmpty(_error))
            {
                GrimoireEditorStyles.DrawErrorBox(_error);
            }

            if (_loading)
            {
                EditorGUILayout.LabelField(_statusMessage ?? "Loading...", GrimoireEditorStyles.MiniSecondaryStyle);
            }

            if (_document == null)
            {
                if (!_loading && string.IsNullOrEmpty(_error))
                {
                    GrimoireEditorStyles.DrawInfoBox(
                        "Select a GameObject with a Grimoire Object Link component to view its Grimoire data.");
                }

                if (_link != null && !_loading)
                {
                    DrawObjectHeader();
                }

                return;
            }

            DrawObjectHeader();

            _selectedObjectTab = GrimoireEditorStyles.DrawTabBar(_selectedObjectTab, ObjectTabLabels);

            switch (_selectedObjectTab)
            {
                case ObjectTabInfo:
                    DrawObjectInfoTab();
                    break;
                case ObjectTabEditable:
                    DrawObjectEditableTab();
                    break;
                case ObjectTabGameEngine:
                    DrawObjectGameEngineTab();
                    break;
            }
        }

        private void DrawObjectHeader()
        {
            var objectName = _document?.@object?.name;
            if (string.IsNullOrEmpty(objectName) && _link != null && _link.HasKey)
            {
                objectName = _link.ObjectKey;
            }

            EditorGUILayout.BeginHorizontal();

            if (!string.IsNullOrEmpty(objectName))
            {
                EditorGUILayout.LabelField(objectName, GrimoireEditorStyles.TitleStyle);
            }
            else
            {
                GUILayout.FlexibleSpace();
            }

            using (new EditorGUI.DisabledScope(_link == null || _loading))
            {
                if (GUILayout.Button(
                        new GUIContent("Refresh object", "Re-fetch this object from the Grimoire server."),
                        GUILayout.Width(110)))
                {
                    RefreshObject();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        private void DrawObjectInfoTab()
        {
            _objectScroll = EditorGUILayout.BeginScrollView(_objectScroll);

            GrimoireObjectViewRenderer.Draw(_document);
            _objectTasksPanel.Draw(GrimoireSettings.GameId, _document.tasks);

            EditorGUILayout.EndScrollView();
        }

        private void DrawObjectEditableTab()
        {
            _editableScroll = EditorGUILayout.BeginScrollView(_editableScroll);
            GrimoireEditableFieldsRenderer.Draw(_document);
            EditorGUILayout.EndScrollView();
        }

        private void DrawObjectGameEngineTab()
        {
            _gameEngineScroll = EditorGUILayout.BeginScrollView(_gameEngineScroll);
            GrimoireGameEngineDataRenderer.Draw(_document, _link);
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            GrimoireEditorStyles.BeginToolbar();

            if (GrimoireSettings.IsConfigured)
            {
                var companyLabel = string.IsNullOrEmpty(GrimoireSettings.CompanyName)
                    ? "Company"
                    : GrimoireSettings.CompanyName;
                var gameLabel = string.IsNullOrEmpty(GrimoireSettings.GameName)
                    ? "Game"
                    : GrimoireSettings.GameName;

                string objectTitle = null;
                if (_selectedTab == TabObject)
                {
                    objectTitle = _document?.@object?.name
                                  ?? (_link != null && _link.HasKey ? _link.ObjectKey : "No object");
                }

                GrimoireEditorStyles.DrawToolbarBreadcrumb(companyLabel, gameLabel, objectTitle);
            }
            else
            {
                GUILayout.Label("Grimoire Connect", GrimoireEditorStyles.TitleStyle);
            }

            GUILayout.FlexibleSpace();

            if (GrimoireSettings.IsConfigured)
            {
                using (new EditorGUI.DisabledScope(_loading))
                {
                    if (GrimoireEditorStyles.ToolbarButton("Refresh"))
                    {
                        RefreshAll();
                    }
                }

                if (GrimoireAuthSession.IsSignedIn)
                {
                    GUILayout.Label(GrimoireAuthSession.UserName, GrimoireEditorStyles.MiniSecondaryStyle);
                    if (GrimoireEditorStyles.ToolbarButton("Change workspace"))
                    {
                        _setupQueue.BeginWorkspaceSelection();
                    }

                    if (GrimoireEditorStyles.ToolbarButton("Sign out"))
                    {
                        GrimoireAuthSession.SignOut();
                    }
                }
            }

            if (GrimoireEditorStyles.ToolbarButton("Settings", _settingsOpen))
            {
                _settingsOpen = !_settingsOpen;
            }

            GrimoireEditorStyles.EndToolbar();
        }

        private void DrawSettings()
        {
            GrimoireEditorStyles.EnsureStyles();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Settings", GrimoireEditorStyles.TitleStyle);
            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("API base URL", GrimoireEditorStyles.FieldLabelStyle);
            var apiBaseUrl = EditorGUILayout.TextField(
                new GUIContent("", "Default: " + GrimoireSettings.DefaultApiBaseUrl),
                GrimoireSettings.ApiBaseUrl);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Locale", GrimoireEditorStyles.FieldLabelStyle);
            var locale = EditorGUILayout.TextField(
                new GUIContent("", "Language code for translatable fields; empty shows source text"),
                GrimoireSettings.Locale);

            if (EditorGUI.EndChangeCheck())
            {
                GrimoireSettings.ApiBaseUrl = apiBaseUrl;
                GrimoireSettings.Locale = locale;
                GrimoireObjectKeyResolver.InvalidateCache();
            }

            if (GrimoireSettings.HasCompanyId)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Company", GrimoireSettings.CompanyName, GrimoireEditorStyles.MiniSecondaryStyle);
            }

            if (GrimoireSettings.HasGameId)
            {
                EditorGUILayout.LabelField("Game", GrimoireSettings.GameName, GrimoireEditorStyles.MiniSecondaryStyle);
                EditorGUILayout.LabelField("Game ID", GrimoireSettings.GameId, GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }
    }
}
