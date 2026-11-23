using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>Downloads a source Object View plus every configured locale.</summary>
    internal static class GrimoireLocalizedObjectLoader
    {
        internal sealed class LoadedObject
        {
            public ObjectViewDocument Source;
            public GrimoireObjectSnapshot Snapshot;
        }

        public static async Task<ApiResult<LoadedObject>> LoadAsync(
            string gameId,
            string objectId,
            GrimoireTranslationCatalog catalog = null,
            Action<string> progress = null,
            bool includeLogic = false,
            IReadOnlyDictionary<string, GrimoireDialogAsset> syncedDialogs = null)
        {
            progress?.Invoke("Downloading object source...");
            var source = await GrimoireApiClient.GetObjectViewAsync(gameId, objectId, includeLogic: includeLogic);
            if (!source.Success)
            {
                return ApiResult<LoadedObject>.Fail(
                    source.Error, source.Code, source.HttpStatus);
            }
            if (source.Data?.@object == null ||
                !string.Equals(source.Data.@object.id, objectId, StringComparison.OrdinalIgnoreCase))
                return ApiResult<LoadedObject>.Fail("The server returned an unexpected object.", "malformed_response");

            IReadOnlyList<string> languages;
            string sourceLanguage;
            if (catalog != null)
            {
                languages = catalog.Languages;
                sourceLanguage = catalog.SourceLanguage;
            }
            else
            {
                var games = await GrimoireApiClient.ListGamesAsync(gameId: gameId);
                if (!games.Success)
                {
                    return ApiResult<LoadedObject>.Fail(
                        games.Error, games.Code, games.HttpStatus);
                }

                var entry = FindGame(games.Data, gameId);
                if (entry == null)
                {
                    return ApiResult<LoadedObject>.Fail(
                        "The selected game is no longer available.", "game_not_found");
                }

                sourceLanguage = entry.default_language?.Trim() ?? "";
                languages = UniqueLanguages(entry.languages, sourceLanguage);
            }

            var snapshot = GrimoireObjectSnapshotMapper.FromDocument(source.Data);
            if (includeLogic) snapshot.SyncedSourceRevision = source.Data?.@object?.updated_at;
            var locales = languages.Where(language => !string.IsNullOrWhiteSpace(language) &&
                !string.Equals(language, sourceLanguage, StringComparison.OrdinalIgnoreCase)).ToArray();
            const int maxConcurrentLocales = 3;
            for (var start = 0; start < locales.Length; start += maxConcurrentLocales)
            {
                var batch = locales.Skip(start).Take(maxConcurrentLocales).ToArray();
                progress?.Invoke($"Downloading object ({string.Join(", ", batch)})...");
                var results = await Task.WhenAll(batch.Select(language =>
                    GrimoireApiClient.GetObjectViewAsync(gameId, objectId, language)));
                for (var i = 0; i < results.Length; i++)
                {
                    var localized = results[i];
                    if (!localized.Success)
                        return ApiResult<LoadedObject>.Fail(
                            $"Could not download object locale '{batch[i]}': {localized.Error}",
                            localized.Code, localized.HttpStatus);
                    if (!string.IsNullOrWhiteSpace(localized.Data?.locale?.resolved))
                        GrimoireObjectSnapshotMapper.MergeLocalizedDocument(snapshot, localized.Data, batch[i]);
                }
            }

            if (includeLogic && snapshot.Logic?.version == 1)
            {
                var dependencies = await GrimoireLogicDependencies.LoadAsync(
                    gameId, snapshot, catalog, progress, syncedDialogs);
                if (!dependencies.Success)
                    return ApiResult<LoadedObject>.Fail(dependencies.Error, dependencies.Code, dependencies.HttpStatus);
            }

            return ApiResult<LoadedObject>.Ok(new LoadedObject
            {
                Source = source.Data,
                Snapshot = snapshot,
            });
        }

        private static GameDirectoryEntry FindGame(GameDirectoryEntry[] games, string gameId)
        {
            foreach (var game in games ?? Array.Empty<GameDirectoryEntry>())
            {
                if (game != null && string.Equals(game.id, gameId, StringComparison.Ordinal))
                {
                    return game;
                }
            }

            return null;
        }

        private static IReadOnlyList<string> UniqueLanguages(
            string[] languages,
            string sourceLanguage)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(sourceLanguage);
            if (languages != null)
            {
                foreach (var language in languages)
                {
                    Add(language);
                }
            }

            return result;

            void Add(string language)
            {
                var code = language?.Trim();
                if (!string.IsNullOrEmpty(code) && seen.Add(code))
                {
                    result.Add(code);
                }
            }
        }
    }
}
