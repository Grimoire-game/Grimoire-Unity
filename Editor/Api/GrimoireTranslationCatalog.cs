using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// One operation-scoped download of the game's languages and translated
    /// string library. Importers share it to avoid repeatedly downloading the
    /// same catalog.
    /// </summary>
    public sealed class GrimoireTranslationCatalog
    {
        private readonly Dictionary<string, StringResource> _byId =
            new Dictionary<string, StringResource>(StringComparer.Ordinal);

        private GrimoireTranslationCatalog(
            string gameId,
            string sourceLanguage,
            IReadOnlyList<string> languages,
            StringResource[] strings)
        {
            GameId = gameId ?? "";
            SourceLanguage = sourceLanguage ?? "";
            Languages = languages ?? Array.Empty<string>();
            Strings = strings ?? Array.Empty<StringResource>();

            foreach (var resource in Strings)
            {
                if (resource != null && !string.IsNullOrEmpty(resource.id))
                {
                    _byId[resource.id] = resource;
                }
            }
        }

        public string GameId { get; }
        public string SourceLanguage { get; }
        public IReadOnlyList<string> Languages { get; }
        public IReadOnlyList<StringResource> Strings { get; }

        public bool TryGet(string stringId, out StringResource resource)
        {
            resource = null;
            return !string.IsNullOrEmpty(stringId) && _byId.TryGetValue(stringId, out resource);
        }

        public bool TryResolve(string idOrCode, out StringResource resource)
        {
            resource = null;
            if (string.IsNullOrWhiteSpace(idOrCode))
            {
                return false;
            }

            var query = idOrCode.Trim();
            if (_byId.TryGetValue(query, out resource))
            {
                return true;
            }

            foreach (var candidate in Strings)
            {
                if (candidate == null)
                {
                    continue;
                }

                if (string.Equals(candidate.abbrev, query, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        string.IsNullOrEmpty(candidate.section)
                            ? candidate.abbrev
                            : candidate.section + "/" + candidate.abbrev,
                        query,
                        StringComparison.OrdinalIgnoreCase))
                {
                    resource = candidate;
                    return true;
                }
            }

            return false;
        }

        public List<GrimoireLocalizedText> GetTranslations(string stringId)
        {
            var result = new List<GrimoireLocalizedText>();
            if (!TryGet(stringId, out var resource) || resource.translations == null)
            {
                return result;
            }

            foreach (var row in resource.translations)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.language_code) ||
                    string.IsNullOrEmpty(row.translated_text))
                {
                    continue;
                }

                result.Add(new GrimoireLocalizedText(row.language_code.Trim(), row.translated_text));
            }

            return result;
        }

        public static async Task<ApiResult<GrimoireTranslationCatalog>> LoadAsync(
            string gameId,
            Action<string> progress = null)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return ApiResult<GrimoireTranslationCatalog>.Fail(
                    "Choose a game first.", "missing_parameter");
            }

            progress?.Invoke("Downloading game languages...");
            var games = await GrimoireApiClient.ListGamesAsync(gameId: gameId);
            if (!games.Success)
            {
                return ApiResult<GrimoireTranslationCatalog>.Fail(
                    games.Error, games.Code, games.HttpStatus);
            }

            GameDirectoryEntry game = null;
            foreach (var entry in games.Data ?? Array.Empty<GameDirectoryEntry>())
            {
                if (entry != null && string.Equals(entry.id, gameId, StringComparison.Ordinal))
                {
                    game = entry;
                    break;
                }
            }

            if (game == null)
            {
                return ApiResult<GrimoireTranslationCatalog>.Fail(
                    "The selected game is no longer available.", "game_not_found");
            }

            progress?.Invoke("Downloading translations...");
            var strings = await GrimoireApiClient.ListStringsAsync(
                gameId, includeTranslations: true, includeContext: false);
            if (!strings.Success)
            {
                return ApiResult<GrimoireTranslationCatalog>.Fail(
                    strings.Error, strings.Code, strings.HttpStatus);
            }

            var languages = NormalizeLanguages(game.languages, game.default_language);
            return ApiResult<GrimoireTranslationCatalog>.Ok(
                new GrimoireTranslationCatalog(
                    gameId,
                    game.default_language?.Trim(),
                    languages,
                    strings.Data));
        }

        private static IReadOnlyList<string> NormalizeLanguages(
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
