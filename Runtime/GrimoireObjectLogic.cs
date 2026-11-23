using System;
using System.Globalization;
using Grimoire.PluginV2.Internal;
using UnityEngine;

namespace Grimoire.PluginV2
{
    // Serializable wire format shared by the API response and the offline snapshot.
    [Serializable]
    public sealed class GrimoireObjectLogic
    {
        public int version;
        public bool IsSupported => version == 1 || version == 2;
        public Function[] functions;

        [Serializable] public sealed class Function
        {
            public string name;
            public string error;
            public Step[] steps;
        }

        [Serializable] public sealed class Step
        {
            public int depth;
            public Group[] conditions;
            public string[] joins;
            public Dialog[] dialogs;
            public Expression[] sets;
        }

        [Serializable] public sealed class Group
        {
            public Expression[] terms;
            public string[] joins;
        }

        [Serializable] public sealed class Expression
        {
            public Operand left;
            public string op;
            public Operand right;
        }

        [Serializable] public sealed class Operand
        {
            public string kind;
            public string value;
            public string objectId;
        }

        [Serializable] public sealed class Dialog
        {
            public string id;
            public string field;
            public string start;
        }

        internal bool Trigger(GrimoireObjectLink link, string name)
        {
            if (!IsSupported)
            {
                if (version != 0) Debug.LogWarning($"[Grimoire Logic] Unsupported logic version {version} on '{link.ObjectKey}'.", link);
                return false;
            }
            var fn = Array.Find(functions ?? Array.Empty<Function>(), f => f != null && f.name == name);
            return fn != null && new Invocation(link, fn).Start();
        }

        private sealed class Invocation
        {
            private readonly GrimoireObjectLink _link;
            private readonly GrimoireObjectSnapshot _snapshot;
            private readonly Function _function;
            private int _step;
            private int _dialog;
            private bool _entered;

            public Invocation(GrimoireObjectLink link, Function function)
            {
                _link = link;
                _snapshot = link.ApiSnapshot;
                _function = function;
            }

            public bool Start() => string.IsNullOrEmpty(_function.error) ? Advance() : Fail(_function.error);

            // Each playback resumes this invocation, on the original receiver.
            private bool Advance()
            {
                if (!_link || !_link.isActiveAndEnabled ||
                    _link.ActiveSource != GrimoireObjectDataSource.ConnectedSnapshot ||
                    _link.ApiSnapshot?.ObjectId != _snapshot.ObjectId) return false;
                try
                {
                    var steps = _function.steps ?? Array.Empty<Step>();
                    while (_step < steps.Length)
                    {
                        var step = steps[_step];
                        if (!_entered && !Passes(step))
                        {
                            do { _step++; } while (_step < steps.Length && steps[_step].depth > step.depth);
                            continue;
                        }
                        _entered = true;
                        if (_dialog < (step.dialogs?.Length ?? 0)) return Play(step.dialogs[_dialog++]);
                        var sets = step.sets ?? Array.Empty<Expression>();
                        // An unloaded target must not leave an earlier assignment in this step applied.
                        if (_snapshot.Logic.version == 2)
                            foreach (var set in sets) ValidateAssignment(set);
                        foreach (var set in sets) Set(set);
                        _step++;
                        _dialog = 0;
                        _entered = false;
                    }
                    return true;
                }
                catch (InvalidOperationException error)
                {
                    return Fail(error.Message);
                }
            }

            private bool Play(Dialog action)
            {
                var id = action.id;
                if (!string.IsNullOrEmpty(action.field))
                {
                    if (!_snapshot.TryGetField(action.field, out var field))
                        throw new InvalidOperationException($"Missing dialog field '{action.field}'.");
                    var ids = field.ReferencedIds();
                    if (ids.Count == 0)
                        return Fail($"Choose a dialog for '{field.DisplayLabel}' in Grimoire and refresh the Object Link.");
                    id = ids[0];
                }
                var asset = _snapshot.LogicDialogs?.Find(d => d && (d.DialogId == id || d.DialogKey == id));
                if (!asset) return Fail($"Dialog '{id}' is unavailable. Refresh the Object Link.");
                if (!string.IsNullOrEmpty(action.start) && asset.FindNode(action.start) == null)
                    return Fail($"Dialog '{id}' has no start node '{action.start}'.");
                var player = GrimoireDialogPlayer.Main;
                if (!player) return Fail("The scene needs a Grimoire Dialog Player.");
                if (player.IsPlaying) return false;
                player.StartDialog(asset, action.start, reason =>
                {
                    if (reason == GrimoireDialogEndReason.Completed) Advance();
                });
                return true;
            }

            private GrimoireCachedField Field(Operand operand, out GrimoireObjectLink owner)
            {
                owner = _link;
                var snapshot = _snapshot;
                if (operand.kind == "objectField" && (string.IsNullOrEmpty(operand.objectId) ||
                    !GrimoireDialogObjectResolver.TryFindLogicTarget(_link, operand.objectId, out owner, out snapshot)))
                    throw new InvalidOperationException($"Unavailable target object '{operand.objectId}'.");
                if (snapshot.TryGetField(operand.value, out var field)) return field;
                throw new InvalidOperationException($"Missing field '{operand.value}' on '{snapshot.ObjectId}'.");
            }

            private object Read(Operand operand)
            {
                switch (operand.kind)
                {
                    case "boolean": return bool.Parse(operand.value);
                    case "number": return double.Parse(operand.value, CultureInfo.InvariantCulture);
                    case "string": return operand.value ?? "";
                    case "field":
                    case "objectField":
                        var field = Field(operand, out _);
                        if (!field.IsReferenceField) return GrimoireDialogObjectResolver.ToValue(field);
                        var ids = field.ReferencedIds();
                        return ids.Count == 1 ? (object)ids[0] : ids;
                    case "variable":
                        if (GrimoireVariables.TryGetElement(operand.value, out _))
                            return GrimoireVariables.GetValue(operand.value);
                        throw new InvalidOperationException($"Missing variable element '{operand.value}'.");
                    default: throw new InvalidOperationException($"Unsupported operand '{operand.kind}'.");
                }
            }

            private void ValidateAssignment(Expression set)
            {
                if (set.op != "equal" && set.op != "add" && set.op != "subtract")
                    throw new InvalidOperationException($"Unsupported assignment '{set.op}'.");
                if (set.left.kind == "variable" && set.op == "equal")
                {
                    Read(set.left);
                }
                else
                {
                    if (set.left.kind != "field" && set.left.kind != "objectField")
                        throw new InvalidOperationException("Assignment requires a field.");
                    var field = Field(set.left, out _);
                    if (set.op == "subtract" && !(field.Multiple &&
                        (field.FieldType == "glossaryItem" || field.FieldType == "objectList")))
                        throw new InvalidOperationException($"Subtract requires an object list on '{field.DisplayLabel}'.");
                }
                Read(set.right);
            }

            private void Set(Expression set)
            {
                var value = Read(set.right);
                if (set.left.kind == "variable" && set.op == "equal")
                {
                    if (!GrimoireVariables.TryGetElement(set.left.value, out _))
                        throw new InvalidOperationException($"Missing variable element '{set.left.value}'.");
                    GrimoireVariables.SetValue(set.left.value, value);
                    return;
                }
                if (set.left.kind != "field" && set.left.kind != "objectField")
                    throw new InvalidOperationException("Assignment requires a field.");
                var field = Field(set.left, out var owner);
                switch (set.op)
                {
                    case "equal": break;
                    case "add": value = AddToField(field, value); break;
                    case "subtract":
                        if (!(value is string id) || string.IsNullOrWhiteSpace(id)) return;
                        var ids = field.ReferencedIds();
                        if (ids.RemoveAll(entry => SameObject(entry, id)) == 0) return;
                        value = ids;
                        break;
                    default: throw new InvalidOperationException($"Unsupported assignment '{set.op}'.");
                }
                if (!GrimoireDialogObjectResolver.WriteField(owner, field, GrimoireDialogConditions.AsString(value), _function.name))
                    throw new InvalidOperationException($"Unavailable assignment target '{set.left.value}'.");
            }

            private static bool SameObject(string entry, string id)
            {
                if (string.Equals(entry, id, StringComparison.OrdinalIgnoreCase)) return true;
                if (!GrimoireObjectCache.TryGet(entry, out var item)) return false;
                return item.MatchesIdOrKey(id) || (GrimoireObjectCache.TryGet(id, out var other) &&
                    item.MatchesIdOrKey(other.ObjectId));
            }

            private static double AddToField(GrimoireCachedField field, object amount)
            {
                if (field.Kind != "number" || field.Multiple || !GrimoireDialogConditions.IsNumberLike(amount))
                    throw new InvalidOperationException($"Add on '{field.DisplayLabel}' requires a single numeric field and amount.");

                var current = 0d;
                if (!string.IsNullOrEmpty(field.Value) &&
                    !double.TryParse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out current))
                    throw new InvalidOperationException($"'{field.DisplayLabel}' does not contain a number.");
                var sum = current + GrimoireDialogConditions.ToNumber(amount);
                if (double.IsNaN(sum) || double.IsInfinity(sum))
                    throw new InvalidOperationException($"Add on '{field.DisplayLabel}' exceeds the numeric range.");
                return sum;
            }

            private bool Passes(Step step) => Evaluate(step.conditions, step.joins,
                group => Evaluate(group.terms, group.joins, Compare));

            private static bool Evaluate<T>(T[] items, string[] joins, Func<T, bool> evaluate)
            {
                if (items == null || items.Length == 0) return true;
                var result = evaluate(items[0]);
                for (var i = 1; i < items.Length; i++)
                {
                    var next = evaluate(items[i]);
                    switch (joins[i - 1])
                    {
                        case "and": result &= next; break;
                        case "or": result |= next; break;
                        case "xor": result ^= next; break;
                        default: throw new InvalidOperationException("Unsupported condition join.");
                    }
                }
                return result;
            }

            private bool Compare(Expression term)
            {
                var left = Read(term.left);
                var right = Read(term.right);
                switch (term.op)
                {
                    case "equal": return GrimoireDialogConditions.Compare(left, right, "==");
                    case "not_equal": return GrimoireDialogConditions.Compare(left, right, "!=");
                    case "less_than": return GrimoireDialogConditions.Compare(left, right, "<");
                    case "greater_than": return GrimoireDialogConditions.Compare(left, right, ">");
                    case "less_than_or_equal": return GrimoireDialogConditions.Compare(left, right, "<=");
                    case "greater_than_or_equal": return GrimoireDialogConditions.Compare(left, right, ">=");
                    case "includes":
                    case "not_includes": return GrimoireDialogConditions.Compare(left, right, term.op);
                    default: throw new InvalidOperationException($"Unsupported condition '{term.op}'.");
                }
            }

            private bool Fail(string detail)
            {
                Debug.LogWarning($"[Grimoire Logic] {_link.ObjectKey}.{_function.name}: {detail}", _link);
                return false;
            }
        }
    }
}
