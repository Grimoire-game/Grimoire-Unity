using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Tracks linked Grimoire Object Links so destroying the component removes
    /// the matching <c>game_engine_data</c> entry. Relies on
    /// <see cref="GrimoireObjectLink.EditorDestroyed"/> (edit-mode OnDestroy).
    /// </summary>
    [InitializeOnLoad]
    internal static class GrimoireGameEngineSyncHooks
    {
        private static readonly Dictionary<int, GrimoireGameEngineSync.LinkIdentity> Tracked =
            new Dictionary<int, GrimoireGameEngineSync.LinkIdentity>();

        private static readonly HashSet<string> RemovalsInFlight = new HashSet<string>();

        static GrimoireGameEngineSyncHooks()
        {
            GrimoireObjectLink.EditorDestroyed += OnLinkDestroyed;
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                Tracked.Clear();
                RemovalsInFlight.Clear();
            };
        }

        public static void Remember(GrimoireObjectLink link)
        {
            if (link == null || string.IsNullOrEmpty(link.CachedObjectId))
            {
                return;
            }

            var identity = GrimoireGameEngineSync.CaptureIdentity(link);
            if (string.IsNullOrEmpty(identity.EngineInstanceId) ||
                string.IsNullOrEmpty(identity.ObjectId))
            {
                return;
            }

            Tracked[link.GetInstanceID()] = identity;
        }

        public static void Forget(GrimoireObjectLink link)
        {
            if (link != null)
            {
                Tracked.Remove(link.GetInstanceID());
            }
        }

        private static void OnLinkDestroyed(GrimoireObjectLink link)
        {
            if (ShouldIgnoreLifecycleEvent())
            {
                return;
            }

            var instanceId = link != null ? link.GetInstanceID() : 0;
            if (instanceId == 0 || !Tracked.TryGetValue(instanceId, out var identity))
            {
                return;
            }

            Tracked.Remove(instanceId);
            RemoveDeferred(identity);
        }

        private static async void RemoveDeferred(GrimoireGameEngineSync.LinkIdentity identity)
        {
            var key = RemovalKey(identity);
            if (!RemovalsInFlight.Add(key))
            {
                return;
            }

            try
            {
                var result = await GrimoireGameEngineSync.RemoveAsync(null, identity);
                if (!result.Success)
                {
                    Debug.LogWarning($"[Grimoire] Could not remove game_engine_data entry: {result.Error}");
                }
            }
            finally
            {
                RemovalsInFlight.Remove(key);
            }
        }

        private static string RemovalKey(GrimoireGameEngineSync.LinkIdentity identity) =>
            $"{identity.GameId}|{identity.ObjectId}|{GrimoireGameEngineSync.NormalizeEngineInstanceId(identity.EngineInstanceId)}|{identity.Scene}";

        private static bool ShouldIgnoreLifecycleEvent()
        {
            return EditorApplication.isPlayingOrWillChangePlaymode ||
                   EditorApplication.isCompiling ||
                   EditorApplication.isUpdating;
        }
    }
}
