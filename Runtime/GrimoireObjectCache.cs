using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Optional API-backed loader used for lazy nested fetches and Editor Play
    /// Mode refresh. The editor assembly registers an implementation; player
    /// builds leave this null so scripts only see snapshotted data.
    /// </summary>
    public interface IGrimoireObjectLoader
    {
        Task<GrimoireObjectSnapshot> LoadAsync(string objectId, string objectKey);
    }

    /// <summary>
    /// In-memory index of object snapshots, keyed by UUID and <c>code_id</c>.
    /// Seeded from scene Object Links; lazy loads go through
    /// <see cref="Loader"/> when one is registered.
    /// </summary>
    public static class GrimoireObjectCache
    {
        private static readonly Dictionary<string, GrimoireObjectSnapshot> ById =
            new Dictionary<string, GrimoireObjectSnapshot>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, GrimoireObjectSnapshot> ByKey =
            new Dictionary<string, GrimoireObjectSnapshot>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Task<GrimoireObjectSnapshot>> InFlight =
            new Dictionary<string, Task<GrimoireObjectSnapshot>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Editor-registered fetcher. Null in player builds.</summary>
        public static IGrimoireObjectLoader Loader { get; set; }

        /// <summary>Fired after a play-mode root refresh or a cache-wide replace.</summary>
        public static event Action Updated;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Clear();
            Updated = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void SeedFromScene()
        {
            var links = UnityEngine.Object.FindObjectsOfType<GrimoireObjectLink>(true);
            if (links == null)
            {
                return;
            }

            for (var i = 0; i < links.Length; i++)
            {
                Register(links[i]);
            }
        }

        public static void Clear()
        {
            ById.Clear();
            ByKey.Clear();
            InFlight.Clear();
        }

        public static void NotifyUpdated() => Updated?.Invoke();

        public static void Register(GrimoireObjectLink link)
        {
            if (link == null)
            {
                return;
            }

            Register(link.Snapshot);
            var nested = link.NestedSnapshots;
            if (nested == null)
            {
                return;
            }

            for (var i = 0; i < nested.Count; i++)
            {
                Register(nested[i]);
            }
        }

        public static void Register(GrimoireObjectSnapshot snapshot)
        {
            if (snapshot == null || snapshot.IsEmpty)
            {
                return;
            }

            if (!string.IsNullOrEmpty(snapshot.ObjectId))
            {
                ById[snapshot.ObjectId] = snapshot;
            }

            if (!string.IsNullOrEmpty(snapshot.ObjectKey))
            {
                ByKey[snapshot.ObjectKey] = snapshot;
            }
        }

        public static bool TryGet(string idOrKey, out GrimoireObjectSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrEmpty(idOrKey))
            {
                return false;
            }

            return ById.TryGetValue(idOrKey, out snapshot)
                   || ByKey.TryGetValue(idOrKey, out snapshot);
        }

        public static bool TryGet(GrimoireObjectRef stub, out GrimoireObjectSnapshot snapshot)
        {
            snapshot = null;
            if (stub == null)
            {
                return false;
            }

            if (stub.HasId && ById.TryGetValue(stub.Id, out snapshot))
            {
                return true;
            }

            return stub.HasKey && ByKey.TryGetValue(stub.CodeId, out snapshot);
        }

        /// <summary>
        /// Returns a cached snapshot, or fetches via <see cref="Loader"/> on a miss.
        /// Returns null when there is no cache hit and no loader (typical in builds).
        /// </summary>
        public static async Task<GrimoireObjectSnapshot> LoadAsync(GrimoireObjectRef stub)
        {
            if (TryGet(stub, out var cached))
            {
                return cached;
            }

            if (stub == null || (!stub.HasId && !stub.HasKey))
            {
                return null;
            }

            if (Loader == null)
            {
                Debug.LogWarning(
                    "[Grimoire] Nested object '"
                    + stub.DisplayName
                    + "' is not in the snapshot cache. Prefetch it from the Object Link in the editor.");
                return null;
            }

            var flightKey = stub.HasId ? "id:" + stub.Id : "key:" + stub.CodeId;
            if (InFlight.TryGetValue(flightKey, out var pending))
            {
                return await pending;
            }

            var task = LoadUncachedAsync(stub);
            InFlight[flightKey] = task;
            try
            {
                var loaded = await task;
                if (loaded != null)
                {
                    Register(loaded);
                }

                return loaded;
            }
            finally
            {
                InFlight.Remove(flightKey);
            }
        }

        private static async Task<GrimoireObjectSnapshot> LoadUncachedAsync(GrimoireObjectRef stub)
        {
            try
            {
                var loaded = await Loader.LoadAsync(stub.Id, stub.CodeId);
                if (loaded == null)
                {
                    Debug.LogWarning(
                        "[Grimoire] Could not load nested object '" + stub.DisplayName + "'.");
                }

                return loaded;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[Grimoire] Nested object load failed for '" + stub.DisplayName + "': " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>Facade for scripts that look up objects by key without a scene link.</summary>
    public static class GrimoireObjectData
    {
        public static bool TryGet(string objectKey, out GrimoireObjectSnapshot snapshot) =>
            GrimoireObjectCache.TryGet(objectKey, out snapshot);

        public static Task<GrimoireObjectSnapshot> GetAsync(string objectKey) =>
            GrimoireObjectCache.LoadAsync(new GrimoireObjectRef { CodeId = objectKey ?? "" });
    }
}
