using System;
using System.Collections.Generic;
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
            Action<string> progress = null)
        {
            progress?.Invoke("Downloading object source...");
            var source = await GrimoireApiClient.GetObjectViewAsync(gameId, objectId);
            if (!source.Success)
            {
                return ApiResult<LoadedObject>.Fail(
                    source.Error, source.Code, source.HttpStatus);
            }

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
            foreach (var language in languages)
            {
                if (string.IsNullOrWhiteSpace(language) ||
                    string.Equals(language, sourceLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                progress?.Invoke($"Downloading object ({language})...");
                var localized = await GrimoireApiClient.GetObjectViewAsync(gameId, objectId, language);
                if (!localized.Success)
                {
                    return ApiResult<LoadedObject>.Fail(
                        $"Could not download object locale '{language}': {localized.Error}",
                        localized.Code,
                        localized.HttpStatus);
                }

                if (string.IsNullOrWhiteSpace(localized.Data?.locale?.resolved))
                {
                    continue;
                }

                GrimoireObjectSnapshotMapper.MergeLocalizedDocument(
                    snapshot, localized.Data, language);
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
