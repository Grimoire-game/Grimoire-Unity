using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>Coordinates a complete pull of every Grimoire item used by this project.</summary>
    public static class GrimoireSyncEverything
    {
        public sealed class Summary
        {
            public int Variables;
            public int Dialogs;
            public int TextLinks;
            public int Objects;
            public int NestedObjects;
            public int Skipped;
            public int Failed;
            public bool Cancelled;
            public readonly List<string> Errors = new List<string>();

            public override string ToString()
            {
                var result =
                    $"Updated {Variables} variable library, {Dialogs} dialogs, " +
                    $"{TextLinks} text links, {Objects} objects and {NestedObjects} nested objects.";
                if (Skipped > 0)
                {
                    result += $" Skipped {Skipped}.";
                }

                if (Failed > 0)
                {
                    result += $" Failed {Failed}.";
                }

                if (Cancelled)
                {
                    result += " Cancelled.";
                }

                return result;
            }
        }

        /// <param name="progress">
        /// Receives stage text and progress (0..1). Return true to cancel.
        /// </param>
        public static async Task<ApiResult<Summary>> RunAsync(
            Func<string, float, bool> progress = null)
        {
            if (!GrimoireSettings.IsConfigured)
            {
                return ApiResult<Summary>.Fail(
                    "Sign in and choose a workspace first.", "not_configured");
            }

            if (!await GrimoireAuthSession.EnsureFreshTokenAsync())
            {
                return ApiResult<Summary>.Fail(
                    "Your Grimoire session has expired. Sign in again.",
                    "missing_credentials");
            }

            var summary = new Summary();
            var gameId = GrimoireSettings.GameId;

            if (Cancelled(progress, "Downloading languages and translations...", 0.02f, summary))
            {
                return ApiResult<Summary>.Ok(summary);
            }

            var catalogResult = await GrimoireTranslationCatalog.LoadAsync(
                gameId, message => progress?.Invoke(message, 0.05f));
            if (!catalogResult.Success)
            {
                return ApiResult<Summary>.Fail(
                    catalogResult.Error, catalogResult.Code, catalogResult.HttpStatus);
            }

            var catalog = catalogResult.Data;
            if (Cancelled(progress, "Syncing variables...", 0.12f, summary))
            {
                return ApiResult<Summary>.Ok(summary);
            }

            var variables = await GrimoireVariableSync.SyncAsync(
                gameId,
                message => progress?.Invoke(message, 0.16f),
                catalog,
                saveAssets: false);
            if (variables.Success)
            {
                summary.Variables = 1;
            }
            else
            {
                AddFailure(summary, "Variables", variables.Error);
            }

            var dialogs = GrimoireDialogImporter.FindAllImported().Values
                .Where(asset => asset != null && !string.IsNullOrEmpty(asset.DialogId))
                .GroupBy(asset => asset.DialogId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var dialogCache = new Dictionary<string, DialogResource>(StringComparer.Ordinal);
            for (var i = 0; i < dialogs.Count; i++)
            {
                var asset = dialogs[i];
                if (Cancelled(
                        progress,
                        $"Updating dialog {i + 1}/{dialogs.Count}: {asset.DisplayName}",
                        Portion(0.2f, 0.2f, i, dialogs.Count),
                        summary))
                {
                    Finish(summary);
                    return ApiResult<Summary>.Ok(summary);
                }

                var imported = await GrimoireDialogImporter.ImportAsync(
                    gameId,
                    asset.DialogId,
                    null,
                    catalog,
                    syncVariables: false,
                    saveAssets: false,
                    resourceCache: dialogCache);
                if (imported.Success)
                {
                    summary.Dialogs++;
                    foreach (var note in GrimoireDialogImporter.LastImportNotes)
                    {
                        summary.Skipped++;
                        summary.Errors.Add($"Dialog '{asset.DisplayName}': {note}");
                    }
                }
                else
                {
                    AddFailure(summary, $"Dialog '{asset.DisplayName}'", imported.Error);
                }
            }

            var openedScenes = OpenUnloadedProjectScenes(summary);
            var textLinks = CollectTextLinks();
            for (var i = 0; i < textLinks.Count; i++)
            {
                var link = textLinks[i];
                if (Cancelled(
                        progress,
                        $"Updating text link {i + 1}/{textLinks.Count}: {link.TextCode}",
                        Portion(0.4f, 0.15f, i, textLinks.Count),
                        summary))
                {
                    SaveAndClose(openedScenes, summary);
                    Finish(summary);
                    return ApiResult<Summary>.Ok(summary);
                }

                var key = !string.IsNullOrEmpty(link.CachedStringId)
                    ? link.CachedStringId
                    : link.TextCode;
                if (!catalog.TryResolve(key, out var resource))
                {
                    summary.Skipped++;
                    summary.Errors.Add($"Text link '{link.TextCode}' was not found.");
                    continue;
                }

                GrimoireTextLinkStore.ApplyFromResource(
                    link, resource, preserveLocalEdits: true);
                summary.TextLinks++;
            }

            var objectLinks = CollectObjectLinks();
            var objectCache =
                new Dictionary<string, GrimoireLocalizedObjectLoader.LoadedObject>(
                    StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < objectLinks.Count; i++)
            {
                var link = objectLinks[i];
                if (Cancelled(
                        progress,
                        $"Updating object {i + 1}/{objectLinks.Count}: {link.ObjectKey}",
                        Portion(0.55f, 0.4f, i, objectLinks.Count),
                        summary))
                {
                    SaveAndClose(openedScenes, summary);
                    Finish(summary);
                    return ApiResult<Summary>.Ok(summary);
                }

                var objectId = link.CachedObjectId;
                if (string.IsNullOrEmpty(objectId) && link.HasKey)
                {
                    var resolved = await GrimoireObjectKeyResolver.ResolveAsync(
                        gameId, link.ObjectKey);
                    if (!resolved.Success)
                    {
                        AddFailure(summary, $"Object '{link.ObjectKey}'", resolved.Error);
                        continue;
                    }

                    objectId = resolved.Data;
                    link.CachedObjectId = objectId;
                    EditorUtility.SetDirty(link);
                }

                var refreshed = await LoadObjectCached(
                    gameId, objectId, catalog, objectCache);
                if (!refreshed.Success)
                {
                    AddFailure(summary, $"Object '{link.ObjectKey}'", refreshed.Error);
                    continue;
                }

                GrimoireLinkedFieldStore.ApplyFromDocument(
                    link,
                    refreshed.Data.Source,
                    preserveLocalEdits: true,
                    localizedSnapshot: CloneSnapshot(refreshed.Data.Snapshot));
                if (refreshed.Data.Source?.@object != null)
                {
                    GrimoireObjectKeyResolver.RememberSummary(
                        gameId, refreshed.Data.Source.@object);
                }

                summary.Objects++;
                foreach (var reference in link.ApiSnapshot.CollectObjectReferences())
                {
                    var nestedId = reference.Id;
                    if (string.IsNullOrEmpty(nestedId) && reference.HasKey)
                    {
                        var resolved = await GrimoireObjectKeyResolver.ResolveAsync(
                            gameId, reference.CodeId);
                        if (!resolved.Success)
                        {
                            AddFailure(
                                summary,
                                $"Nested object '{reference.CodeId}'",
                                resolved.Error);
                            continue;
                        }

                        nestedId = resolved.Data;
                    }

                    if (string.IsNullOrEmpty(nestedId) ||
                        string.Equals(
                            nestedId,
                            link.ApiSnapshot.ObjectId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var nested = await LoadObjectCached(
                        gameId, nestedId, catalog, objectCache);
                    if (nested.Success)
                    {
                        link.RememberNestedSnapshot(CloneSnapshot(nested.Data.Snapshot));
                        summary.NestedObjects++;
                    }
                    else
                    {
                        AddFailure(summary, $"Nested object '{nestedId}'", nested.Error);
                    }
                }
            }

            SaveAndClose(openedScenes, summary);
            progress?.Invoke("Saving synced content...", 0.97f);
            Finish(summary);
            progress?.Invoke("Sync complete", 1f);
            return ApiResult<Summary>.Ok(summary);
        }

        private static async Task<ApiResult<GrimoireLocalizedObjectLoader.LoadedObject>>
            LoadObjectCached(
                string gameId,
                string objectId,
                GrimoireTranslationCatalog catalog,
                Dictionary<string, GrimoireLocalizedObjectLoader.LoadedObject> cache)
        {
            if (string.IsNullOrEmpty(objectId))
            {
                return ApiResult<GrimoireLocalizedObjectLoader.LoadedObject>.Fail(
                    "The object has no id.", "missing_parameter");
            }

            if (cache.TryGetValue(objectId, out var cached))
            {
                return ApiResult<GrimoireLocalizedObjectLoader.LoadedObject>.Ok(cached);
            }

            var loaded = await GrimoireLocalizedObjectLoader.LoadAsync(
                gameId, objectId, catalog);
            if (loaded.Success)
            {
                cache[objectId] = loaded.Data;
            }

            return loaded;
        }

        private static GrimoireObjectSnapshot CloneSnapshot(
            GrimoireObjectSnapshot snapshot)
        {
            return snapshot == null
                ? new GrimoireObjectSnapshot()
                : JsonUtility.FromJson<GrimoireObjectSnapshot>(
                    JsonUtility.ToJson(snapshot));
        }

        private static void Finish(Summary summary)
        {
            AssetDatabase.SaveAssets();
            GrimoireVariables.Reload();
            GrimoireObjectCache.NotifyUpdated();
            GrimoireLinkedFieldStore.NotifyExternalChange();
            GrimoireLocalization.NotifyLanguageChanged();
            SceneView.RepaintAll();
        }

        private static List<GrimoireTextLink> CollectTextLinks()
        {
            var result = new List<GrimoireTextLink>();
            GrimoireTextLinkStore.CollectSceneLinks(result);
            AddPrefabComponents(result);
            return Distinct(result);
        }

        private static List<Scene> OpenUnloadedProjectScenes(Summary summary)
        {
            var opened = new List<Scene>();
            var originalActive = SceneManager.GetActiveScene();
            var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
                {
                    loadedPaths.Add(scene.path);
                }
            }

            foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || loadedPaths.Contains(path))
                {
                    continue;
                }

                try
                {
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    if (scene.IsValid())
                    {
                        opened.Add(scene);
                    }
                }
                catch (Exception exception)
                {
                    AddFailure(summary, $"Scene '{path}'", exception.Message);
                }
            }

            if (originalActive.IsValid() && originalActive.isLoaded)
            {
                SceneManager.SetActiveScene(originalActive);
            }

            return opened;
        }

        private static void SaveAndClose(List<Scene> scenes, Summary summary)
        {
            if (scenes == null)
            {
                return;
            }

            foreach (var scene in scenes)
            {
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                var canClose = true;
                try
                {
                    if (scene.isDirty && !EditorSceneManager.SaveScene(scene))
                    {
                        canClose = false;
                        AddFailure(summary, $"Scene '{scene.path}'", "Unity could not save the scene.");
                    }
                }
                catch (Exception exception)
                {
                    canClose = false;
                    AddFailure(summary, $"Scene '{scene.path}'", exception.Message);
                }

                if (canClose)
                {
                    EditorSceneManager.CloseScene(scene, removeScene: true);
                }
            }
        }

        private static List<GrimoireObjectLink> CollectObjectLinks()
        {
            var result = new List<GrimoireObjectLink>();
            GrimoireGameEngineDirtyTracker.CollectSceneLinks(result);
            AddPrefabComponents(result);
            return Distinct(result);
        }

        private static void AddPrefabComponents<T>(List<T> destination) where T : Component
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    continue;
                }

                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null)
                {
                    continue;
                }

                destination.AddRange(root.GetComponentsInChildren<T>(true));
            }
        }

        internal static List<T> Distinct<T>(IEnumerable<T> items) where T : UnityEngine.Object
        {
            var result = new List<T>();
            var ids = new HashSet<int>();
            foreach (var item in items)
            {
                if (item != null && ids.Add(item.GetInstanceID()))
                {
                    result.Add(item);
                }
            }

            return result;
        }

        private static bool Cancelled(
            Func<string, float, bool> progress,
            string message,
            float amount,
            Summary summary)
        {
            if (progress == null || !progress(message, amount))
            {
                return false;
            }

            summary.Cancelled = true;
            return true;
        }

        private static float Portion(float start, float length, int index, int count) =>
            count <= 0 ? start + length : start + length * index / count;

        private static void AddFailure(Summary summary, string item, string error)
        {
            summary.Failed++;
            summary.Errors.Add($"{item}: {error ?? "Unknown error"}");
        }
    }
}
