using System;
using System.Collections.Generic;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// One value of a Grimoire variable, e.g. "Angry" in "Mood". Object fields of
    /// type <c>types</c> store the element <see cref="Id"/>.
    /// </summary>
    [Serializable]
    public class GrimoireVariableElement
    {
        [SerializeField] private string _id = "";
        [SerializeField] private string _name = "";
        [SerializeField] private string _description = "";
        [SerializeField] private GrimoireDialogValue _value = new GrimoireDialogValue();
        [SerializeField] private string _stringId = "";
        [SerializeField] private List<GrimoireLocalizedText> _localizedNames =
            new List<GrimoireLocalizedText>();
        [SerializeField] private bool _hasRange;
        [SerializeField] private double _rangeMin;
        [SerializeField] private double _rangeMax;

        [NonSerialized] private GrimoireVariable _variable;

        public GrimoireVariableElement()
        {
        }

        internal GrimoireVariableElement(
            string id, string name, string description, object value, string stringId,
            double? rangeMin = null, double? rangeMax = null,
            List<GrimoireLocalizedText> localizedNames = null)
        {
            _id = id ?? "";
            _name = name ?? "";
            _description = description ?? "";
            _value = GrimoireDialogValue.From(value);
            _stringId = stringId ?? "";
            _localizedNames = localizedNames ?? new List<GrimoireLocalizedText>();
            _hasRange = rangeMin.HasValue && rangeMax.HasValue;
            _rangeMin = rangeMin ?? 0;
            _rangeMax = rangeMax ?? 0;
        }

        public string Id => _id;
        public string Name => _name;
        public string DisplayName => GetDisplayName();
        public string Description => _description;

        /// <summary>Grimoire string id of the name, when the variable is translated.</summary>
        public string StringId => _stringId;

        public IReadOnlyList<GrimoireLocalizedText> LocalizedNames =>
            _localizedNames ?? (_localizedNames = new List<GrimoireLocalizedText>());

        public string GetDisplayName(string languageCode = null) =>
            GrimoireLocalization.Resolve(_name, _localizedNames, languageCode);

        /// <summary>
        /// The value authored in Grimoire: a number for number / percentage variables,
        /// a bool for boolean ones, otherwise null. For the value at runtime (after
        /// dialog setters), use <see cref="GrimoireVariables.GetValue"/>.
        /// </summary>
        public object AuthoredValue => _value != null ? _value.ToObject() : null;

        /// <summary>True for elements of a range variable; see <see cref="RangeMin"/> and <see cref="RangeMax"/>.</summary>
        public bool HasRange => _hasRange;

        public double RangeMin => _rangeMin;
        public double RangeMax => _rangeMax;

        /// <summary>The variable this element belongs to. Null for an element that is not in the synced data.</summary>
        public GrimoireVariable Variable => _variable;

        internal void Attach(GrimoireVariable variable)
        {
            _variable = variable;
        }

        public override string ToString()
        {
            return _variable != null ? $"{_variable.Name}: {DisplayName}" : DisplayName;
        }
    }

    /// <summary>A Grimoire variable (for example "Mood") with its elements.</summary>
    [Serializable]
    public class GrimoireVariable
    {
        [SerializeField] private string _id = "";
        [SerializeField] private string _name = "";
        [SerializeField] private string _elementType = "";
        [SerializeField] private string _description = "";
        [SerializeField] private string _group = "";
        [SerializeField] private string _updatedAt = "";
        [SerializeField] private List<GrimoireVariableElement> _elements = new List<GrimoireVariableElement>();

        public GrimoireVariable()
        {
        }

        internal GrimoireVariable(
            string id, string name, string elementType, string description, string group, string updatedAt,
            List<GrimoireVariableElement> elements)
        {
            _id = id ?? "";
            _name = name ?? "";
            _elementType = elementType ?? "";
            _description = description ?? "";
            _group = group ?? "";
            _updatedAt = updatedAt ?? "";
            _elements = elements ?? new List<GrimoireVariableElement>();
        }

        public string Id => _id;
        public string Name => _name;

        /// <summary>string, number, percentage, range, type, or boolean.</summary>
        public string ElementType => _elementType;

        public string Description => _description;
        public string Group => _group;
        public string UpdatedAt => _updatedAt;
        public IReadOnlyList<GrimoireVariableElement> Elements => _elements;

        /// <summary>An element by id or (case-insensitive) name.</summary>
        public GrimoireVariableElement FindElement(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName))
            {
                return null;
            }

            foreach (var element in _elements)
            {
                if (element != null && string.Equals(element.Id, idOrName, StringComparison.OrdinalIgnoreCase))
                {
                    return element;
                }
            }

            foreach (var element in _elements)
            {
                if (element != null && string.Equals(element.Name, idOrName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return element;
                }
            }

            return null;
        }

        internal void AttachElements()
        {
            foreach (var element in _elements)
            {
                element?.Attach(this);
            }
        }
    }

    /// <summary>
    /// The game's variables, synced from Grimoire Connect into
    /// Assets/GrimoireVariables/Resources so builds read them without signing in.
    /// Use <see cref="GrimoireVariables"/> to look values up.
    /// </summary>
    public class GrimoireVariableLibrary : ScriptableObject
    {
        /// <summary>Name passed to Resources.Load.</summary>
        public const string ResourcesName = "GrimoireVariables";

        [SerializeField] private string _gameId = "";
        [SerializeField] private string _syncedAt = "";
        [SerializeField] private List<GrimoireVariable> _variables = new List<GrimoireVariable>();

        [NonSerialized] private Dictionary<string, GrimoireVariableElement> _elementIndex;

        public string GameId => _gameId;

        /// <summary>When Grimoire Connect last synced the variables (UTC, ISO 8601).</summary>
        public string SyncedAt => _syncedAt;

        public IReadOnlyList<GrimoireVariable> Variables => _variables;

        public bool TryGetElement(string elementId, out GrimoireVariableElement element)
        {
            element = null;
            if (string.IsNullOrEmpty(elementId))
            {
                return false;
            }

            EnsureIndex();
            return _elementIndex.TryGetValue(elementId.Trim(), out element);
        }

        /// <summary>A variable by id or (case-insensitive) name.</summary>
        public GrimoireVariable FindVariable(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName))
            {
                return null;
            }

            EnsureIndex();
            foreach (var variable in _variables)
            {
                if (variable != null && string.Equals(variable.Id, idOrName, StringComparison.OrdinalIgnoreCase))
                {
                    return variable;
                }
            }

            foreach (var variable in _variables)
            {
                if (variable != null && string.Equals(variable.Name, idOrName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return variable;
                }
            }

            return null;
        }

        internal void SetSyncedData(string gameId, List<GrimoireVariable> variables)
        {
            _gameId = gameId ?? "";
            _syncedAt = DateTime.UtcNow.ToString("o");
            _variables = variables ?? new List<GrimoireVariable>();
            _elementIndex = null;
        }

        private void EnsureIndex()
        {
            if (_elementIndex != null)
            {
                return;
            }

            _elementIndex = new Dictionary<string, GrimoireVariableElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in _variables)
            {
                if (variable == null)
                {
                    continue;
                }

                variable.AttachElements();
                foreach (var element in variable.Elements)
                {
                    if (element != null && !string.IsNullOrEmpty(element.Id) && !_elementIndex.ContainsKey(element.Id))
                    {
                        _elementIndex[element.Id] = element;
                    }
                }
            }
        }

        private void OnValidate()
        {
            _elementIndex = null;
        }
    }
}
