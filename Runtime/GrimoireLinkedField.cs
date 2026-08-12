using System;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// One game-engine-editable field cached on a <see cref="GrimoireObjectLink"/>.
    /// Local values survive scene saves; <see cref="GrimoireValue"/> is the last
    /// known Grimoire baseline used for deviation / sync detection.
    /// </summary>
    [Serializable]
    public class GrimoireLinkedField
    {
        [SerializeField]
        private string _fieldId = "";

        [SerializeField]
        private string _label = "";

        [SerializeField]
        private string _kind = "";

        [SerializeField]
        private string _fieldType = "";

        [SerializeField]
        private string _sectionId = "";

        [SerializeField]
        private string _sectionTitle = "";

        [SerializeField]
        private bool _multiline;

        [SerializeField]
        private bool _readOnly;

        [SerializeField]
        private int _characterLimit;

        [SerializeField]
        private string _localValue = "";

        [SerializeField]
        private string _grimoireValue = "";

        public string FieldId
        {
            get => _fieldId;
            set => _fieldId = value ?? "";
        }

        public string Label
        {
            get => _label;
            set => _label = value ?? "";
        }

        public string Kind
        {
            get => _kind;
            set => _kind = value ?? "";
        }

        public string FieldType
        {
            get => _fieldType;
            set => _fieldType = value ?? "";
        }

        public string SectionId
        {
            get => _sectionId;
            set => _sectionId = value ?? "";
        }

        public string SectionTitle
        {
            get => _sectionTitle;
            set => _sectionTitle = value ?? "";
        }

        public bool Multiline
        {
            get => _multiline;
            set => _multiline = value;
        }

        public bool ReadOnly
        {
            get => _readOnly;
            set => _readOnly = value;
        }

        public int CharacterLimit
        {
            get => _characterLimit;
            set => _characterLimit = value;
        }

        /// <summary>Unity-side value persisted on the Object Link.</summary>
        public string LocalValue
        {
            get => _localValue;
            set => _localValue = value ?? "";
        }

        /// <summary>Last known value from Grimoire (baseline for deviation checks).</summary>
        public string GrimoireValue
        {
            get => _grimoireValue;
            set => _grimoireValue = value ?? "";
        }

        /// <summary>
        /// True when the local value differs from the cached Grimoire baseline
        /// and the field is writable.
        /// </summary>
        public bool IsDeviated =>
            !_readOnly &&
            !string.Equals(_localValue ?? "", _grimoireValue ?? "", StringComparison.Ordinal);

        public string DisplayLabel => string.IsNullOrEmpty(_label) ? _fieldId : _label;
    }
}
