using System;
using System.Collections.Generic;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Links a GameObject to a Grimoire object through its key (<c>code_id</c>,
    /// e.g. <c>characters/aragorn</c>).
    ///
    /// This component is pure serialized data. Everything that talks to the
    /// Grimoire API — key resolution, the object widget, task updates, and
    /// game-engine sync — lives in the editor assembly, so builds carry nothing
    /// but these fields.
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

        /// <summary>
        /// Fired in the editor when this component is destroyed so the editor
        /// assembly can remove the matching <c>game_engine_data</c> entry.
        /// </summary>
        public static event Action<GrimoireObjectLink> EditorDestroyed;

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

        /// <summary>Editor/API helper: replace the full linked-field list.</summary>
        public void ReplaceLinkedFields(List<GrimoireLinkedField> fields)
        {
            _linkedFields = fields ?? new List<GrimoireLinkedField>();
        }

        /// <summary>Editor/API helper: clear cached editable fields (e.g. on unlink).</summary>
        public void ClearLinkedFields()
        {
            _linkedFields?.Clear();
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

#if UNITY_EDITOR
        private void OnDestroy()
        {
            EditorDestroyed?.Invoke(this);
        }
#endif
    }
}
