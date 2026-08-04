using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Resolves an object key (<c>code_id</c>) to the object's UUID.
    ///
    /// The public API has no code_id filter on GET /api/v1/objects, so
    /// resolution pages through the listing (max page size 200) and matches
    /// client-side. Every summary seen on the way is cached, so resolving one
    /// key warms the cache for the whole library and later lookups are free
    /// until the cache is invalidated.
    /// </summary>
    public static class GrimoireObjectKeyResolver
    {
        private const int PageSize = 200;

        /// <summary>A stale hit only survives until the widget refetch 404s, so a short TTL is enough.</summary>
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

        private class GameCache
        {
            public readonly Dictionary<string, string> IdByCodeId =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public DateTime BuiltAtUtc;
            public bool Complete;
        }

        private static readonly Dictionary<string, GameCache> Caches = new Dictionary<string, GameCache>();

        /// <summary>
        /// Returns the UUID for <paramref name="codeId"/>, or a failure whose
        /// error explains what went wrong (no match, API error, ...).
        /// </summary>
        public static async Task<ApiResult<string>> ResolveAsync(string gameId, string codeId)
        {
            if (string.IsNullOrWhiteSpace(codeId))
            {
                return ApiResult<string>.Fail("No object key set.", "missing_key");
            }

            codeId = codeId.Trim();

            if (TryGetCached(gameId, codeId, out var cachedId))
            {
                return ApiResult<string>.Ok(cachedId);
            }

            var cache = new GameCache { BuiltAtUtc = DateTime.UtcNow };
            var offset = 0;

            while (true)
            {
                var page = await GrimoireApiClient.ListObjectsAsync(gameId, limit: PageSize, offset: offset);
                if (!page.Success)
                {
                    return ApiResult<string>.Fail(page.Error, page.Code, page.HttpStatus);
                }

                foreach (var summary in page.Data)
                {
                    if (!string.IsNullOrEmpty(summary.code_id))
                    {
                        cache.IdByCodeId[summary.code_id] = summary.id;
                    }
                }

                if (page.Data.Length < PageSize)
                {
                    cache.Complete = true;
                    break;
                }

                // Early out once the key is present; the partial cache is still
                // useful for other keys seen so far.
                if (cache.IdByCodeId.ContainsKey(codeId))
                {
                    break;
                }

                offset += PageSize;
            }

            Caches[gameId] = cache;

            if (cache.IdByCodeId.TryGetValue(codeId, out var id))
            {
                return ApiResult<string>.Ok(id);
            }

            return ApiResult<string>.Fail(
                $"No object with key '{codeId}' found in this game. Check the key against the object's Code ID in Grimoire.",
                "key_not_found");
        }

        /// <summary>Record a key→id pair learned elsewhere (e.g. the object picker).</summary>
        public static void Remember(string gameId, string codeId, string objectId)
        {
            if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(codeId) || string.IsNullOrWhiteSpace(objectId))
            {
                return;
            }

            if (!Caches.TryGetValue(gameId, out var cache))
            {
                cache = new GameCache { BuiltAtUtc = DateTime.UtcNow };
                Caches[gameId] = cache;
            }

            cache.IdByCodeId[codeId.Trim()] = objectId;
        }

        public static void InvalidateCache(string gameId = null)
        {
            if (gameId == null)
            {
                Caches.Clear();
            }
            else
            {
                Caches.Remove(gameId);
            }
        }

        private static bool TryGetCached(string gameId, string codeId, out string objectId)
        {
            objectId = null;

            if (!Caches.TryGetValue(gameId, out var cache))
            {
                return false;
            }

            if (DateTime.UtcNow - cache.BuiltAtUtc > CacheLifetime)
            {
                Caches.Remove(gameId);
                return false;
            }

            // A missing key in an incomplete cache proves nothing — the object
            // may live on a page that was never fetched.
            if (cache.IdByCodeId.TryGetValue(codeId, out objectId))
            {
                return true;
            }

            return false;
        }
    }
}
