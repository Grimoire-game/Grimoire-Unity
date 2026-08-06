using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// The Grimoire object widget: select a GameObject carrying a
    /// <see cref="GrimoireObjectLink"/> and this window shows the linked
    /// object's data (Object View Document) plus its attached tasks.
    ///
    /// Setup follows the Grimoire platform: sign in, pick a game, then work.
    /// </summary>
    public class GrimoireWidgetWindow : EditorWindow
    {
        private GrimoireObjectLink _link;
        private ObjectViewDocument _document;
        private string _statusMessage;
        private string _error;
        private bool _loading;
        private bool _settingsOpen;
        private Vector2 _scroll;

        private int _loadGeneration;

        private GrimoireTasksPanel _tasksPanel;
        private GrimoireSetupPanel _setupPanel;

        [MenuItem("Window/Grimoire/Object Widget 2")]
        public static void Open()
        {
            var window = GetWindow<GrimoireWidgetWindow>("Grimoire Object");
            window.minSize = new Vector2(420, 520);
            window.Show();
        }

        public static void ShowAndLoad(GrimoireObjectLink link)
        {
            Open();
            var window = GetWindow<GrimoireWidgetWindow>();
            window.LoadLink(link);
        }

        private void OnEnable()
        {
            _tasksPanel = new GrimoireTasksPanel();
            _tasksPanel.RepaintNeeded += Repaint;
            _tasksPanel.TaskStatusChanged += OnTaskStatusChanged;

            _setupPanel = new GrimoireSetupPanel();
            _setupPanel.RepaintNeeded += Repaint;
            _setupPanel.SetupCompleted += OnSetupCompleted;

            Selection.selectionChanged += OnSelectionChanged;
            GrimoireAuthSession.Changed += OnAuthChanged;
            GrimoireSettings.Changed += OnSettingsChanged;
            GrimoireObjectViewRenderer.RepaintNeeded += Repaint;

            OnSelectionChanged();
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            GrimoireAuthSession.Changed -= OnAuthChanged;
            GrimoireSettings.Changed -= OnSettingsChanged;
            GrimoireObjectViewRenderer.RepaintNeeded -= Repaint;
        }

        private void OnAuthChanged()
        {
            _tasksPanel.ResetStatusFetchState();

            if (!GrimoireAuthSession.IsSignedIn)
            {
                _setupPanel.OnSignedOut();
                _document = null;
            }

            Repaint();
        }

        private void OnSettingsChanged()
        {
            _tasksPanel.ResetStatusFetchState();
            Repaint();
        }

        private void OnSetupCompleted()
        {
            _error = null;
            ReloadCurrent();
        }

        private void OnSelectionChanged()
        {
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
            if (_setupPanel.NeedsSetup)
            {
                if (_setupPanel.CurrentPhase == SetupPhase.Login)
                {
                    _setupPanel.Draw(new Rect(0f, 0f, position.width, position.height));
                }
                else
                {
                    DrawToolbar();
                    var toolbarHeight = EditorStyles.toolbar.fixedHeight;
                    _setupPanel.Draw(new Rect(0f, toolbarHeight, position.width, position.height - toolbarHeight));
                }

                return;
            }

            DrawToolbar();

            if (_settingsOpen)
            {
                DrawSettings();
            }

            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }

            if (_loading)
            {
                EditorGUILayout.LabelField(_statusMessage ?? "Loading...", EditorStyles.centeredGreyMiniLabel);
            }

            if (_document == null)
            {
                if (!_loading && string.IsNullOrEmpty(_error))
                {
                    EditorGUILayout.HelpBox(
                        "Select a GameObject with a Grimoire Object Link component to view its Grimoire data.",
                        MessageType.Info);
                }

                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            GrimoireObjectViewRenderer.Draw(_document);
            _tasksPanel.Draw(GrimoireSettings.GameId, _document.tasks);

            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GrimoireSettings.IsConfigured)
            {
                var gameLabel = string.IsNullOrEmpty(GrimoireSettings.GameName)
                    ? "Game"
                    : GrimoireSettings.GameName;
                GUILayout.Label(gameLabel, EditorStyles.boldLabel);
                GUILayout.Label("·", EditorStyles.miniLabel);

                var title = _document?.@object?.name
                            ?? (_link != null && _link.HasKey ? _link.ObjectKey : "No object");
                GUILayout.Label(title, EditorStyles.label);
            }
            else
            {
                GUILayout.Label("Grimoire", EditorStyles.boldLabel);
            }

            GUILayout.FlexibleSpace();

            if (GrimoireSettings.IsConfigured)
            {
                using (new EditorGUI.DisabledScope(_link == null || _loading))
                {
                    if (GUILayout.Button(new GUIContent("Refresh", "Re-fetch this object from Grimoire"), EditorStyles.toolbarButton))
                    {
                        GrimoireObjectKeyResolver.InvalidateCache(GrimoireSettings.GameId);
                        ReloadCurrent();
                    }
                }

                if (GrimoireAuthSession.IsSignedIn)
                {
                    GUILayout.Label(GrimoireAuthSession.UserName, EditorStyles.miniLabel);
                    if (GUILayout.Button("Change game", EditorStyles.toolbarButton))
                    {
                        _setupPanel.BeginGameSelection();
                    }

                    if (GUILayout.Button("Sign out", EditorStyles.toolbarButton))
                    {
                        GrimoireAuthSession.SignOut();
                    }
                }
            }

            _settingsOpen = GUILayout.Toggle(_settingsOpen, "Settings", EditorStyles.toolbarButton);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            var apiBaseUrl = EditorGUILayout.TextField(
                new GUIContent("API base URL", "Default: " + GrimoireSettings.DefaultApiBaseUrl),
                GrimoireSettings.ApiBaseUrl);
            var locale = EditorGUILayout.TextField(
                new GUIContent("Locale", "Language code for translatable fields; empty shows source text"),
                GrimoireSettings.Locale);

            if (EditorGUI.EndChangeCheck())
            {
                GrimoireSettings.ApiBaseUrl = apiBaseUrl;
                GrimoireSettings.Locale = locale;
                GrimoireObjectKeyResolver.InvalidateCache();
            }

            if (GrimoireSettings.HasGameId)
            {
                EditorGUILayout.LabelField("Game", GrimoireSettings.GameName, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Game ID", GrimoireSettings.GameId, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }
    }
}
