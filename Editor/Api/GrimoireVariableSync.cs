using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Downloads the game's variables (Grimoire types with their elements) into a
    /// <see cref="GrimoireVariableLibrary"/> under a Resources folder, so
    /// <see cref="GrimoireVariables"/> can turn element ids into names in builds.
    /// </summary>
    public static class GrimoireVariableSync
    {
        public const string AssetsFolder = "Assets/GrimoireVariables/Resources";
        public const string AssetPath = AssetsFolder + "/" + GrimoireVariableLibrary.ResourcesName + ".asset";

        private const int PageSize = 200;
        private const int MaxPages = 50;

        public static event Action<GrimoireVariableLibrary> Synced;

        /// <summary>The synced library in the project, or null.</summary>
        public static GrimoireVariableLibrary FindLibrary()
        {
            return AssetDatabase.LoadAssetAtPath<GrimoireVariableLibrary>(AssetPath);
        }

        public static async Task<ApiResult<GrimoireVariableLibrary>> SyncAsync(
            string gameId,
            Action<string> progress = null,
            GrimoireTranslationCatalog catalog = null,
            bool saveAssets = true)
        {
            if (string.IsNullOrEmpty(gameId))
            {
                return ApiResult<GrimoireVariableLibrary>.Fail("Choose a game first.");
            }

            var context = GrimoireContentSync.CurrentContext();
            if (!await GrimoireAuthSession.EnsureFreshTokenAsync())
            {
                return ApiResult<GrimoireVariableLibrary>.Fail("Your Grimoire session has expired. Sign in again.");
            }

            if (catalog == null)
            {
                var catalogResult = await GrimoireTranslationCatalog.LoadAsync(gameId, progress);
                if (catalogResult.Success)
                {
                    catalog = catalogResult.Data;
                }
                else
                {
                    Debug.LogWarning(
                        $"[Grimoire Variables] Translations could not be downloaded; " +
                        $"source names were kept: {catalogResult.Error}");
                }
            }

            var resources = new List<VariableResource>();
            for (var page = 0; page < MaxPages; page++)
            {
                progress?.Invoke(page == 0 ? "Downloading variables..." : $"Downloading variables ({resources.Count})...");
                var result = await GrimoireApiClient.ListVariablesAsync(gameId, PageSize, page * PageSize);
                if (!result.Success)
                {
                    return ApiResult<GrimoireVariableLibrary>.Fail(Describe(result), result.Code, result.HttpStatus);
                }

                resources.AddRange(result.Data.Where(r => r != null));
                if (result.Data.Length < PageSize)
                {
                    break;
                }
            }

            if (GrimoireContentSync.CurrentContext() != context || GrimoireContentSync.ItemPullInterrupted)
                return ApiResult<GrimoireVariableLibrary>.Fail("The workspace or editor mode changed during sync.");
            var library = GetOrCreateLibrary();
            library.SetSyncedData(gameId, resources.Select(r => ToVariable(r, catalog)).ToList());
            return Save(library, saveAssets);
        }

        internal static async Task<ApiResult<VariableResource>> SyncOneAsync(string gameId, string variableId)
        {
            var context = GrimoireContentSync.CurrentContext();
            if (!await GrimoireAuthSession.EnsureFreshTokenAsync())
                return ApiResult<VariableResource>.Fail("Your Grimoire session has expired. Sign in again.");
            var result = await GrimoireApiClient.GetVariableAsync(gameId, variableId);
            if (!result.Success) return result;

            GrimoireTranslationCatalog catalog = null;
            if (result.Data.elements?.Any(e => !string.IsNullOrEmpty(e?.string_id)) == true)
            {
                var translations = await GrimoireTranslationCatalog.LoadAsync(gameId);
                if (!translations.Success)
                    return ApiResult<VariableResource>.Fail(translations.Error, translations.Code, translations.HttpStatus);
                catalog = translations.Data;
            }

            if (!GrimoireContentSync.CanApply(context))
                return ApiResult<VariableResource>.Fail("The workspace or editor mode changed during sync.");
            var library = GetOrCreateLibrary();
            if (!string.IsNullOrEmpty(library.GameId) && library.GameId != gameId)
                return ApiResult<VariableResource>.Fail("The variable library belongs to another game. Sync the game's variable library first.");
            library.SetSyncedVariable(gameId, ToVariable(result.Data, catalog));
            Save(library, true);
            return result;
        }

        private static GrimoireVariableLibrary GetOrCreateLibrary()
        {
            var library = FindLibrary();
            if (library == null)
            {
                EnsureFolder();
                library = ScriptableObject.CreateInstance<GrimoireVariableLibrary>();
                AssetDatabase.CreateAsset(library, AssetPath);
            }

            return library;
        }

        private static ApiResult<GrimoireVariableLibrary> Save(GrimoireVariableLibrary library, bool saveAssets)
        {
            EditorUtility.SetDirty(library);
            if (saveAssets)
            {
                AssetDatabase.SaveAssetIfDirty(library);
            }

            GrimoireVariables.Reload();
            Synced?.Invoke(library);
            return ApiResult<GrimoireVariableLibrary>.Ok(library);
        }

        private static GrimoireVariable ToVariable(
            VariableResource resource,
            GrimoireTranslationCatalog catalog)
        {
            var elements = (resource.elements ?? Array.Empty<VariableElementDto>())
                .Where(e => e != null && !string.IsNullOrEmpty(e.id))
                .OrderBy(e => e.order_index ?? int.MaxValue)
                .Select(e => new GrimoireVariableElement(
                    e.id,
                    e.name,
                    e.description,
                    e.value,
                    e.string_id,
                    e.range_min,
                    e.range_max,
                    catalog?.GetTranslations(e.string_id)))
                .ToList();

            return new GrimoireVariable(
                resource.id,
                resource.name,
                resource.element_type,
                resource.description,
                resource.group,
                resource.updated_at,
                elements);
        }

        private static string Describe(ApiResult<VariableResource[]> result)
        {
            if (result.HttpStatus == 403)
            {
                return "Grimoire refused to share variables (HTTP 403). Your account or plan needs access to " +
                       "Variables (scope variables:read).";
            }

            return result.HttpStatus > 0 ? $"{result.Error} (HTTP {result.HttpStatus})" : result.Error;
        }

        private static void EnsureFolder()
        {
            var current = "Assets";
            foreach (var part in AssetsFolder.Substring("Assets/".Length).Split('/'))
            {
                var next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, part);
                }

                current = next;
            }
        }
    }
}
