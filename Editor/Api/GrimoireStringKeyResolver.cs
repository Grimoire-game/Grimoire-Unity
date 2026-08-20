using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Resolves a Grimoire string code (abbrev, section/abbrev, or UUID) to the
    /// string id. GET /api/v1/strings returns the full library, so matches are
    /// client-side with caching similar to <see cref="GrimoireObjectKeyResolver"/>.
    /// </summary>
    public static class GrimoireStringKeyResolver
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

        private class GameCache
        {
            public readonly Dictionary<string, StringResource> ById =
                new Dictionary<string, StringResource>(StringComparer.OrdinalIgnoreCase);

            public readonly Dictionary<string, StringResource> ByAbbrev =
                new Dictionary<string, StringResource>(StringComparer.OrdinalIgnoreCase);

            public readonly Dictionary<string, StringResource> BySectionAbbrev =
                new Dictionary<string, StringResource>(StringComparer.OrdinalIgnoreCase);

            public DateTime BuiltAtUtc;
            public bool Complete;
        }

        private static readonly Dictionary<string, GameCache> Caches = new Dictionary<string, GameCache>();

        public static async Task<ApiResult<StringResource>> ResolveAsync(string gameId, string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return ApiResult<StringResource>.Fail("No string code set.", "missing_key");
            }

            code = code.Trim();

            if (TryGetCached(gameId, code, out var cached))
            {
                return ApiResult<StringResource>.Ok(cached);
            }

            var cache = new GameCache { BuiltAtUtc = DateTime.UtcNow };
            var result = await GrimoireApiClient.ListStringsAsync(
                gameId, includeTranslations: true, includeContext: false);
            if (!result.Success)
            {
                return ApiResult<StringResource>.Fail(result.Error, result.Code, result.HttpStatus);
            }

            foreach (var row in result.Data)
            {
                RememberInto(cache, row);
            }

            cache.Complete = true;
            Caches[gameId] = cache;

            if (TryGetFromCache(cache, code, out cached))
            {
                return ApiResult<StringResource>.Ok(cached);
            }

            return ApiResult<StringResource>.Fail(
                $"No string with code '{code}' found in this game. Use the string abbrev or UUID from Grimoire.",
                "key_not_found");
        }

        public static void Remember(string gameId, StringResource resource)
        {
            if (string.IsNullOrWhiteSpace(gameId) || resource == null || string.IsNullOrEmpty(resource.id))
            {
                return;
            }

            if (!Caches.TryGetValue(gameId, out var cache))
            {
                cache = new GameCache { BuiltAtUtc = DateTime.UtcNow };
                Caches[gameId] = cache;
            }

            RememberInto(cache, resource);
        }

        public static void Invalidate(string gameId = null)
        {
            if (string.IsNullOrEmpty(gameId))
            {
                Caches.Clear();
                return;
            }

            Caches.Remove(gameId);
        }

        private static bool TryGetCached(string gameId, string code, out StringResource resource)
        {
            resource = null;
            if (!Caches.TryGetValue(gameId, out var cache))
            {
                return false;
            }

            if (DateTime.UtcNow - cache.BuiltAtUtc > CacheLifetime)
            {
                Caches.Remove(gameId);
                return false;
            }

            return TryGetFromCache(cache, code, out resource);
        }

        private static bool TryGetFromCache(GameCache cache, string code, out StringResource resource)
        {
            resource = null;
            if (cache == null || string.IsNullOrEmpty(code))
            {
                return false;
            }

            if (cache.ById.TryGetValue(code, out resource))
            {
                return true;
            }

            if (cache.ByAbbrev.TryGetValue(code, out resource))
            {
                return true;
            }

            if (cache.BySectionAbbrev.TryGetValue(code, out resource))
            {
                return true;
            }

            return false;
        }

        private static void RememberInto(GameCache cache, StringResource row)
        {
            if (cache == null || row == null || string.IsNullOrEmpty(row.id))
            {
                return;
            }

            cache.ById[row.id] = row;

            if (!string.IsNullOrEmpty(row.abbrev))
            {
                cache.ByAbbrev[row.abbrev.Trim()] = row;
            }

            if (!string.IsNullOrEmpty(row.section) && !string.IsNullOrEmpty(row.abbrev))
            {
                cache.BySectionAbbrev[$"{row.section.Trim()}/{row.abbrev.Trim()}"] = row;
            }
        }
    }
}
