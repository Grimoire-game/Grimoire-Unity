using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Registers an API loader on <see cref="GrimoireObjectCache"/> and refreshes
    /// scene Object Link snapshots when entering Play Mode while signed in.
    /// </summary>
    [InitializeOnLoad]
    internal static class GrimoireEditorObjectLoader
    {
        static GrimoireEditorObjectLoader()
        {
            GrimoireObjectCache.Loader = new ApiLoader();
            GrimoireObjectLink.NestedSnapshotsChanged += OnNestedSnapshotsChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnNestedSnapshotsChanged(GrimoireObjectLink link)
        {
            if (link == null || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            EditorUtility.SetDirty(link);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
            {
                return;
            }

            if (!GrimoireSettings.IsConfigured)
            {
                return;
            }

            RefreshSceneRootsAsync();
        }

        private static async void RefreshSceneRootsAsync()
        {
            var links = new List<GrimoireObjectLink>();
            GrimoireGameEngineDirtyTracker.CollectSceneLinks(links);
            if (links.Count == 0)
            {
                return;
            }

            var refreshed = 0;
            foreach (var link in links)
            {
                if (link == null || (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId)))
                {
                    continue;
                }

                var result = await GrimoireLinkedFieldStore.RefreshFromGrimoireAsync(
                    link, preserveLocalEdits: true);
                if (result.Success)
                {
                    refreshed++;
                }
                else if (!string.IsNullOrEmpty(result.Error))
                {
                    Debug.LogWarning($"[Grimoire] Play Mode snapshot refresh failed for '{link.ObjectKey}': {result.Error}");
                }
            }

            if (refreshed > 0)
            {
                GrimoireObjectCache.NotifyUpdated();
            }
        }

        private sealed class ApiLoader : IGrimoireObjectLoader
        {
            public async Task<GrimoireObjectSnapshot> LoadAsync(string objectId, string objectKey)
            {
                if (!GrimoireSettings.IsConfigured)
                {
                    Debug.LogWarning(
                        "[Grimoire] Sign in and choose a workspace to load nested objects from the API.");
                    return null;
                }

                await GrimoireAuthSession.EnsureFreshTokenAsync();

                var gameId = GrimoireSettings.GameId;
                var id = objectId;
                if (string.IsNullOrEmpty(id))
                {
                    if (string.IsNullOrEmpty(objectKey))
                    {
                        return null;
                    }

                    var resolved = await GrimoireObjectKeyResolver.ResolveAsync(gameId, objectKey);
                    if (!resolved.Success)
                    {
                        Debug.LogWarning($"[Grimoire] Could not resolve '{objectKey}': {resolved.Error}");
                        return null;
                    }

                    id = resolved.Data;
                }

                var view = await GrimoireApiClient.GetObjectViewAsync(
                    gameId, id, GrimoireSettings.Locale);
                if (!view.Success)
                {
                    Debug.LogWarning($"[Grimoire] Could not load object '{objectKey ?? id}': {view.Error}");
                    return null;
                }

                if (view.Data?.@object != null)
                {
                    GrimoireObjectKeyResolver.RememberSummary(gameId, view.Data.@object);
                }

                return GrimoireObjectSnapshotMapper.FromDocument(view.Data);
            }
        }
    }
}
