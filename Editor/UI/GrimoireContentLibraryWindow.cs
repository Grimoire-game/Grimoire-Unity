using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// A single, dockable browser for the selected game's objects, dialogs and
    /// variables. The same window is used as a constrained picker by inspectors.
    /// </summary>
    public sealed class GrimoireContentLibraryWindow : EditorWindow
    {
        private enum Kind { Object, Dialog, Variable, DialogVariable }

        private sealed class Item
        {
            public string Id;
            public string Name;
            public string Key;
            public string Folder;
            public string Status;
            public string Source;
            public string Summary;
            public Kind Type;
            public ObjectSummary Object;
            public DialogSummary Dialog;
            public VariableResource Variable;
            public GrimoireVariable CachedVariable;
            public GrimoireDialogAsset Asset;
            public GrimoireDialogVariable DialogVariable;
        }

        private const int ObjectPageSize = 50;
        private const int DialogPageSize = 50;
        private const int VariablePageSize = 200;
        private const int MaxPages = 200;
        private const string AllFolder = "All content";
        private const string StylePath = "Packages/com.grimoire.plugin/Editor/UI/GrimoireContentLibrary.uss";

        private readonly List<ObjectSummary> _objects = new List<ObjectSummary>();
        private readonly List<DialogSummary> _dialogs = new List<DialogSummary>();
        private readonly List<VariableResource> _variables = new List<VariableResource>();
        private readonly List<Item> _items = new List<Item>();
        private readonly List<Item> _visible = new List<Item>();
        private readonly List<string> _folders = new List<string>();
        private readonly Dictionary<string, ObjectViewDocument> _objectDetails =
            new Dictionary<string, ObjectViewDocument>(StringComparer.Ordinal);
        private readonly Dictionary<string, DialogResource> _dialogDetails =
            new Dictionary<string, DialogResource>(StringComparer.Ordinal);
        private Dictionary<string, GrimoireDialogAsset> _imported =
            new Dictionary<string, GrimoireDialogAsset>(StringComparer.Ordinal);

        private GrimoireVariableLibrary _cachedVariables;
        private bool _remoteVariablesLoaded;
        private bool _objectsLoading;
        private bool _dialogsLoading;
        private bool _variablesLoading;
        private bool _detailLoading;
        private bool _busy;
        private string _objectError;
        private string _dialogError;
        private string _variableError;
        private string _detailError;
        private string _detailFailedId;
        private string _message;
        private string _search = "";
        private string _folder = AllFolder;
        private string _selectedId;
        private Kind? _typeFilter;
        private Kind? _pickerKind;
        private Action<ObjectSummary> _onObjectPicked;
        private Action<GrimoireDialogAsset> _onDialogPicked;
        private Action<string> _onVariablePicked;
        private int _refreshGeneration;
        private int _detailGeneration;
        private bool _suppressSelection;

        private VisualElement _root;
        private VisualElement _folderList;
        private VisualElement _detailContent;
        private VisualElement _filterButtons;
        private VisualElement _pickerBar;
        private Button _folderPopup;
        private Label _breadcrumb;
        private Label _title;
        private Label _subtitle;
        private Label _resultsHeading;
        private Label _footer;
        private Label _notice;
        private TextField _searchField;
        private ListView _resultList;

        private bool IsPicker => _pickerKind.HasValue;

        [MenuItem("Window/Grimoire/Content Library")]
        public static void Open()
        {
            var window = GetWindow<GrimoireContentLibraryWindow>("Grimoire Library");
            window.minSize = new Vector2(350, 420);
            window.Show();
        }

        public static void OpenObjectPicker(Action<ObjectSummary> onPicked)
        {
            var window = CreateInstance<GrimoireContentLibraryWindow>();
            window.titleContent = new GUIContent("Pick Grimoire Object");
            window._pickerKind = Kind.Object;
            window._onObjectPicked = onPicked;
            window._folder = AllFolder;
            window._selectedId = null;
            window.minSize = new Vector2(430, 440);
            window.ShowUtility();
        }

        public static void OpenDialogPicker(Action<GrimoireDialogAsset> onPicked)
        {
            var window = CreateInstance<GrimoireContentLibraryWindow>();
            window.titleContent = new GUIContent("Pick Grimoire Dialog");
            window._pickerKind = Kind.Dialog;
            window._onDialogPicked = onPicked;
            window._folder = AllFolder;
            window._selectedId = null;
            window.minSize = new Vector2(430, 440);
            window.ShowUtility();
        }

        /// <summary>
        /// Picks a game variable id or a dialog-scoped "dialogKey/variableName".
        /// Callers can use this to bind a compatible reference field.
        /// </summary>
        public static void OpenVariablePicker(Action<string> onPicked)
        {
            var window = CreateInstance<GrimoireContentLibraryWindow>();
            window.titleContent = new GUIContent("Pick Grimoire Variable");
            window._pickerKind = Kind.Variable;
            window._onVariablePicked = onPicked;
            window._folder = AllFolder;
            window._selectedId = null;
            window.minSize = new Vector2(430, 440);
            window.ShowUtility();
        }

        public static void OpenDialog(string dialogId)
        {
            Open();
            var window = GetWindow<GrimoireContentLibraryWindow>();
            window._selectedId = "dialog:" + dialogId;
            window._folder = AllFolder;
            window.RenderItems();
        }

        private void OnEnable()
        {
            GrimoireSettings.Changed += OnWorkspaceChanged;
            GrimoireSettings.LocaleChanged += OnLocaleChanged;
            GrimoireAuthSession.Changed += OnWorkspaceChanged;
            GrimoireDialogImporter.Imported += OnDialogImported;
            GrimoireVariableSync.Synced += OnVariablesSynced;
            EditorApplication.projectChanged += OnProjectChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            _folder = EditorPrefs.GetString(PrefKey("Folder"), AllFolder);
            _selectedId = EditorPrefs.GetString(PrefKey("Selected"), "");
        }

        private void OnDisable()
        {
            _refreshGeneration++;
            _detailGeneration++;
            GrimoireSettings.Changed -= OnWorkspaceChanged;
            GrimoireSettings.LocaleChanged -= OnLocaleChanged;
            GrimoireAuthSession.Changed -= OnWorkspaceChanged;
            GrimoireDialogImporter.Imported -= OnDialogImported;
            GrimoireVariableSync.Synced -= OnVariablesSynced;
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private static string PrefKey(string suffix) =>
            $"GrimoireContentLibrary_{PlayerSettings.productGUID}_{GrimoireSettings.GameId}_{suffix}";

        private void OnWorkspaceChanged()
        {
            _folder = AllFolder;
            _selectedId = null;
            RefreshAll();
        }

        private void OnLocaleChanged()
        {
            _objectDetails.Clear();
            _detailGeneration++;
            _detailFailedId = null;
            _detailError = null;
            _detailLoading = false;
            RenderDetail();
            LoadSelectedDetail();
        }

        private void OnDialogImported(GrimoireDialogAsset _) => OnProjectChanged();
        private void OnVariablesSynced(GrimoireVariableLibrary _) => OnProjectChanged();

        private void OnProjectChanged()
        {
            ReadLocalAssets();
            BuildItems();
        }

        private void OnPlayModeChanged(PlayModeStateChange _) => RenderDetail();

        public void CreateGUI()
        {
            _root = rootVisualElement;
            _root.Clear();
            _root.AddToClassList("grimoire-library");
            _root.EnableInClassList("light", !EditorGUIUtility.isProSkin);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (sheet == null)
            {
                var guids = AssetDatabase.FindAssets("GrimoireContentLibrary t:StyleSheet");
                if (guids.Length > 0)
                {
                    sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
                }
            }
            if (sheet != null)
            {
                _root.styleSheets.Add(sheet);
            }

            BuildChrome();
            _root.RegisterCallback<GeometryChangedEvent>(evt =>
                _root.EnableInClassList("compact", evt.newRect.width < 720f));
            RefreshAll();
        }

        private void BuildChrome()
        {
            var top = Row("topbar");
            _breadcrumb = new Label { name = "breadcrumb" };
            _breadcrumb.AddToClassList("breadcrumb");
            top.Add(_breadcrumb);
            top.Add(new Button(RefreshAll) { text = "Refresh" });
            top.Add(new Button(GrimoireConnectWindow.Open) { text = "Connect" });
            _root.Add(top);

            _pickerBar = Row("pickerbar");
            _pickerBar.Add(new Label(IsPicker ? "Picking for " + PickerLabel() : ""));
            _root.Add(_pickerBar);
            _pickerBar.style.display = IsPicker ? DisplayStyle.Flex : DisplayStyle.None;

            var titlebar = Row("titlebar");
            var titleStack = Column("title-stack");
            _title = new Label();
            _title.AddToClassList("title");
            _subtitle = new Label();
            _subtitle.AddToClassList("subtitle");
            titleStack.Add(_title);
            titleStack.Add(_subtitle);
            titlebar.Add(titleStack);
            _root.Add(titlebar);

            _notice = new Label();
            _notice.AddToClassList("error-box");
            _root.Add(_notice);

            var searchbar = Row("searchbar");
            _folderPopup = new Button(ShowFolderMenu);
            _folderPopup.AddToClassList("folder-popup");
            searchbar.Add(_folderPopup);
            _searchField = new TextField("Search") { value = _search };
            _searchField.AddToClassList("search-field");
            _searchField.RegisterValueChangedCallback(evt =>
            {
                _search = evt.newValue ?? "";
                RenderItems();
            });
            searchbar.Add(_searchField);
            _filterButtons = Row("filters");
            if (!IsPicker)
            {
                foreach (var option in new[] { "All", "Objects", "Dialogs", "Variables" })
                {
                    var value = option;
                    _filterButtons.Add(new Button(() => SetTypeFilter(value)) { text = option });
                }
                searchbar.Add(_filterButtons);
            }
            _root.Add(searchbar);

            var panes = Row("panes");
            var folders = Column("folder-pane");
            folders.Add(Heading("Folders"));
            var folderScroll = new ScrollView();
            folderScroll.style.flexGrow = 1f;
            _folderList = folderScroll;
            folders.Add(folderScroll);
            panes.Add(folders);

            var results = Column("result-pane");
            _resultsHeading = Heading("Content");
            results.Add(_resultsHeading);
            _resultList = new ListView(_visible, 56, MakeRow, BindRow)
            {
                selectionType = SelectionType.Single,
            };
            _resultList.AddToClassList("result-list");
            _resultList.onSelectionChange += OnSelectionChange;
            _resultList.onItemsChosen += OnItemsChosen;
            results.Add(_resultList);
            panes.Add(results);

            var details = Column("details-pane");
            details.Add(Heading("Details"));
            var detailScroll = new ScrollView();
            detailScroll.AddToClassList("detail-scroll");
            _detailContent = Column("detail-content");
            detailScroll.Add(_detailContent);
            details.Add(detailScroll);
            panes.Add(details);
            _root.Add(panes);

            _footer = new Label();
            _footer.AddToClassList("footer");
            _root.Add(_footer);
        }

        private static VisualElement Row(string className)
        {
            var element = new VisualElement();
            element.AddToClassList(className);
            element.style.flexDirection = FlexDirection.Row;
            return element;
        }

        private static VisualElement Column(string className)
        {
            var element = new VisualElement();
            element.AddToClassList(className);
            element.style.flexDirection = FlexDirection.Column;
            return element;
        }

        private static Label Heading(string title)
        {
            var label = new Label(title);
            label.AddToClassList("pane-heading");
            return label;
        }

        private string PickerLabel()
        {
            switch (_pickerKind)
            {
                case Kind.Object: return "Grimoire Object Link";
                case Kind.Dialog: return "dialog asset";
                default: return "variable reference";
            }
        }

        private void RefreshAll()
        {
            if (_root == null)
            {
                return;
            }

            var generation = ++_refreshGeneration;
            ++_detailGeneration;
            _objects.Clear();
            _dialogs.Clear();
            _variables.Clear();
            _objectDetails.Clear();
            _dialogDetails.Clear();
            _remoteVariablesLoaded = false;
            _objectError = null;
            _dialogError = null;
            _variableError = null;
            _detailError = null;
            _detailFailedId = null;
            _message = null;
            _objectsLoading = false;
            _dialogsLoading = false;
            _variablesLoading = false;
            ReadLocalAssets();
            BuildItems();

            if (!GrimoireSettings.IsConfigured)
            {
                _message = "Sign in and choose a game in Grimoire Connect to load objects, dialogs and variables.";
                RenderItems();
                return;
            }

            _objectsLoading = true;
            _dialogsLoading = true;
            _variablesLoading = true;
            RenderItems();
            BeginRemoteLoad(generation, GrimoireSettings.GameId);
        }

        private async void BeginRemoteLoad(int generation, string gameId)
        {
            try
            {
                if (!await GrimoireAuthSession.EnsureFreshTokenAsync())
                {
                    if (IsCurrent(generation, gameId))
                    {
                        _objectsLoading = _dialogsLoading = _variablesLoading = false;
                        _message = "Your Grimoire session has expired. Sign in again from Grimoire Connect.";
                        RenderItems();
                    }
                    return;
                }

                if (!IsCurrent(generation, gameId))
                {
                    return;
                }

                await Task.WhenAll(
                    LoadObjects(generation, gameId),
                    LoadDialogs(generation, gameId),
                    LoadVariables(generation, gameId));
            }
            catch (Exception ex)
            {
                if (IsCurrent(generation, gameId))
                {
                    _objectsLoading = _dialogsLoading = _variablesLoading = false;
                    _message = "Could not refresh the Content Library: " + ex.Message;
                    RenderItems();
                }
            }
        }

        private bool IsCurrent(int generation, string gameId) =>
            this != null && generation == _refreshGeneration &&
            string.Equals(gameId, GrimoireSettings.GameId, StringComparison.Ordinal);

        private async Task LoadObjects(int generation, string gameId)
        {
            try
            {
                for (var page = 0; page < MaxPages; page++)
                {
                    var result = await GrimoireApiClient.ListObjectsAsync(
                        gameId, null, ObjectPageSize, page * ObjectPageSize);
                    if (!IsCurrent(generation, gameId)) return;
                    if (!result.Success)
                    {
                        _objectError = DescribeError("Objects", result.Error, result.HttpStatus);
                        break;
                    }

                    _objects.AddRange((result.Data ?? Array.Empty<ObjectSummary>())
                        .Where(item => item != null && !string.IsNullOrEmpty(item.id)));
                    BuildItems();
                    if (result.Data == null || result.Data.Length < ObjectPageSize) break;
                    if (page == MaxPages - 1) _objectError = "Objects: stopped after the maximum number of pages. Refresh to try again.";
                }
            }
            catch (Exception ex)
            {
                if (IsCurrent(generation, gameId)) _objectError = "Objects: " + ex.Message;
            }
            finally
            {
                if (IsCurrent(generation, gameId))
                {
                    _objectsLoading = false;
                    RenderItems();
                }
            }
        }

        private async Task LoadDialogs(int generation, string gameId)
        {
            try
            {
                for (var page = 0; page < MaxPages; page++)
                {
                    var result = await GrimoireApiClient.ListDialogsAsync(
                        gameId, null, DialogPageSize, page * DialogPageSize);
                    if (!IsCurrent(generation, gameId)) return;
                    if (!result.Success)
                    {
                        _dialogError = DescribeError("Dialogs", result.Error, result.HttpStatus);
                        break;
                    }

                    _dialogs.AddRange((result.Data ?? Array.Empty<DialogSummary>())
                        .Where(item => item != null && !string.IsNullOrEmpty(item.id)));
                    BuildItems();
                    if (result.Data == null || result.Data.Length < DialogPageSize) break;
                    if (page == MaxPages - 1) _dialogError = "Dialogs: stopped after the maximum number of pages. Refresh to try again.";
                }
            }
            catch (Exception ex)
            {
                if (IsCurrent(generation, gameId)) _dialogError = "Dialogs: " + ex.Message;
            }
            finally
            {
                if (IsCurrent(generation, gameId))
                {
                    _dialogsLoading = false;
                    RenderItems();
                }
            }
        }

        private async Task LoadVariables(int generation, string gameId)
        {
            try
            {
                for (var page = 0; page < MaxPages; page++)
                {
                    var result = await GrimoireApiClient.ListVariablesAsync(
                        gameId, VariablePageSize, page * VariablePageSize);
                    if (!IsCurrent(generation, gameId)) return;
                    if (!result.Success)
                    {
                        _variableError = DescribeError("Variables", result.Error, result.HttpStatus);
                        break;
                    }

                    _remoteVariablesLoaded = true;
                    _variables.AddRange((result.Data ?? Array.Empty<VariableResource>())
                        .Where(item => item != null && !string.IsNullOrEmpty(item.id)));
                    BuildItems();
                    if (result.Data == null || result.Data.Length < VariablePageSize) break;
                    if (page == MaxPages - 1) _variableError = "Variables: stopped after the maximum number of pages. Refresh to try again.";
                }
            }
            catch (Exception ex)
            {
                if (IsCurrent(generation, gameId)) _variableError = "Variables: " + ex.Message;
            }
            finally
            {
                if (IsCurrent(generation, gameId))
                {
                    _variablesLoading = false;
                    RenderItems();
                }
            }
        }

        private static string DescribeError(string source, string error, long status) =>
            source + ": " + (string.IsNullOrEmpty(error) ? "Request failed" : error) +
            (status > 0 ? $" (HTTP {status})" : "");

        private void ReadLocalAssets()
        {
            _imported = GrimoireDialogImporter.FindAllImported();
            _cachedVariables = GrimoireVariableSync.FindLibrary();
            if (_cachedVariables != null &&
                !string.Equals(_cachedVariables.GameId, GrimoireSettings.GameId, StringComparison.Ordinal))
            {
                _cachedVariables = null;
            }
        }

        private void BuildItems()
        {
            _items.Clear();
            var gameId = GrimoireSettings.GameId;
            var dialogIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var summary in _objects.GroupBy(item => item.id).Select(group => group.Last()))
            {
                _items.Add(new Item
                {
                    Id = "object:" + summary.id,
                    Name = Display(summary.name, summary.code_id, summary.id),
                    Key = Display(summary.code_id, summary.id),
                    Folder = Folder("Objects", summary.folder),
                    Status = Display(summary.status, "Grimoire"),
                    Source = "Grimoire API",
                    Summary = Display(summary.template?.name, summary.type, "Object"),
                    Type = Kind.Object,
                    Object = summary,
                });
            }

            foreach (var summary in _dialogs.GroupBy(item => item.id).Select(group => group.Last()))
            {
                dialogIds.Add(summary.id);
                _imported.TryGetValue(summary.id, out var asset);
                if (asset != null && asset.GameId != gameId) asset = null;
                _items.Add(new Item
                {
                    Id = "dialog:" + summary.id,
                    Name = Display(summary.name, summary.dialog_key, summary.id),
                    Key = Display(summary.dialog_key, summary.id),
                    Folder = Folder("Dialogs", summary.folder),
                    Status = DialogStatus(summary, asset),
                    Source = asset != null ? "Grimoire + local asset" : "Grimoire API",
                    Summary = asset != null ? $"{asset.Nodes.Count} nodes" : "Dialog",
                    Type = Kind.Dialog,
                    Dialog = summary,
                    Asset = asset,
                });
            }

            foreach (var pair in _imported)
            {
                var asset = pair.Value;
                if (asset == null || asset.GameId != gameId) continue;
                if (!dialogIds.Contains(pair.Key))
                {
                    _items.Add(new Item
                    {
                        Id = "dialog:" + pair.Key,
                        Name = asset.DisplayName,
                        Key = Display(asset.DialogKey, asset.DialogId),
                        Folder = Folder("Dialogs", "Imported assets"),
                        Status = "Local asset",
                        Source = "Imported dialog asset",
                        Summary = $"{asset.Nodes.Count} nodes",
                        Type = Kind.Dialog,
                        Asset = asset,
                    });
                }

                foreach (var variable in asset.Variables)
                {
                    if (variable == null || string.IsNullOrWhiteSpace(variable.name)) continue;
                    _items.Add(new Item
                    {
                        Id = "dialog-variable:" + pair.Key + ":" + variable.name,
                        Name = variable.name,
                        Key = Display(asset.DialogKey, asset.DialogId) + "/" + variable.name,
                        Folder = Folder("Variables", "Dialog/" + asset.DisplayName),
                        Status = "Imported",
                        Source = "Dialog asset: " + asset.DisplayName,
                        Summary = variable.type,
                        Type = Kind.DialogVariable,
                        Asset = asset,
                        DialogVariable = variable,
                    });
                }
            }

            if (_remoteVariablesLoaded)
            {
                foreach (var variable in _variables.GroupBy(item => item.id).Select(group => group.Last()))
                {
                    _items.Add(new Item
                    {
                        Id = "variable:" + variable.id,
                        Name = Display(variable.name, variable.id),
                        Key = Display(variable.name, variable.id),
                        Folder = Folder("Variables", !string.IsNullOrWhiteSpace(variable.folder)
                            ? variable.folder : variable.group),
                        Status = Display(variable.status, "Grimoire"),
                        Source = "Grimoire API",
                        Summary = $"{variable.elements?.Length ?? 0} elements · {variable.element_type}",
                        Type = Kind.Variable,
                        Variable = variable,
                        CachedVariable = _cachedVariables?.FindVariable(variable.id),
                    });
                }
            }
            else if (_cachedVariables != null)
            {
                foreach (var variable in _cachedVariables.Variables)
                {
                    if (variable == null) continue;
                    _items.Add(new Item
                    {
                        Id = "variable:" + variable.Id,
                        Name = Display(variable.Name, variable.Id),
                        Key = Display(variable.Name, variable.Id),
                        Folder = Folder("Variables", variable.Group),
                        Status = "Synced copy",
                        Source = "Local GrimoireVariables asset",
                        Summary = $"{variable.Elements.Count} elements · {variable.ElementType}",
                        Type = Kind.Variable,
                        CachedVariable = variable,
                    });
                }
            }

            _items.Sort((a, b) =>
            {
                var byKind = CategoryOrder(a.Type).CompareTo(CategoryOrder(b.Type));
                return byKind != 0 ? byKind : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            BuildFolders();
            RenderItems();
        }

        private static int CategoryOrder(Kind kind) =>
            kind == Kind.Object ? 0 : kind == Kind.Dialog ? 1 : 2;

        private static string DialogStatus(DialogSummary summary, GrimoireDialogAsset asset)
        {
            if (asset == null) return "Not imported";
            return string.Equals(asset.UpdatedAt, summary.updated_at ?? "", StringComparison.Ordinal)
                ? "Imported"
                : "Update available";
        }

        private static string Display(params string[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "(unnamed)";

        internal static string Folder(string root, string path)
        {
            var clean = (path ?? "").Replace('\\', '/').Split('/')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0 && part != "." && part != "..")
                .ToList();
            if (clean.Count > 0 && string.Equals(clean[0], root, StringComparison.OrdinalIgnoreCase))
            {
                clean.RemoveAt(0);
            }
            if (clean.Count == 0) clean.Add("Unfiled");
            return root + "/" + string.Join("/", clean);
        }

        private void BuildFolders()
        {
            _folders.Clear();
            _folders.Add(AllFolder);
            _folders.Add("Objects");
            _folders.Add("Dialogs");
            _folders.Add("Variables");
            foreach (var item in _items)
            {
                var parts = item.Folder.Split('/');
                for (var count = 2; count <= parts.Length; count++)
                {
                    var path = string.Join("/", parts.Take(count));
                    if (!_folders.Contains(path)) _folders.Add(path);
                }
            }
            _folders.Sort((a, b) =>
            {
                if (a == AllFolder) return -1;
                if (b == AllFolder) return 1;
                var aOrder = a.StartsWith("Objects", StringComparison.Ordinal) ? 0
                    : a.StartsWith("Dialogs", StringComparison.Ordinal) ? 1 : 2;
                var bOrder = b.StartsWith("Objects", StringComparison.Ordinal) ? 0
                    : b.StartsWith("Dialogs", StringComparison.Ordinal) ? 1 : 2;
                return aOrder != bOrder ? aOrder.CompareTo(bOrder)
                    : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
        }

        private void SetTypeFilter(string value)
        {
            _typeFilter = value == "Objects" ? Kind.Object : value == "Dialogs" ? Kind.Dialog
                : value == "Variables" ? Kind.Variable : (Kind?)null;
            _folder = AllFolder;
            RenderItems();
        }

        private void SetFolder(string path)
        {
            _folder = path;
            if (!IsPicker) EditorPrefs.SetString(PrefKey("Folder"), path);
            RenderItems();
        }

        private void ShowFolderMenu()
        {
            var menu = new GenericMenu();
            foreach (var path in _folders)
            {
                var selected = path;
                menu.AddItem(new GUIContent(path), path == _folder, () => SetFolder(selected));
            }
            menu.ShowAsContext();
        }

        private bool Allowed(Item item)
        {
            if (_pickerKind == Kind.Object && item.Type != Kind.Object) return false;
            if (_pickerKind == Kind.Dialog && item.Type != Kind.Dialog) return false;
            if (_pickerKind == Kind.Variable && item.Type != Kind.Variable && item.Type != Kind.DialogVariable) return false;
            if (_typeFilter == Kind.Object && item.Type != Kind.Object) return false;
            if (_typeFilter == Kind.Dialog && item.Type != Kind.Dialog) return false;
            if (_typeFilter == Kind.Variable && item.Type != Kind.Variable && item.Type != Kind.DialogVariable) return false;
            if (_folder != AllFolder && item.Folder != _folder &&
                !item.Folder.StartsWith(_folder + "/", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(_search)) return true;
            var query = _search.Trim();
            return Contains(item.Name, query) || Contains(item.Key, query) ||
                Contains(item.Folder, query) || Contains(item.Status, query) ||
                Contains(item.Summary, query) || Contains(item.Id, query) ||
                Contains(ValueText(item.DialogVariable?.initialValue?.ToObject()), query) ||
                item.Variable?.elements?.Any(element => element != null &&
                    (Contains(element.name, query) || Contains(ValueText(element.value), query))) == true ||
                item.CachedVariable?.Elements.Any(element => element != null &&
                    (Contains(element.Name, query) || Contains(ValueText(element.AuthoredValue), query))) == true;
        }

        private static bool Contains(string source, string query) =>
            !string.IsNullOrEmpty(source) && source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private void RenderItems()
        {
            if (_root == null || _resultList == null) return;
            if (!_folders.Contains(_folder)) _folder = AllFolder;
            _visible.Clear();
            _visible.AddRange(_items.Where(Allowed));
            _suppressSelection = true;
            _resultList.Rebuild();
            var selectedIndex = _visible.FindIndex(item => item.Id == _selectedId);
            if (selectedIndex >= 0) _resultList.SetSelectionWithoutNotify(new[] { selectedIndex });
            else _resultList.ClearSelection();
            _suppressSelection = false;

            _breadcrumb.text = "Grimoire  /  " + (IsPicker ? PickerLabel() : "Content Library");
            _title.text = IsPicker ? "Pick " + PickerLabel() : "Content Library";
            _subtitle.text = "Objects, dialogs and variables for " +
                (string.IsNullOrEmpty(GrimoireSettings.GameId) ? "your selected game" : GrimoireSettings.GameId);
            _folderPopup.text = _folder + " ▾";
            _resultsHeading.text = _folder + "  ·  " + _visible.Count;
            _footer.text = $"{_items.Count} items · {_objects.Count} objects · {_dialogs.Count} dialogs · " +
                $"{(_remoteVariablesLoaded ? _variables.Count : _cachedVariables?.Variables.Count ?? 0)} game variables" +
                (_objectsLoading || _dialogsLoading || _variablesLoading ? " · Loading…" : "");
            if (!IsPicker)
            {
                foreach (var button in _filterButtons.Children().OfType<Button>())
                {
                    button.EnableInClassList("selected", button.text == "All" && !_typeFilter.HasValue ||
                        button.text == "Objects" && _typeFilter == Kind.Object ||
                        button.text == "Dialogs" && _typeFilter == Kind.Dialog ||
                        button.text == "Variables" && _typeFilter == Kind.Variable);
                }
            }
            var errors = new[] { _message, _objectError, _dialogError, _variableError }
                .Where(value => !string.IsNullOrEmpty(value)).ToArray();
            _notice.text = string.Join("\n", errors);
            _notice.style.display = errors.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            RenderFolders();
            RenderDetail();
            if (selectedIndex >= 0) LoadSelectedDetail();
        }

        private void RenderFolders()
        {
            _folderList.Clear();
            foreach (var path in _folders)
            {
                if (_pickerKind == Kind.Object && path != AllFolder && !path.StartsWith("Objects", StringComparison.Ordinal)) continue;
                if (_pickerKind == Kind.Dialog && path != AllFolder && !path.StartsWith("Dialogs", StringComparison.Ordinal)) continue;
                if (_pickerKind == Kind.Variable && path != AllFolder && !path.StartsWith("Variables", StringComparison.Ordinal)) continue;
                var current = path;
                var count = _items.Count(item => (path == AllFolder || item.Folder == path ||
                    item.Folder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) &&
                    (!_pickerKind.HasValue || _pickerKind == Kind.Object && item.Type == Kind.Object ||
                     _pickerKind == Kind.Dialog && item.Type == Kind.Dialog ||
                     _pickerKind == Kind.Variable && (item.Type == Kind.Variable || item.Type == Kind.DialogVariable)));
                var row = Row("folder-row");
                if (path == _folder) row.AddToClassList("selected");
                var indent = Math.Max(0, path.Count(character => character == '/')) * 11;
                row.style.paddingLeft = 10 + indent;
                var name = new Label(path == AllFolder ? path : path.Split('/').Last());
                name.AddToClassList("folder-name");
                row.Add(name);
                var number = new Label(count.ToString(CultureInfo.InvariantCulture));
                number.AddToClassList("folder-count");
                row.Add(number);
                row.RegisterCallback<MouseUpEvent>(_ => SetFolder(current));
                _folderList.Add(row);
            }
        }

        private static VisualElement MakeRow()
        {
            var row = Row("result-row");
            var main = Column("result-main");
            main.Add(new Label { name = "name" });
            main.Add(new Label { name = "meta" });
            row.Add(main);
            var side = Column("result-side");
            side.Add(new Label { name = "summary" });
            side.Add(new Label { name = "status" });
            row.Add(side);
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _visible.Count) return;
            var item = _visible[index];
            var name = row.Q<Label>("name");
            name.text = item.Name;
            name.AddToClassList("result-name");
            var meta = row.Q<Label>("meta");
            meta.text = item.Key + "  ·  " + item.Folder;
            meta.AddToClassList("result-meta");
            var summary = row.Q<Label>("summary");
            summary.text = item.Summary;
            summary.AddToClassList("result-summary");
            var status = row.Q<Label>("status");
            status.text = item.Status;
            status.AddToClassList("result-status");
        }

        private void OnSelectionChange(IEnumerable<object> selection)
        {
            if (_suppressSelection) return;
            var item = selection.OfType<Item>().FirstOrDefault();
            _selectedId = item?.Id;
            if (!IsPicker) EditorPrefs.SetString(PrefKey("Selected"), _selectedId ?? "");
            ++_detailGeneration;
            _detailError = null;
            _detailFailedId = null;
            _detailLoading = false;
            RenderDetail();
            LoadSelectedDetail();
        }

        private void OnItemsChosen(IEnumerable<object> selection)
        {
            var item = selection.OfType<Item>().FirstOrDefault();
            if (item != null && IsPicker) Pick(item);
        }

        private Item SelectedItem() => _items.FirstOrDefault(item => item.Id == _selectedId);

        private void RenderDetail()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            var item = SelectedItem();
            if (item == null)
            {
                Info(_items.Count == 0 && (_objectsLoading || _dialogsLoading || _variablesLoading)
                    ? "Loading content…" : "Select an item to inspect its values and copy its key.");
                return;
            }

            var kind = item.Type == Kind.DialogVariable ? "DIALOG VARIABLE" : item.Type.ToString().ToUpperInvariant();
            DetailLabel(kind, "detail-kind");
            DetailLabel(item.Name, "detail-name");
            DetailLabel(item.Key, "detail-key");
            var actions = Row("actions");
            if (IsPicker)
            {
                var canPick = item.Type != Kind.Dialog || item.Asset != null;
                var pick = new Button(() => Pick(item)) { text = canPick ? "Use selection" : "Import to use" };
                pick.AddToClassList("primary");
                pick.SetEnabled(canPick);
                actions.Add(pick);
            }
            CopyButton(actions, "Copy key", item.Key);
            CopyButton(actions, "Copy ID", item.Type == Kind.Object ? item.Object?.id :
                item.Type == Kind.Dialog ? item.Dialog?.id ?? item.Asset?.DialogId :
                item.Type == Kind.Variable ? item.Variable?.id ?? item.CachedVariable?.Id : item.Id);
            if (item.Asset != null)
            {
                actions.Add(new Button(() => EditorGUIUtility.PingObject(item.Asset)) { text = "Ping asset" });
            }
            _detailContent.Add(actions);
            ValueRow("Folder", item.Folder);
            ValueRow("Source", item.Source);
            ValueRow("Status", item.Status);

            if (_detailLoading) Info("Loading values…");
            if (!string.IsNullOrEmpty(_detailError)) Info(_detailError);
            switch (item.Type)
            {
                case Kind.Object: RenderObject(item); break;
                case Kind.Dialog: RenderDialog(item); break;
                case Kind.Variable: RenderVariable(item); break;
                case Kind.DialogVariable: RenderDialogVariable(item); break;
            }
        }

        private void RenderObject(Item item)
        {
            if (item.Object == null || !_objectDetails.TryGetValue(item.Object.id, out var document)) return;
            var snapshot = GrimoireObjectSnapshotMapper.FromDocument(document);
            if (snapshot == null) return;
            string section = null;
            foreach (var field in snapshot.Fields)
            {
                if (field == null) continue;
                if (field.SectionTitle != section)
                {
                    section = field.SectionTitle;
                    Section(string.IsNullOrEmpty(section) ? "Fields" : section);
                }
                var values = field.IsReference
                    ? field.References.Where(reference => reference != null).Select(reference => reference.DisplayName).ToArray()
                    : field.Multiple ? field.GetDisplayValues(GrimoireSettings.Locale).ToArray()
                    : new[] { field.GetDisplayValue(GrimoireSettings.Locale) };
                ValueRow(field.DisplayLabel, string.Join(", ", values), field.FieldType);
            }
            if (snapshot.Fields.Count == 0) Info("This object has no fields.");
        }

        private void RenderDialog(Item item)
        {
            if (item.Dialog != null && GrimoireSettings.IsConfigured)
            {
                var import = new Button(() => ImportDialog(item))
                {
                    text = item.Asset == null ? "Import dialog into project" : "Update imported dialog"
                };
                import.AddToClassList("primary");
                import.SetEnabled(!_busy);
                _detailContent.Add(import);
            }
            var asset = item.Asset;
            if (asset != null)
            {
                if (!string.IsNullOrEmpty(asset.Description)) Info(asset.Description);
                Section("Imported nodes · " + asset.Nodes.Count);
                foreach (var node in asset.Nodes.Where(node => node != null).Take(100))
                {
                    ValueRow(Display(node.name, node.identifier, node.id),
                        Display(node.text, node.type.ToString()));
                }
                if (asset.Nodes.Count > 100) Info("Showing the first 100 nodes.");
                Section("Dialog variables · " + asset.Variables.Count);
                foreach (var variable in asset.Variables.Where(variable => variable != null))
                {
                    ValueRow(variable.name, ValueText(variable.initialValue?.ToObject()), variable.type);
                }
            }
            else if (item.Dialog != null) Info("Import this dialog to use it in a Unity component.");

            if (item.Dialog == null || !_dialogDetails.TryGetValue(item.Dialog.id, out var resource)) return;
            if (asset == null)
            {
                if (!string.IsNullOrEmpty(resource.description)) Info(resource.description);
                Section("Nodes · " + (resource.data?.nodes?.Length ?? 0));
                foreach (var node in (resource.data?.nodes ?? Array.Empty<DialogNodeDto>()).Where(node => node != null).Take(100))
                {
                    ValueRow(Display(node.name, node.node_identifier, node.id), Display(node.text, node.type));
                }
                Section("Dialog variables · " + (resource.data?.variables?.Length ?? 0));
                foreach (var variable in (resource.data?.variables ?? Array.Empty<DialogVariableDto>()).Where(variable => variable != null))
                {
                    ValueRow(variable.name, ValueText(variable.initialValue), variable.type);
                }
            }
        }

        private void RenderVariable(Item item)
        {
            var resource = item.Variable;
            if (resource != null)
            {
                if (!string.IsNullOrEmpty(resource.description)) Info(resource.description);
                Section("Elements · " + (resource.elements?.Length ?? 0));
                foreach (var element in (resource.elements ?? Array.Empty<VariableElementDto>()).Where(element => element != null))
                {
                    var authored = element.value ?? (object)element.name;
                    ValueRow(Display(element.name, element.id), ValueText(authored), resource.element_type);
                    if (EditorApplication.isPlaying && _cachedVariables != null &&
                        _cachedVariables.TryGetElement(element.id, out _))
                        ValueRow("Play mode value", ValueText(GrimoireVariables.GetValue(element.id)));
                    if (element.range_min.HasValue && element.range_max.HasValue)
                        ValueRow("Allowed range", $"{element.range_min} – {element.range_max}");
                    CopyButton(_detailContent, "Copy element ID: " + Display(element.name, element.id), element.id);
                }
            }
            else if (item.CachedVariable != null)
            {
                var variable = item.CachedVariable;
                if (!string.IsNullOrEmpty(variable.Description)) Info(variable.Description);
                Section("Elements · " + variable.Elements.Count);
                foreach (var element in variable.Elements.Where(element => element != null))
                {
                    var value = EditorApplication.isPlaying ? GrimoireVariables.GetValue(element.Id)
                        : element.AuthoredValue ?? (object)element.DisplayName;
                    ValueRow(element.DisplayName, ValueText(value), variable.ElementType);
                    CopyButton(_detailContent, "Copy element ID: " + element.DisplayName, element.Id);
                }
            }
            if (_cachedVariables == null && resource != null && GrimoireSettings.IsConfigured)
            {
                _detailContent.Add(new Button(SyncVariables) { text = "Sync variables into Unity project" });
            }
        }

        private void RenderDialogVariable(Item item)
        {
            var variable = item.DialogVariable;
            if (variable == null) return;
            if (!string.IsNullOrEmpty(variable.description)) Info(variable.description);
            ValueRow("Type", variable.type);
            ValueRow("Authored value", ValueText(variable.initialValue?.ToObject()));
            if (variable.hasRange) ValueRow("Allowed range", $"{variable.rangeMin} – {variable.rangeMax}");
            if (EditorApplication.isPlaying && item.Asset != null)
            {
                var player = UnityEngine.Object.FindObjectOfType<GrimoireDialogPlayer>();
                if (player != null) ValueRow("Play mode value", ValueText(player.GetVariable<object>(item.Asset, variable.name)));
            }
        }

        private void DetailLabel(string value, string className)
        {
            var label = new Label(value ?? "");
            label.AddToClassList(className);
            _detailContent.Add(label);
        }

        private void Section(string title)
        {
            DetailLabel(title, "detail-section");
        }

        private void Info(string message)
        {
            DetailLabel(message, "info-box");
        }

        private void ValueRow(string label, string value, string hint = null)
        {
            var row = Row("value-row");
            var caption = new Label(string.IsNullOrEmpty(hint) ? label : label + "  ·  " + hint);
            caption.AddToClassList("value-label");
            row.Add(caption);
            var text = new Label(string.IsNullOrEmpty(value) ? "—" : value);
            text.tooltip = value ?? "";
            text.AddToClassList("value-text");
            row.Add(text);
            if (!string.IsNullOrEmpty(value)) CopyButton(row, "Copy", value, "value-copy");
            _detailContent.Add(row);
        }

        private static void CopyButton(VisualElement parent, string label, string value, string className = null)
        {
            if (string.IsNullOrEmpty(value)) return;
            var button = new Button(() => EditorGUIUtility.systemCopyBuffer = value) { text = label };
            if (!string.IsNullOrEmpty(className)) button.AddToClassList(className);
            parent.Add(button);
        }

        private static string ValueText(object value)
        {
            if (value == null) return "";
            if (value is JValue json) value = json.Value;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private async void LoadSelectedDetail()
        {
            var item = SelectedItem();
            if (item == null || !GrimoireSettings.IsConfigured || _detailLoading ||
                item.Id == _detailFailedId) return;
            var isObject = item.Type == Kind.Object && item.Object != null &&
                !_objectDetails.ContainsKey(item.Object.id);
            var isDialog = item.Type == Kind.Dialog && item.Dialog != null &&
                !_dialogDetails.ContainsKey(item.Dialog.id);
            if (!isObject && !isDialog) return;

            var generation = ++_detailGeneration;
            var gameId = GrimoireSettings.GameId;
            _detailLoading = true;
            _detailError = null;
            RenderDetail();
            try
            {
                if (!await GrimoireAuthSession.EnsureFreshTokenAsync())
                {
                    if (IsCurrentDetail(generation, gameId))
                    {
                        _detailError = "Sign in again to load values.";
                        _detailFailedId = item.Id;
                    }
                    return;
                }
                if (isObject)
                {
                    var result = await GrimoireApiClient.GetObjectViewAsync(gameId, item.Object.id, GrimoireSettings.Locale);
                    if (!IsCurrentDetail(generation, gameId)) return;
                    if (result.Success && result.Data != null) _objectDetails[item.Object.id] = result.Data;
                    else
                    {
                        _detailError = DescribeError("Object values", result.Error, result.HttpStatus);
                        _detailFailedId = item.Id;
                    }
                }
                else
                {
                    var result = await GrimoireApiClient.GetDialogAsync(gameId, item.Dialog.id);
                    if (!IsCurrentDetail(generation, gameId)) return;
                    if (result.Success && result.Data != null) _dialogDetails[item.Dialog.id] = result.Data;
                    else
                    {
                        _detailError = DescribeError("Dialog values", result.Error, result.HttpStatus);
                        _detailFailedId = item.Id;
                    }
                }
            }
            catch (Exception ex)
            {
                if (IsCurrentDetail(generation, gameId))
                {
                    _detailError = ex.Message;
                    _detailFailedId = item.Id;
                }
            }
            finally
            {
                if (IsCurrentDetail(generation, gameId))
                {
                    _detailLoading = false;
                    RenderDetail();
                }
            }
        }

        private bool IsCurrentDetail(int generation, string gameId) =>
            this != null && generation == _detailGeneration &&
            string.Equals(gameId, GrimoireSettings.GameId, StringComparison.Ordinal);

        private void Pick(Item item)
        {
            if (item.Type == Kind.Object && item.Object != null) _onObjectPicked?.Invoke(item.Object);
            else if (item.Type == Kind.Dialog && item.Asset != null) _onDialogPicked?.Invoke(item.Asset);
            else if (item.Type == Kind.Variable || item.Type == Kind.DialogVariable)
                _onVariablePicked?.Invoke(item.Type == Kind.Variable
                    ? item.Variable?.id ?? item.CachedVariable?.Id : item.Key);
            else return;
            Close();
        }

        private async void ImportDialog(Item item)
        {
            if (_busy || item.Dialog == null) return;
            _busy = true;
            _message = "Importing " + item.Name + "…";
            RenderItems();
            var gameId = GrimoireSettings.GameId;
            try
            {
                var result = await GrimoireDialogImporter.ImportAsync(gameId, item.Dialog.id);
                if (this == null || gameId != GrimoireSettings.GameId) return;
                _message = result.Success ? "Imported " + item.Name + " into the project." :
                    DescribeError("Dialog import", result.Error, result.HttpStatus);
                ReadLocalAssets();
                BuildItems();
            }
            catch (Exception ex)
            {
                if (this != null) _message = "Dialog import: " + ex.Message;
            }
            finally
            {
                if (this != null)
                {
                    _busy = false;
                    RenderItems();
                }
            }
        }

        private async void SyncVariables()
        {
            if (_busy || !GrimoireSettings.IsConfigured) return;
            _busy = true;
            _message = "Syncing variables into the project…";
            RenderItems();
            var gameId = GrimoireSettings.GameId;
            try
            {
                var result = await GrimoireVariableSync.SyncAsync(gameId);
                if (this == null || gameId != GrimoireSettings.GameId) return;
                _message = result.Success ? "Variables synced into the project." :
                    DescribeError("Variable sync", result.Error, result.HttpStatus);
                ReadLocalAssets();
                BuildItems();
            }
            catch (Exception ex)
            {
                if (this != null) _message = "Variable sync: " + ex.Message;
            }
            finally
            {
                if (this != null)
                {
                    _busy = false;
                    RenderItems();
                }
            }
        }
    }
}
