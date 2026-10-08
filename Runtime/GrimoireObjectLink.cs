using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Links a GameObject to a Grimoire object through its key (<c>code_id</c>,
    /// e.g. <c>characters/aragorn</c>).
    ///
    /// Serialized link plus a query API for snapshotted fields. Networking lives
    /// in the editor assembly; an optional <see cref="IGrimoireObjectLoader"/> is
    /// registered there for lazy nested fetches while signed in. Player builds
    /// read the snapshot cached on this component.
    /// </summary>
    [AddComponentMenu("Grimoire/Grimoire Object Link")]
    [DisallowMultipleComponent]
    public class GrimoireObjectLink : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("The Grimoire object key (code_id), e.g. 'characters/aragorn'.")]
        private string _objectKey = "";

        [SerializeField]
        [HideInInspector]
        private string _cachedObjectId = "";

        [SerializeField]
        [Tooltip("Include this object's world position in game_engine_data.location.")]
        private bool _syncPosition = true;

        [SerializeField]
        [Tooltip("Include this object's world euler angles in game_engine_data.rotation.")]
        private bool _syncRotation = true;

        [SerializeField]
        [Tooltip("Include this object's local scale in game_engine_data.scale.")]
        private bool _syncScale = true;

        [SerializeField]
        [Tooltip("Include this object's engine id / name in game_engine_data.engine_instance_id.")]
        private bool _syncIdName = true;

        [SerializeField]
        [HideInInspector]
        private List<GrimoireLinkedField> _linkedFields = new List<GrimoireLinkedField>();

        [SerializeField]
        [HideInInspector]
        private GrimoireObjectSnapshot _snapshot = new GrimoireObjectSnapshot();

        [SerializeField]
        [HideInInspector]
        private List<GrimoireObjectSnapshot> _nestedSnapshots = new List<GrimoireObjectSnapshot>();

        [NonSerialized]
        private List<GrimoireRuntimeFieldChange> _runtimeChanges;

        /// <summary>
        /// Fired in the editor when this component is destroyed so the editor
        /// assembly can remove the matching <c>game_engine_data</c> entry.
        /// </summary>
        public static event Action<GrimoireObjectLink> EditorDestroyed;

        /// <summary>
        /// Fired when a field value on this link changes while playing (dialog
        /// setter or <see cref="SetFieldValue(string, string, string)"/>).
        /// </summary>
        public static event Action<GrimoireObjectLink, GrimoireRuntimeFieldChange> RuntimeFieldChanged;

        /// <summary>
        /// Fired when a nested snapshot is persisted in Edit Mode so the editor
        /// assembly can mark the component dirty.
        /// </summary>
        public static event Action<GrimoireObjectLink> NestedSnapshotsChanged;

        /// <summary>The Grimoire object key (<c>code_id</c>) this GameObject is linked to.</summary>
        public string ObjectKey
        {
            get => _objectKey;
            set
            {
                var trimmed = value?.Trim() ?? "";
                if (trimmed == _objectKey)
                {
                    return;
                }

                _objectKey = trimmed;
                // The cached UUID belongs to the previous key; a stale id would
                // silently show the wrong object in the widget.
                _cachedObjectId = "";
                ClearSnapshot();
            }
        }

        /// <summary>
        /// The resolved Grimoire object UUID for <see cref="ObjectKey"/>. Cached
        /// so the widget can skip the key-resolution listing call. Cleared
        /// whenever the key changes; the editor re-resolves when it is empty or
        /// no longer matches.
        /// </summary>
        public string CachedObjectId
        {
            get => _cachedObjectId;
            set => _cachedObjectId = value ?? "";
        }

        public bool HasKey => !string.IsNullOrWhiteSpace(_objectKey);

        public bool SyncPosition
        {
            get => _syncPosition;
            set => _syncPosition = value;
        }

        public bool SyncRotation
        {
            get => _syncRotation;
            set => _syncRotation = value;
        }

        public bool SyncScale
        {
            get => _syncScale;
            set => _syncScale = value;
        }

        public bool SyncIdName
        {
            get => _syncIdName;
            set => _syncIdName = value;
        }

        /// <summary>
        /// Game-engine-editable fields cached on this link. Local values persist
        /// with the scene; compare against <see cref="GrimoireLinkedField.GrimoireValue"/>
        /// to detect deviations that need syncing.
        /// </summary>
        public IReadOnlyList<GrimoireLinkedField> LinkedFields => _linkedFields;

        /// <summary>True when any writable linked field differs from its Grimoire baseline.</summary>
        public bool HasFieldDeviations
        {
            get
            {
                if (_linkedFields == null)
                {
                    return false;
                }

                for (var i = 0; i < _linkedFields.Count; i++)
                {
                    if (_linkedFields[i] != null && _linkedFields[i].IsDeviated)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>Number of writable linked fields that differ from Grimoire.</summary>
        public int FieldDeviationCount
        {
            get
            {
                if (_linkedFields == null)
                {
                    return 0;
                }

                var count = 0;
                for (var i = 0; i < _linkedFields.Count; i++)
                {
                    if (_linkedFields[i] != null && _linkedFields[i].IsDeviated)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Last API snapshot of this object (all fields + reference stubs).
        /// Nested objects live in <see cref="NestedSnapshots"/> after a lazy load.
        /// </summary>
        public GrimoireObjectSnapshot Snapshot => _snapshot;

        public bool HasSnapshot => _snapshot != null && !_snapshot.IsEmpty;

        /// <summary>Nested objects fetched on demand and persisted in Edit Mode.</summary>
        public IReadOnlyList<GrimoireObjectSnapshot> NestedSnapshots => _nestedSnapshots;

        /// <summary>Editor/API helper: replace the full linked-field list.</summary>
        public void ReplaceLinkedFields(List<GrimoireLinkedField> fields)
        {
            _linkedFields = fields ?? new List<GrimoireLinkedField>();
        }

        /// <summary>Editor/API helper: replace the root API snapshot.</summary>
        public void ReplaceSnapshot(GrimoireObjectSnapshot snapshot)
        {
            _snapshot = snapshot ?? new GrimoireObjectSnapshot();
            PruneNestedSnapshots();
            GrimoireObjectCache.Register(this);
        }

        /// <summary>Editor/API helper: clear cached editable fields (e.g. on unlink).</summary>
        public void ClearLinkedFields()
        {
            _linkedFields?.Clear();
            ClearSnapshot();
        }

        public void ClearSnapshot()
        {
            _snapshot = new GrimoireObjectSnapshot();
            _nestedSnapshots?.Clear();
        }

        /// <summary>
        /// Keep a lazily loaded nested snapshot on this component. In Edit Mode
        /// this is persisted with the scene; Play Mode should use the cache only.
        /// </summary>
        public void RememberNestedSnapshot(GrimoireObjectSnapshot snapshot)
        {
            if (snapshot == null || snapshot.IsEmpty)
            {
                return;
            }

            if (_nestedSnapshots == null)
            {
                _nestedSnapshots = new List<GrimoireObjectSnapshot>();
            }

            for (var i = 0; i < _nestedSnapshots.Count; i++)
            {
                var existing = _nestedSnapshots[i];
                if (existing == null)
                {
                    continue;
                }

                var sameId = !string.IsNullOrEmpty(snapshot.ObjectId)
                             && string.Equals(existing.ObjectId, snapshot.ObjectId, StringComparison.OrdinalIgnoreCase);
                var sameKey = !string.IsNullOrEmpty(snapshot.ObjectKey)
                              && string.Equals(existing.ObjectKey, snapshot.ObjectKey, StringComparison.OrdinalIgnoreCase);
                if (sameId || sameKey)
                {
                    _nestedSnapshots[i] = snapshot;
                    GrimoireObjectCache.Register(snapshot);
                    NestedSnapshotsChanged?.Invoke(this);
                    return;
                }
            }

            _nestedSnapshots.Add(snapshot);
            GrimoireObjectCache.Register(snapshot);
            NestedSnapshotsChanged?.Invoke(this);
        }

        /// <summary>Copy local editable values onto the snapshot so scripts read them.</summary>
        public void OverlayLinkedFieldValues()
        {
            if (_snapshot == null || _linkedFields == null)
            {
                return;
            }

            for (var i = 0; i < _linkedFields.Count; i++)
            {
                var linked = _linkedFields[i];
                if (linked == null || string.IsNullOrEmpty(linked.FieldId))
                {
                    continue;
                }

                _snapshot.SetFieldValue(linked.FieldId, linked.LocalValue);
            }
        }

        public bool TryGetLinkedField(string fieldId, out GrimoireLinkedField field)
        {
            field = null;
            if (string.IsNullOrEmpty(fieldId) || _linkedFields == null)
            {
                return false;
            }

            for (var i = 0; i < _linkedFields.Count; i++)
            {
                var candidate = _linkedFields[i];
                if (candidate != null &&
                    string.Equals(candidate.FieldId, fieldId, StringComparison.Ordinal))
                {
                    field = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Field values changed while playing, oldest first. Not saved with the scene.</summary>
        public IReadOnlyList<GrimoireRuntimeFieldChange> RuntimeChanges =>
            (IReadOnlyList<GrimoireRuntimeFieldChange>)_runtimeChanges ?? Array.Empty<GrimoireRuntimeFieldChange>();

        public bool HasRuntimeChanges => _runtimeChanges != null && _runtimeChanges.Count > 0;

        /// <summary>The most recent runtime change of a field, if any.</summary>
        public bool TryGetRuntimeChange(string fieldId, out GrimoireRuntimeFieldChange change)
        {
            change = null;
            if (string.IsNullOrEmpty(fieldId) || _runtimeChanges == null)
            {
                return false;
            }

            for (var i = _runtimeChanges.Count - 1; i >= 0; i--)
            {
                var candidate = _runtimeChanges[i];
                if (candidate != null && string.Equals(candidate.FieldId, fieldId, StringComparison.Ordinal))
                {
                    change = candidate;
                    return true;
                }
            }

            return false;
        }

        public void ClearRuntimeChanges()
        {
            _runtimeChanges?.Clear();
        }

        /// <summary>
        /// Set a field on this object while playing, e.g. <c>SetFieldValue("has beard", "true", "Barber script")</c>.
        /// Updates the editable field (when the field is one) and the snapshot, and
        /// records the change so the Object Link inspector shows it. Returns false
        /// when the snapshot has no such field.
        /// </summary>
        public bool SetFieldValue(string fieldName, string value, string source = null)
        {
            if (_snapshot == null || !_snapshot.TryGetField(fieldName, out var field))
            {
                return false;
            }

            value = value ?? "";
            var previous = field.Value ?? "";
            if (TryGetLinkedField(field.FieldId, out var linked))
            {
                previous = linked.LocalValue ?? "";
                linked.LocalValue = value;
            }

            if (field.Multiple)
            {
                field.Values.Clear();
                foreach (var part in value.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    field.Values.Add(part.Trim());
                }
            }

            field.Value = value;

            var change = new GrimoireRuntimeFieldChange(field.FieldId, field.DisplayLabel, previous, value, source);
            (_runtimeChanges ?? (_runtimeChanges = new List<GrimoireRuntimeFieldChange>())).Add(change);
            RuntimeFieldChanged?.Invoke(this, change);
            GrimoireObjectCache.NotifyUpdated();
            return true;
        }

        public bool SetFieldValue(string fieldName, bool value, string source = null) =>
            SetFieldValue(fieldName, value ? "true" : "false", source);

        public bool SetFieldValue(string fieldName, double value, string source = null) =>
            SetFieldValue(fieldName, value.ToString(System.Globalization.CultureInfo.InvariantCulture), source);

        public bool TryGetString(string fieldName, out string value)
        {
            value = "";
            return _snapshot != null && _snapshot.TryGetString(fieldName, out value);
        }

        public bool TryGetNumber(string fieldName, out double value)
        {
            value = 0;
            return _snapshot != null && _snapshot.TryGetNumber(fieldName, out value);
        }

        public bool TryGetBool(string fieldName, out bool value)
        {
            value = false;
            return _snapshot != null && _snapshot.TryGetBool(fieldName, out value);
        }

        public bool TryGetVector(string fieldName, out Vector3 value)
        {
            value = Vector3.zero;
            return _snapshot != null && _snapshot.TryGetVector(fieldName, out value);
        }

        /// <summary>Reference stubs for a field such as <c>levels</c>. Never hits the network.</summary>
        public IReadOnlyList<GrimoireObjectRef> GetReferences(string fieldName)
        {
            if (_snapshot == null)
            {
                return Array.Empty<GrimoireObjectRef>();
            }

            return _snapshot.GetReferences(fieldName);
        }

        /// <summary>Cache-only lookup of a nested object. Never hits the network.</summary>
        public bool TryGetReferencedObject(string fieldName, int index, out GrimoireObjectSnapshot snapshot)
        {
            snapshot = null;
            var refs = GetReferences(fieldName);
            if (index < 0 || index >= refs.Count)
            {
                return false;
            }

            return TryResolve(refs[index], out snapshot);
        }

        public bool TryResolve(GrimoireObjectRef stub, out GrimoireObjectSnapshot snapshot)
        {
            if (GrimoireObjectCache.TryGet(stub, out snapshot))
            {
                return true;
            }

            snapshot = FindNested(stub);
            return snapshot != null;
        }

        /// <summary>
        /// Load a nested object referenced by <paramref name="fieldName"/>.
        /// Uses the snapshot cache first; fetches via the editor loader on a miss.
        /// Persists the result on this component in Edit Mode only.
        /// </summary>
        public async Task<GrimoireObjectSnapshot> GetReferencedObjectAsync(string fieldName, int index)
        {
            var refs = GetReferences(fieldName);
            if (index < 0 || index >= refs.Count)
            {
                return null;
            }

            return await GetReferencedObjectAsync(refs[index]);
        }

        public async Task<GrimoireObjectSnapshot> GetReferencedObjectAsync(GrimoireObjectRef stub)
        {
            if (TryResolve(stub, out var cached))
            {
                if (!Application.isPlaying)
                {
                    RememberNestedSnapshot(cached);
                }

                return cached;
            }

            var loaded = await GrimoireObjectCache.LoadAsync(stub);
            if (loaded == null)
            {
                return null;
            }

            if (!Application.isPlaying)
            {
                RememberNestedSnapshot(loaded);
            }

            return loaded;
        }

        /// <summary>Fetch every referenced object on this snapshot (one hop, no recursion).</summary>
        public async Task PrefetchReferencesAsync()
        {
            var refs = CollectRootReferences();
            for (var i = 0; i < refs.Count; i++)
            {
                await GetReferencedObjectAsync(refs[i]);
            }
        }

        public bool IsNestedLoaded(GrimoireObjectRef stub) => TryResolve(stub, out _);

        private void OnEnable()
        {
            GrimoireObjectCache.Register(this);
        }

        private GrimoireObjectSnapshot FindNested(GrimoireObjectRef stub)
        {
            if (stub == null || _nestedSnapshots == null)
            {
                return null;
            }

            for (var i = 0; i < _nestedSnapshots.Count; i++)
            {
                var nested = _nestedSnapshots[i];
                if (nested != null && nested.Matches(stub))
                {
                    return nested;
                }
            }

            return null;
        }

        private List<GrimoireObjectRef> CollectRootReferences()
        {
            var list = new List<GrimoireObjectRef>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_snapshot?.Fields == null)
            {
                return list;
            }

            for (var f = 0; f < _snapshot.Fields.Count; f++)
            {
                var field = _snapshot.Fields[f];
                if (field?.References == null)
                {
                    continue;
                }

                for (var r = 0; r < field.References.Count; r++)
                {
                    var stub = field.References[r];
                    if (stub == null || (!stub.HasId && !stub.HasKey))
                    {
                        continue;
                    }

                    var key = stub.HasId ? stub.Id : stub.CodeId;
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    list.Add(stub);
                }
            }

            return list;
        }

        private void PruneNestedSnapshots()
        {
            if (_nestedSnapshots == null || _nestedSnapshots.Count == 0)
            {
                return;
            }

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _snapshot?.CollectReferenceKeys(keep);
            if (keep.Count == 0)
            {
                _nestedSnapshots.Clear();
                return;
            }

            for (var i = _nestedSnapshots.Count - 1; i >= 0; i--)
            {
                var nested = _nestedSnapshots[i];
                if (nested == null
                    || (!keep.Contains(nested.ObjectId) && !keep.Contains(nested.ObjectKey)))
                {
                    _nestedSnapshots.RemoveAt(i);
                }
            }
        }

#if UNITY_EDITOR
        private void OnDestroy()
        {
            EditorDestroyed?.Invoke(this);
        }
#endif
    }

    /// <summary>One field value change made on an Object Link while playing.</summary>
    public sealed class GrimoireRuntimeFieldChange
    {
        public GrimoireRuntimeFieldChange(string fieldId, string label, string previousValue, string value, string source)
        {
            FieldId = fieldId ?? "";
            Label = string.IsNullOrEmpty(label) ? FieldId : label;
            PreviousValue = previousValue ?? "";
            Value = value ?? "";
            Source = source ?? "";
            Time = DateTime.Now;
            GameTime = Application.isPlaying ? UnityEngine.Time.time : 0f;
        }

        public string FieldId { get; }
        public string Label { get; }
        public string PreviousValue { get; }
        public string Value { get; }

        /// <summary>Who made the change, e.g. "Dialog 'Barber' / node set_beard".</summary>
        public string Source { get; }

        public DateTime Time { get; }
        public float GameTime { get; }

        public bool IsSameValue => string.Equals(PreviousValue, Value, StringComparison.Ordinal);
    }
}
