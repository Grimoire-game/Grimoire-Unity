using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Upserts / removes this Unity GameObject in a Grimoire object's
    /// <c>game_engine_data</c> array via PATCH (full-array replace).
    /// Editor-only — never mutate engine data while Play Mode is active.
    /// </summary>
    public static class GrimoireGameEngineSync
    {
        public static event Action<ObjectViewDocument> DocumentUpdated;

        /// <summary>
        /// True while Play Mode (or transitioning) — engine-data mutations must not run.
        /// </summary>
        public static bool IsPlayModeBlocked =>
            EditorApplication.isPlayingOrWillChangePlaymode;

        /// <summary>
        /// Fail result used when a caller tries to mutate <c>game_engine_data</c> in Play Mode.
        /// </summary>
        public static ApiResult<T> PlayModeBlockedResult<T>() =>
            ApiResult<T>.Fail(
                "Game engine data sync is editor-only. Exit Play Mode to update engine data.",
                "play_mode_blocked");

        /// <summary>
        /// Snapshot of a linked instance used when the component is already
        /// gone (destroy / unlink) and we can no longer read the Transform.
        /// </summary>
        public struct LinkIdentity
        {
            public string GameId;
            public string ObjectId;
            public string EngineInstanceId;
            public string Scene;
        }

        public static string GetEngineInstanceId(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return "";
            }

            return GlobalObjectId.GetGlobalObjectIdSlow(gameObject).ToString();
        }

        public static string GetSceneName(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return "";
            }

            var scene = gameObject.scene;
            if (scene.IsValid() && !string.IsNullOrEmpty(scene.name))
            {
                return scene.name;
            }

            return SceneManager.GetActiveScene().name ?? "";
        }

        public static LinkIdentity CaptureIdentity(GrimoireObjectLink link)
        {
            if (link == null)
            {
                return default;
            }

            return new LinkIdentity
            {
                GameId = GrimoireSettings.GameId,
                ObjectId = link.CachedObjectId ?? "",
                EngineInstanceId = GetEngineInstanceId(link.gameObject),
                Scene = GetSceneName(link.gameObject),
            };
        }

        /// <summary>
        /// Pull the matching <c>game_engine_data</c> entry from Grimoire and
        /// apply location / rotation / scale (and name when present) back onto
        /// the Unity transform.
        /// </summary>
        public static async Task<ApiResult<ObjectViewDocument>> ResetTransformFromGrimoireAsync(
            GrimoireObjectLink link)
        {
            if (link == null)
            {
                return ApiResult<ObjectViewDocument>.Fail("No Grimoire Object Link.", "missing_link");
            }

            if (!GrimoireSettings.IsConfigured)
            {
                return ApiResult<ObjectViewDocument>.Fail(
                    "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).",
                    "not_configured");
            }

            if (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId))
            {
                return ApiResult<ObjectViewDocument>.Fail("No Grimoire object linked.", "missing_key");
            }

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var gameId = GrimoireSettings.GameId;
            var objectId = await ResolveObjectIdAsync(link, gameId);
            if (!objectId.Success)
            {
                return ApiResult<ObjectViewDocument>.Fail(objectId.Error, objectId.Code, objectId.HttpStatus);
            }

            var current = await GrimoireApiClient.GetObjectViewAsync(gameId, objectId.Data, GrimoireSettings.Locale);
            if (!current.Success)
            {
                return ApiResult<ObjectViewDocument>.Fail(current.Error, current.Code, current.HttpStatus);
            }

            var engineInstanceId = GetEngineInstanceId(link.gameObject);
            var scene = GetSceneName(link.gameObject);
            var existing = FindInstance(current.Data?.game_engine_data, engineInstanceId, scene);
            if (existing == null)
            {
                return ApiResult<ObjectViewDocument>.Fail(
                    "No matching game_engine_data entry on Grimoire for this object.",
                    "not_found");
            }

            if (!ApplyInstanceToTransform(link, existing))
            {
                return ApiResult<ObjectViewDocument>.Fail(
                    "Could not apply Grimoire transform to the Unity object.",
                    "apply_failed");
            }

            GrimoireGameEngineDirtyTracker.MarkClean(link);
            DocumentUpdated?.Invoke(current.Data);
            return ApiResult<ObjectViewDocument>.Ok(current.Data);
        }

        /// <summary>
        /// Apply a Grimoire instance onto a local link without fetching. Used
        /// when the Object View Document is already loaded.
        /// </summary>
        public static bool ResetTransformFromInstance(GrimoireObjectLink link, GameEngineInstance instance)
        {
            if (link == null || instance == null)
            {
                return false;
            }

            if (!ApplyInstanceToTransform(link, instance))
            {
                return false;
            }

            GrimoireGameEngineDirtyTracker.MarkClean(link);
            return true;
        }

        public static bool TryFindSceneLink(GameEngineInstance instance, out GrimoireObjectLink link)
        {
            link = null;
            if (instance == null || string.IsNullOrEmpty(instance.engine_instance_id))
            {
                return false;
            }

            var normalized = NormalizeEngineInstanceId(instance.engine_instance_id);
            if (GlobalObjectId.TryParse(normalized, out var globalId))
            {
                var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
                if (obj is GameObject go)
                {
                    link = go.GetComponent<GrimoireObjectLink>();
                    if (link != null)
                    {
                        return true;
                    }
                }
                else if (obj is Component component)
                {
                    link = component.GetComponent<GrimoireObjectLink>()
                           ?? component.GetComponentInParent<GrimoireObjectLink>();
                    if (link != null)
                    {
                        return true;
                    }
                }
            }

            // Fallback: match by GlobalObjectId of every linked object in open scenes.
            foreach (var candidate in Resources.FindObjectsOfTypeAll<GrimoireObjectLink>())
            {
                if (candidate == null || EditorUtility.IsPersistent(candidate))
                {
                    continue;
                }

                var scene = candidate.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                if (!string.Equals(GetSceneName(candidate.gameObject), instance.scene ?? "", StringComparison.Ordinal))
                {
                    continue;
                }

                if (SameInstance(instance, GetEngineInstanceId(candidate.gameObject), instance.scene))
                {
                    link = candidate;
                    return true;
                }
            }

            return false;
        }

        public static bool IsVectorDirty(GameEngineVector3 saved, Vector3 live, bool compareEuler = false)
        {
            if (saved == null)
            {
                return true;
            }

            var savedVec = ToVector3(saved);
            return compareEuler ? !ApproximatelyEuler(savedVec, live) : !Approximately(savedVec, live);
        }

        public static Vector3 ToVector3(GameEngineVector3 value) =>
            value == null ? Vector3.zero : new Vector3((float)value.x, (float)value.y, (float)value.z);

        private static bool ApplyInstanceToTransform(GrimoireObjectLink link, GameEngineInstance instance)
        {
            var transform = link.transform;
            Undo.RecordObject(transform, "Reset transform from Grimoire");
            if (link.SyncIdName)
            {
                Undo.RecordObject(link.gameObject, "Reset name from Grimoire");
            }

            if (link.SyncPosition && instance.location != null)
            {
                transform.position = ToVector3(instance.location);
            }

            if (link.SyncRotation && instance.rotation != null)
            {
                transform.eulerAngles = ToVector3(instance.rotation);
            }

            if (link.SyncScale && instance.scale != null)
            {
                transform.localScale = ToVector3(instance.scale);
            }

            if (link.SyncIdName)
            {
                var name = ExtractNameFromEngineInstanceId(instance.engine_instance_id);
                if (!string.IsNullOrEmpty(name) && link.gameObject.name != name)
                {
                    link.gameObject.name = name;
                }
            }

            EditorUtility.SetDirty(transform);
            EditorUtility.SetDirty(link.gameObject);
            return true;
        }

        private static string ExtractNameFromEngineInstanceId(string engineInstanceId)
        {
            if (string.IsNullOrEmpty(engineInstanceId))
            {
                return null;
            }

            var separator = engineInstanceId.IndexOf('|');
            return separator >= 0 && separator < engineInstanceId.Length - 1
                ? engineInstanceId.Substring(separator + 1)
                : null;
        }

        private const float Epsilon = 0.0001f;

        private static bool Approximately(Vector3 a, Vector3 b) =>
            Mathf.Abs(a.x - b.x) <= Epsilon &&
            Mathf.Abs(a.y - b.y) <= Epsilon &&
            Mathf.Abs(a.z - b.z) <= Epsilon;

        private static bool ApproximatelyEuler(Vector3 a, Vector3 b) =>
            Mathf.Abs(Mathf.DeltaAngle(a.x, b.x)) <= Epsilon &&
            Mathf.Abs(Mathf.DeltaAngle(a.y, b.y)) <= Epsilon &&
            Mathf.Abs(Mathf.DeltaAngle(a.z, b.z)) <= Epsilon;

        /// <summary>
        /// Resolve the linked object if needed, then queue an upsert of this
        /// instance into <c>game_engine_data</c> for review (single-object
        /// commit via PATCH). Prefer <see cref="GrimoireEngineCommit"/> when
        /// the user is committing titled batches from the Sync tab.
        /// </summary>
        public static async Task<ApiResult<EngineCommitQueuedData>> UpsertAsync(
            GrimoireObjectLink link, string title = null, string description = null)
        {
            if (IsPlayModeBlocked)
            {
                return PlayModeBlockedResult<EngineCommitQueuedData>();
            }

            if (link == null)
            {
                return ApiResult<EngineCommitQueuedData>.Fail("No Grimoire Object Link.", "missing_link");
            }

            if (!GrimoireSettings.IsConfigured)
            {
                return ApiResult<EngineCommitQueuedData>.Fail(
                    "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).",
                    "not_configured");
            }

            if (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId))
            {
                return ApiResult<EngineCommitQueuedData>.Fail("No Grimoire object linked.", "missing_key");
            }

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var gameId = GrimoireSettings.GameId;
            var built = await BuildUpsertedGameEngineDataAsync(link);
            if (!built.Success)
            {
                return ApiResult<EngineCommitQueuedData>.Fail(built.Error, built.Code, built.HttpStatus);
            }

            var objectId = await ResolveObjectIdAsync(link, gameId);
            if (!objectId.Success)
            {
                return ApiResult<EngineCommitQueuedData>.Fail(objectId.Error, objectId.Code, objectId.HttpStatus);
            }

            var commitTitle = string.IsNullOrWhiteSpace(title)
                ? $"Unity: {link.gameObject.name}"
                : title;
            var patched = await GrimoireApiClient.PatchObjectGameEngineDataAsync(
                gameId, objectId.Data, built.Data, commitTitle, description);
            if (patched.Success)
            {
                GrimoireGameEngineDirtyTracker.MarkClean(link);
            }

            return patched;
        }

        /// <summary>
        /// Build the full <c>game_engine_data</c> array after upserting this
        /// scene instance (does not POST). Used by engine commits.
        /// </summary>
        public static async Task<ApiResult<GameEngineInstance[]>> BuildUpsertedGameEngineDataAsync(
            GrimoireObjectLink link)
        {
            if (IsPlayModeBlocked)
            {
                return PlayModeBlockedResult<GameEngineInstance[]>();
            }

            if (link == null)
            {
                return ApiResult<GameEngineInstance[]>.Fail("No Grimoire Object Link.", "missing_link");
            }

            if (!GrimoireSettings.IsConfigured)
            {
                return ApiResult<GameEngineInstance[]>.Fail(
                    "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).",
                    "not_configured");
            }

            if (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId))
            {
                return ApiResult<GameEngineInstance[]>.Fail("No Grimoire object linked.", "missing_key");
            }

            var gameId = GrimoireSettings.GameId;
            var objectId = await ResolveObjectIdAsync(link, gameId);
            if (!objectId.Success)
            {
                return ApiResult<GameEngineInstance[]>.Fail(objectId.Error, objectId.Code, objectId.HttpStatus);
            }

            var current = await GrimoireApiClient.GetObjectViewAsync(gameId, objectId.Data, GrimoireSettings.Locale);
            if (!current.Success)
            {
                return ApiResult<GameEngineInstance[]>.Fail(current.Error, current.Code, current.HttpStatus);
            }

            var engineInstanceId = GetEngineInstanceId(link.gameObject);
            var scene = GetSceneName(link.gameObject);
            var existing = FindInstance(current.Data?.game_engine_data, engineInstanceId, scene);
            var built = BuildInstance(link, existing, engineInstanceId, scene);
            return ApiResult<GameEngineInstance[]>.Ok(UpsertInstance(current.Data?.game_engine_data, built));
        }

        public static Task<ApiResult<string>> ResolveObjectIdForCommitAsync(
            GrimoireObjectLink link, string gameId) =>
            ResolveObjectIdAsync(link, gameId);

        /// <summary>
        /// Remove this Unity instance from the linked object's
        /// <c>game_engine_data</c>. Uses <paramref name="identity"/> when the
        /// component can no longer supply live Transform / id data.
        /// </summary>
        public static async Task<ApiResult<EngineCommitQueuedData>> RemoveAsync(
            GrimoireObjectLink link, LinkIdentity? identity = null)
        {
            if (IsPlayModeBlocked)
            {
                return PlayModeBlockedResult<EngineCommitQueuedData>();
            }

            var snap = identity ?? (link != null ? CaptureIdentity(link) : default);

            if (string.IsNullOrEmpty(snap.GameId) ||
                string.IsNullOrEmpty(snap.ObjectId) ||
                string.IsNullOrEmpty(snap.EngineInstanceId))
            {
                return ApiResult<EngineCommitQueuedData>.Fail(
                    "Nothing to remove from game_engine_data.",
                    "missing_identity");
            }

            if (!GrimoireAuthSession.IsSignedIn)
            {
                return ApiResult<EngineCommitQueuedData>.Fail(
                    "Not signed in. Sign in with your Grimoire account first.",
                    "missing_credentials");
            }

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var current = await GrimoireApiClient.GetObjectViewAsync(
                snap.GameId, snap.ObjectId, GrimoireSettings.Locale);
            if (!current.Success)
            {
                return ApiResult<EngineCommitQueuedData>.Fail(current.Error, current.Code, current.HttpStatus);
            }

            var existing = current.Data?.game_engine_data;
            if (existing == null || existing.Length == 0)
            {
                return ApiResult<EngineCommitQueuedData>.Ok(new EngineCommitQueuedData
                {
                    status = "pending",
                    changes = Array.Empty<EngineCommitChangeResult>(),
                });
            }

            var next = RemoveInstance(existing, snap.EngineInstanceId, snap.Scene);
            if (next.Length == existing.Length)
            {
                return ApiResult<EngineCommitQueuedData>.Ok(new EngineCommitQueuedData
                {
                    status = "pending",
                    changes = Array.Empty<EngineCommitChangeResult>(),
                });
            }

            var title = link != null
                ? $"Unity: unlink {link.gameObject.name}"
                : "Unity: remove scene instance";
            return await GrimoireApiClient.PatchObjectGameEngineDataAsync(
                snap.GameId, snap.ObjectId, next.Length == 0 ? null : next, title);
        }

        private static async Task<ApiResult<string>> ResolveObjectIdAsync(GrimoireObjectLink link, string gameId)
        {
            if (!string.IsNullOrEmpty(link.CachedObjectId))
            {
                return ApiResult<string>.Ok(link.CachedObjectId);
            }

            var resolved = await GrimoireObjectKeyResolver.ResolveAsync(gameId, link.ObjectKey);
            if (!resolved.Success)
            {
                return resolved;
            }

            link.CachedObjectId = resolved.Data;
            EditorUtility.SetDirty(link);
            return resolved;
        }

        private static GameEngineInstance BuildInstance(
            GrimoireObjectLink link,
            GameEngineInstance existing,
            string engineInstanceId,
            string scene)
        {
            var transform = link.transform;

            var instance = new GameEngineInstance
            {
                id = existing?.id,
                scene = scene,
                engine_instance_id = link.SyncIdName
                    ? BuildIdName(link.gameObject, engineInstanceId)
                    : (existing?.engine_instance_id ?? engineInstanceId),
                location = link.SyncPosition
                    ? FromVector3(transform.position)
                    : (existing?.location ?? Zero()),
                rotation = link.SyncRotation
                    ? FromVector3(transform.eulerAngles)
                    : (existing?.rotation ?? Zero()),
                scale = link.SyncScale
                    ? FromVector3(transform.localScale)
                    : (existing?.scale ?? One()),
            };

            // Matching key must stay stable even when SyncIdName is off later.
            if (string.IsNullOrEmpty(instance.engine_instance_id))
            {
                instance.engine_instance_id = engineInstanceId;
            }

            return instance;
        }

        /// <summary>
        /// Stable GlobalObjectId, with the GameObject name appended for
        /// readability in Grimoire when Sync Id/Name is enabled.
        /// </summary>
        private static string BuildIdName(GameObject gameObject, string globalObjectId)
        {
            var name = gameObject != null ? gameObject.name : "";
            if (string.IsNullOrEmpty(name))
            {
                return globalObjectId;
            }

            return $"{globalObjectId}|{name}";
        }

        /// <summary>
        /// Strip an optional <c>|name</c> suffix so remove/upsert still match
        /// after renames when comparing against a raw GlobalObjectId.
        /// </summary>
        public static string NormalizeEngineInstanceId(string engineInstanceId)
        {
            if (string.IsNullOrEmpty(engineInstanceId))
            {
                return "";
            }

            var separator = engineInstanceId.IndexOf('|');
            return separator >= 0 ? engineInstanceId.Substring(0, separator) : engineInstanceId;
        }

        private static bool SameInstance(GameEngineInstance instance, string engineInstanceId, string scene)
        {
            if (instance == null)
            {
                return false;
            }

            if (!string.Equals(instance.scene ?? "", scene ?? "", StringComparison.Ordinal))
            {
                return false;
            }

            var left = NormalizeEngineInstanceId(instance.engine_instance_id);
            var right = NormalizeEngineInstanceId(engineInstanceId);
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        private static GameEngineInstance FindInstance(
            GameEngineInstance[] instances, string engineInstanceId, string scene)
        {
            if (instances == null)
            {
                return null;
            }

            foreach (var instance in instances)
            {
                if (SameInstance(instance, engineInstanceId, scene))
                {
                    return instance;
                }
            }

            return null;
        }

        private static GameEngineInstance[] UpsertInstance(
            GameEngineInstance[] current, GameEngineInstance built)
        {
            var list = new List<GameEngineInstance>();
            var replaced = false;

            if (current != null)
            {
                foreach (var instance in current)
                {
                    if (SameInstance(instance, built.engine_instance_id, built.scene))
                    {
                        list.Add(built);
                        replaced = true;
                    }
                    else
                    {
                        list.Add(instance);
                    }
                }
            }

            if (!replaced)
            {
                list.Add(built);
            }

            return list.ToArray();
        }

        private static GameEngineInstance[] RemoveInstance(
            GameEngineInstance[] current, string engineInstanceId, string scene)
        {
            var list = new List<GameEngineInstance>();
            foreach (var instance in current)
            {
                if (!SameInstance(instance, engineInstanceId, scene))
                {
                    list.Add(instance);
                }
            }

            return list.ToArray();
        }

        private static GameEngineVector3 FromVector3(Vector3 value) =>
            new GameEngineVector3 { x = value.x, y = value.y, z = value.z };

        private static GameEngineVector3 Zero() =>
            new GameEngineVector3 { x = 0, y = 0, z = 0 };

        private static GameEngineVector3 One() =>
            new GameEngineVector3 { x = 1, y = 1, z = 1 };
    }
}
