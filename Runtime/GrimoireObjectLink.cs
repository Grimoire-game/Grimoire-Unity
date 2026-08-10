using System;
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

#if UNITY_EDITOR
        private void OnDestroy()
        {
            EditorDestroyed?.Invoke(this);
        }
#endif
    }
}
