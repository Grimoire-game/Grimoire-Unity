using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// A pointer to another Grimoire object (id / key / name). Nested field
    /// data is not loaded until a script or the inspector asks for it.
    /// </summary>
    [Serializable]
    public class GrimoireObjectRef
    {
        [SerializeField]
        private string _id = "";

        [SerializeField]
        private string _codeId = "";

        [SerializeField]
        private string _name = "";

        public string Id
        {
            get => _id;
            set => _id = value ?? "";
        }

        public string CodeId
        {
            get => _codeId;
            set => _codeId = value ?? "";
        }

        public string Name
        {
            get => _name;
            set => _name = value ?? "";
        }

        public bool HasId => !string.IsNullOrWhiteSpace(_id);

        public bool HasKey => !string.IsNullOrWhiteSpace(_codeId);

        public string DisplayName =>
            !string.IsNullOrEmpty(_name) ? _name
            : !string.IsNullOrEmpty(_codeId) ? _codeId
            : _id ?? "";
    }

    /// <summary>
    /// One field on a <see cref="GrimoireObjectSnapshot"/>. Scalars use
    /// <see cref="Value"/>; <c>reference</c> fields use <see cref="References"/>.
    /// </summary>
    [Serializable]
    public class GrimoireCachedField
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
        private string _sectionTitle = "";

        [SerializeField]
        private bool _multiple;

        [SerializeField]
        private bool _gameEngineEditable;

        [SerializeField]
        private string _value = "";

        [SerializeField]
        private List<string> _values = new List<string>();

        [SerializeField]
        private List<GrimoireObjectRef> _references = new List<GrimoireObjectRef>();

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

        public string SectionTitle
        {
            get => _sectionTitle;
            set => _sectionTitle = value ?? "";
        }

        public bool Multiple
        {
            get => _multiple;
            set => _multiple = value;
        }

        public bool GameEngineEditable
        {
            get => _gameEngineEditable;
            set => _gameEngineEditable = value;
        }

        /// <summary>Effective scalar value (local Object Link edit when one exists).</summary>
        public string Value
        {
            get => _value;
            set => _value = value ?? "";
        }

        public List<string> Values
        {
            get => _values ?? (_values = new List<string>());
            set => _values = value ?? new List<string>();
        }

        public List<GrimoireObjectRef> References
        {
            get => _references ?? (_references = new List<GrimoireObjectRef>());
            set => _references = value ?? new List<GrimoireObjectRef>();
        }

        public bool IsReference =>
            string.Equals(_kind, "reference", StringComparison.OrdinalIgnoreCase);

        public string DisplayLabel => string.IsNullOrEmpty(_label) ? _fieldId : _label;

        public bool MatchesName(string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return false;
            }

            return string.Equals(_fieldId, query, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(_label, query, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(_fieldType, query, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// API snapshot of a Grimoire object: scalars plus reference stubs.
    /// Nested objects are not included until lazily loaded.
    /// </summary>
    [Serializable]
    public class GrimoireObjectSnapshot
    {
        [SerializeField]
        private string _objectId = "";

        [SerializeField]
        private string _objectKey = "";

        [SerializeField]
        private string _name = "";

        [SerializeField]
        private List<GrimoireCachedField> _fields = new List<GrimoireCachedField>();

        public string ObjectId
        {
            get => _objectId;
            set => _objectId = value ?? "";
        }

        public string ObjectKey
        {
            get => _objectKey;
            set => _objectKey = value ?? "";
        }

        public string Name
        {
            get => _name;
            set => _name = value ?? "";
        }

        public List<GrimoireCachedField> Fields
        {
            get => _fields ?? (_fields = new List<GrimoireCachedField>());
            set => _fields = value ?? new List<GrimoireCachedField>();
        }

        public bool IsEmpty =>
            string.IsNullOrEmpty(_objectId) && string.IsNullOrEmpty(_objectKey)
            && (Fields == null || Fields.Count == 0);

        public bool Matches(GrimoireObjectRef stub)
        {
            if (stub == null)
            {
                return false;
            }

            if (stub.HasId && string.Equals(_objectId, stub.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return stub.HasKey
                   && string.Equals(_objectKey, stub.CodeId, StringComparison.OrdinalIgnoreCase);
        }

        public bool MatchesIdOrKey(string idOrKey)
        {
            if (string.IsNullOrEmpty(idOrKey))
            {
                return false;
            }

            return string.Equals(_objectId, idOrKey, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(_objectKey, idOrKey, StringComparison.OrdinalIgnoreCase);
        }

        public bool TryGetField(string name, out GrimoireCachedField field)
        {
            field = null;
            if (string.IsNullOrEmpty(name) || _fields == null)
            {
                return false;
            }

            for (var i = 0; i < _fields.Count; i++)
            {
                var candidate = _fields[i];
                if (candidate != null && candidate.MatchesName(name))
                {
                    field = candidate;
                    return true;
                }
            }

            return false;
        }

        public bool TryGetString(string name, out string value)
        {
            value = "";
            if (!TryGetField(name, out var field) || field.IsReference)
            {
                return false;
            }

            value = field.Value ?? "";
            return true;
        }

        public bool TryGetNumber(string name, out double value)
        {
            value = 0;
            if (!TryGetString(name, out var raw)
                || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            return true;
        }

        public bool TryGetBool(string name, out bool value)
        {
            value = false;
            if (!TryGetString(name, out var raw))
            {
                return false;
            }

            raw = (raw ?? "").Trim();
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                || raw == "1"
                || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
                || raw == "0"
                || string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase)
                || raw.Length == 0)
            {
                value = false;
                return true;
            }

            return false;
        }

        public bool TryGetVector(string name, out Vector3 value)
        {
            value = Vector3.zero;
            if (!TryGetString(name, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var parts = raw
                .Replace("(", "")
                .Replace(")", "")
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                return false;
            }

            var z = 0f;
            if (parts.Length >= 3
                && !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            {
                return false;
            }

            value = new Vector3(x, y, z);
            return true;
        }

        public IReadOnlyList<GrimoireObjectRef> GetReferences(string name)
        {
            if (!TryGetField(name, out var field) || field.References == null)
            {
                return Array.Empty<GrimoireObjectRef>();
            }

            return field.References;
        }

        public void SetFieldValue(string fieldId, string value)
        {
            if (string.IsNullOrEmpty(fieldId) || _fields == null)
            {
                return;
            }

            for (var i = 0; i < _fields.Count; i++)
            {
                var field = _fields[i];
                if (field != null
                    && string.Equals(field.FieldId, fieldId, StringComparison.Ordinal))
                {
                    field.Value = value ?? "";
                    return;
                }
            }
        }

        /// <summary>Object ids and keys referenced by this snapshot (one hop).</summary>
        public void CollectReferenceKeys(HashSet<string> destination)
        {
            if (destination == null || _fields == null)
            {
                return;
            }

            for (var i = 0; i < _fields.Count; i++)
            {
                var field = _fields[i];
                if (field?.References == null)
                {
                    continue;
                }

                for (var r = 0; r < field.References.Count; r++)
                {
                    var stub = field.References[r];
                    if (stub == null)
                    {
                        continue;
                    }

                    if (stub.HasId)
                    {
                        destination.Add(stub.Id);
                    }

                    if (stub.HasKey)
                    {
                        destination.Add(stub.CodeId);
                    }
                }
            }
        }
    }
}
