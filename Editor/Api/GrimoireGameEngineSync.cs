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
    /// </summary>
    public static class GrimoireGameEngineSync
    {
        public static event Action<ObjectViewDocument> DocumentUpdated;

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
        /// Resolve the linked object if needed, then upsert this instance into
        /// <c>game_engine_data</c> according to the link's sync toggles.
        /// </summary>
        public static async Task<ApiResult<ObjectViewDocument>> UpsertAsync(GrimoireObjectLink link)
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
            var built = BuildInstance(link, existing, engineInstanceId, scene);

            var next = UpsertInstance(current.Data?.game_engine_data, built);
            var patched = await GrimoireApiClient.PatchObjectGameEngineDataAsync(gameId, objectId.Data, next);
            if (patched.Success)
            {
                DocumentUpdated?.Invoke(patched.Data);
            }

            return patched;
        }

        /// <summary>
        /// Remove this Unity instance from the linked object's
        /// <c>game_engine_data</c>. Uses <paramref name="identity"/> when the
        /// component can no longer supply live Transform / id data.
        /// </summary>
        public static async Task<ApiResult<ObjectViewDocument>> RemoveAsync(
            GrimoireObjectLink link, LinkIdentity? identity = null)
        {
            var snap = identity ?? (link != null ? CaptureIdentity(link) : default);

            if (string.IsNullOrEmpty(snap.GameId) ||
                string.IsNullOrEmpty(snap.ObjectId) ||
                string.IsNullOrEmpty(snap.EngineInstanceId))
            {
                return ApiResult<ObjectViewDocument>.Fail(
                    "Nothing to remove from game_engine_data.",
                    "missing_identity");
            }

            if (!GrimoireAuthSession.IsSignedIn)
            {
                return ApiResult<ObjectViewDocument>.Fail(
                    "Not signed in. Sign in with your Grimoire account first.",
                    "missing_credentials");
            }

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var current = await GrimoireApiClient.GetObjectViewAsync(
                snap.GameId, snap.ObjectId, GrimoireSettings.Locale);
            if (!current.Success)
            {
                return ApiResult<ObjectViewDocument>.Fail(current.Error, current.Code, current.HttpStatus);
            }

            var existing = current.Data?.game_engine_data;
            if (existing == null || existing.Length == 0)
            {
                return ApiResult<ObjectViewDocument>.Ok(current.Data);
            }

            var next = RemoveInstance(existing, snap.EngineInstanceId, snap.Scene);
            if (next.Length == existing.Length)
            {
                return ApiResult<ObjectViewDocument>.Ok(current.Data);
            }

            var patched = await GrimoireApiClient.PatchObjectGameEngineDataAsync(
                snap.GameId, snap.ObjectId, next.Length == 0 ? null : next);
            if (patched.Success)
            {
                DocumentUpdated?.Invoke(patched.Data);
            }

            return patched;
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
