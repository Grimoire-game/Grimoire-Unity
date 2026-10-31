using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Lists all tasks for the selected game via GET /api/v1/tasks, with an
    /// assignee filter (all users, me, or a specific game member).
    /// </summary>
    public class GrimoireUserTasksPanel
    {
        private const string AllAssigneesKey = "";
        private const string MyTasksKey = "__me__";
        private const string AllOpenStatusesKey = "";

        private readonly GrimoireTasksPanel _tasksPanel = new GrimoireTasksPanel();

        private GrimoireTask[] _tasks = Array.Empty<GrimoireTask>();
        private UserDirectoryEntry[] _users = Array.Empty<UserDirectoryEntry>();
        private string[] _assigneeKeys = Array.Empty<string>();
        private string[] _assigneeLabels = Array.Empty<string>();
        private string[] _statusFilterKeys = Array.Empty<string>();
        private string[] _statusFilterLabels = Array.Empty<string>();
        private int _assigneeFilterIndex;
        private int _statusFilterIndex;
        private bool _loading;
        private bool _usersLoading;
        private string _error;
        private string _loadedGameId;
        private string _loadedAssigneeKey;
        private string _usersFetchedForGameId;
        private int _fetchGeneration;
        private Vector2 _scroll;
        private Vector2 _detailsScroll;
        private string _search = "";
        private string _selectedTaskId;

        public event Action RepaintNeeded;

        public GrimoireUserTasksPanel()
        {
            _tasksPanel.RepaintNeeded += RequestRepaint;
            _tasksPanel.TaskStatusChanged += (_, __) => RequestTasksRefresh();
            BuildAssigneeFilterOptions();
        }

        public void Reset()
        {
            _tasks = Array.Empty<GrimoireTask>();
            _users = Array.Empty<UserDirectoryEntry>();
            _assigneeFilterIndex = 0;
            _statusFilterIndex = 0;
            _loading = false;
            _usersLoading = false;
            _error = null;
            _loadedGameId = null;
            _loadedAssigneeKey = null;
            _usersFetchedForGameId = null;
            _fetchGeneration++;
            BuildAssigneeFilterOptions();
            BuildStatusFilterOptions();
            _tasksPanel.ResetStatusFetchState();
        }

        /// <summary>
        /// Re-fetch users, workflow statuses, and tasks from the server, keeping current filters.
        /// </summary>
        public void Refresh()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                return;
            }

            _usersFetchedForGameId = null;
            _tasksPanel.ResetStatusFetchState();
            RequestTasksRefresh();
        }

        /// <summary>
        /// Schedule loading users and tasks outside the current IMGUI pass.
        /// </summary>
        public void Activate()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                return;
            }

            EditorApplication.delayCall -= DeferredActivate;
            EditorApplication.delayCall += DeferredActivate;
        }

        public void Draw(float width, float height)
        {
            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireEditorStyles.DrawInfoBox("Sign in and choose a company and game to view tasks.");
                return;
            }

            GrimoireWorkflowGUI.Header("Tasks", "Find assigned work, inspect its linked content, and update its status.");
            DrawWorkspaceFilters();

            if (_usersLoading || _loading)
            {
                EditorGUILayout.LabelField(
                    _usersLoading ? "Loading team members..." : "Loading tasks...",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }

            if (!string.IsNullOrEmpty(_error))
            {
                GrimoireEditorStyles.DrawErrorBox(_error);
                if (GUILayout.Button("Retry", GrimoireEditorStyles.PrimaryButtonStyle, GUILayout.Width(80)))
                {
                    RequestTasksRefresh();
                }
            }

            var tasks = GetVisibleTasks();
            var paneHeight = Mathf.Max(220f, height - 190f);
            var narrow = GrimoireWorkflowGUI.IsNarrow(width);
            if (!narrow) EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(narrow ? GUILayout.ExpandWidth(true) : GUILayout.Width(Mathf.Max(230f, width * 0.57f)));
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(narrow ? 215f : paneHeight));
            GrimoireWorkflowGUI.Section($"Open tasks · {tasks.Count(MatchesSearch)}");
            if (!tasks.Any(task => task.id == _selectedTaskId && MatchesSearch(task)))
                _selectedTaskId = tasks.FirstOrDefault(MatchesSearch)?.id;
            foreach (var task in tasks)
            {
                if (!MatchesSearch(task)) continue;
                var assignee = AssigneeName(task);
                if (GrimoireWorkflowGUI.Row(
                        string.IsNullOrEmpty(task.Body) ? "(untitled task)" : task.Body,
                        (task.entity_type ?? "Content") + " · " + (task.entity_id ?? "No link"),
                        assignee,
                        StatusLabel(task.status),
                        task.id == _selectedTaskId))
                    _selectedTaskId = task.id;
            }
            if (!tasks.Any(MatchesSearch)) GrimoireEditorStyles.DrawInfoBox("No open tasks match these filters.");
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandWidth(true));
            _detailsScroll = EditorGUILayout.BeginScrollView(_detailsScroll, GUILayout.Height(narrow ? Mathf.Max(220f, paneHeight - 215f) : paneHeight));
            DrawWorkspaceDetail(tasks);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            if (!narrow) EditorGUILayout.EndHorizontal();
        }

        private void DrawWorkspaceFilters()
        {
            BuildStatusFilterOptions();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Search", GUILayout.Width(44f));
            _search = EditorGUILayout.TextField(_search ?? "", GUILayout.MinWidth(90f));
            using (new EditorGUI.DisabledScope(_usersLoading || _assigneeLabels.Length == 0))
            {
                var next = EditorGUILayout.Popup(_assigneeFilterIndex, _assigneeLabels, GUILayout.Width(130f));
                if (next != _assigneeFilterIndex)
                {
                    _assigneeFilterIndex = next;
                    RequestTasksRefresh();
                }
            }
            if (_statusFilterLabels.Length > 0)
                _statusFilterIndex = EditorGUILayout.Popup(_statusFilterIndex, _statusFilterLabels, GUILayout.Width(125f));
            if (GUILayout.Button("Refresh", GUILayout.Width(65f))) Refresh();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(5f);
        }

        private bool MatchesSearch(GrimoireTask task)
        {
            if (string.IsNullOrWhiteSpace(_search)) return true;
            var query = _search.Trim();
            return (task.Body ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (task.entity_id ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (task.entity_type ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string AssigneeName(GrimoireTask task)
        {
            if (task.assignees == null || task.assignees.Length == 0) return "Unassigned";
            if (task.assignees.Length > 1) return $"{task.assignees.Length} assignees";
            var id = task.assignees[0];
            if (id == GrimoireAuthSession.UserId) return "You";
            return _users.FirstOrDefault(user => user.id == id)?.DisplayName ?? "Assigned";
        }

        private static string StatusLabel(string key)
        {
            var statuses = GrimoireTasksPanel.GetCachedStatuses(GrimoireSettings.GameId);
            return statuses?.FirstOrDefault(status => status.key == key)?.label ?? key ?? "Open";
        }

        private void DrawWorkspaceDetail(GrimoireTask[] tasks)
        {
            var visible = Array.FindAll(tasks, MatchesSearch);
            var task = Array.Find(visible, item => item.id == _selectedTaskId) ??
                       (visible.Length > 0 ? visible[0] : null);
            if (task == null)
            {
                GrimoireWorkflowGUI.DetailHeading("Task details", "Select a task", null);
                return;
            }
            GrimoireWorkflowGUI.DetailHeading("Task details", task.Body, task.entity_id);
            GrimoireWorkflowGUI.DetailLine("Status", StatusLabel(task.status));
            GrimoireWorkflowGUI.DetailLine("Assignee", AssigneeName(task));
            GrimoireWorkflowGUI.DetailLine("Linked type", task.entity_type);
            if (!string.IsNullOrEmpty(task.due_by))
                GrimoireWorkflowGUI.DetailLine("Due", GrimoireExportImporterService.FormatDate(task.due_by));
            GrimoireWorkflowGUI.Section("Update status");
            _tasksPanel.DrawActions(GrimoireSettings.GameId, task);
            GUILayout.Space(8f);
            if (!string.IsNullOrEmpty(task.entity_id) && GUILayout.Button("Open linked content"))
                OpenLinkedContent(task);
            if (task.reply_count > 0)
                GrimoireWorkflowGUI.Note($"{task.reply_count} replies on this task.");
        }

        private static void OpenLinkedContent(GrimoireTask task)
        {
            var id = task.entity_id;
            if (string.IsNullOrEmpty(id)) return;
            foreach (var link in UnityEngine.Object.FindObjectsOfType<GrimoireObjectLink>(true))
            {
                if (link.CachedObjectId != id && link.ObjectKey != id) continue;
                Selection.activeGameObject = link.gameObject;
                EditorGUIUtility.PingObject(link.gameObject);
                GrimoireConnectWindow.ShowAndLoad(link);
                return;
            }
            foreach (var link in UnityEngine.Object.FindObjectsOfType<GrimoireTextLink>(true))
            {
                if (link.CachedStringId != id && link.TextCode != id) continue;
                Selection.activeGameObject = link.gameObject;
                EditorGUIUtility.PingObject(link.gameObject);
                return;
            }
            GrimoireConnectWindow.OpenLibrary(task.entity_type != null &&
                task.entity_type.IndexOf("dialog", StringComparison.OrdinalIgnoreCase) >= 0 ? id : null);
        }

        private GrimoireTask[] GetVisibleTasks()
        {
            var gameId = GrimoireSettings.GameId;
            IEnumerable<GrimoireTask> visible = _tasks.Where(task =>
                task.is_task && !GrimoireTasksPanel.IsTaskDone(gameId, task.status));

            var statusKey = GetSelectedStatusKey();
            if (!string.IsNullOrEmpty(statusKey))
            {
                visible = visible.Where(task => task.status == statusKey);
            }

            return visible.ToArray();
        }

        private string GetSelectedStatusKey()
        {
            if (_statusFilterIndex < 0 || _statusFilterIndex >= _statusFilterKeys.Length)
            {
                return AllOpenStatusesKey;
            }

            return _statusFilterKeys[_statusFilterIndex] ?? AllOpenStatusesKey;
        }

        private void DrawFilterBar()
        {
            if (!GrimoireEditorStyles.BeginCollapsibleSection("tasks-filter", "Filter tasks", defaultExpanded: true))
            {
                return;
            }

            BuildStatusFilterOptions();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Assignee", GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(56));
            using (new EditorGUI.DisabledScope(_usersLoading || _assigneeLabels.Length == 0))
            {
                EditorGUI.BeginChangeCheck();
                _assigneeFilterIndex = EditorGUILayout.Popup(_assigneeFilterIndex, _assigneeLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    RequestTasksRefresh();
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Status", GrimoireEditorStyles.FieldLabelStyle, GUILayout.Width(56));
            using (new EditorGUI.DisabledScope(_statusFilterLabels.Length == 0))
            {
                EditorGUI.BeginChangeCheck();
                _statusFilterIndex = EditorGUILayout.Popup(_statusFilterIndex, _statusFilterLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    RequestRepaint();
                }
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_loading || _usersLoading))
            {
                if (GrimoireEditorStyles.ToolbarButton("Refresh", false, GUILayout.Width(64)))
                {
                    RequestTasksRefresh();
                }
            }

            EditorGUILayout.EndHorizontal();
            GrimoireEditorStyles.EndCollapsibleSection();
        }

        private void DeferredActivate()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                return;
            }

            var gameId = GrimoireSettings.GameId;
            if (_usersFetchedForGameId != gameId)
            {
                FetchUsers(gameId);
                return;
            }

            _tasksPanel.ScheduleStatusLoad(gameId);
            RequestTasksIfNeeded(force: false);
        }

        private void RequestTasksRefresh()
        {
            _loadedGameId = null;
            _loadedAssigneeKey = null;
            EditorApplication.delayCall -= DeferredActivate;
            EditorApplication.delayCall += DeferredActivate;
        }

        private void RequestTasksIfNeeded(bool force)
        {
            var gameId = GrimoireSettings.GameId;
            if (string.IsNullOrEmpty(gameId) || _loading || _usersLoading)
            {
                return;
            }

            var assigneeKey = GetSelectedAssigneeKey();
            if (!force &&
                _loadedGameId == gameId &&
                string.Equals(_loadedAssigneeKey, assigneeKey, StringComparison.Ordinal))
            {
                return;
            }

            FetchTasks(force);
        }

        private string GetSelectedAssigneeKey()
        {
            if (_assigneeFilterIndex < 0 || _assigneeFilterIndex >= _assigneeKeys.Length)
            {
                return AllAssigneesKey;
            }

            return _assigneeKeys[_assigneeFilterIndex] ?? AllAssigneesKey;
        }

        private string GetSelectedUserId()
        {
            var key = GetSelectedAssigneeKey();
            if (key == AllAssigneesKey)
            {
                return null;
            }

            if (key == MyTasksKey)
            {
                return string.IsNullOrEmpty(GrimoireAuthSession.UserId) ? null : GrimoireAuthSession.UserId;
            }

            return key;
        }

        private async void FetchUsers(string gameId)
        {
            if (_usersLoading)
            {
                return;
            }

            _usersLoading = true;
            _error = null;
            RequestRepaint();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (!hasSession)
            {
                _usersLoading = false;
                _error = "Your Grimoire session has expired. Sign in again.";
                RequestRepaint();
                return;
            }

            var result = await GrimoireApiClient.ListUsersAsync(gameId);
            _usersLoading = false;
            _usersFetchedForGameId = gameId;

            if (!result.Success)
            {
                _users = Array.Empty<UserDirectoryEntry>();
            }
            else
            {
                _users = result.Data ?? Array.Empty<UserDirectoryEntry>();
            }

            BuildAssigneeFilterOptions();
            RequestTasksIfNeeded(force: true);
            RequestRepaint();
        }

        private void BuildAssigneeFilterOptions()
        {
            var keys = new System.Collections.Generic.List<string> { AllAssigneesKey, MyTasksKey };
            var labels = new System.Collections.Generic.List<string> { "All assignees", "Assigned to me" };

            foreach (var user in _users.OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (user.id == GrimoireAuthSession.UserId)
                {
                    continue;
                }

                keys.Add(user.id);
                labels.Add(user.DisplayName);
            }

            var previousKey = GetSelectedAssigneeKey();
            _assigneeKeys = keys.ToArray();
            _assigneeLabels = labels.ToArray();

            var restoredIndex = Array.IndexOf(_assigneeKeys, previousKey);
            _assigneeFilterIndex = restoredIndex >= 0 ? restoredIndex : 0;
        }

        private void BuildStatusFilterOptions()
        {
            var gameId = GrimoireSettings.GameId;
            var openStatuses = string.IsNullOrEmpty(gameId)
                ? Array.Empty<WorkflowStatusEntry>()
                : GrimoireTasksPanel.GetOpenWorkflowStatuses(gameId);

            var keys = new System.Collections.Generic.List<string> { AllOpenStatusesKey };
            var labels = new System.Collections.Generic.List<string> { "All open statuses" };

            foreach (var status in openStatuses)
            {
                keys.Add(status.key);
                labels.Add(status.label ?? status.key);
            }

            var previousKey = GetSelectedStatusKey();
            _statusFilterKeys = keys.ToArray();
            _statusFilterLabels = labels.ToArray();

            var restoredIndex = Array.IndexOf(_statusFilterKeys, previousKey);
            _statusFilterIndex = restoredIndex >= 0 ? restoredIndex : 0;
        }

        private async void FetchTasks(bool force)
        {
            var gameId = GrimoireSettings.GameId;
            if (string.IsNullOrEmpty(gameId))
            {
                return;
            }

            var assigneeKey = GetSelectedAssigneeKey();
            if (!force &&
                _loadedGameId == gameId &&
                string.Equals(_loadedAssigneeKey, assigneeKey, StringComparison.Ordinal))
            {
                return;
            }

            if (_loading)
            {
                return;
            }

            var generation = ++_fetchGeneration;
            _loading = true;
            _error = null;
            RequestRepaint();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (generation != _fetchGeneration)
            {
                return;
            }

            if (!hasSession)
            {
                _loading = false;
                _error = "Your Grimoire session has expired. Sign in again.";
                RequestRepaint();
                return;
            }

            var result = await GrimoireApiClient.ListTasksAsync(gameId, GetSelectedUserId());
            if (generation != _fetchGeneration)
            {
                return;
            }

            _loading = false;

            if (!result.Success)
            {
                _error = result.Error;
                RequestRepaint();
                return;
            }

            _tasks = result.Data ?? Array.Empty<GrimoireTask>();
            _loadedGameId = gameId;
            _loadedAssigneeKey = assigneeKey;
            BuildStatusFilterOptions();
            RequestRepaint();
        }

        private void RequestRepaint()
        {
            EditorApplication.delayCall -= InvokeRepaint;
            EditorApplication.delayCall += InvokeRepaint;
        }

        private void InvokeRepaint()
        {
            RepaintNeeded?.Invoke();
        }
    }
}
