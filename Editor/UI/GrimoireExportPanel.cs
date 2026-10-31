using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Export tab: list and download Unity export ZIPs for the selected game,
    /// extracting them into Assets/Grimoire for offline runtime use.
    /// </summary>
    public class GrimoireExportPanel
    {
        private const int VersionsPerPage = 5;

        private Vector2 _scroll;
        private Vector2 _detailsScroll;
        private string _search = "";
        private string _selectedVersionId;
        private ExportVersion[] _versions;
        private bool _loading;
        private bool _downloading;
        private float _downloadProgress;
        private string _downloadStatus;
        private string _status;
        private string _error;
        private int _page;
        private int _loadGeneration;
        private string _loadedGameId;
        private string _downloadingVersionId;

        public event Action RepaintNeeded;

        public void Activate()
        {
            EnsureGame();
            if (GrimoireSettings.IsConfigured && _versions == null && !_loading)
            {
                RefreshAsync();
            }
        }

        /// <summary>Re-fetch export versions from the server.</summary>
        public void Refresh()
        {
            EnsureGame();
            if (GrimoireSettings.IsConfigured && !_loading && !_downloading)
            {
                RefreshAsync();
            }
        }

        public void Draw(float width, float height)
        {
            EnsureGame();
            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Sign in and choose a workspace first to download exports.");
                return;
            }

            GrimoireWorkflowGUI.Header("Versions", "Choose the database version used by the Unity runtime.");
            var importedId = GetImportedVersionId();
            var imported = _versions?.FirstOrDefault(version => version.id == importedId);
            var hasImport = Directory.Exists(GrimoireExportImporterService.ExtractPath);
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField(hasImport
                ? "Imported: " + (imported?.version_name ?? "Version unknown")
                : "No version imported", EditorStyles.boldLabel);
            if (GUILayout.Button("Browse database", GUILayout.Width(125f)))
                GrimoireDatabaseBrowser.ShowWindow();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Search", GUILayout.Width(44f));
            _search = EditorGUILayout.TextField(_search ?? "", GUILayout.MinWidth(100f));
            using (new EditorGUI.DisabledScope(_loading || _downloading))
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(68f))) Refresh();
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(5f);

            if (!string.IsNullOrEmpty(_error))
            {
                GrimoireEditorStyles.DrawErrorBox(_error);
            }

            if (!string.IsNullOrEmpty(_status) && string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.LabelField(_status, GrimoireEditorStyles.MiniSecondaryStyle);
            }

            if (_downloading)
            {
                EditorGUI.ProgressBar(
                    EditorGUILayout.GetControlRect(false, 18f),
                    _downloadProgress,
                    _downloadStatus ?? "Downloading...");
                EditorGUILayout.Space(4);
            }

            var paneHeight = Mathf.Max(220f, height - 230f);
            var narrow = GrimoireWorkflowGUI.IsNarrow(width);
            if (!narrow) EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(narrow ? GUILayout.ExpandWidth(true) : GUILayout.Width(Mathf.Max(230f, width * 0.57f)));
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(narrow ? 215f : paneHeight));
            DrawWorkspaceList(importedId);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandWidth(true));
            _detailsScroll = EditorGUILayout.BeginScrollView(_detailsScroll, GUILayout.Height(narrow ? Mathf.Max(220f, paneHeight - 215f) : paneHeight));
            DrawWorkspaceDetail(importedId);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            if (!narrow) EditorGUILayout.EndHorizontal();
        }

        private void DrawWorkspaceList(string importedId)
        {
            GrimoireWorkflowGUI.Section("Available versions" + (_versions == null ? "" :
                $" · {_versions.Count(VersionMatchesSearch)}"));
            if (_loading && _versions == null)
            {
                EditorGUILayout.LabelField("Loading versions…", GrimoireEditorStyles.MiniSecondaryStyle);
                return;
            }
            if (_versions == null || _versions.Length == 0)
            {
                GrimoireEditorStyles.DrawInfoBox("No Unity export versions found for this game.");
                return;
            }
            if (!_versions.Any(v => v.id == _selectedVersionId && VersionMatchesSearch(v)))
                _selectedVersionId = _versions.OrderByDescending(v => v.id == importedId)
                    .ThenByDescending(v => v.created_at).FirstOrDefault(VersionMatchesSearch)?.id;
            var shown = 0;
            foreach (var version in _versions.OrderByDescending(v => v.id == importedId).ThenByDescending(v => v.created_at))
            {
                if (!VersionMatchesSearch(version)) continue;
                shown++;
                var status = version.id == importedId ? "Imported" : "Available to import";
                if (GrimoireWorkflowGUI.Row(
                        string.IsNullOrEmpty(version.version_name) ? "(unnamed version)" : version.version_name,
                        (version.template_name ?? version.platform ?? "Unity") + " · " + GrimoireExportImporterService.FormatDate(version.created_at),
                        GrimoireExportImporterService.FormatFileSize(version.file_size),
                        status,
                        version.id == _selectedVersionId))
                    _selectedVersionId = version.id;
            }
            if (shown == 0) GrimoireEditorStyles.DrawInfoBox("No versions match this search.");
        }

        private bool VersionMatchesSearch(ExportVersion version)
        {
            if (string.IsNullOrWhiteSpace(_search)) return true;
            var query = _search.Trim();
            return (version.version_name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (version.template_name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (version.platform ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawWorkspaceDetail(string importedId)
        {
            var visible = _versions?.Where(VersionMatchesSearch).ToArray() ?? Array.Empty<ExportVersion>();
            var version = visible.FirstOrDefault(v => v.id == _selectedVersionId) ?? visible.FirstOrDefault();
            if (version == null)
            {
                GrimoireWorkflowGUI.DetailHeading("Version details", "Select a version", null);
                return;
            }
            GrimoireWorkflowGUI.DetailHeading("Version details", version.version_name, version.template_name);
            var isCurrent = version.id == importedId;
            using (new EditorGUI.DisabledScope(_downloading || string.IsNullOrEmpty(version.download_url)))
            {
                if (GUILayout.Button(_downloading && _downloadingVersionId == version.id ? "Downloading…" :
                        isCurrent ? "Reimport version" : "Download & Extract",
                        GrimoireEditorStyles.PrimaryButtonStyle, GUILayout.Height(28f)))
                    DownloadAndImportAsync(version);
            }
            GrimoireWorkflowGUI.DetailLine("Created", GrimoireExportImporterService.FormatDate(version.created_at));
            GrimoireWorkflowGUI.DetailLine("Size", GrimoireExportImporterService.FormatFileSize(version.file_size));
            GrimoireWorkflowGUI.DetailLine("Platform", version.platform);
            GrimoireWorkflowGUI.DetailLine("Project status", isCurrent ? "Imported" : "Not imported");
            GrimoireWorkflowGUI.Section("What it enables");
            GrimoireWorkflowGUI.DetailLine("Objects & variables", "Included");
            GrimoireWorkflowGUI.DetailLine("Dialogs & logic", "Included");
            GrimoireWorkflowGUI.DetailLine("Localization", "Included");
            GrimoireWorkflowGUI.Note("Object Links keep their selected data source. Importing a version does not switch them automatically.");
        }

        private static string ImportedVersionPrefKey =>
            "Grimoire.ImportedVersion." + Application.dataPath + "." + GrimoireSettings.GameId;

        private static string GetImportedVersionId() =>
            Directory.Exists(GrimoireExportImporterService.ExtractPath)
                ? EditorPrefs.GetString(ImportedVersionPrefKey, "") : "";

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(GrimoireSettings.GameName)
                    ? "Export versions"
                    : $"Exports for {GrimoireSettings.GameName}",
                GrimoireEditorStyles.TitleStyle);
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_loading || _downloading))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    RefreshAsync();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void DrawVersionPage()
        {
            var totalPages = Math.Max(1, (_versions.Length + VersionsPerPage - 1) / VersionsPerPage);
            if (_page >= totalPages)
            {
                _page = totalPages - 1;
            }

            var start = _page * VersionsPerPage;
            var end = Math.Min(start + VersionsPerPage, _versions.Length);

            for (var i = start; i < end; i++)
            {
                DrawVersionCard(_versions[i]);
                EditorGUILayout.Space(6);
            }

            if (totalPages > 1)
            {
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(_page <= 0 || _downloading))
                {
                    if (GUILayout.Button("Previous", GUILayout.Width(80)))
                    {
                        _page--;
                    }
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField($"Page {_page + 1} / {totalPages}", GrimoireEditorStyles.MiniSecondaryStyle);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(_page >= totalPages - 1 || _downloading))
                {
                    if (GUILayout.Button("Next", GUILayout.Width(80)))
                    {
                        _page++;
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawVersionCard(ExportVersion version)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var title = string.IsNullOrEmpty(version.version_name) ? "(unnamed)" : version.version_name;
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            EditorGUILayout.LabelField(
                $"Created: {GrimoireExportImporterService.FormatDate(version.created_at)}",
                GrimoireEditorStyles.MiniSecondaryStyle);

            if (version.file_size > 0)
            {
                EditorGUILayout.LabelField(
                    $"Size: {GrimoireExportImporterService.FormatFileSize(version.file_size)}",
                    GrimoireEditorStyles.MiniSecondaryStyle);
            }

            if (!string.IsNullOrEmpty(version.platform) || !string.IsNullOrEmpty(version.template_name))
            {
                var meta = string.IsNullOrEmpty(version.template_name)
                    ? version.platform
                    : $"{version.platform} · {version.template_name}";
                EditorGUILayout.LabelField(meta, GrimoireEditorStyles.MiniSecondaryStyle);
            }

            EditorGUILayout.Space(4);

            var isThis = _downloading && _downloadingVersionId == version.id;
            using (new EditorGUI.DisabledScope(_downloading || string.IsNullOrEmpty(version.download_url)))
            {
                if (GUILayout.Button(isThis ? "Downloading..." : "Download & Extract"))
                {
                    DownloadAndImportAsync(version);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private async void RefreshAsync()
        {
            var generation = ++_loadGeneration;
            var gameId = GrimoireSettings.GameId;
            _loading = true;
            _error = null;
            _status = "Loading export versions...";
            RequestRepaint();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (generation != _loadGeneration || gameId != GrimoireSettings.GameId)
            {
                return;
            }

            if (!hasSession)
            {
                _loading = false;
                _status = null;
                _error = "Your Grimoire session has expired. Sign in again.";
                RequestRepaint();
                return;
            }

            var result = await GrimoireApiClient.ListExportVersionsAsync(gameId);
            if (generation != _loadGeneration || gameId != GrimoireSettings.GameId)
            {
                return;
            }

            _loading = false;

            if (!result.Success)
            {
                _versions = null;
                _status = null;
                _error = result.Error;
                RequestRepaint();
                return;
            }

            _versions = (result.Data ?? Array.Empty<ExportVersion>())
                .Where(v => !IsMarkdownExport(v))
                .ToArray();
            _page = 0;
            _error = null;
            _status = _versions.Length > 0
                ? $"Found {_versions.Length} version(s)"
                : "No versions found";
            RequestRepaint();
        }

        private async void DownloadAndImportAsync(ExportVersion version)
        {
            if (version == null || string.IsNullOrEmpty(version.download_url))
            {
                _error = "This version has no download URL.";
                RequestRepaint();
                return;
            }

            _downloading = true;
            _downloadingVersionId = version.id;
            _downloadProgress = 0f;
            _downloadStatus = "Starting download...";
            _error = null;
            RequestRepaint();

            var download = await GrimoireApiClient.DownloadExportZipAsync(
                version.download_url,
                (message, progress) =>
                {
                    _downloadStatus = message;
                    _downloadProgress = progress;
                    RequestRepaint();
                });

            if (!download.Success)
            {
                _downloading = false;
                _downloadingVersionId = null;
                _downloadStatus = null;
                _error = download.Error;
                EditorUtility.DisplayDialog("Download Failed", download.Error, "OK");
                RequestRepaint();
                return;
            }

            _downloadStatus = "Importing...";
            RequestRepaint();

            var imported = GrimoireExportImporterService.ConfirmAndImport(download.Data, out var message);

            _downloading = false;
            _downloadingVersionId = null;
            _downloadStatus = null;
            _downloadProgress = 0f;

            if (imported)
            {
                EditorPrefs.SetString(ImportedVersionPrefKey, version.id ?? "");
                _status = message;
                EditorUtility.DisplayDialog("Import Complete", message, "OK");
            }
            else
            {
                if (!string.IsNullOrEmpty(message) && !message.StartsWith("Import cancelled", StringComparison.Ordinal))
                {
                    _error = message;
                    EditorUtility.DisplayDialog("Import Failed", message, "OK");
                }
                else
                {
                    _status = message;
                }
            }

            RequestRepaint();
        }

        private void EnsureGame()
        {
            var gameId = GrimoireSettings.GameId;
            if (_loadedGameId == gameId) return;
            _loadedGameId = gameId;
            _loadGeneration++;
            _loading = false;
            _versions = null;
            _selectedVersionId = null;
            _search = "";
            _status = null;
            _error = null;
        }

        private static bool IsMarkdownExport(ExportVersion version)
        {
            if (version == null)
            {
                return true;
            }

            var fileType = version.file_type?.Trim().ToLowerInvariant();
            if (fileType == "md" || fileType == "markdown" || fileType == "text/markdown")
            {
                return true;
            }

            var path = version.storage_path ?? version.download_url ?? string.Empty;
            var queryStart = path.IndexOf('?');
            if (queryStart >= 0)
            {
                path = path.Substring(0, queryStart);
            }

            return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        }

        private void RequestRepaint()
        {
            RepaintNeeded?.Invoke();
        }
    }
}
