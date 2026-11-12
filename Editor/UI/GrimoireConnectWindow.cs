using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// One dockable Grimoire workspace for content and editor workflows.
    /// </summary>
    public class GrimoireConnectWindow : EditorWindow
    {
        private const int TabTasks = 0;
        private const int TabScene = 1;
        private const int TabSync = 3;
        private const int TabExport = 4;
        private const int TabRuntime = 5;
        private const int TabDialogs = 6;
        private const int TabVariables = 7;
        private const int TabStrings = 8;

        private const int ObjectTabInfo = 0;
        private const int ObjectTabEditable = 1;
        private const int ObjectTabTasks = 2;
        private const int ObjectTabVariables = 3;
        private const string WorkspaceStylePath = "Packages/com.grimoire.plugin/Editor/UI/GrimoireWorkspace.uss";

        private static readonly string[] ObjectTabLabels = { "Info", "Editable", "Tasks", "Variables" };

        private GrimoireObjectLink _link;
        private ObjectViewDocument _document;
        private string _statusMessage;
        private string _error;
        private bool _loading;
        private bool _settingsOpen;
        private Vector2 _objectScroll;
        private Vector2 _editableScroll;
        private Vector2 _objectTasksScroll;
        private Vector2 _variablesScroll;
        private int _selectedTab;
        private int _selectedObjectTab;
        [SerializeField] private bool _showLibrary = true;
        private string _pendingDialogId;

        private VisualElement _workspaceRoot;
        private VisualElement _setupRoot;
        private VisualElement _libraryHost;
        private VisualElement _sceneHost;
        private VisualElement _stringsHost;
        private VisualElement _legacyHost;
        private GrimoireContentLibraryView _libraryView;
        private GrimoireSceneWorkspaceView _sceneView;
        private GrimoireStringsView _stringsView;
        private Label _workspaceBreadcrumb;
        private Label _connectionStatus;
        private VisualElement _syncStatusBar;
        private ProgressBar _syncStatusProgress;
        private Button _libraryNav;
        private Button _settingsNav;
        private readonly Dictionary<int, Button> _tabNav = new Dictionary<int, Button>();

        private int _loadGeneration;

        private GrimoireTasksPanel _objectTasksPanel;
        private GrimoireUserTasksPanel _userTasksPanel;
        private GrimoireSetupPanel _setupQueue;
        private GrimoireSyncPanel _syncPanel;
        private GrimoireExportPanel _exportPanel;
        private GrimoireRuntimePanel _runtimePanel;
        private GrimoireDialogsPanel _dialogsPanel;
        private GrimoireVariablesPanel _variablesPanel;

        [MenuItem("Window/Grimoire")]
        public static void Open()
        {
            var window = GetWindow<GrimoireConnectWindow>("Grimoire");
            window.minSize = new Vector2(360, 420);
            window.Show();
        }

        public static void OpenLibrary(string dialogId = null)
        {
            Open();
            var window = GetWindow<GrimoireConnectWindow>();
            window._sceneView?.Deactivate();
            window._stringsView?.Deactivate();
            window._showLibrary = true;
            window._settingsOpen = false;
            window._pendingDialogId = dialogId;
            window.UpdateWorkspaceUi();
        }

        public static void OpenExportImporter()
        {
            Open();
            var window = GetWindow<GrimoireConnectWindow>();
            window.SelectTab(TabExport);
            window.Repaint();
        }

        /// <summary>Open Connect focused on the Runtime tab.</summary>
        public static void OpenRuntime()
        {
            Open();
            var window = GetWindow<GrimoireConnectWindow>();
            window.SelectTab(TabRuntime);
            window.Repaint();
        }

        /// <summary>Open Connect focused on the Dialogs tab.</summary>
        public static void OpenDialogs()
        {
            Open();
            var window = GetWindow<GrimoireConnectWindow>();
            window.SelectTab(TabDialogs);
            window.Repaint();
        }

        /// <summary>Open Connect focused on the Variables tab.</summary>
        public static void OpenVariables()
        {
            Open();
            var window = GetWindow<GrimoireConnectWindow>();
            window.SelectTab(TabVariables);
            window.Repaint();
        }

        public static void ShowAndLoad(GrimoireObjectLink link)
        {
            Open();
            var window = GetWindow<GrimoireConnectWindow>();
            if (link != null)
            {
                Selection.activeGameObject = link.gameObject;
            }
            window.SelectTab(TabScene);
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

            _sceneView = new GrimoireSceneWorkspaceView(
                link => link == _link ? _document : null,
                link => link == _link ? _error ?? (_loading ? _statusMessage : null) : null,
                DrawSceneObjectDetailTab,
                RefreshObject,
                () => OpenLibrary());
            _stringsView = new GrimoireStringsView();

            _syncPanel = new GrimoireSyncPanel();
            _syncPanel.RepaintNeeded += ScheduleRepaint;

            _exportPanel = new GrimoireExportPanel();
            _exportPanel.RepaintNeeded += ScheduleRepaint;

            _runtimePanel = GrimoireRuntimePanel.Shared;
            _runtimePanel.RepaintNeeded += ScheduleRepaint;
            _runtimePanel.OpenVersionsRequested += OnOpenVersionsRequested;
            _runtimePanel.Activate();

            _dialogsPanel = new GrimoireDialogsPanel();
            _dialogsPanel.RepaintNeeded += ScheduleRepaint;

            _variablesPanel = new GrimoireVariablesPanel();
            _variablesPanel.RepaintNeeded += ScheduleRepaint;

            Selection.selectionChanged += OnSelectionChanged;
            GrimoireAuthSession.Changed += OnAuthChanged;
            GrimoireSettings.Changed += OnSettingsChanged;
            GrimoireContentSync.Changed += OnContentSyncChanged;
            GrimoireContentSync.Completed += OnContentSyncCompleted;
            GrimoireSettings.LocaleChanged += OnGameLocaleChanged;
            GrimoireGameLanguages.Changed += ScheduleRepaint;
            GrimoireGameLanguagePreview.Changed += ScheduleRepaint;
            GrimoireObjectViewRenderer.RepaintNeeded += Repaint;
            GrimoireGameEngineSync.DocumentUpdated += OnGameEngineDocumentUpdated;
            GrimoireFieldSync.FieldsQueued += OnFieldsQueued;
            GrimoireEngineCommit.Committed += OnEngineCommitQueued;
            GrimoireGameEngineDirtyTracker.Changed += OnGameEngineDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed += OnGameEngineDirtyChanged;

            _userTasksPanel.Activate();
            if (_selectedTab == TabScene && !_showLibrary && !_settingsOpen)
            {
                _sceneView.Activate();
            }
            else if (_selectedTab == TabSync)
            {
                _syncPanel.Activate();
            }
            else if (_selectedTab == TabExport)
            {
                _exportPanel.Activate();
            }
            else if (_selectedTab == TabRuntime)
            {
                _runtimePanel.Activate();
            }
            else if (_selectedTab == TabDialogs)
            {
                _dialogsPanel.Activate();
            }
            else if (_selectedTab == TabVariables)
            {
                _variablesPanel.Activate();
            }
            else if (_selectedTab == TabStrings && !_showLibrary && !_settingsOpen)
            {
                _stringsView.Activate();
            }

            OnSelectionChanged();
        }

        private void ScheduleRepaint()
        {
            EditorApplication.delayCall -= Repaint;
            EditorApplication.delayCall += Repaint;
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.AddToClassList("grimoire-workspace");
            root.EnableInClassList("light", !EditorGUIUtility.isProSkin);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(WorkspaceStylePath);
            if (sheet == null)
            {
                var guids = AssetDatabase.FindAssets("GrimoireWorkspace t:StyleSheet");
                if (guids.Length > 0)
                    sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
            if (sheet != null) root.styleSheets.Add(sheet);

            _setupRoot = new IMGUIContainer(() =>
            {
                if (_setupQueue != null && _setupQueue.NeedsSetup)
                    _setupQueue.Draw(new Rect(0f, 0f, position.width, position.height));
            });
            _setupRoot.AddToClassList("setup-root");
            root.Add(_setupRoot);

            _workspaceRoot = new VisualElement();
            _workspaceRoot.AddToClassList("workspace-root");
            var header = new VisualElement();
            header.AddToClassList("workspace-header");
            var brand = new Label("GRIMOIRE");
            brand.AddToClassList("workspace-brand");
            header.Add(brand);
            _workspaceBreadcrumb = new Label();
            _workspaceBreadcrumb.AddToClassList("workspace-breadcrumb");
            header.Add(_workspaceBreadcrumb);
            _connectionStatus = new Label();
            _connectionStatus.AddToClassList("workspace-status");
            header.Add(_connectionStatus);
            var refresh = new Button(RefreshAll) { text = "Refresh" };
            refresh.AddToClassList("header-action");
            header.Add(refresh);
            _workspaceRoot.Add(header);

            _syncStatusBar = new VisualElement();
            _syncStatusBar.AddToClassList("workspace-sync-status");
            _syncStatusProgress = new ProgressBar();
            _syncStatusBar.Add(_syncStatusProgress);
            _workspaceRoot.Add(_syncStatusBar);

            var body = new VisualElement();
            body.AddToClassList("workspace-body");
            var sidebar = new VisualElement();
            sidebar.AddToClassList("workspace-sidebar");
            sidebar.Add(new Label("BROWSE") { name = "browse-caption" });
            _libraryNav = AddNav(sidebar, "Content Library", () => OpenLibrary());
            _tabNav.Clear();
            _tabNav[TabScene] = AddNav(sidebar, "Scene", () => SelectTab(TabScene));
            _tabNav[TabDialogs] = AddNav(sidebar, "Dialogs", () => SelectTab(TabDialogs));
            sidebar.Add(new Label("WORKFLOWS") { name = "workflow-caption" });
            _tabNav[TabTasks] = AddNav(sidebar, "Tasks", () => SelectTab(TabTasks));
            _tabNav[TabSync] = AddNav(sidebar, "Review", () => SelectTab(TabSync));
            sidebar.Add(new Label("DATABASE") { name = "database-caption" });
            _tabNav[TabExport] = AddNav(sidebar, "Versions", () => SelectTab(TabExport));
            _tabNav[TabRuntime] = AddNav(sidebar, "Runtime", () => SelectTab(TabRuntime));
            sidebar.Add(new Label("ARTIFACTS") { name = "artifacts-caption" });
            _tabNav[TabStrings] = AddNav(sidebar, "Strings", () => SelectTab(TabStrings));
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            sidebar.Add(spacer);
            _settingsNav = AddNav(sidebar, "Settings", OpenSettings);
            body.Add(sidebar);

            var content = new VisualElement();
            content.AddToClassList("workspace-content");
            _libraryHost = new VisualElement();
            _libraryHost.AddToClassList("library-host");
            content.Add(_libraryHost);
            _sceneHost = new VisualElement();
            _sceneHost.AddToClassList("scene-host");
            content.Add(_sceneHost);
            _stringsHost = new VisualElement();
            _stringsHost.AddToClassList("strings-host");
            content.Add(_stringsHost);
            _legacyHost = new IMGUIContainer(DrawWorkspaceBody);
            _legacyHost.AddToClassList("legacy-host");
            content.Add(_legacyHost);
            body.Add(content);
            _workspaceRoot.Add(body);
            root.Add(_workspaceRoot);

            _libraryView?.Deactivate();
            _libraryView = new GrimoireContentLibraryView();
            _libraryView.Activate();
            _libraryView.Build(_libraryHost, true);
            _sceneView?.Build(_sceneHost);
            _stringsView?.Build(_stringsHost);
            OnContentSyncChanged();
            _workspaceRoot.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                var compact = evt.newRect.width < 660f;
                _workspaceRoot.EnableInClassList("compact", compact);
                _libraryNav.text = compact ? "Content" : "Content Library";
            });
            UpdateWorkspaceUi();
        }

        private static Button AddNav(VisualElement sidebar, string label, System.Action action)
        {
            var button = new Button(action) { text = label };
            button.AddToClassList("workspace-nav");
            sidebar.Add(button);
            return button;
        }

        private void OpenSettings()
        {
            _sceneView?.Deactivate();
            _stringsView?.Deactivate();
            _showLibrary = false;
            _settingsOpen = true;
            UpdateWorkspaceUi();
        }

        private void UpdateWorkspaceUi()
        {
            if (_workspaceRoot == null || _setupRoot == null) return;
            var setup = _setupQueue != null && _setupQueue.NeedsSetup;
            _setupRoot.style.display = setup ? DisplayStyle.Flex : DisplayStyle.None;
            _workspaceRoot.style.display = setup ? DisplayStyle.None : DisplayStyle.Flex;
            if (setup) return;

            var libraryVisible = _showLibrary && !_settingsOpen;
            var sceneVisible = !_showLibrary && !_settingsOpen && _selectedTab == TabScene;
            var stringsVisible = !_showLibrary && !_settingsOpen && _selectedTab == TabStrings;
            _libraryHost.style.display = libraryVisible ? DisplayStyle.Flex : DisplayStyle.None;
            _sceneHost.style.display = sceneVisible ? DisplayStyle.Flex : DisplayStyle.None;
            _stringsHost.style.display = stringsVisible ? DisplayStyle.Flex : DisplayStyle.None;
            _legacyHost.style.display = libraryVisible || sceneVisible || stringsVisible ?
                DisplayStyle.None : DisplayStyle.Flex;
            _libraryNav.EnableInClassList("selected", (_showLibrary || _selectedTab == TabVariables) && !_settingsOpen);
            _settingsNav.EnableInClassList("selected", _settingsOpen);
            foreach (var pair in _tabNav)
                pair.Value.EnableInClassList("selected", !_showLibrary && !_settingsOpen && _selectedTab == pair.Key);
            var pending = GrimoireSyncPanel.TotalPendingCount();
            _tabNav[TabSync].text = pending > 0 ? $"Review ({pending})" : "Review";
            _workspaceBreadcrumb.text = GrimoireSettings.IsConfigured
                ? $"{GrimoireSettings.CompanyName}  /  {GrimoireSettings.GameName}"
                : "Choose a workspace";
            if (sceneVisible) _workspaceBreadcrumb.text += "  /  Scene";
            if (stringsVisible) _workspaceBreadcrumb.text += "  /  Strings";
            _connectionStatus.text = GrimoireAuthSession.IsSignedIn ? "● Connected" : "● Offline";
            OnContentSyncChanged();
            if (!string.IsNullOrEmpty(_pendingDialogId) && _libraryView != null)
            {
                _libraryView.SelectDialog(_pendingDialogId);
                _pendingDialogId = null;
            }
        }

        private void OnDisable()
        {
            _libraryView?.Deactivate();
            _libraryView = null;
            _sceneView?.Deactivate();
            _stringsView?.Deactivate();
            Selection.selectionChanged -= OnSelectionChanged;
            GrimoireAuthSession.Changed -= OnAuthChanged;
            GrimoireSettings.Changed -= OnSettingsChanged;
            GrimoireContentSync.Changed -= OnContentSyncChanged;
            GrimoireContentSync.Completed -= OnContentSyncCompleted;
            GrimoireSettings.LocaleChanged -= OnGameLocaleChanged;
            GrimoireGameLanguages.Changed -= ScheduleRepaint;
            GrimoireGameLanguagePreview.Changed -= ScheduleRepaint;
            GrimoireObjectViewRenderer.RepaintNeeded -= Repaint;
            GrimoireGameEngineSync.DocumentUpdated -= OnGameEngineDocumentUpdated;
            GrimoireFieldSync.FieldsQueued -= OnFieldsQueued;
            GrimoireEngineCommit.Committed -= OnEngineCommitQueued;
            GrimoireGameEngineDirtyTracker.Changed -= OnGameEngineDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed -= OnGameEngineDirtyChanged;
            _syncPanel?.Deactivate();
            if (_runtimePanel != null)
            {
                _runtimePanel.RepaintNeeded -= ScheduleRepaint;
                _runtimePanel.OpenVersionsRequested -= OnOpenVersionsRequested;
            }
            _dialogsPanel?.Deactivate();
        }

        private void OnOpenVersionsRequested()
        {
            SelectTab(TabExport);
            Repaint();
        }

        private void OnGameEngineDirtyChanged()
        {
            ScheduleRepaint();
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

            GrimoireEditableFieldsRenderer.Bind(_link, _document);
            _sceneView?.RefreshDetail();
            UpdateWorkspaceUi();
            Repaint();
        }

        private void OnFieldsQueued() => Repaint();

        private void OnEngineCommitQueued() => Repaint();

        private void OnAuthChanged()
        {
            _objectTasksPanel.ResetStatusFetchState();
            _userTasksPanel.Reset();

            if (!GrimoireAuthSession.IsSignedIn)
            {
                _setupQueue.OnSignedOut();
                _document = null;
            }

            UpdateWorkspaceUi();
            Repaint();
        }

        private void OnSettingsChanged()
        {
            _objectTasksPanel.ResetStatusFetchState();
            _userTasksPanel.Reset();
            _userTasksPanel.Activate();
            _dialogsPanel?.Reset();
            if (_selectedTab == TabScene)
            {
                _sceneView?.Refresh();
            }
            else if (_selectedTab == TabExport)
            {
                _exportPanel?.Activate();
            }
            else if (_selectedTab == TabDialogs)
            {
                _dialogsPanel?.Activate();
            }

            UpdateWorkspaceUi();
            Repaint();
        }

        private void OnSetupCompleted()
        {
            _error = null;
            _showLibrary = true;
            _settingsOpen = false;
            _userTasksPanel.Reset();
            _userTasksPanel.Activate();
            _dialogsPanel?.Reset();
            if (_selectedTab == TabScene)
            {
                _sceneView?.Refresh();
            }
            else if (_selectedTab == TabExport)
            {
                _exportPanel?.Activate();
            }
            else if (_selectedTab == TabDialogs)
            {
                _dialogsPanel?.Activate();
            }

            ReloadCurrent();
            UpdateWorkspaceUi();
        }

        private void OnContentSyncChanged()
        {
            if (_syncStatusBar == null || _syncStatusProgress == null) return;
            _syncStatusBar.style.display = GrimoireContentSync.IsRunning
                ? DisplayStyle.Flex : DisplayStyle.None;
            _syncStatusProgress.value = GrimoireContentSync.Progress * 100f;
            _syncStatusProgress.title = GrimoireContentSync.Message ?? "Pulling from Grimoire…";
        }

        private void OnContentSyncCompleted()
        {
            if (GrimoireSettings.IsConfigured)
                ReloadCurrent();
            _sceneView?.Refresh();
            UpdateWorkspaceUi();
            ScheduleRepaint();
        }

        private void OnSelectionChanged()
        {
            if (_selectedTab != TabScene)
            {
                Repaint();
                return;
            }

            var active = Selection.activeGameObject;
            var link = active != null ? active.GetComponent<GrimoireObjectLink>() : null;

            if (link == null)
            {
                ++_loadGeneration;
                _link = null;
                _document = null;
                _loading = false;
                _error = null;
                _sceneView?.RefreshSelection();
                Repaint();
                return;
            }

            _sceneView?.RefreshSelection();
            if (link == _link && (_document != null || _loading))
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

        private void OnGameLocaleChanged()
        {
            ReloadCurrent();
            _sceneView?.RefreshDetail();
            ScheduleRepaint();
        }

        /// <summary>Re-fetch everything from the server: tasks, the current object, and exports.</summary>
        private void RefreshAll()
        {
            _libraryView?.Refresh();
            _stringsView?.Refresh();
            _sceneView?.Refresh();
            _objectTasksPanel.ResetStatusFetchState();
            _userTasksPanel.Refresh();
            _exportPanel.Refresh();
            if (_selectedTab == TabDialogs)
            {
                _dialogsPanel.Refresh();
            }
            else if (_selectedTab == TabVariables)
            {
                _variablesPanel.Sync();
            }

            RefreshObject();
            UpdateWorkspaceUi();
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

            GrimoireEditableFieldsRenderer.Bind(_link, _document);
            _sceneView?.RefreshDetail();
            Repaint();
        }

        private void FinishWithError(string error)
        {
            _loading = false;
            _statusMessage = null;
            _error = error;
            _sceneView?.RefreshDetail();
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

        private void DrawWorkspaceBody()
        {
            if (_settingsOpen)
            {
                DrawSettings();
                return;
            }

            GrimoireEditorStyles.BeginContentArea();
            switch (_selectedTab)
            {
                case TabTasks:
                    DrawTasksTab();
                    break;
                case TabScene:
                    break;
                case TabSync:
                    DrawSyncTab();
                    break;
                case TabExport:
                    DrawExportTab();
                    break;
                case TabRuntime:
                    DrawRuntimeTab();
                    break;
                case TabDialogs:
                    DrawDialogsTab();
                    break;
                case TabVariables:
                    if (GUILayout.Button("← Back to Content Library", GUILayout.Width(185f)))
                    {
                        OpenLibrary();
                        break;
                    }
                    EditorGUILayout.Space(6);
                    _variablesPanel?.Draw();
                    break;
            }

            GrimoireEditorStyles.EndContentArea();
        }

        private void SelectTab(int tab)
        {
            _showLibrary = false;
            _settingsOpen = false;
            if (_selectedTab == TabSync)
            {
                _syncPanel?.Deactivate();
            }
            else if (_selectedTab == TabScene)
            {
                _sceneView?.Deactivate();
            }
            else if (_selectedTab == TabDialogs)
            {
                _dialogsPanel?.Deactivate();
            }
            else if (_selectedTab == TabStrings)
            {
                _stringsView?.Deactivate();
            }

            _selectedTab = tab;
            if (_selectedTab == TabTasks)
            {
                _userTasksPanel.Activate();
            }
            else if (_selectedTab == TabScene)
            {
                _sceneView?.Activate();
                OnSelectionChanged();
            }
            else if (_selectedTab == TabSync)
            {
                _syncPanel?.Activate();
            }
            else if (_selectedTab == TabExport)
            {
                _exportPanel?.Activate();
            }
            else if (_selectedTab == TabRuntime)
            {
                _runtimePanel?.Activate();
            }
            else if (_selectedTab == TabDialogs)
            {
                _dialogsPanel?.Activate();
            }
            else if (_selectedTab == TabVariables)
            {
                _variablesPanel?.Activate();
            }
            else if (_selectedTab == TabStrings)
            {
                _stringsView?.Activate();
            }

            UpdateWorkspaceUi();
        }

        private void DrawTasksTab()
        {
            _userTasksPanel.Draw(WorkflowWidth, WorkflowHeight);
        }

        private void DrawSyncTab()
        {
            _syncPanel?.Draw();
        }

        private void DrawExportTab()
        {
            _exportPanel?.Draw(WorkflowWidth, WorkflowHeight);
        }

        private void DrawRuntimeTab()
        {
            _runtimePanel?.Draw(WorkflowWidth, WorkflowHeight);
        }

        private void DrawDialogsTab()
        {
            _dialogsPanel?.Draw(WorkflowWidth, WorkflowHeight);
        }

        private float WorkflowWidth => Mathf.Max(250f, position.width - (position.width < 660f ? 110f : 195f) - 28f);
        private float WorkflowHeight => Mathf.Max(300f, position.height - 64f);

        private void DrawSceneObjectDetailTab(int tab)
        {
            if (_link == null || Selection.activeGameObject != _link.gameObject)
                return;

            switch (tab)
            {
                case 1:
                    GrimoireEditableFieldsRenderer.Draw(_document, _link);
                    break;
                case 2:
                    _objectTasksPanel.Draw(GrimoireSettings.GameId, _document.tasks);
                    break;
                case 3:
                    GrimoireVariablesRenderer.Draw(_document, _link);
                    break;
                case 4:
                    if (_document != null)
                    {
                        GrimoireObjectViewRenderer.Draw(_document);
                        GrimoireGameEngineDataRenderer.Draw(_document, _link);
                    }
                    break;
            }
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
                case ObjectTabTasks:
                    DrawObjectTasksTab();
                    break;
                case ObjectTabVariables:
                    DrawObjectVariablesTab();
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
            GrimoireGameEngineDataRenderer.Draw(_document, _link);

            EditorGUILayout.EndScrollView();
        }

        private void DrawObjectEditableTab()
        {
            _editableScroll = EditorGUILayout.BeginScrollView(_editableScroll);
            GrimoireEditableFieldsRenderer.Draw(_document, _link);
            EditorGUILayout.EndScrollView();
        }

        private void DrawObjectTasksTab()
        {
            _objectTasksScroll = EditorGUILayout.BeginScrollView(_objectTasksScroll);
            _objectTasksPanel.Draw(GrimoireSettings.GameId, _document.tasks);
            EditorGUILayout.EndScrollView();
        }

        private void DrawObjectVariablesTab()
        {
            _variablesScroll = EditorGUILayout.BeginScrollView(_variablesScroll);
            GrimoireVariablesRenderer.Draw(_document, _link);
            EditorGUILayout.EndScrollView();
        }

        private void DrawSettings()
        {
            GrimoireEditorStyles.EnsureStyles();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Settings", GrimoireEditorStyles.TitleStyle);
            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();
            var objectSource = (GrimoireObjectDataSource)EditorGUILayout.EnumPopup(
                new GUIContent("Object data source", "Default source for Grimoire Object Links in editor and builds."),
                GrimoireObjectSources.ProjectDefault);
            if (EditorGUI.EndChangeCheck())
                GrimoireObjectSourceSettingsStore.SetDefault(objectSource);
            EditorGUILayout.LabelField(
                "Connected Snapshot reads synced API values; Version Database reads the imported Database.Library and ObjectRuntime.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField("API base URL", GrimoireEditorStyles.FieldLabelStyle);
            var apiBaseUrl = EditorGUILayout.TextField(
                new GUIContent("", "Default: " + GrimoireSettings.DefaultApiBaseUrl),
                GrimoireSettings.ApiBaseUrl);
            if (EditorGUI.EndChangeCheck())
            {
                GrimoireSettings.ApiBaseUrl = apiBaseUrl;
                GrimoireObjectKeyResolver.InvalidateCache();
            }

            EditorGUILayout.Space(4);
            GrimoireGameLanguages.EnsureLoaded();
            GrimoireGameLanguagePreview.EnsureLoaded();
            EditorGUILayout.LabelField("Game language", GrimoireEditorStyles.FieldLabelStyle);
            EditorGUI.BeginChangeCheck();
            var locale = GrimoireLanguagePopup.Draw(
                GrimoireSettings.Locale,
                GrimoireGameLanguages.SourceLanguageLabel);
            if (EditorGUI.EndChangeCheck())
            {
                // One choice drives all Grimoire previews in this Unity project.
                GrimoireTextLinkStore.ApplySceneLanguage(locale);
            }
            EditorGUILayout.LabelField(
                "Preview translatable content in the selected game's language throughout Grimoire Connect and linked scene objects.",
                EditorStyles.wordWrappedMiniLabel);
            var languageHelp = GrimoireLanguagePopup.DescribeUnavailable();
            if (languageHelp != null)
                EditorGUILayout.LabelField(languageHelp, EditorStyles.wordWrappedMiniLabel);
            if (GrimoireGameLanguagePreview.IsLoading)
                EditorGUILayout.LabelField("Downloading translations and refreshing scene links…", EditorStyles.wordWrappedMiniLabel);
            else if (!string.IsNullOrEmpty(GrimoireGameLanguagePreview.Error))
                GrimoireEditorStyles.DrawErrorBox(GrimoireGameLanguagePreview.Error);
            else if (GrimoireGameLanguagePreview.IsLoaded)
                EditorGUILayout.LabelField($"{GrimoireGameLanguagePreview.StringCount} strings loaded from Grimoire.", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(GrimoireGameLanguagePreview.IsLoading))
            {
                if (GUILayout.Button("Refresh game languages and translations"))
                {
                    GrimoireGameLanguages.Refresh();
                    GrimoireGameLanguagePreview.Refresh();
                }
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

            EditorGUILayout.Space(8);
            if (GrimoireAuthSession.IsSignedIn)
            {
                EditorGUILayout.LabelField("Signed in as " + GrimoireAuthSession.UserName, EditorStyles.miniLabel);
                if (GUILayout.Button("Change workspace"))
                {
                    _setupQueue.BeginWorkspaceSelection();
                    UpdateWorkspaceUi();
                }
                if (GUILayout.Button("Sign out"))
                    GrimoireAuthSession.SignOut();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }
    }
}
