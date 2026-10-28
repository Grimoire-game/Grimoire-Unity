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
        private const int TabObject = 2;
        private const int TabSync = 3;
        private const int TabExport = 4;
        private const int TabRuntime = 5;
        private const int TabDialogs = 6;
        private const int TabVariables = 7;

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
        private VisualElement _legacyHost;
        private GrimoireContentLibraryView _libraryView;
        private Label _workspaceBreadcrumb;
        private Label _connectionStatus;
        private Button _libraryNav;
        private Button _settingsNav;
        private readonly Dictionary<int, Button> _tabNav = new Dictionary<int, Button>();

        private int _loadGeneration;

        private GrimoireTasksPanel _objectTasksPanel;
        private GrimoireUserTasksPanel _userTasksPanel;
        private GrimoireSetupPanel _setupQueue;
        private GrimoireScenePanel _scenePanel;
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
            window.SelectTab(TabObject);
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

            _runtimePanel = new GrimoireRuntimePanel();
            _runtimePanel.RepaintNeeded += ScheduleRepaint;
            _runtimePanel.OpenVersionsRequested += OnOpenVersionsRequested;

            _dialogsPanel = new GrimoireDialogsPanel();
            _dialogsPanel.RepaintNeeded += ScheduleRepaint;

            _variablesPanel = new GrimoireVariablesPanel();
            _variablesPanel.RepaintNeeded += ScheduleRepaint;

            Selection.selectionChanged += OnSelectionChanged;
            GrimoireAuthSession.Changed += OnAuthChanged;
            GrimoireSettings.Changed += OnSettingsChanged;
            GrimoireObjectViewRenderer.RepaintNeeded += Repaint;
            GrimoireGameEngineSync.DocumentUpdated += OnGameEngineDocumentUpdated;
            GrimoireFieldSync.FieldsQueued += OnFieldsQueued;
            GrimoireEngineCommit.Committed += OnEngineCommitQueued;
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

            var body = new VisualElement();
            body.AddToClassList("workspace-body");
            var sidebar = new VisualElement();
            sidebar.AddToClassList("workspace-sidebar");
            sidebar.Add(new Label("BROWSE") { name = "browse-caption" });
            _libraryNav = AddNav(sidebar, "Content Library", () => OpenLibrary());
            _tabNav.Clear();
            _tabNav[TabScene] = AddNav(sidebar, "Scene", () => SelectTab(TabScene));
            _tabNav[TabObject] = AddNav(sidebar, "Object", () => SelectTab(TabObject));
            _tabNav[TabSync] = AddNav(sidebar, "Review", () => SelectTab(TabSync));
            sidebar.Add(new Label("WORKFLOWS") { name = "workflow-caption" });
            _tabNav[TabDialogs] = AddNav(sidebar, "Dialogs", () => SelectTab(TabDialogs));
            _tabNav[TabTasks] = AddNav(sidebar, "Tasks", () => SelectTab(TabTasks));
            _tabNav[TabExport] = AddNav(sidebar, "Versions", () => SelectTab(TabExport));
            _tabNav[TabRuntime] = AddNav(sidebar, "Runtime", () => SelectTab(TabRuntime));
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

            _libraryHost.style.display = _showLibrary && !_settingsOpen ? DisplayStyle.Flex : DisplayStyle.None;
            _legacyHost.style.display = _showLibrary && !_settingsOpen ? DisplayStyle.None : DisplayStyle.Flex;
            _libraryNav.EnableInClassList("selected", _showLibrary && !_settingsOpen);
            _settingsNav.EnableInClassList("selected", _settingsOpen);
            foreach (var pair in _tabNav)
                pair.Value.EnableInClassList("selected", !_showLibrary && !_settingsOpen && _selectedTab == pair.Key);
            var pending = GrimoireSyncPanel.TotalPendingCount();
            _tabNav[TabSync].text = pending > 0 ? $"Review ({pending})" : "Review";
            _workspaceBreadcrumb.text = GrimoireSettings.IsConfigured
                ? $"{GrimoireSettings.CompanyName}  /  {GrimoireSettings.GameName}"
                : "Choose a workspace";
            _connectionStatus.text = GrimoireAuthSession.IsSignedIn ? "● Connected" : "● Offline";
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
            Selection.selectionChanged -= OnSelectionChanged;
            GrimoireAuthSession.Changed -= OnAuthChanged;
            GrimoireSettings.Changed -= OnSettingsChanged;
            GrimoireObjectViewRenderer.RepaintNeeded -= Repaint;
            GrimoireGameEngineSync.DocumentUpdated -= OnGameEngineDocumentUpdated;
            GrimoireFieldSync.FieldsQueued -= OnFieldsQueued;
            GrimoireEngineCommit.Committed -= OnEngineCommitQueued;
            GrimoireGameEngineDirtyTracker.Changed -= OnGameEngineDirtyChanged;
            GrimoireEditableFieldsRenderer.Changed -= OnGameEngineDirtyChanged;
            _scenePanel?.Deactivate();
            _syncPanel?.Deactivate();
            _runtimePanel?.Deactivate();
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

        private void OnSceneOpenObjectRequested(GrimoireObjectLink link)
        {
            if (link == null)
            {
                return;
            }

            SelectTab(TabObject);
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

            GrimoireEditableFieldsRenderer.Bind(_link, _document);
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
                _scenePanel?.Activate();
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
            _userTasksPanel.Reset();
            _userTasksPanel.Activate();
            _dialogsPanel?.Reset();
            if (_selectedTab == TabScene)
            {
                _scenePanel?.Activate();
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
            _libraryView?.Refresh();
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
                case TabRuntime:
                    DrawRuntimeTab();
                    break;
                case TabDialogs:
                    DrawDialogsTab();
                    break;
                case TabVariables:
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
                _scenePanel?.Deactivate();
            }
            else if (_selectedTab == TabRuntime)
            {
                _runtimePanel?.Deactivate();
            }
            else if (_selectedTab == TabDialogs)
            {
                _dialogsPanel?.Deactivate();
            }

            _selectedTab = tab;
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

            UpdateWorkspaceUi();
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
            if (GUILayout.Button("Browse imported database", GUILayout.Width(190f)))
                GrimoireDatabaseBrowser.ShowWindow();
            EditorGUILayout.Space(4);
            _exportPanel?.Draw();
        }

        private void DrawRuntimeTab()
        {
            _runtimePanel?.Draw();
        }

        private void DrawDialogsTab()
        {
            _dialogsPanel?.Draw();
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
            EditorGUILayout.LabelField("Language", GrimoireEditorStyles.FieldLabelStyle);
            EditorGUI.BeginChangeCheck();
            var locale = GrimoireLanguagePopup.Draw(
                GrimoireSettings.Locale,
                GrimoireGameLanguages.SourceLanguageLabel);
            if (EditorGUI.EndChangeCheck())
            {
                // Same switch as the Scene tab, so one language drives everything.
                GrimoireTextLinkStore.ApplySceneLanguage(locale);
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
