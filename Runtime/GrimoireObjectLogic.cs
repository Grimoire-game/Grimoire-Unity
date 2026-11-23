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
        }

        [Serializable] public sealed class Dialog
        {
            public string id;
            public string field;
            public string start;
        }

        internal bool Trigger(GrimoireObjectLink link, string name)
        {
            if (version != 1)
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
                        foreach (var set in step.sets ?? Array.Empty<Expression>()) Set(set);
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
                    var field = Field(action.field);
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

            private GrimoireCachedField Field(string id)
            {
                if (_link.ApiSnapshot.TryGetField(id, out var field)) return field;
                throw new InvalidOperationException($"Missing field '{id}'.");
            }

            private object Read(Operand operand)
            {
                switch (operand.kind)
                {
                    case "boolean": return bool.Parse(operand.value);
                    case "number": return double.Parse(operand.value, CultureInfo.InvariantCulture);
                    case "string": return operand.value ?? "";
                    case "field":
                        var field = Field(operand.value);
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

            private void Set(Expression set)
            {
                if (set.op != "equal" && set.op != "add")
                    throw new InvalidOperationException($"Unsupported assignment '{set.op}'.");
                var value = Read(set.right);
                if (set.op == "add") value = AddToField(set.left, value);
                if (set.left.kind == "variable")
                {
                    if (!GrimoireVariables.TryGetElement(set.left.value, out _))
                        throw new InvalidOperationException($"Missing variable element '{set.left.value}'.");
                    GrimoireVariables.SetValue(set.left.value, value);
                    return;
                }
                if (set.left.kind != "field" || !_link.SetFieldValue(set.left.value,
                    GrimoireDialogConditions.AsString(value), _function.name))
                    throw new InvalidOperationException($"Unavailable assignment target '{set.left.value}'.");
            }

            private double AddToField(Operand target, object amount)
            {
                if (target.kind != "field") throw new InvalidOperationException("Add requires a numeric field.");
                var field = Field(target.value);
                if (field.Kind != "number" || field.Multiple || !GrimoireDialogConditions.IsNumberLike(amount))
                    throw new InvalidOperationException($"Add on '{field.DisplayLabel}' requires a single numeric field and amount.");

                var current = 0d;
                if (!string.IsNullOrEmpty(field.Value) && !_link.TryGetNumber(target.value, out current))
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
