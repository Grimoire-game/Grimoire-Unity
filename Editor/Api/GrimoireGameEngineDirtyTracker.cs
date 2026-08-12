using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Watches every <see cref="GrimoireObjectLink"/> in open scenes and
    /// records when synced fields (position / rotation / scale / id-name)
    /// diverge from the last successful Grimoire push.
    /// </summary>
    [InitializeOnLoad]
    public static class GrimoireGameEngineDirtyTracker
    {
        private const float Epsilon = 0.0001f;

        private struct Snapshot
        {
            public Vector3 Position;
            public Vector3 Euler;
            public Vector3 Scale;
            public string Name;
            public bool HasSyncedBaseline;
        }

        public sealed class PendingChange
        {
            public GrimoireObjectLink Link;
            public string GameObjectName;
            public string ObjectKey;
            public string ObjectId;
            public string Scene;
            public Vector3 Position;
            public Vector3 Euler;
            public Vector3 Scale;
            public string EngineName;
            public bool PositionDirty;
            public bool RotationDirty;
            public bool ScaleDirty;
            public bool IdNameDirty;
            public Vector3 LastPosition;
            public Vector3 LastEuler;
            public Vector3 LastScale;
            public string LastName;

            public bool IsDirty =>
                PositionDirty || RotationDirty || ScaleDirty || IdNameDirty;
        }

        private static readonly Dictionary<int, Snapshot> Baselines =
            new Dictionary<int, Snapshot>();

        private static readonly List<PendingChange> PendingBuffer = new List<PendingChange>();
        private static readonly List<GrimoireObjectLink> LinkBuffer = new List<GrimoireObjectLink>();

        private static int _lastDirtyCount = -1;
        private static double _nextFullRefreshAt;

        public static event Action Changed;

        public static int DirtyCount { get; private set; }

        static GrimoireGameEngineDirtyTracker()
        {
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.hierarchyChanged += () => _nextFullRefreshAt = 0;
            AssemblyReloadEvents.beforeAssemblyReload += Baselines.Clear;
            GrimoireObjectLink.EditorDestroyed += link =>
            {
                if (link != null)
                {
                    Baselines.Remove(link.GetInstanceID());
                    NotifyIfDirtyCountChanged();
                }
            };
        }

        /// <summary>
        /// Record the current transform as matching Grimoire after a successful
        /// upsert.
        /// </summary>
        public static void MarkClean(GrimoireObjectLink link)
        {
            if (link == null)
            {
                return;
            }

            Baselines[link.GetInstanceID()] = Capture(link, hasSyncedBaseline: true);
            RefreshPending(forceNotify: true);
        }

        public static void Forget(GrimoireObjectLink link)
        {
            if (link == null)
            {
                return;
            }

            Baselines.Remove(link.GetInstanceID());
            RefreshPending(forceNotify: true);
        }

        public static IReadOnlyList<PendingChange> GetPendingChanges()
        {
            RefreshPending(forceNotify: false);
            return PendingBuffer;
        }

        public static PendingChange FindPending(GrimoireObjectLink link)
        {
            if (link == null)
            {
                return null;
            }

            foreach (var change in GetPendingChanges())
            {
                if (change.Link == link)
                {
                    return change;
                }
            }

            return null;
        }

        /// <summary>
        /// Fills <paramref name="into"/> with every <see cref="GrimoireObjectLink"/>
        /// in loaded scenes (skips prefab assets / unloaded scenes).
        /// </summary>
        public static void CollectSceneLinks(List<GrimoireObjectLink> into)
        {
            CollectLinks(into);
        }

        /// <summary>
        /// True when this link has unsynced game-engine transform / name changes.
        /// </summary>
        public static bool IsGameEngineDirty(GrimoireObjectLink link)
        {
            var pending = FindPending(link);
            return pending != null && pending.IsDirty;
        }

        private static void OnEditorUpdate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling ||
                EditorApplication.isUpdating)
            {
                return;
            }

            RefreshPending(forceNotify: false);
        }

        private static void RefreshPending(bool forceNotify)
        {
            var now = EditorApplication.timeSinceStartup;
            if (forceNotify || LinkBuffer.Count == 0 || now >= _nextFullRefreshAt)
            {
                CollectLinks(LinkBuffer);
                _nextFullRefreshAt = now + 0.5;
            }

            PendingBuffer.Clear();
            var dirtyCount = 0;
            var valuesMoved = false;

            foreach (var link in LinkBuffer)
            {
                if (link == null || (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId)))
                {
                    continue;
                }

                var id = link.GetInstanceID();
                if (!Baselines.TryGetValue(id, out var baseline))
                {
                    // First sighting this session: treat current pose as clean
                    // so already-synced scene objects don't all show as pending.
                    baseline = Capture(link, hasSyncedBaseline: true);
                    Baselines[id] = baseline;
                }

                var change = BuildChange(link, baseline);
                if (change.IsDirty)
                {
                    dirtyCount++;
                    PendingBuffer.Add(change);
                }

                // Live values for the Sync tab while dragging — even when clean,
                // surface the held object so the tab stays up to date.
                if (!change.IsDirty && IsSelected(link))
                {
                    PendingBuffer.Insert(0, change);
                }

                if (HasLiveMoved(link, baseline))
                {
                    valuesMoved = true;
                }
            }

            DirtyCount = dirtyCount;

            if (forceNotify || dirtyCount != _lastDirtyCount || (valuesMoved && DirtyCount > 0))
            {
                _lastDirtyCount = dirtyCount;
                Changed?.Invoke();
            }
            else if (valuesMoved)
            {
                // Selected object moving while still "clean" baseline — still repaint
                // Sync UI so numbers stay live.
                Changed?.Invoke();
            }
        }

        private static bool IsSelected(GrimoireObjectLink link)
        {
            var active = Selection.activeGameObject;
            return active != null && link != null && link.gameObject == active;
        }

        private static bool HasLiveMoved(GrimoireObjectLink link, Snapshot baseline)
        {
            var t = link.transform;
            return !Approximately(t.position, baseline.Position) ||
                   !ApproximatelyEuler(t.eulerAngles, baseline.Euler) ||
                   !Approximately(t.localScale, baseline.Scale) ||
                   !string.Equals(link.gameObject.name, baseline.Name, StringComparison.Ordinal);
        }

        private static PendingChange BuildChange(GrimoireObjectLink link, Snapshot baseline)
        {
            var t = link.transform;
            var change = new PendingChange
            {
                Link = link,
                GameObjectName = link.gameObject.name,
                ObjectKey = link.ObjectKey,
                ObjectId = link.CachedObjectId,
                Scene = GrimoireGameEngineSync.GetSceneName(link.gameObject),
                Position = t.position,
                Euler = t.eulerAngles,
                Scale = t.localScale,
                EngineName = link.gameObject.name,
                LastPosition = baseline.Position,
                LastEuler = baseline.Euler,
                LastScale = baseline.Scale,
                LastName = baseline.Name,
                PositionDirty = link.SyncPosition && !Approximately(t.position, baseline.Position),
                RotationDirty = link.SyncRotation && !ApproximatelyEuler(t.eulerAngles, baseline.Euler),
                ScaleDirty = link.SyncScale && !Approximately(t.localScale, baseline.Scale),
                IdNameDirty = link.SyncIdName &&
                              !string.Equals(link.gameObject.name, baseline.Name, StringComparison.Ordinal),
            };

            return change;
        }

        private static Snapshot Capture(GrimoireObjectLink link, bool hasSyncedBaseline)
        {
            var t = link.transform;
            return new Snapshot
            {
                Position = t.position,
                Euler = t.eulerAngles,
                Scale = t.localScale,
                Name = link.gameObject.name,
                HasSyncedBaseline = hasSyncedBaseline,
            };
        }

        private static void CollectLinks(List<GrimoireObjectLink> into)
        {
            into.Clear();
            var found = Resources.FindObjectsOfTypeAll<GrimoireObjectLink>();
            foreach (var link in found)
            {
                if (link == null || link.gameObject == null)
                {
                    continue;
                }

                if (EditorUtility.IsPersistent(link))
                {
                    continue;
                }

                var scene = link.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                into.Add(link);
            }
        }

        private static void NotifyIfDirtyCountChanged()
        {
            RefreshPending(forceNotify: true);
        }

        private static bool Approximately(Vector3 a, Vector3 b) =>
            Mathf.Abs(a.x - b.x) <= Epsilon &&
            Mathf.Abs(a.y - b.y) <= Epsilon &&
            Mathf.Abs(a.z - b.z) <= Epsilon;

        private static bool ApproximatelyEuler(Vector3 a, Vector3 b) =>
            Mathf.Abs(Mathf.DeltaAngle(a.x, b.x)) <= Epsilon &&
            Mathf.Abs(Mathf.DeltaAngle(a.y, b.y)) <= Epsilon &&
            Mathf.Abs(Mathf.DeltaAngle(a.z, b.z)) <= Epsilon;
    }
}
