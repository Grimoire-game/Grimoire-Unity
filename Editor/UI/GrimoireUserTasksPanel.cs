using System;
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

        private readonly GrimoireTasksPanel _tasksPanel = new GrimoireTasksPanel();

        private GrimoireTask[] _tasks = Array.Empty<GrimoireTask>();
        private UserDirectoryEntry[] _users = Array.Empty<UserDirectoryEntry>();
        private string[] _assigneeKeys = Array.Empty<string>();
        private string[] _assigneeLabels = Array.Empty<string>();
        private int _assigneeFilterIndex;
        private bool _loading;
        private bool _usersLoading;
        private string _error;
        private string _loadedGameId;
        private string _loadedAssigneeKey;
        private int _fetchGeneration;
        private Vector2 _scroll;

        public event Action RepaintNeeded;

        public GrimoireUserTasksPanel()
        {
            _tasksPanel.RepaintNeeded += () => RepaintNeeded?.Invoke();
            _tasksPanel.TaskStatusChanged += (_, __) => FetchTasks(force: true);
        }

        public void Reset()
        {
            _tasks = Array.Empty<GrimoireTask>();
            _users = Array.Empty<UserDirectoryEntry>();
            _assigneeKeys = Array.Empty<string>();
            _assigneeLabels = Array.Empty<string>();
            _assigneeFilterIndex = 0;
            _loading = false;
            _usersLoading = false;
            _error = null;
            _loadedGameId = null;
            _loadedAssigneeKey = null;
            _tasksPanel.ResetStatusFetchState();
        }

        public void Draw()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                EditorGUILayout.HelpBox("Sign in and choose a company and game to view tasks.", MessageType.Info);
                return;
            }

            EnsureUsers();
            DrawFilterBar();

            if (_loading)
            {
                EditorGUILayout.LabelField("Loading tasks...", EditorStyles.centeredGreyMiniLabel);
            }

            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
                if (GUILayout.Button("Retry"))
                {
                    FetchTasks(force: true);
                }
            }

            EnsureTasks();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            _tasksPanel.Draw(GrimoireSettings.GameId, _tasks, showHeader: false);
            EditorGUILayout.EndScrollView();
        }

        private void DrawFilterBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField("Assignee", GUILayout.Width(56));
            using (new EditorGUI.DisabledScope(_usersLoading || _assigneeLabels.Length == 0))
            {
                EditorGUI.BeginChangeCheck();
                _assigneeFilterIndex = EditorGUILayout.Popup(_assigneeFilterIndex, _assigneeLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    FetchTasks(force: true);
                }
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_loading))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(64)))
                {
                    FetchTasks(force: true);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void EnsureUsers()
        {
            var gameId = GrimoireSettings.GameId;
            if (string.IsNullOrEmpty(gameId) || _usersLoading || _loadedGameId == gameId && _users.Length > 0)
            {
                return;
            }

            FetchUsers(gameId);
        }

        private void EnsureTasks()
        {
            var gameId = GrimoireSettings.GameId;
            if (string.IsNullOrEmpty(gameId) || _loading)
            {
                return;
            }

            var assigneeKey = GetSelectedAssigneeKey();
            if (_loadedGameId == gameId && _loadedAssigneeKey == assigneeKey)
            {
                return;
            }

            FetchTasks(force: false);
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
            _usersLoading = true;
            _error = null;
            RepaintNeeded?.Invoke();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (!hasSession)
            {
                _usersLoading = false;
                _error = "Your Grimoire session has expired. Sign in again.";
                RepaintNeeded?.Invoke();
                return;
            }

            var result = await GrimoireApiClient.ListUsersAsync(gameId);
            _usersLoading = false;

            if (!result.Success)
            {
                _users = Array.Empty<UserDirectoryEntry>();
                _loadedGameId = gameId;
                BuildAssigneeFilterOptions();
                FetchTasks(force: true);
                RepaintNeeded?.Invoke();
                return;
            }

            _users = result.Data ?? Array.Empty<UserDirectoryEntry>();
            _loadedGameId = gameId;
            BuildAssigneeFilterOptions();
            FetchTasks(force: true);
            RepaintNeeded?.Invoke();
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
                _loadedAssigneeKey == assigneeKey &&
                !_loading)
            {
                return;
            }

            var generation = ++_fetchGeneration;
            _loading = true;
            _error = null;
            RepaintNeeded?.Invoke();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (generation != _fetchGeneration)
            {
                return;
            }

            if (!hasSession)
            {
                _loading = false;
                _error = "Your Grimoire session has expired. Sign in again.";
                RepaintNeeded?.Invoke();
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
                RepaintNeeded?.Invoke();
                return;
            }

            _tasks = result.Data ?? Array.Empty<GrimoireTask>();
            _loadedGameId = gameId;
            _loadedAssigneeKey = assigneeKey;
            RepaintNeeded?.Invoke();
        }
    }
}
