using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Draws the tasks and notes attached to an object (the `tasks` array of
    /// the Object View Document) and, for a signed-in user, lets them complete
    /// a task or move it to any workflow status.
    ///
    /// Status keys are game-specific, so the panel fetches
    /// GET /api/v1/statuses?domain=tasks once per game and caches it for the
    /// editor session.
    /// </summary>
    public class GrimoireTasksPanel
    {
        private static readonly Dictionary<string, WorkflowStatusEntry[]> StatusCache =
            new Dictionary<string, WorkflowStatusEntry[]>();

        /// <summary>EnsureStatuses runs from OnGUI; without a cooldown a failing fetch would retry every repaint.</summary>
        private const double StatusRetryCooldownSeconds = 30;

        public static void InvalidateStatusCache()
        {
            StatusCache.Clear();
        }

        /// <summary>Clears cached statuses so a settings change can refetch immediately.</summary>
        public void ResetStatusFetchState()
        {
            InvalidateStatusCache();
            _statusFetchFailedAt.Clear();
            _statusesLoading = false;
            _error = null;
        }

        private readonly HashSet<string> _pendingTasks = new HashSet<string>();
        private readonly Dictionary<string, double> _statusFetchFailedAt = new Dictionary<string, double>();
        private string _statusesGameId;
        private bool _statusesLoading;
        private string _error;

        /// <summary>Raised after a successful status update with the new status key.</summary>
        public event Action<string, string> TaskStatusChanged;

        /// <summary>Raised when async work finished and the window should repaint.</summary>
        public event Action RepaintNeeded;

        public void Draw(string gameId, GrimoireTask[] tasks, bool showHeader = true)
        {
            if (showHeader)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            }
            else
            {
                EditorGUILayout.BeginVertical();
            }

            if (showHeader)
            {
                var openCount = tasks?.Count(task => task.is_task && !IsDone(gameId, task.status)) ?? 0;
                EditorGUILayout.LabelField(
                    tasks == null || tasks.Length == 0
                        ? "Tasks"
                        : $"Tasks ({tasks.Length}, {openCount} open)",
                    EditorStyles.boldLabel);
            }

            if (tasks == null || tasks.Length == 0)
            {
                EditorGUILayout.LabelField(
                    showHeader
                        ? "No tasks or notes are attached to this object."
                        : "No tasks match the current filter.",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            EnsureStatusesScheduled(gameId);

            if (!GrimoireAuthSession.IsSignedIn)
            {
                EditorGUILayout.HelpBox("Sign in with your Grimoire account to update task statuses.", MessageType.Info);
            }
            else if (_statusesLoading)
            {
                EditorGUILayout.LabelField("Loading task statuses...", EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }

            foreach (var task in tasks)
            {
                DrawTask(gameId, task);
            }

            EditorGUILayout.EndVertical();
        }

        private void EnsureStatusesScheduled(string gameId)
        {
            if (string.IsNullOrEmpty(gameId) || !GrimoireAuthSession.IsSignedIn ||
                StatusCache.ContainsKey(gameId) ||
                (_statusesLoading && _statusesGameId == gameId))
            {
                return;
            }

            if (_statusFetchFailedAt.TryGetValue(gameId, out var failedAt) &&
                EditorApplication.timeSinceStartup - failedAt < StatusRetryCooldownSeconds)
            {
                return;
            }

            EditorApplication.delayCall += () => EnsureStatuses(gameId);
        }

        private void DrawTask(string gameId, GrimoireTask task)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(task.is_task ? "Task" : "Note", EditorStyles.miniBoldLabel, GUILayout.Width(36));

            if (task.is_task)
            {
                DrawStatusPill(gameId, task.status);
            }

            GUILayout.FlexibleSpace();

            if (!string.IsNullOrEmpty(task.due_by) && DateTime.TryParse(task.due_by, out var dueBy))
            {
                var overdue = task.is_task && !IsDone(gameId, task.status) && dueBy < DateTime.Now;
                var style = new GUIStyle(EditorStyles.miniLabel);
                if (overdue)
                {
                    style.normal.textColor = new Color(0.95f, 0.45f, 0.4f);
                }

                EditorGUILayout.LabelField($"Due {dueBy:yyyy-MM-dd}", style, GUILayout.Width(100));
            }

            EditorGUILayout.EndHorizontal();

            var body = task.Body;
            if (!string.IsNullOrEmpty(body))
            {
                EditorGUILayout.LabelField(body, EditorStyles.wordWrappedLabel);
            }

            EditorGUILayout.BeginHorizontal();

            var meta = new List<string>();
            if (task.assignees != null && task.assignees.Length > 0)
            {
                meta.Add(task.assignees.Length == 1 ? "1 assignee" : $"{task.assignees.Length} assignees");
            }

            if (task.reply_count > 0)
            {
                meta.Add(task.reply_count == 1 ? "1 reply" : $"{task.reply_count} replies");
            }

            if (!string.IsNullOrEmpty(task.updated_at) && DateTime.TryParse(task.updated_at, out var updatedAt))
            {
                meta.Add($"updated {updatedAt:yyyy-MM-dd HH:mm}");
            }

            if (meta.Count > 0)
            {
                EditorGUILayout.LabelField(string.Join("  ·  ", meta), EditorStyles.miniLabel);
            }

            GUILayout.FlexibleSpace();

            if (task.is_task)
            {
                DrawTaskActions(gameId, task);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawTaskActions(string gameId, GrimoireTask task)
        {
            var statuses = GetStatuses(gameId);
            var pending = _pendingTasks.Contains(task.id);
            var canUpdate = GrimoireAuthSession.IsSignedIn &&
                            statuses != null &&
                            statuses.Length > 0;

            using (new EditorGUI.DisabledScope(!canUpdate || pending))
            {
                if (statuses != null && statuses.Length > 0)
                {
                    var keys = statuses.Select(status => status.key).ToArray();
                    var labels = statuses.Select(status => status.label ?? status.key).ToArray();
                    var currentIndex = Array.FindIndex(keys, key => key == task.status);

                    // Keep the current status visible even if it is not in the workflow list.
                    if (currentIndex < 0 && !string.IsNullOrEmpty(task.status))
                    {
                        keys = new[] { task.status }.Concat(keys).ToArray();
                        labels = new[] { task.status }.Concat(labels).ToArray();
                        currentIndex = 0;
                    }
                    else if (currentIndex < 0)
                    {
                        currentIndex = 0;
                    }

                    var pickedIndex = EditorGUILayout.Popup(currentIndex, labels, GUILayout.Width(110));
                    if (pickedIndex != currentIndex && pickedIndex >= 0 && pickedIndex < keys.Length)
                    {
                        UpdateStatus(gameId, task, keys[pickedIndex]);
                    }

                    var doneStatus = PickDoneStatus(statuses);
                    if (doneStatus != null && task.status != doneStatus.key &&
                        GUILayout.Button(new GUIContent("Complete", $"Set status to '{doneStatus.label}'"),
                            EditorStyles.miniButton, GUILayout.Width(70)))
                    {
                        UpdateStatus(gameId, task, doneStatus.key);
                    }
                }
            }

            if (pending)
            {
                EditorGUILayout.LabelField("Saving...", EditorStyles.miniLabel, GUILayout.Width(56));
            }
        }

        private void DrawStatusPill(string gameId, string statusKey)
        {
            if (string.IsNullOrEmpty(statusKey))
            {
                return;
            }

            var entry = GetStatuses(gameId)?.FirstOrDefault(status => status.key == statusKey);
            var label = entry?.label ?? statusKey;

            var style = new GUIStyle(EditorStyles.miniBoldLabel);
            if (entry?.color != null && ColorUtility.TryParseHtmlString(entry.color, out var color))
            {
                style.normal.textColor = color;
            }

            EditorGUILayout.LabelField(label, style, GUILayout.Width(90));
        }

        private async void UpdateStatus(string gameId, GrimoireTask task, string statusKey)
        {
            if (_pendingTasks.Contains(task.id) || task.status == statusKey)
            {
                return;
            }

            _pendingTasks.Add(task.id);
            _error = null;
            RequestRepaint();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (!hasSession)
            {
                _pendingTasks.Remove(task.id);
                _error = "Your Grimoire session has expired. Sign in again to update tasks.";
                RequestRepaint();
                return;
            }

            var result = await GrimoireApiClient.UpdateTaskStatusAsync(gameId, task.id, statusKey);
            _pendingTasks.Remove(task.id);

            if (!result.Success)
            {
                _error = result.Error;
                RequestRepaint();
                return;
            }

            task.status = statusKey;
            TaskStatusChanged?.Invoke(task.id, statusKey);
        }

        /// <summary>
        /// The "complete" target: the `done` key when the game has one, else a
        /// locked status (Grimoire locks its done-equivalent), else the last
        /// status in workflow order.
        /// </summary>
        private static WorkflowStatusEntry PickDoneStatus(WorkflowStatusEntry[] statuses)
        {
            return statuses.FirstOrDefault(status => status.key == "done")
                ?? statuses.FirstOrDefault(status => status.locked)
                ?? statuses.OrderBy(status => status.order).LastOrDefault();
        }

        private bool IsDone(string gameId, string statusKey)
        {
            var statuses = GetStatuses(gameId);
            if (statuses == null)
            {
                return false;
            }

            var done = PickDoneStatus(statuses);
            return done != null && done.key == statusKey;
        }

        private WorkflowStatusEntry[] GetStatuses(string gameId)
        {
            return StatusCache.TryGetValue(gameId, out var statuses) ? statuses : null;
        }

        private async void EnsureStatuses(string gameId)
        {
            if (string.IsNullOrEmpty(gameId) || !GrimoireAuthSession.IsSignedIn ||
                StatusCache.ContainsKey(gameId) ||
                (_statusesLoading && _statusesGameId == gameId))
            {
                return;
            }

            if (_statusFetchFailedAt.TryGetValue(gameId, out var failedAt) &&
                EditorApplication.timeSinceStartup - failedAt < StatusRetryCooldownSeconds)
            {
                return;
            }

            _statusesLoading = true;
            _statusesGameId = gameId;

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (!hasSession)
            {
                _statusesLoading = false;
                _error = "Your Grimoire session has expired. Sign in again to load task statuses.";
                RequestRepaint();
                return;
            }

            var result = await GrimoireApiClient.GetTaskStatusesAsync(gameId);
            _statusesLoading = false;

            if (result.Success)
            {
                StatusCache[gameId] = result.Data;
                _statusFetchFailedAt.Remove(gameId);
            }
            else
            {
                // Without statuses the panel still lists tasks; only the
                // actions are missing, so report it softly.
                _statusFetchFailedAt[gameId] = EditorApplication.timeSinceStartup;
                _error = $"Could not load task statuses: {result.Error}";
            }

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
