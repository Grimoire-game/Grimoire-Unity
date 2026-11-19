using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Grimoire.PluginV2.Editor
{
    internal static class GrimoireLogicDependencies
    {
        internal static async Task<ApiResult<bool>> LoadAsync(
            string gameId, GrimoireObjectSnapshot snapshot, GrimoireTranslationCatalog catalog,
            Action<string> progress, IReadOnlyDictionary<string, GrimoireDialogAsset> syncedDialogs)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var usesDialogField = false;
            var usesVariables = false;
            foreach (var fn in snapshot.Logic.functions ?? Array.Empty<GrimoireObjectLogic.Function>())
            foreach (var step in fn.steps ?? Array.Empty<GrimoireObjectLogic.Step>())
            {
                foreach (var dialog in step.dialogs ?? Array.Empty<GrimoireObjectLogic.Dialog>())
                {
                    if (!string.IsNullOrEmpty(dialog.id)) ids.Add(dialog.id);
                    if (string.IsNullOrEmpty(dialog.field)) continue;
                    usesDialogField = true;
                    if (snapshot.TryGetField(dialog.field, out var field))
                        ids.UnionWith(field.ReferencedIds());
                }
                var expressions = (step.conditions ?? Array.Empty<GrimoireObjectLogic.Group>())
                    .SelectMany(group => group.terms ?? Array.Empty<GrimoireObjectLogic.Expression>())
                    .Concat(step.sets ?? Array.Empty<GrimoireObjectLogic.Expression>());
                usesVariables |= expressions.Any(row => row.left.kind == "variable" || row.right.kind == "variable");
                // A setter can select another dialog for a later step or interaction.
                foreach (var set in step.sets ?? Array.Empty<GrimoireObjectLogic.Expression>())
                {
                    if (set.left.kind == "field" && set.right.kind == "string" &&
                        snapshot.TryGetField(set.left.value, out var field) && field.FieldType == "dialog" &&
                        !string.IsNullOrEmpty(set.right.value)) ids.Add(set.right.value);
                }
            }

            if (usesVariables && syncedDialogs == null)
            {
                var variables = await GrimoireVariableSync.SyncAsync(gameId, progress, catalog);
                if (!variables.Success) return ApiResult<bool>.Fail(variables.Error, variables.Code, variables.HttpStatus);
            }

            foreach (var id in ids)
            {
                GrimoireDialogAsset asset;
                if (syncedDialogs == null)
                {
                    var imported = await GrimoireDialogImporter.ImportAsync(gameId, id, progress, catalog);
                    if (!imported.Success)
                        return ApiResult<bool>.Fail($"Logic dialog '{id}': {imported.Error}", imported.Code, imported.HttpStatus);
                    asset = imported.Data;
                }
                else if (!syncedDialogs.TryGetValue(id, out asset))
                    return ApiResult<bool>.Fail($"Logic dialog '{id}' was not synced.");
                snapshot.LogicDialogs.Add(asset);
            }

            // A field can change during play. Retain imported alternatives from this game.
            if (usesDialogField)
            {
                var available = syncedDialogs ?? GrimoireDialogImporter.FindAllImported();
                foreach (var asset in available.Values)
                    if (asset && asset.GameId == gameId && ids.Add(asset.DialogId)) snapshot.LogicDialogs.Add(asset);
            }
            return ApiResult<bool>.Ok(true);
        }
    }
}
