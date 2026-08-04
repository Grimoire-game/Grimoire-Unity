using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// The Grimoire object widget: select a GameObject carrying a
    /// <see cref="GrimoireObjectLink"/> and this window shows the linked
    /// object's data (Object View Document) plus its attached tasks.
    ///
    /// Loading pipeline per selection: resolve the link's key to a UUID
    /// (component cache first, then <see cref="GrimoireObjectKeyResolver"/>),
    /// then fetch GET /api/v1/objects/{id}, which carries the tasks too.
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

        // Selection can change while a fetch is in flight; responses tagged
        // with an old generation are dropped instead of overwriting the newer
        // object.
        private int _loadGeneration;

        private GrimoireTasksPanel _tasksPanel;

        [MenuItem("Window/Grimoire/Object Widget 2")]
        public static void Open()
        {
            var window = GetWindow<GrimoireWidgetWindow>("Grimoire Object");
            window.minSize = new Vector2(360, 300);
            window.Show();
        }

        /// <summary>Opens the window focused on one specific link (used by the inspector).</summary>
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
            _tasksPanel.TaskUpdated += ReloadCurrent;

            Selection.selectionChanged += OnSelectionChanged;
            GrimoireAuthSession.Changed += Repaint;
            GrimoireObjectViewRenderer.RepaintNeeded += Repaint;

            _settingsOpen = !GrimoireSettings.IsConfigured;

            OnSelectionChanged();
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            GrimoireAuthSession.Changed -= Repaint;
            GrimoireObjectViewRenderer.RepaintNeeded -= Repaint;
        }

        private void OnSelectionChanged()
        {
            var active = Selection.activeGameObject;
            var link = active != null ? active.GetComponent<GrimoireObjectLink>() : null;

            if (link == null)
            {
                // Keep showing the last object rather than blanking: designers
                // click around the hierarchy constantly and losing the widget
                // content on every unlinked click would make it useless.
                Repaint();
                return;
            }

            if (link == _link && _document != null)
            {
                return;
            }

            LoadLink(link);
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
                _error = "Configure the API base URL, game id and API key in Settings first.";
                _settingsOpen = true;
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

            // A cached UUID can go stale when the object was deleted and the
            // key was reused, or the cache simply predates a migration. One
            // re-resolve pass covers both.
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
                FinishWithError(view.Error);
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

        // -----------------------------------------------------------------
        // GUI
        // -----------------------------------------------------------------

        private void OnGUI()
        {
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

            var title = _document?.@object?.name
                        ?? (_link != null && _link.HasKey ? _link.ObjectKey : "No object");
            GUILayout.Label(title, EditorStyles.boldLabel);

            GUILayout.FlexibleSpace();

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
                if (GUILayout.Button("Sign out", EditorStyles.toolbarButton))
                {
                    GrimoireAuthSession.SignOut();
                }
            }
            else if (GUILayout.Button("Sign in...", EditorStyles.toolbarButton))
            {
                GrimoireLoginWindow.Open();
            }

            _settingsOpen = GUILayout.Toggle(_settingsOpen, "Settings", EditorStyles.toolbarButton);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Grimoire API", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            var apiBaseUrl = EditorGUILayout.TextField(
                new GUIContent("API base URL", "Default: " + GrimoireSettings.DefaultApiBaseUrl),
                GrimoireSettings.ApiBaseUrl);
            var gameId = EditorGUILayout.TextField(new GUIContent("Game ID", "UUID of the game"), GrimoireSettings.GameId);
            var apiKey = EditorGUILayout.TextField(
                new GUIContent("API key", "Company API key (loca_key_...) with objects:read and tasks:read scopes"),
                GrimoireSettings.ApiKey);
            var apiSecret = EditorGUILayout.PasswordField(
                new GUIContent("API secret", "Shown once when the key is created"),
                GrimoireSettings.ApiSecret);
            var locale = EditorGUILayout.TextField(
                new GUIContent("Locale", "Language code for translatable fields; empty shows source text"),
                GrimoireSettings.Locale);

            if (EditorGUI.EndChangeCheck())
            {
                GrimoireSettings.ApiBaseUrl = apiBaseUrl;
                GrimoireSettings.GameId = gameId;
                GrimoireSettings.ApiKey = apiKey;
                GrimoireSettings.ApiSecret = apiSecret;
                GrimoireSettings.Locale = locale;
                GrimoireObjectKeyResolver.InvalidateCache();
            }

            if (!GrimoireSettings.IsConfigured)
            {
                EditorGUILayout.HelpBox(
                    "Enter the game id plus an API key and secret. Keys are managed in the Grimoire platform under Settings > API Keys.",
                    MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }
    }
}
