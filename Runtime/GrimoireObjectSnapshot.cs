using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Grimoire.PluginV2
{
    [Serializable]
    public class GrimoireLocalizedValues
    {
        [SerializeField] private string _languageCode = "";
        [SerializeField] private string _value = "";
        [SerializeField] private List<string> _values = new List<string>();

        public string LanguageCode
        {
            get => _languageCode;
            set => _languageCode = value ?? "";
        }

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
    }

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

        [SerializeField]
        private string _kind = "";

        [SerializeField]
        private List<GrimoireLocalizedText> _localizedNames = new List<GrimoireLocalizedText>();

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

        public string Kind
        {
            get => _kind;
            set => _kind = value ?? "";
        }

        public bool HasId => !string.IsNullOrWhiteSpace(_id);

        public bool HasKey => !string.IsNullOrWhiteSpace(_codeId);

        public string DisplayName
        {
            get
            {
                var name = GetDisplayName();
                return !string.IsNullOrEmpty(name) ? name
                    : !string.IsNullOrEmpty(_codeId) ? _codeId
                    : _id ?? "";
            }
        }

        public List<GrimoireLocalizedText> LocalizedNames =>
            _localizedNames ?? (_localizedNames = new List<GrimoireLocalizedText>());

        public string GetDisplayName(string languageCode = null) =>
            GrimoireLocalization.Resolve(_name, LocalizedNames, languageCode);

        public void SetLocalizedName(string languageCode, string name)
        {
            SetLocalizedText(LocalizedNames, languageCode, name);
        }

        internal static void SetLocalizedText(
            List<GrimoireLocalizedText> rows,
            string languageCode,
            string text)
        {
            if (rows == null || string.IsNullOrWhiteSpace(languageCode))
            {
                return;
            }

            var code = languageCode.Trim();
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null &&
                    string.Equals(rows[i].LanguageCode, code, StringComparison.OrdinalIgnoreCase))
                {
                    rows[i].Text = text ?? "";
                    return;
                }
            }

            rows.Add(new GrimoireLocalizedText(code, text));
        }
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
        private List<GrimoireLocalizedText> _localizedLabels = new List<GrimoireLocalizedText>();

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

        [SerializeField]
        private List<GrimoireLocalizedValues> _localizedValues = new List<GrimoireLocalizedValues>();

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

        public List<GrimoireLocalizedValues> LocalizedValues =>
            _localizedValues ?? (_localizedValues = new List<GrimoireLocalizedValues>());

        public string GetDisplayValue(string languageCode = null)
        {
            var localized = FindLocalized(languageCode);
            return localized != null && !string.IsNullOrEmpty(localized.Value)
                ? localized.Value
                : _value ?? "";
        }

        public IReadOnlyList<string> GetDisplayValues(string languageCode = null)
        {
            var localized = FindLocalized(languageCode);
            return localized != null && localized.Values.Count > 0
                ? (IReadOnlyList<string>)localized.Values
                : Values;
        }

        public void SetLocalizedValues(
            string languageCode,
            string value,
            List<string> values)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
            {
                return;
            }

            var localized = FindLocalizedExact(languageCode) ?? new GrimoireLocalizedValues
            {
                LanguageCode = languageCode.Trim(),
            };
            if (!LocalizedValues.Contains(localized))
            {
                LocalizedValues.Add(localized);
            }

            localized.Value = value ?? "";
            localized.Values = values ?? new List<string>();
        }

        public void ClearLocalizedValues()
        {
            _localizedValues?.Clear();
        }

        private GrimoireLocalizedValues FindLocalized(string languageCode)
        {
            var code = string.IsNullOrWhiteSpace(languageCode)
                ? GrimoireLocalization.ActiveLanguage
                : languageCode.Trim();
            return FindLocalizedExact(code);
        }

        private GrimoireLocalizedValues FindLocalizedExact(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode) || _localizedValues == null)
            {
                return null;
            }

            for (var i = 0; i < _localizedValues.Count; i++)
            {
                var row = _localizedValues[i];
                if (row != null &&
                    string.Equals(row.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }

            return null;
        }

        public bool IsReference =>
            string.Equals(_kind, "reference", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for <c>types</c> / <c>typesList</c> fields, which hold Grimoire variable
        /// element ids. Resolve them with <see cref="GrimoireObjectSnapshot.GetVariableElements"/>.
        /// </summary>
        public bool IsVariableField => IsVariableFieldType(_fieldType);

        /// <summary>
        /// True for fields that point at other Grimoire data (objects, variable elements,
        /// dialogs, documents), even when an empty value was cached as text.
        /// </summary>
        public bool IsReferenceField => IsReference || IsReferenceFieldType(_fieldType);

        public string DisplayLabel
        {
            get
            {
                var localized = GrimoireLocalization.Resolve(_label, _localizedLabels);
                return string.IsNullOrEmpty(localized) ? _fieldId : localized;
            }
        }

        public void SetLocalizedLabel(string languageCode, string label)
        {
            GrimoireObjectRef.SetLocalizedText(
                _localizedLabels ?? (_localizedLabels = new List<GrimoireLocalizedText>()),
                languageCode,
                label);
        }

        public static bool IsVariableFieldType(string fieldType)
        {
            return string.Equals(fieldType, "types", StringComparison.Ordinal)
                   || string.Equals(fieldType, "typesList", StringComparison.Ordinal);
        }

        public static bool IsReferenceFieldType(string fieldType)
        {
            switch (fieldType)
            {
                case "types":
                case "typesList":
                case "glossaryItem":
                case "objectList":
                case "dialog":
                case "document":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The ids this field points at: its reference stubs, or the ids in its text
        /// value when it has no stubs (a value written by code or a dialog setter).
        /// </summary>
        public List<string> ReferencedIds()
        {
            var ids = new List<string>();
            if (_references != null && _references.Count > 0)
            {
                foreach (var stub in _references)
                {
                    if (stub != null && (stub.HasId || stub.HasKey))
                    {
                        ids.Add(stub.HasId ? stub.Id : stub.CodeId);
                    }
                }

                return ids;
            }

            foreach (var part in SplitValue(_multiple && Values.Count > 0 ? string.Join(",", Values) : _value))
            {
                ids.Add(part);
            }

            return ids;
        }

        /// <summary>
        /// Replace the reference stubs with the ids in <paramref name="text"/> (comma or
        /// newline separated). Known stubs are kept; new ones get their name from the
        /// synced variables or the object cache.
        /// </summary>
        internal void SetReferencesFromText(string text)
        {
            var previous = References;
            var next = new List<GrimoireObjectRef>();
            foreach (var id in SplitValue(text))
            {
                var stub = previous.Find(r => r != null &&
                    (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(r.CodeId, id, StringComparison.OrdinalIgnoreCase)));
                next.Add(stub ?? CreateStub(id));
            }

            _references = next;
        }

        private GrimoireObjectRef CreateStub(string id)
        {
            var stub = new GrimoireObjectRef { Id = id };
            if (IsVariableField)
            {
                if (GrimoireVariables.TryGetElement(id, out var element))
                {
                    stub.Name = element.DisplayName;
                }
            }
            else if (GrimoireObjectCache.TryGet(id, out var snapshot) && snapshot != null)
            {
                stub.Name = snapshot.Name;
                stub.CodeId = snapshot.ObjectKey;
            }

            return stub;
        }

        private static IEnumerable<string> SplitValue(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            foreach (var part in text.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
        }

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
        private List<GrimoireLocalizedText> _localizedNames = new List<GrimoireLocalizedText>();

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

        public string DisplayName => GetDisplayName();

        public string GetDisplayName(string languageCode = null) =>
            GrimoireLocalization.Resolve(_name, _localizedNames, languageCode);

        public List<GrimoireLocalizedText> LocalizedNames =>
            _localizedNames ?? (_localizedNames = new List<GrimoireLocalizedText>());

        public void SetLocalizedName(string languageCode, string name)
        {
            GrimoireObjectRef.SetLocalizedText(LocalizedNames, languageCode, name);
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

        /// <summary>Localized display copy without changing the raw value used by logic.</summary>
        public bool TryGetDisplayString(string name, out string value, string languageCode = null)
        {
            value = "";
            if (!TryGetField(name, out var field) || field.IsReference)
            {
                return false;
            }

            value = field.GetDisplayValue(languageCode);
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

        /// <summary>
        /// The variable element a <c>types</c> field points at, e.g. "Angry" for Mood.
        /// Needs synced variables (Grimoire Connect &gt; Dialogs &gt; Sync variables) for
        /// values written at runtime; values from Grimoire carry their name already.
        /// </summary>
        public bool TryGetVariableElement(string name, out GrimoireVariableElement element)
        {
            var elements = GetVariableElements(name);
            element = elements.Count > 0 ? elements[0] : null;
            return element != null;
        }

        /// <summary>Every variable element a <c>types</c> or <c>typesList</c> field points at.</summary>
        public IReadOnlyList<GrimoireVariableElement> GetVariableElements(string name)
        {
            if (!TryGetField(name, out var field))
            {
                return Array.Empty<GrimoireVariableElement>();
            }

            var elements = new List<GrimoireVariableElement>();
            foreach (var id in field.ReferencedIds())
            {
                if (GrimoireVariables.TryGetElement(id, out var element))
                {
                    elements.Add(element);
                    continue;
                }

                var stub = field.References.Find(r => r != null && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (stub != null && !string.IsNullOrEmpty(stub.Name))
                {
                    elements.Add(new GrimoireVariableElement(id, stub.Name, "", null, ""));
                }
            }

            return elements;
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
                    field.ClearLocalizedValues();
                    if (field.IsReferenceField)
                    {
                        field.SetReferencesFromText(field.Value);
                    }

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

        /// <summary>Reference stubs used by this snapshot (one hop), deduplicated.</summary>
        public List<GrimoireObjectRef> CollectReferences()
        {
            return CollectReferences(objectOnly: false);
        }

        /// <summary>Only references that can be loaded through the objects endpoint.</summary>
        public List<GrimoireObjectRef> CollectObjectReferences()
        {
            return CollectReferences(objectOnly: true);
        }

        private List<GrimoireObjectRef> CollectReferences(bool objectOnly)
        {
            var result = new List<GrimoireObjectRef>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_fields == null)
            {
                return result;
            }

            foreach (var field in _fields)
            {
                if (field?.References == null)
                {
                    continue;
                }

                foreach (var reference in field.References)
                {
                    if (reference == null || (!reference.HasId && !reference.HasKey))
                    {
                        continue;
                    }

                    if (objectOnly &&
                        !IsObjectReferenceField(field.FieldType) &&
                        !string.Equals(reference.Kind, "object", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var key = reference.HasId ? reference.Id : reference.CodeId;
                    if (seen.Add(key))
                    {
                        result.Add(reference);
                    }
                }
            }

            return result;
        }

        private static bool IsObjectReferenceField(string fieldType)
        {
            switch (fieldType)
            {
                case "glossaryItem":
                case "object":
                case "objectList":
                case "objects":
                    return true;
                default:
                    return false;
            }
        }
    }
}
