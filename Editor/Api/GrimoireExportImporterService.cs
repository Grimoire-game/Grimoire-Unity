using System;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Extracts a downloaded Grimoire export ZIP into <c>Assets/Grimoire/</c>,
    /// replacing any previous export contents.
    /// </summary>
    public static class GrimoireExportImporterService
    {
        public const string AssetsFolder = "Assets/Grimoire";

        /// <summary>Absolute filesystem path for <see cref="AssetsFolder"/>.</summary>
        public static string ExtractPath => Path.Combine(Application.dataPath, "Grimoire");

        /// <summary>
        /// Confirm with the user, then replace <c>Assets/Grimoire/</c> with the
        /// contents of <paramref name="tempZipPath"/> and refresh the asset database.
        /// Deletes the temp ZIP on success or cancel.
        /// </summary>
        public static bool ConfirmAndImport(string tempZipPath, out string message)
        {
            message = null;

            if (string.IsNullOrEmpty(tempZipPath) || tempZipPath.Contains(".."))
            {
                message = "Invalid download path.";
                return false;
            }

            if (!File.Exists(tempZipPath))
            {
                message = "Downloaded file not found.";
                return false;
            }

            var confirmed = EditorUtility.DisplayDialog(
                "Replace Grimoire Files?",
                "This will remove all existing files in Assets/Grimoire/ and replace them with the new version.\n\n" +
                "This action cannot be undone.\n\n" +
                "Do you want to continue?",
                "Replace Files",
                "Cancel");

            if (!confirmed)
            {
                TryDelete(tempZipPath);
                message = "Import cancelled. No files were changed.";
                return false;
            }

            return Import(tempZipPath, out message);
        }

        /// <summary>Replace Assets/Grimoire with the ZIP contents (no confirm dialog).</summary>
        public static bool Import(string tempZipPath, out string message)
        {
            message = null;

            if (string.IsNullOrEmpty(tempZipPath) || tempZipPath.Contains(".."))
            {
                message = "Invalid download path.";
                return false;
            }

            try
            {
                if (!File.Exists(tempZipPath))
                {
                    message = "Downloaded file not found.";
                    return false;
                }

                var extractPath = ExtractPath;

                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }

                Directory.CreateDirectory(extractPath);
                ZipFile.ExtractToDirectory(tempZipPath, extractPath);
                TryDelete(tempZipPath);

                AssetDatabase.Refresh();
                message = "Successfully imported to Assets/Grimoire/.";
                return true;
            }
            catch (Exception exception)
            {
                message = $"Import failed: {exception.Message}";
                return false;
            }
        }

        public static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            var order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }

        public static string FormatDate(string isoDate)
        {
            if (string.IsNullOrEmpty(isoDate))
            {
                return "";
            }

            try
            {
                return DateTime.Parse(isoDate).ToString("yyyy-MM-dd HH:mm");
            }
            catch (Exception)
            {
                return isoDate;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup.
            }
        }
    }
}
