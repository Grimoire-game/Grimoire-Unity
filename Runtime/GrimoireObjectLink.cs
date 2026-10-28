using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Links a GameObject to a Grimoire object through its key (<c>code_id</c>,
    /// e.g. <c>characters/aragorn</c>).
    ///
    /// One query API for either the connected snapshot or an imported version.
    /// Networking lives in the editor assembly; an optional
    /// <see cref="IGrimoireObjectLoader"/> is registered there for lazy connected
    /// reference fetches. Player builds read the saved snapshot or the version
    /// database selected for this link.
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
        private GrimoireObjectLinkSource _dataSource = GrimoireObjectLinkSource.ProjectDefault;

        [SerializeField]
        [Tooltip("Key in the imported Database.Library. Leave empty to match the Grimoire object key when possible.")]
        private string _versionKey = "";

        [SerializeField]
        private List<GrimoireVersionFieldAlias> _versionFieldAliases = new List<GrimoireVersionFieldAlias>();

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

        [NonSerialized]
        private Dictionary<string, string> _versionOverrides;

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
        /// Fired by <see cref="TriggerFunction"/> for either source. A game can
        /// handle functions here when no generated LogicRuntime is imported.
        /// </summary>
        public static event Action<GrimoireObjectLink, string> FunctionTriggered;

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
                _versionKey = "";
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

        public GrimoireObjectLinkSource DataSource
        {
            get => _dataSource;
            set => _dataSource = value;
        }

        public GrimoireObjectDataSource ActiveSource => _dataSource == GrimoireObjectLinkSource.ProjectDefault
            ? GrimoireObjectSources.ProjectDefault
            : _dataSource == GrimoireObjectLinkSource.VersionDatabase
                ? GrimoireObjectDataSource.VersionDatabase
                : GrimoireObjectDataSource.ConnectedSnapshot;

        public string VersionKey
        {
            get => _versionKey;
            set => _versionKey = value?.Trim() ?? "";
        }

        /// <summary>Manual field-name mappings when an export renames a field.</summary>
        public List<GrimoireVersionFieldAlias> VersionFieldAliases =>
            _versionFieldAliases ?? (_versionFieldAliases = new List<GrimoireVersionFieldAlias>());

        /// <summary>Keys available in the imported version for inspector pickers.</summary>
        public static IReadOnlyList<string> AvailableVersionKeys => GrimoireVersionObjectAdapter.Keys;

        public bool TryGetResolvedVersionKey(out string key) =>
            GrimoireVersionObjectAdapter.TryResolveKey(_versionKey, _objectKey, _snapshot?.Name, out key);

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
        /// Active object values from the chosen source. Connected values come
        /// from the saved API snapshot; version values come from Database.Library
        /// and ObjectRuntime. Nested connected objects live in
        /// <see cref="NestedSnapshots"/> after a lazy load.
        /// </summary>
        public GrimoireObjectSnapshot Snapshot
        {
            get
            {
                if (ActiveSource == GrimoireObjectDataSource.ConnectedSnapshot) return _snapshot;
                if (!TryGetResolvedVersionKey(out var key) ||
                    !GrimoireVersionObjectAdapter.TryBuildSnapshot(key, _snapshot?.Name, out var snapshot))
                    return null;
                ApplyVersionAliases(snapshot);
                OverlayVersionLocalValues(snapshot);
                return snapshot;
            }
        }

        private void ApplyVersionAliases(GrimoireObjectSnapshot version)
        {
            foreach (var field in version.Fields)
            {
                if (field == null) continue;
                if (_snapshot?.Fields != null)
                {
                    foreach (var apiField in _snapshot.Fields)
                    {
                        if (apiField == null ||
                            (!SameFieldName(field.Label, apiField.Label) &&
                             !SameFieldName(field.FieldId, apiField.Label) &&
                             !SameFieldName(field.FieldId, apiField.FieldId))) continue;
                        if (!string.IsNullOrEmpty(apiField.FieldId)) field.Aliases.Add(apiField.FieldId);
                        if (!string.IsNullOrEmpty(apiField.Label)) field.Aliases.Add(apiField.Label);
                    }
                }
                foreach (var alias in VersionFieldAliases)
                {
                    if (alias != null && SameFieldName(alias.VersionField, field.FieldId) &&
                        !string.IsNullOrWhiteSpace(alias.ApiField))
                    {
                        field.Aliases.Add(alias.ApiField.Trim());
                        if (_snapshot != null && _snapshot.TryGetField(alias.ApiField.Trim(), out var apiField))
                        {
                            if (!string.IsNullOrEmpty(apiField.FieldId)) field.Aliases.Add(apiField.FieldId);
                            if (!string.IsNullOrEmpty(apiField.Label)) field.Aliases.Add(apiField.Label);
                        }
                    }
                }
            }
        }

        /// <summary>The serialized API copy, even while the version is selected.</summary>
        public GrimoireObjectSnapshot ApiSnapshot => _snapshot;

        public bool HasSnapshot
        {
            get
            {
                var snapshot = Snapshot;
                return snapshot != null && !snapshot.IsEmpty;
            }
        }

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
            _versionOverrides?.Clear();
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

        private void OverlayVersionLocalValues(GrimoireObjectSnapshot snapshot)
        {
            if (_linkedFields != null)
            {
                foreach (var linked in _linkedFields)
                {
                    if (linked == null || !linked.IsDeviated) continue;
                    var field = MatchVersionField(snapshot, linked);
                    if (field != null) snapshot.SetFieldValue(field.FieldId, linked.LocalValue);
                }
            }
            if (_versionOverrides != null)
            {
                foreach (var pair in _versionOverrides)
                    snapshot.SetFieldValue(pair.Key, pair.Value);
            }
        }

        private static GrimoireCachedField MatchVersionField(
            GrimoireObjectSnapshot snapshot, GrimoireLinkedField linked)
        {
            if (snapshot.TryGetField(linked.FieldId, out var direct)) return direct;
            var matches = snapshot.Fields.Where(field => field != null &&
                (SameFieldName(field.Label, linked.Label) || SameFieldName(field.FieldId, linked.Label)))
                .Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static bool SameFieldName(string first, string second) =>
            !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
            string.Equals(new string(first.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()),
                new string(second.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()),
                StringComparison.Ordinal);

        /// <summary>
        /// Set a field on this object while playing, e.g. <c>SetFieldValue("has beard", "true", "Barber script")</c>.
        /// Updates the editable field (when the field is one) and the snapshot, and
        /// records the change so the Object Link inspector shows it. Returns false
        /// when the snapshot has no such field.
        /// </summary>
        public bool SetFieldValue(string fieldName, string value, string source = null)
        {
            var snapshot = Snapshot;
            if (snapshot == null || !snapshot.TryGetField(fieldName, out var field))
            {
                return false;
            }

            value = value ?? "";
            var previous = field.Value ?? "";
            GrimoireLinkedField linked = null;
            if (TryGetLinkedField(field.FieldId, out linked) ||
                ActiveSource == GrimoireObjectDataSource.VersionDatabase &&
                (linked = _linkedFields?.FirstOrDefault(candidate => candidate != null &&
                    SameFieldName(candidate.Label, field.Label))) != null)
            {
                previous = linked.LocalValue ?? "";
                linked.LocalValue = value;
            }

            if (ActiveSource == GrimoireObjectDataSource.VersionDatabase)
            {
                var wrote = TryGetResolvedVersionKey(out var versionKey) &&
                    GrimoireVersionObjectAdapter.TryWrite(versionKey, field, value);
                if (wrote) _versionOverrides?.Remove(field.FieldId);
                else
                    (_versionOverrides ?? (_versionOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)))
                        [field.FieldId] = value;
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
            if (field.IsReferenceField)
            {
                field.SetReferencesFromText(value);
            }

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

        /// <summary>
        /// Fire a named object function through the selected source. Imported
        /// versions use LogicRuntime. The event also fires in either mode, so
        /// custom gameplay handlers can use the same call without an export.
        /// Returns false when no function runtime or handler is available.
        /// </summary>
        public bool TriggerFunction(string functionName)
        {
            if (string.IsNullOrWhiteSpace(functionName)) return false;
            var handled = false;
            if (ActiveSource == GrimoireObjectDataSource.VersionDatabase &&
                TryGetResolvedVersionKey(out var key))
                handled = GrimoireVersionObjectAdapter.TryTriggerFunction(key, functionName);
            var handler = FunctionTriggered;
            if (handler != null)
            {
                handler(this, functionName);
                handled = true;
            }
            return handled;
        }

        public bool TryGetString(string fieldName, out string value)
        {
            value = "";
            var snapshot = Snapshot;
            return snapshot != null && snapshot.TryGetString(fieldName, out value);
        }

        /// <summary>
        /// Read localized display copy for a text field. Conditions and setters
        /// should continue using <see cref="TryGetString"/>.
        /// </summary>
        public bool TryGetDisplayString(
            string fieldName,
            out string value,
            string languageCode = null)
        {
            value = "";
            var snapshot = Snapshot;
            if (snapshot == null || !snapshot.TryGetField(fieldName, out var field)) return false;
            if (ActiveSource == GrimoireObjectDataSource.VersionDatabase &&
                !HasVersionLocalOverride(snapshot, field) &&
                !string.IsNullOrEmpty(languageCode) && TryGetResolvedVersionKey(out var key) &&
                GrimoireVersionObjectAdapter.TryGetLocalizedText(key, field.FieldId, languageCode, out value))
                return true;
            return snapshot.TryGetDisplayString(fieldName, out value, languageCode);
        }

        private bool HasVersionLocalOverride(GrimoireObjectSnapshot snapshot, GrimoireCachedField field)
        {
            if (_versionOverrides != null && _versionOverrides.ContainsKey(field.FieldId)) return true;
            return _linkedFields != null && _linkedFields.Any(linked => linked != null &&
                linked.IsDeviated && MatchVersionField(snapshot, linked) == field);
        }

        public bool TryGetNumber(string fieldName, out double value)
        {
            value = 0;
            var snapshot = Snapshot;
            return snapshot != null && snapshot.TryGetNumber(fieldName, out value);
        }

        public bool TryGetBool(string fieldName, out bool value)
        {
            value = false;
            var snapshot = Snapshot;
            return snapshot != null && snapshot.TryGetBool(fieldName, out value);
        }

        public bool TryGetVector(string fieldName, out Vector3 value)
        {
            value = Vector3.zero;
            var snapshot = Snapshot;
            return snapshot != null && snapshot.TryGetVector(fieldName, out value);
        }

        /// <summary>
        /// The variable element a <c>types</c> field holds, e.g. TryGetVariableElement("Mood", out var mood)
        /// gives mood.Name == "Angry". Follows runtime changes such as dialog setters.
        /// </summary>
        public bool TryGetVariableElement(string fieldName, out GrimoireVariableElement element)
        {
            element = null;
            var snapshot = Snapshot;
            return snapshot != null && snapshot.TryGetVariableElement(fieldName, out element);
        }

        /// <summary>Every variable element a <c>types</c> or <c>typesList</c> field holds.</summary>
        public IReadOnlyList<GrimoireVariableElement> GetVariableElements(string fieldName)
        {
            var snapshot = Snapshot;
            return snapshot != null ? snapshot.GetVariableElements(fieldName) : Array.Empty<GrimoireVariableElement>();
        }

        /// <summary>Reference stubs for a field such as <c>levels</c>. Never hits the network.</summary>
        public IReadOnlyList<GrimoireObjectRef> GetReferences(string fieldName)
        {
            var snapshot = Snapshot;
            if (snapshot == null)
            {
                return Array.Empty<GrimoireObjectRef>();
            }

            return snapshot.GetReferences(fieldName);
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
            if (ActiveSource == GrimoireObjectDataSource.VersionDatabase)
            {
                snapshot = null;
                return stub != null &&
                    GrimoireVersionObjectAdapter.TryResolveKey(null, stub.CodeId, stub.Name, out var key) &&
                    GrimoireVersionObjectAdapter.TryBuildSnapshot(key, stub.Name, out snapshot);
            }
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
            if (ActiveSource == GrimoireObjectDataSource.VersionDatabase)
            {
                TryResolve(stub, out var versionSnapshot);
                return versionSnapshot;
            }
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
            var snapshot = Snapshot;
            if (snapshot?.Fields == null)
            {
                return list;
            }

            for (var f = 0; f < snapshot.Fields.Count; f++)
            {
                var field = snapshot.Fields[f];
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

    [Serializable]
    public sealed class GrimoireVersionFieldAlias
    {
        [Tooltip("Field name or ID used by game code or the connected snapshot.")]
        public string ApiField;

        [Tooltip("Matching field name in the imported version.")]
        public string VersionField;
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
