using System;
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
        private ExportVersion[] _versions;
        private bool _loading;
        private bool _downloading;
        private float _downloadProgress;
        private string _downloadStatus;
        private string _status;
        private string _error;
        private int _page;
        private int _loadGeneration;
        private string _downloadingVersionId;

        public event Action RepaintNeeded;

        public void Activate()
        {
            if (GrimoireSettings.IsConfigured && _versions == null && !_loading)
            {
                RefreshAsync();
            }
        }

        /// <summary>Re-fetch export versions from the server.</summary>
        public void Refresh()
        {
            if (GrimoireSettings.IsConfigured && !_loading && !_downloading)
            {
                RefreshAsync();
            }
        }

        public void Draw()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "Sign in and choose a workspace first to download exports.");
                return;
            }

            GrimoireEditorStyles.DrawInfoBox(
                "Import a Grimoire export to use logic, translations, and variables in Play Mode. " +
                "Skip this if you only use Connect.");

            DrawToolbar();

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

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_loading && _versions == null)
            {
                EditorGUILayout.LabelField("Loading export versions...", GrimoireEditorStyles.MiniSecondaryStyle);
            }
            else if (_versions == null || _versions.Length == 0)
            {
                if (!_loading)
                {
                    GrimoireEditorStyles.DrawInfoBox(
                        "No export versions found for this game. Create a Unity export on the Grimoire platform first.");
                }
            }
            else
            {
                DrawVersionPage();
            }

            EditorGUILayout.EndScrollView();
        }

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
            _loading = true;
            _error = null;
            _status = "Loading export versions...";
            RequestRepaint();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (generation != _loadGeneration)
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

            var result = await GrimoireApiClient.ListExportVersionsAsync(GrimoireSettings.GameId);
            if (generation != _loadGeneration)
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

            _versions = result.Data ?? Array.Empty<ExportVersion>();
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

        private void RequestRepaint()
        {
            RepaintNeeded?.Invoke();
        }
    }
}
