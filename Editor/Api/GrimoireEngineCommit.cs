using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Builds and posts engine commits via <c>POST /api/v1/engine-commits</c>.
    /// Changes are queued for review in Grimoire — they are not applied immediately.
    /// </summary>
    public static class GrimoireEngineCommit
    {
        public static event Action Committed;

        /// <summary>
        /// Queue transform and/or editable-field changes for the given links as
        /// one titled commit. Marks local dirty state clean on success.
        /// </summary>
        public static async Task<ApiResult<EngineCommitCreatedData>> CommitAsync(
            string title,
            string description,
            IReadOnlyList<GrimoireObjectLink> links,
            bool includeEngineData,
            bool includeEditableFields)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return ApiResult<EngineCommitCreatedData>.Fail(
                    "Enter a commit title before pushing changes.",
                    "missing_parameter");
            }

            if (!GrimoireSettings.IsConfigured)
            {
                return ApiResult<EngineCommitCreatedData>.Fail(
                    "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).",
                    "not_configured");
            }

            if (links == null || links.Count == 0)
            {
                return ApiResult<EngineCommitCreatedData>.Fail(
                    "No objects to commit.",
                    "missing_parameter");
            }

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var gameId = GrimoireSettings.GameId;
            var changes = new List<EngineCommitChangeRequest>();
            var touchedLinks = new List<GrimoireObjectLink>();
            var includeFieldsForLoaded = includeEditableFields;

            foreach (var link in links)
            {
                if (link == null)
                {
                    continue;
                }

                var objectId = await GrimoireGameEngineSync.ResolveObjectIdForCommitAsync(link, gameId);
                if (!objectId.Success)
                {
                    return ApiResult<EngineCommitCreatedData>.Fail(
                        objectId.Error, objectId.Code, objectId.HttpStatus);
                }

                var added = false;

                if (includeEngineData && GrimoireGameEngineDirtyTracker.IsGameEngineDirty(link))
                {
                    var built = await GrimoireGameEngineSync.BuildUpsertedGameEngineDataAsync(link);
                    if (!built.Success)
                    {
                        return ApiResult<EngineCommitCreatedData>.Fail(
                            built.Error, built.Code, built.HttpStatus);
                    }

                    changes.Add(new EngineCommitChangeRequest
                    {
                        object_id = objectId.Data,
                        game_engine_data = built.Data,
                    });
                    added = true;
                }

                if (includeFieldsForLoaded &&
                    GrimoireEditableFieldsRenderer.HasDirtyEditsForObject(objectId.Data))
                {
                    if (!GrimoireEditableFieldsRenderer.TryBuildDirtyFieldChanges(
                            out var fieldChanges, out var fieldError))
                    {
                        return ApiResult<EngineCommitCreatedData>.Fail(
                            fieldError ?? "Could not build field changes.",
                            "invalid_value");
                    }

                    foreach (var field in fieldChanges)
                    {
                        changes.Add(new EngineCommitChangeRequest
                        {
                            object_id = objectId.Data,
                            field_id = field.id,
                            value = field.value,
                            base_value = field.base_value,
                        });
                    }

                    added = true;
                }

                if (added)
                {
                    touchedLinks.Add(link);
                }
            }

            if (changes.Count == 0)
            {
                return ApiResult<EngineCommitCreatedData>.Fail(
                    "Nothing to commit.",
                    "missing_parameter");
            }

            if (changes.Count > 500)
            {
                return ApiResult<EngineCommitCreatedData>.Fail(
                    $"A commit can include at most 500 changes (got {changes.Count}).",
                    "too_many_changes");
            }

            var result = await GrimoireApiClient.CreateEngineCommitAsync(
                gameId, title, description, changes);
            if (!result.Success)
            {
                return result;
            }

            foreach (var link in touchedLinks)
            {
                if (includeEngineData)
                {
                    GrimoireGameEngineDirtyTracker.MarkClean(link);
                }
            }

            if (includeFieldsForLoaded)
            {
                GrimoireEditableFieldsRenderer.AcceptSubmittedEdits();
            }

            Committed?.Invoke();
            return result;
        }

        /// <summary>
        /// Queue only the loaded object's dirty editable fields as one commit.
        /// </summary>
        public static async Task<ApiResult<EngineCommitCreatedData>> CommitLoadedFieldsAsync(
            string title, string description)
        {
            var objectId = GrimoireEditableFieldsRenderer.LoadedObjectId;
            if (string.IsNullOrEmpty(objectId))
            {
                return ApiResult<EngineCommitCreatedData>.Fail(
                    "No object loaded.",
                    "missing_parameter");
            }

            GrimoireObjectLink link = null;
            var scratch = new List<GrimoireObjectLink>();
            GrimoireGameEngineDirtyTracker.CollectSceneLinks(scratch);
            foreach (var candidate in scratch)
            {
                if (candidate != null &&
                    string.Equals(candidate.CachedObjectId, objectId, StringComparison.Ordinal))
                {
                    link = candidate;
                    break;
                }
            }

            // Field-only commits do not require a scene link; synthesize a
            // temporary holder so CommitAsync can resolve the object id.
            if (link == null)
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    return ApiResult<EngineCommitCreatedData>.Fail(
                        "Enter a commit title before pushing changes.",
                        "missing_parameter");
                }

                if (!GrimoireSettings.IsConfigured)
                {
                    return ApiResult<EngineCommitCreatedData>.Fail(
                        "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).",
                        "not_configured");
                }

                await GrimoireAuthSession.EnsureFreshTokenAsync();

                if (!GrimoireEditableFieldsRenderer.TryBuildDirtyFieldChanges(
                        out var fieldChanges, out var fieldError))
                {
                    return ApiResult<EngineCommitCreatedData>.Fail(
                        fieldError ?? "Could not build field changes.",
                        "invalid_value");
                }

                if (fieldChanges.Count == 0)
                {
                    return ApiResult<EngineCommitCreatedData>.Fail(
                        "Nothing to commit.",
                        "missing_parameter");
                }

                var changes = new List<EngineCommitChangeRequest>(fieldChanges.Count);
                foreach (var field in fieldChanges)
                {
                    changes.Add(new EngineCommitChangeRequest
                    {
                        object_id = objectId,
                        field_id = field.id,
                        value = field.value,
                        base_value = field.base_value,
                    });
                }

                var result = await GrimoireApiClient.CreateEngineCommitAsync(
                    GrimoireSettings.GameId, title, description, changes);
                if (result.Success)
                {
                    GrimoireEditableFieldsRenderer.AcceptSubmittedEdits();
                    Committed?.Invoke();
                }

                return result;
            }

            return await CommitAsync(
                title,
                description,
                new[] { link },
                includeEngineData: false,
                includeEditableFields: true);
        }

        public static string FormatConflictSummary(EngineCommitChangeResult[] changes)
        {
            if (changes == null || changes.Length == 0)
            {
                return null;
            }

            var conflicts = 0;
            foreach (var change in changes)
            {
                if (change != null &&
                    string.Equals(change.state, "conflict", StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                }
            }

            if (conflicts == 0)
            {
                return null;
            }

            return conflicts == 1
                ? "1 change conflicts with a newer value in Grimoire."
                : $"{conflicts} changes conflict with newer values in Grimoire.";
        }
    }
}
