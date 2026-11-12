using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// The Scene workspace follows Unity's loaded hierarchy. Every GameObject is
    /// browsable here; Grimoire links add their values and workflows to Details.
    /// </summary>
    internal sealed class GrimoireSceneWorkspaceView
    {
        private sealed class SceneItem
        {
            public GameObject GameObject;
            public Scene Scene;
            public GameObject Root;
            public string Path;
            public GrimoireObjectLink ObjectLink;
            public GrimoireTextLink TextLink;
            public bool IsDirty;
        }

        private const string AllGroup = "all";
        private const string StylePath = "Packages/com.grimoire.plugin/Editor/UI/GrimoireSceneWorkspace.uss";

        private readonly List<SceneItem> _items = new List<SceneItem>();
        private readonly List<SceneItem> _visible = new List<SceneItem>();
        private readonly HashSet<string> _kinds = new HashSet<string>();
        private readonly Func<GrimoireObjectLink, ObjectViewDocument> _documentFor;
        private readonly Action<int> _drawObjectTab;
        private readonly Action _refreshObject;
        private readonly Action _openLibrary;
        private readonly Func<GrimoireObjectLink, string> _statusFor;

        private VisualElement _root;
        private VisualElement _groups;
        private VisualElement _detail;
        private ListView _list;
        private Label _summary;
        private Label _summaryTitle;
        private Label _languageBadge;
        private Label _resultHeading;
        private Label _footer;
        private Button _groupPopup;
        private Button _filterButton;
        private string _search = "";
        private string _group = AllGroup;
        private int _detailTab;
        private bool _changedOnly;
        private bool _active;
        private bool _suppressSelection;
        private bool _refreshQueued;

        internal GrimoireSceneWorkspaceView(
            Func<GrimoireObjectLink, ObjectViewDocument> documentFor,
            Func<GrimoireObjectLink, string> statusFor,
            Action<int> drawObjectTab,
            Action refreshObject,
            Action openLibrary)
        {
            _documentFor = documentFor;
            _statusFor = statusFor;
            _drawObjectTab = drawObjectTab;
            _refreshObject = refreshObject;
            _openLibrary = openLibrary;
        }

        internal void Activate()
        {
            if (_active) return;
            _active = true;
            EditorApplication.hierarchyChanged += QueueRefresh;
            Selection.selectionChanged += RefreshSelection;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            GrimoireGameEngineDirtyTracker.Changed += QueueRefresh;
            GrimoireEditableFieldsRenderer.Changed += QueueRefresh;
            GrimoireTextLinkStore.Changed += QueueRefresh;
            GrimoireSettings.LocaleChanged += RefreshDetail;
            GrimoireGameLanguagePreview.Changed += RefreshDetail;
            GrimoireObjectLink.NestedSnapshotsChanged += OnNestedSnapshotChanged;
            GrimoireObjectLink.RuntimeFieldChanged += OnRuntimeFieldChanged;
            GrimoireObjectCache.Updated += RefreshDetail;
            GrimoireGameLanguages.EnsureLoaded();
            GrimoireGameLanguagePreview.EnsureLoaded();
            Refresh();
        }

        internal void Deactivate()
        {
            if (!_active) return;
            _active = false;
            EditorApplication.hierarchyChanged -= QueueRefresh;
            Selection.selectionChanged -= RefreshSelection;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            GrimoireGameEngineDirtyTracker.Changed -= QueueRefresh;
            GrimoireEditableFieldsRenderer.Changed -= QueueRefresh;
            GrimoireTextLinkStore.Changed -= QueueRefresh;
            GrimoireSettings.LocaleChanged -= RefreshDetail;
            GrimoireGameLanguagePreview.Changed -= RefreshDetail;
            GrimoireObjectLink.NestedSnapshotsChanged -= OnNestedSnapshotChanged;
            GrimoireObjectLink.RuntimeFieldChanged -= OnRuntimeFieldChanged;
            GrimoireObjectCache.Updated -= RefreshDetail;
            EditorApplication.delayCall -= FlushRefresh;
            _refreshQueued = false;
        }

        private void OnPlayModeChanged(PlayModeStateChange _) => QueueRefresh();

        private void QueueRefresh()
        {
            if (!_active || _refreshQueued) return;
            _refreshQueued = true;
            EditorApplication.delayCall += FlushRefresh;
        }

        private void FlushRefresh()
        {
            _refreshQueued = false;
            if (_active) Refresh();
        }

        private void OnNestedSnapshotChanged(GrimoireObjectLink _) => RefreshDetail();

        private void OnRuntimeFieldChanged(GrimoireObjectLink link, GrimoireRuntimeFieldChange _)
        {
            if (link == null) return;
            if (string.IsNullOrWhiteSpace(_search))
            {
                if (link.gameObject == Selection.activeGameObject) RefreshDetail();
            }
            else
            {
                RenderList();
            }
        }

        internal void Build(VisualElement host)
        {
            _root = host;
            _root.Clear();
            _root.AddToClassList("grimoire-scene");
            _root.EnableInClassList("light", !EditorGUIUtility.isProSkin);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (sheet == null)
            {
                var guids = AssetDatabase.FindAssets("GrimoireSceneWorkspace t:StyleSheet");
                if (guids.Length > 0)
                    sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
            if (sheet != null) _root.styleSheets.Add(sheet);

            var title = Row("scene-titlebar");
            var titleText = Column("scene-title-text");
            titleText.Add(Label("Scene", "scene-title"));
            titleText.Add(Label("Every GameObject in the loaded Unity scenes, with Grimoire values beside it.", "scene-muted"));
            title.Add(titleText);
            title.Add(new Button(() =>
            {
                var selected = Selection.activeGameObject;
                if (selected != null) EditorGUIUtility.PingObject(selected);
            }) { text = "Select in Hierarchy" });
            var createObject = new Button(() => GrimoireCreateObjectWindow.Open(null))
                { text = "Create object" };
            createObject.AddToClassList("scene-primary");
            title.Add(createObject);
            _root.Add(title);

            var summaryRow = Row("scene-summary");
            var summaryText = Column("scene-summary-text");
            _summaryTitle = Label("", "scene-summary-title");
            summaryText.Add(_summaryTitle);
            _summary = Label("", "scene-muted");
            summaryText.Add(_summary);
            summaryRow.Add(summaryText);
            var summaryBadges = Row("scene-summary-badges");
            _languageBadge = Label("", "scene-language-badge");
            summaryBadges.Add(_languageBadge);
            summaryBadges.Add(Label("Selection synced with Unity", "scene-sync-badge"));
            summaryRow.Add(summaryBadges);
            _root.Add(summaryRow);

            var searchBar = Row("scene-searchbar");
            _groupPopup = new Button(ShowGroupMenu) { text = "All loaded scenes ▾" };
            _groupPopup.AddToClassList("scene-group-popup");
            searchBar.Add(_groupPopup);
            var search = new TextField("Search") { value = _search };
            search.AddToClassList("scene-search");
            search.RegisterValueChangedCallback(evt =>
            {
                _search = evt.newValue ?? "";
                RenderList();
            });
            searchBar.Add(search);
            _filterButton = new Button(ShowFilterMenu) { text = "Filters ▾" };
            _filterButton.AddToClassList("scene-filters");
            searchBar.Add(_filterButton);
            _root.Add(searchBar);

            var panes = Row("scene-panes");
            var groupPane = Column("scene-group-pane");
            groupPane.Add(Label("SCENES & HIERARCHY", "scene-pane-heading"));
            var groupScroll = new ScrollView();
            groupScroll.style.flexGrow = 1f;
            _groups = groupScroll;
            groupPane.Add(groupScroll);
            panes.Add(groupPane);

            var listPane = Column("scene-list-pane");
            _resultHeading = Label("SCENE OBJECTS", "scene-pane-heading");
            listPane.Add(_resultHeading);
            listPane.Add(Label("Click a row to select its GameObject in Unity and inspect it here.",
                "scene-list-hint"));
            _list = new ListView(_visible, 62, MakeRow, BindRow)
            {
                selectionType = SelectionType.Single,
            };
            _list.AddToClassList("scene-list");
            _list.onSelectionChange += OnListSelection;
            listPane.Add(_list);
            panes.Add(listPane);

            var detailPane = Column("scene-detail-pane");
            detailPane.Add(Label("SELECTED GAMEOBJECT", "scene-pane-heading"));
            var detailScroll = new ScrollView();
            detailScroll.AddToClassList("scene-detail-scroll");
            _detail = Column("scene-detail-content");
            detailScroll.Add(_detail);
            detailPane.Add(detailScroll);
            panes.Add(detailPane);
            _root.Add(panes);

            _footer = Label("", "scene-footer");
            _root.Add(_footer);
            _root.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                var width = evt.newRect.width;
                _root.EnableInClassList("compact", width < 850f);
                _root.EnableInClassList("medium", width >= 850f && width < 1100f);
            });
            Refresh();
        }

        internal void Refresh()
        {
            _items.Clear();
            var dirtyLinks = new HashSet<GrimoireObjectLink>(
                GrimoireGameEngineDirtyTracker.GetPendingChanges()
                    .Where(change => change.IsDirty && change.Link != null)
                    .Select(change => change.Link));
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    Collect(root, root, scene, root.name, dirtyLinks);
            }
            if (_group != AllGroup && !_items.Any(item => _group == SceneKey(item.Scene) ||
                item.Root != null && _group == RootKey(item.Root)))
                _group = AllGroup;
            RenderGroups();
            RenderList();
        }

        private void Collect(GameObject gameObject, GameObject root, Scene scene, string path,
            HashSet<GrimoireObjectLink> dirtyLinks)
        {
            if (gameObject == null || (gameObject.hideFlags & HideFlags.HideInHierarchy) != 0) return;
            var objectLink = gameObject.GetComponent<GrimoireObjectLink>();
            var textLink = gameObject.GetComponent<GrimoireTextLink>();
            _items.Add(new SceneItem
            {
                GameObject = gameObject,
                Scene = scene,
                Root = root,
                Path = path,
                ObjectLink = objectLink,
                TextLink = textLink,
                IsDirty = objectLink != null &&
                    (dirtyLinks.Contains(objectLink) ||
                     GrimoireEditableFieldsRenderer.HasDirtyEditsForLink(objectLink)) ||
                    textLink != null && GrimoireTextLinkStore.HasDeviations(textLink),
            });
            var transform = gameObject.transform;
            for (var i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                Collect(child.gameObject, root, scene, path + "/" + child.name, dirtyLinks);
            }
        }

        private void RenderGroups()
        {
            if (_groups == null) return;
            _groups.Clear();
            AddGroup("All loaded scenes", AllGroup, _items.Count, 0);
            foreach (var sceneGroup in _items.GroupBy(item => item.Scene.handle))
            {
                var scene = sceneGroup.First().Scene;
                var sceneKey = SceneKey(scene);
                AddGroup(string.IsNullOrEmpty(scene.name) ? "Untitled scene" : scene.name,
                    sceneKey, sceneGroup.Count(), 1);
                foreach (var rootGroup in sceneGroup.GroupBy(item => item.Root))
                {
                    var root = rootGroup.Key;
                    if (root != null)
                        AddHierarchyRoot(root, rootGroup.Count());
                }
            }
        }

        private void AddHierarchyRoot(GameObject root, int count)
        {
            var row = Row("scene-group-row");
            if (_group == RootKey(root)) row.AddToClassList("selected");
            if (Selection.activeGameObject == root) row.AddToClassList("unity-selected");
            row.style.paddingLeft = 36;
            row.Add(Label(root.name, "scene-group-name"));
            row.Add(Label(count.ToString(), "scene-group-count"));
            row.RegisterCallback<MouseUpEvent>(_ => SelectHierarchyRoot(root));
            _groups.Add(row);
        }

        internal void SelectHierarchyRoot(GameObject root)
        {
            if (root == null || !_items.Any(item => item.GameObject == root)) return;
            SetGroup(RootKey(root));
            SelectGameObject(root);
        }

        private void AddGroup(string name, string key, int count, int depth)
        {
            var row = Row("scene-group-row");
            if (_group == key) row.AddToClassList("selected");
            row.style.paddingLeft = 10 + depth * 13;
            row.Add(Label(name, "scene-group-name"));
            row.Add(Label(count.ToString(), "scene-group-count"));
            row.RegisterCallback<MouseUpEvent>(_ => SetGroup(key));
            _groups.Add(row);
        }

        private void SetGroup(string key)
        {
            _group = key;
            RenderGroups();
            RenderList();
        }

        private void SelectGameObject(GameObject gameObject)
        {
            if (gameObject == null) return;
            _detailTab = 0;
            Selection.activeGameObject = gameObject;
            EditorGUIUtility.PingObject(gameObject);
            RefreshSelection();
        }

        private static string SceneKey(Scene scene) => "scene:" + scene.handle;
        private static string RootKey(GameObject root) => "root:" + root.GetInstanceID();

        private void ShowGroupMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("All loaded scenes"), _group == AllGroup, () => SetGroup(AllGroup));
            foreach (var sceneGroup in _items.GroupBy(item => item.Scene.handle))
            {
                var scene = sceneGroup.First().Scene;
                var sceneKey = SceneKey(scene);
                var sceneName = string.IsNullOrEmpty(scene.name) ? "Untitled scene" : scene.name;
                menu.AddItem(new GUIContent(sceneName), _group == sceneKey, () => SetGroup(sceneKey));
                foreach (var root in sceneGroup.Select(item => item.Root).Where(root => root != null).Distinct())
                {
                    var rootKey = RootKey(root);
                    menu.AddItem(new GUIContent(sceneName + "/" + root.name), _group == rootKey,
                        () => SelectHierarchyRoot(root));
                }
            }
            menu.ShowAsContext();
        }

        private void ShowFilterMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("All scene objects"), _kinds.Count == 0 && !_changedOnly, () =>
            {
                _kinds.Clear();
                _changedOnly = false;
                RenderList();
            });
            menu.AddSeparator("");
            AddKindOption(menu, "Object links", "object");
            AddKindOption(menu, "Text links", "text");
            AddKindOption(menu, "Unlinked", "unlinked");
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Changed only"), _changedOnly, () =>
            {
                _changedOnly = !_changedOnly;
                RenderList();
            });
            menu.ShowAsContext();
        }

        private void AddKindOption(GenericMenu menu, string label, string kind)
        {
            menu.AddItem(new GUIContent(label), _kinds.Contains(kind), () =>
            {
                if (!_kinds.Add(kind)) _kinds.Remove(kind);
                RenderList();
            });
        }

        private void RenderList()
        {
            if (_list == null) return;
            _visible.Clear();
            _visible.AddRange(_items.Where(Allowed));
            _suppressSelection = true;
            _list.Rebuild();
            var selectedIndex = _visible.FindIndex(item => item.GameObject == Selection.activeGameObject);
            if (selectedIndex >= 0) _list.SetSelectionWithoutNotify(new[] { selectedIndex });
            else _list.ClearSelection();
            _suppressSelection = false;
            var groupName = _group == AllGroup ? "SCENE OBJECTS" :
                _group.StartsWith("scene:", StringComparison.Ordinal) ?
                    _items.FirstOrDefault(item => SceneKey(item.Scene) == _group)?.Scene.name :
                    _items.FirstOrDefault(item => item.Root != null && RootKey(item.Root) == _group)?.Root.name;
            _resultHeading.text = (string.IsNullOrEmpty(groupName) ? "SCENE OBJECTS" : groupName.ToUpperInvariant()) +
                "  ·  " + _visible.Count + " SHOWN";
            _filterButton.text = _kinds.Count + (_changedOnly ? 1 : 0) > 0
                ? $"Filters ({_kinds.Count + (_changedOnly ? 1 : 0)}) ▾" : "Filters ▾";
            _filterButton.EnableInClassList("selected", _kinds.Count > 0 || _changedOnly);
            _groupPopup.text = _group == AllGroup ? "All loaded scenes ▾" : "Hierarchy group ▾";
            var linked = _items.Count(item => item.ObjectLink != null);
            var texts = _items.Count(item => item.TextLink != null);
            _summaryTitle.text = SceneManager.sceneCount == 1 && _items.Count > 0
                ? _items[0].Scene.name : "Loaded scenes";
            _summary.text = $"{_items.Count} GameObjects · {linked} Object Links · {texts} Text Links · " +
                $"{_items.Count(item => item.ObjectLink == null && item.TextLink == null)} unlinked";
            _languageBadge.text = "Game language: " + (string.IsNullOrEmpty(GrimoireSettings.Locale)
                ? GrimoireGameLanguages.SourceLanguageLabel
                : GrimoireGameLanguages.DisplayName(GrimoireSettings.Locale));
            _footer.text = $"{_visible.Count} shown in loaded scenes  ·  Game language: " +
                (string.IsNullOrEmpty(GrimoireSettings.Locale)
                    ? GrimoireGameLanguages.SourceLanguageLabel
                    : GrimoireGameLanguages.DisplayName(GrimoireSettings.Locale));
            RefreshDetail();
        }

        private bool Allowed(SceneItem item)
        {
            if (_group != AllGroup && _group != SceneKey(item.Scene) &&
                (item.Root == null || _group != RootKey(item.Root))) return false;
            if (_kinds.Count > 0 && !(_kinds.Contains("object") && item.ObjectLink != null ||
                _kinds.Contains("text") && item.TextLink != null ||
                _kinds.Contains("unlinked") && item.ObjectLink == null && item.TextLink == null)) return false;
            if (_changedOnly && !item.IsDirty) return false;
            if (string.IsNullOrWhiteSpace(_search)) return true;
            var query = _search.Trim();
            return Contains(item.Path, query) || Contains(item.Scene.name, query) ||
                Contains(item.ObjectLink?.ObjectKey, query) ||
                Contains(item.TextLink?.TextCode, query) ||
                MatchesObjectValues(item.ObjectLink, query) ||
                Contains(item.TextLink != null
                    ? item.TextLink.GetPreviewText(GrimoireTextLinkStore.ResolvePreviewLanguage(item.TextLink))
                    : null, query);
        }

        private static bool MatchesObjectValues(GrimoireObjectLink link, string query)
        {
            if (link == null || !link.HasSnapshot) return false;
            foreach (var field in link.Snapshot.Fields)
            {
                if (field == null) continue;
                if (Contains(field.DisplayLabel, query) || Contains(field.FieldId, query) ||
                    Contains(field.GetDisplayValue(GrimoireSettings.Locale), query) ||
                    field.GetDisplayValues(GrimoireSettings.Locale).Any(value => Contains(value, query)) ||
                    field.References.Any(reference => reference != null &&
                        Contains(reference.GetDisplayName(GrimoireSettings.Locale), query)))
                    return true;
            }
            return false;
        }

        private static bool Contains(string text, string query) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private static VisualElement MakeRow()
        {
            var row = Row("scene-item-row");
            var main = Column("scene-item-main");
            var title = Row("scene-item-title");
            title.Add(new Label { name = "name" });
            title.Add(new Label { name = "badge" });
            main.Add(title);
            main.Add(new Label { name = "meta" });
            row.Add(main);
            var side = Column("scene-item-side");
            side.Add(new Label { name = "type" });
            side.Add(new Label { name = "status" });
            row.Add(side);
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _visible.Count) return;
            var item = _visible[index];
            row.EnableInClassList("selected", item.GameObject == Selection.activeGameObject);
            var name = row.Q<Label>("name");
            name.text = item.GameObject.name;
            name.AddToClassList("scene-item-name");
            var meta = row.Q<Label>("meta");
            meta.text = item.Path + (item.ObjectLink != null && item.ObjectLink.HasKey
                ? "  ·  " + item.ObjectLink.ObjectKey : "");
            meta.AddToClassList("scene-item-meta");
            var badge = row.Q<Label>("badge");
            badge.text = item.ObjectLink != null ? "Object Link" : item.TextLink != null ? "Text Link" : "Unlinked";
            badge.RemoveFromClassList("object");
            badge.RemoveFromClassList("text");
            badge.RemoveFromClassList("unlinked");
            badge.AddToClassList("scene-item-badge");
            badge.AddToClassList(item.ObjectLink != null ? "object" : item.TextLink != null ? "text" : "unlinked");
            var type = row.Q<Label>("type");
            type.text = ItemType(item);
            type.AddToClassList("scene-item-type");
            var status = row.Q<Label>("status");
            status.text = item.IsDirty ? "Changed" : item.TextLink != null ? "Localized" :
                item.ObjectLink != null ? "Linked" : "No Grimoire link";
            status.AddToClassList("scene-item-status");
        }

        private static string ItemType(SceneItem item)
        {
            if (item.ObjectLink != null)
            {
                if (GrimoireObjectKeyResolver.TryGetSummary(GrimoireSettings.GameId,
                    item.ObjectLink.CachedObjectId, item.ObjectLink.ObjectKey, out var summary))
                    return summary.template?.name ?? summary.template?.id ?? "Object";
                return "Object";
            }
            if (item.TextLink != null) return "UI Text";
            var component = item.GameObject.GetComponents<Component>().FirstOrDefault(value =>
                value != null && !(value is Transform));
            return component != null ? component.GetType().Name : "GameObject";
        }

        private void OnListSelection(IEnumerable<object> selection)
        {
            if (_suppressSelection) return;
            var item = selection.OfType<SceneItem>().FirstOrDefault();
            if (item?.GameObject == null) return;
            SelectGameObject(item.GameObject);
        }

        internal void RefreshSelection()
        {
            if (_list == null) return;
            _suppressSelection = true;
            var index = _visible.FindIndex(item => item.GameObject == Selection.activeGameObject);
            if (index >= 0) _list.SetSelectionWithoutNotify(new[] { index });
            else _list.ClearSelection();
            _list.RefreshItems();
            _suppressSelection = false;
            _detailTab = 0;
            RefreshDetail();
            RenderGroups();
        }

        internal void RefreshDetail()
        {
            if (_detail == null) return;
            _detail.Clear();
            var selected = Selection.activeGameObject;
            if (selected == null || !selected.scene.IsValid() || !selected.scene.isLoaded ||
                EditorUtility.IsPersistent(selected))
            {
                _detail.Add(Label("SELECTED GAMEOBJECT", "scene-detail-kind"));
                _detail.Add(Label("Select a scene object", "scene-detail-name"));
                Info("Choose an object in the list or Unity's Hierarchy to see its values and scene binding.");
                return;
            }

            var link = selected.GetComponent<GrimoireObjectLink>();
            var textLink = selected.GetComponent<GrimoireTextLink>();
            _detail.Add(Label("SELECTED IN UNITY HIERARCHY", "scene-detail-kind"));
            _detail.Add(Label(selected.name, "scene-detail-name"));
            var path = _items.FirstOrDefault(item => item.GameObject == selected)?.Path ?? selected.name;
            _detail.Add(Label(selected.scene.name + " / " + path.Replace("/", " / "), "scene-detail-key"));
            var actions = Row("scene-actions");
            actions.Add(new Button(() =>
            {
                Selection.activeGameObject = selected;
                if (SceneView.lastActiveSceneView != null)
                    SceneView.lastActiveSceneView.FrameSelected();
            }) { text = "Focus in Scene" });
            actions.Add(new Button(() =>
            {
                Selection.activeGameObject = selected;
                EditorApplication.ExecuteMenuItem("Window/General/Inspector");
            }) { text = "Open Inspector" });
            _detail.Add(actions);

            if (link != null) RenderObjectDetail(selected, link);
            else if (textLink != null) RenderTextDetail(selected, textLink);
            else RenderUnlinkedDetail(selected);
        }

        private void RenderObjectDetail(GameObject selected, GrimoireObjectLink link)
        {
            var document = _documentFor(link);
            var linkCard = Column("scene-link-card");
            linkCard.Add(Label("GRIMOIRE OBJECT LINK", "scene-detail-kind"));
            var linkTitle = Row("scene-link-title");
            linkTitle.Add(Label(string.IsNullOrEmpty(link.ObjectKey) ? selected.name : link.ObjectKey,
                "scene-link-name"));
            linkTitle.Add(Label("Linked", "scene-linked-badge"));
            linkCard.Add(linkTitle);
            linkCard.Add(Label((document?.@object?.name ?? link.ObjectKey) + " · " +
                (link.ActiveSource == GrimoireObjectDataSource.VersionDatabase
                    ? "Version database" : "Connected snapshot"), "scene-muted"));
            _detail.Add(linkCard);
            var tabs = Row("scene-detail-tabs");
            AddTab(tabs, "Values", 0);
            AddTab(tabs, "Edit", 1);
            AddTab(tabs, "Use in code", 3);
            var more = new Button(ShowObjectMoreMenu) { text = _detailTab == 2 ? "Tasks ▾" :
                _detailTab == 4 ? "Full details ▾" : "More ▾" };
            more.AddToClassList("scene-detail-tab");
            if (_detailTab == 2 || _detailTab == 4) more.AddToClassList("selected");
            tabs.Add(more);
            _detail.Add(tabs);
            var status = _statusFor(link);
            if (!string.IsNullOrEmpty(status)) Info(status);
            if (_detailTab != 0)
            {
                if (document == null && (_detailTab != 3 || !link.HasSnapshot))
                {
                    if (string.IsNullOrEmpty(status)) Info("Loading Grimoire object data…");
                    return;
                }
                var tab = _detailTab;
                var container = new IMGUIContainer(() =>
                {
                    if (Selection.activeGameObject == selected &&
                        (_documentFor(link) != null || tab == 3 && link.HasSnapshot))
                        _drawObjectTab(tab);
                });
                container.AddToClassList("scene-legacy-detail");
                _detail.Add(container);
                return;
            }

            var snapshot = link.HasSnapshot ? link.Snapshot :
                document != null ? GrimoireObjectSnapshotMapper.FromDocument(document) : null;
            if (snapshot == null || snapshot.Fields.Count == 0)
            {
                Info(document == null ? _statusFor(link) ?? "Loading values from Grimoire…" :
                    "This object has no fields.");
            }
            else
            {
                string section = null;
                foreach (var field in snapshot.Fields)
                {
                    if (field == null) continue;
                    var heading = string.IsNullOrEmpty(field.SectionTitle) ? "Values" : field.SectionTitle;
                    if (section != heading)
                    {
                        section = heading;
                        Section(section);
                    }
                    var value = field.IsReference
                        ? string.Join(", ", field.References.Where(reference => reference != null)
                            .Select(reference => reference.GetDisplayName(GrimoireSettings.Locale)))
                        : field.Multiple
                            ? string.Join(", ", field.GetDisplayValues(GrimoireSettings.Locale))
                            : field.GetDisplayValue(GrimoireSettings.Locale);
                    var fieldId = field.FieldId;
                    ValueCard(field.DisplayLabel, string.IsNullOrEmpty(value) ? "Not set" : value,
                        field.FieldType, () =>
                        {
                            GrimoireVariablesRenderer.FocusField(fieldId);
                            _detailTab = 3;
                            RefreshDetail();
                        });
                }
            }
            Section("Scene instance");
            ValueCard("Position", selected.transform.localPosition.ToString(), null);
            ValueCard("Rotation", selected.transform.localEulerAngles.ToString(), null);
            ValueCard("Scale", selected.transform.localScale.ToString(), null);
            if (link.GetComponent<GrimoireTextLink>() != null)
            {
                Section("Text link");
                ValueCard("Preview", link.GetComponent<GrimoireTextLink>().GetPreviewText(
                    GrimoireTextLinkStore.ResolvePreviewLanguage(link.GetComponent<GrimoireTextLink>())), null);
            }
        }

        private void RenderTextDetail(GameObject selected, GrimoireTextLink link)
        {
            var linkCard = Column("scene-link-card");
            linkCard.Add(Label("GRIMOIRE TEXT LINK", "scene-detail-kind"));
            linkCard.Add(Label(link.TextCode, "scene-link-name"));
            _detail.Add(linkCard);
            Section("Game language preview");
            var locale = GrimoireTextLinkStore.ResolvePreviewLanguage(link);
            ValueCard("Language", string.IsNullOrEmpty(locale)
                ? GrimoireGameLanguages.SourceLanguageLabel : GrimoireGameLanguages.DisplayName(locale), null);
            ValueCard("Text", link.GetPreviewText(locale), null);
            ValueCard("Code", link.TextCode, null);
            Info("Change the game language in Settings. The scene preview follows that choice.");
            Section("Scene instance");
            ValueCard("Scene", selected.scene.name, null);
            ValueCard("Position", selected.transform.localPosition.ToString(), null);
        }

        private void RenderUnlinkedDetail(GameObject selected)
        {
            var linkCard = Column("scene-link-card");
            linkCard.Add(Label("NO GRIMOIRE LINK", "scene-detail-kind"));
            linkCard.Add(Label("Ready to bind", "scene-link-name"));
            linkCard.Add(Label("Choose a Grimoire object to show its values here.", "scene-muted"));
            _detail.Add(linkCard);
            var bind = new Button(_openLibrary) { text = "Bind from Content Library" };
            bind.AddToClassList("scene-primary");
            _detail.Add(bind);
            Section("Scene instance");
            ValueCard("Scene", selected.scene.name, null);
            ValueCard("Position", selected.transform.localPosition.ToString(), null);
            ValueCard("Rotation", selected.transform.localEulerAngles.ToString(), null);
            ValueCard("Scale", selected.transform.localScale.ToString(), null);
            Section("Unity components");
            foreach (var component in selected.GetComponents<Component>())
                if (component != null) ValueCard(component.GetType().Name, "Attached", null);
        }

        private void AddTab(VisualElement parent, string text, int tab)
        {
            var button = new Button(() =>
            {
                if (tab == 3) GrimoireVariablesRenderer.FocusField(null);
                _detailTab = tab;
                RefreshDetail();
            }) { text = text };
            button.AddToClassList("scene-detail-tab");
            if (_detailTab == tab) button.AddToClassList("selected");
            parent.Add(button);
        }

        private void ShowObjectMoreMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Tasks"), _detailTab == 2, () =>
            {
                _detailTab = 2;
                RefreshDetail();
            });
            menu.AddItem(new GUIContent("Full details"), _detailTab == 4, () =>
            {
                _detailTab = 4;
                RefreshDetail();
            });
            menu.ShowAsContext();
        }

        private void Section(string title) => _detail.Add(Label(title, "scene-section"));

        private void Info(string message) => _detail.Add(Label(message, "scene-info"));

        private void ValueCard(string name, string value, string type, Action useInCode = null)
        {
            var card = Column("scene-value-card");
            var header = Row("scene-value-header");
            header.Add(Label(name ?? "Field", "scene-value-name"));
            if (!string.IsNullOrEmpty(type)) header.Add(Label(type, "scene-value-type"));
            card.Add(header);
            card.Add(Label(string.IsNullOrEmpty(value) ? "—" : value, "scene-value-text"));
            if (useInCode != null)
                card.Add(new Button(useInCode) { text = "Use in code →" });
            _detail.Add(card);
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

        private static Label Label(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }
    }
}
