using System;
using System.Collections.Generic;

namespace Grimoire.PluginV2.Internal
{
    /// <summary>
    /// Dialog-scoped variables. Each dialog keeps its own set, seeded from the
    /// initial values authored in Grimoire the first time the dialog is used.
    /// </summary>
    public sealed class GrimoireDialogVariables
    {
        public readonly struct Entry
        {
            public Entry(GrimoireDialogAsset dialog, string name, object value)
            {
                Dialog = dialog;
                Name = name;
                Value = value;
            }

            public GrimoireDialogAsset Dialog { get; }
            public string Name { get; }
            public object Value { get; }
        }

        private readonly Dictionary<GrimoireDialogAsset, Dictionary<string, object>> _values =
            new Dictionary<GrimoireDialogAsset, Dictionary<string, object>>();

        /// <summary>Raised with (dialog, name, oldValue, newValue).</summary>
        public event Action<GrimoireDialogAsset, string, object, object> Changed;

        public object Get(GrimoireDialogAsset dialog, string name)
        {
            if (dialog == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            return Scope(dialog).TryGetValue(name, out var value) ? value : null;
        }

        public void Set(GrimoireDialogAsset dialog, string name, object value, bool clampToRange = true)
        {
            if (dialog == null || string.IsNullOrEmpty(name))
            {
                return;
            }

            var scope = Scope(dialog);
            var declared = dialog.FindVariable(name);
            var next = Coerce(declared, value, clampToRange);
            scope.TryGetValue(name, out var old);
            scope[name] = next;

            if (!GrimoireDialogConditions.LooseEqual(old, next))
            {
                Changed?.Invoke(dialog, name, old, next);
            }
        }

        public void Reset(GrimoireDialogAsset dialog = null)
        {
            if (dialog == null)
            {
                _values.Clear();
            }
            else
            {
                _values.Remove(dialog);
            }
        }

        public IEnumerable<Entry> All()
        {
            foreach (var pair in _values)
            {
                foreach (var variable in pair.Value)
                {
                    yield return new Entry(pair.Key, variable.Key, variable.Value);
                }
            }
        }

        private Dictionary<string, object> Scope(GrimoireDialogAsset dialog)
        {
            if (_values.TryGetValue(dialog, out var scope))
            {
                return scope;
            }

            scope = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var variable in dialog.Variables)
            {
                if (variable != null && !string.IsNullOrEmpty(variable.name))
                {
                    scope[variable.name] = DefaultValue(variable);
                }
            }

            _values[dialog] = scope;
            return scope;
        }

        private static object DefaultValue(GrimoireDialogVariable variable)
        {
            var initial = variable.initialValue != null && variable.initialValue.HasValue
                ? variable.initialValue.ToObject()
                : null;
            return Coerce(variable, initial, clampToRange: false);
        }

        private static object Coerce(GrimoireDialogVariable declared, object value, bool clampToRange)
        {
            if (declared == null)
            {
                return value;
            }

            switch (declared.type)
            {
                case "boolean":
                    return GrimoireDialogConditions.ToBool(value);
                case "text":
                    return GrimoireDialogConditions.AsString(value);
                case "number":
                case "range":
                case "range-selector":
                    var n = GrimoireDialogConditions.ToNumber(value);
                    if (clampToRange && declared.hasRange)
                    {
                        n = Math.Max(declared.rangeMin, Math.Min(declared.rangeMax, n));
                    }

                    return n;
                default:
                    return value;
            }
        }
    }
}
