using System;
using System.Collections.Generic;
using UnityEngine;

namespace Grimoire.PluginV2.Internal
{
    /// <summary>
    /// Walks a <see cref="GrimoireDialogAsset"/> graph. Lines and questions wait
    /// for input; context, condition, setter, jump, and external-dialog nodes are
    /// resolved automatically. <see cref="GrimoireDialogPlayer"/> wraps this.
    /// </summary>
    public sealed class GrimoireDialogRunner
    {
        private const int MaxAutomaticSteps = 512;
        private const string LogPrefix = "[Grimoire Dialog] ";

        private readonly GrimoireDialogVariables _variables;
        private readonly HashSet<string> _pickedOptions = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<GrimoireDialogOption> _visibleOptions = new List<GrimoireDialogOption>();
        private readonly HashSet<string> _warnedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _objectFieldOverrides = new Dictionary<string, object>(StringComparer.Ordinal);

        public GrimoireDialogRunner(GrimoireDialogVariables variables)
        {
            _variables = variables ?? new GrimoireDialogVariables();
        }

        public GrimoireDialogVariables Variables => _variables;
        public GrimoireDialogAsset CurrentDialog { get; private set; }
        public GrimoireDialogNode CurrentNode { get; private set; }
        public bool IsActive => CurrentNode != null;

        /// <summary>Options on the current question that pass visibility and pick-once rules.</summary>
        public IReadOnlyList<GrimoireDialogOption> VisibleOptions => _visibleOptions;

        /// <summary>Reads type-element and object-field references. Optional; object fields fall back to scene Object Links, then ObjectRuntime.</summary>
        public Func<GrimoireDialogValueRef, object> ReadExternalValue;

        /// <summary>Writes type-element and object-field references. Optional; object fields fall back to scene Object Links, then ObjectRuntime.</summary>
        public Action<GrimoireDialogValueRef, object> WriteExternalValue;

        /// <summary>Raised when the runner stops on a line or question.</summary>
        public event Action<GrimoireDialogNode> NodeEntered;

        public event Action<GrimoireDialogEndReason> Ended;

        public bool Start(GrimoireDialogAsset dialog, string startNode = null)
        {
            if (dialog == null)
            {
                return false;
            }

            CurrentNode = null;
            CurrentDialog = dialog;
            _visibleOptions.Clear();
            _pickedOptions.Clear();

            var target = string.IsNullOrEmpty(startNode) ? dialog.StartingNode : startNode;
            if (string.IsNullOrEmpty(target) && dialog.Nodes.Count > 0)
            {
                target = dialog.Nodes[0]?.identifier;
            }

            return Run(dialog, target);
        }

        /// <summary>Continue past the current line. Ignored on a question that has answers.</summary>
        public void Advance()
        {
            var node = CurrentNode;
            if (node == null)
            {
                return;
            }

            if (node.type == GrimoireDialogNodeType.Question && _visibleOptions.Count > 0)
            {
                Debug.LogWarning(LogPrefix + $"'{node.identifier}' is a question. Call Choose(index) instead of Next().");
                return;
            }

            if (node.type == GrimoireDialogNodeType.End)
            {
                Finish(GrimoireDialogEndReason.Completed);
                return;
            }

            Follow(CurrentDialog, node.next);
        }

        /// <summary>Pick an answer by its index in <see cref="VisibleOptions"/>.</summary>
        public void Choose(int visibleIndex)
        {
            var node = CurrentNode;
            if (node == null || node.type != GrimoireDialogNodeType.Question)
            {
                Debug.LogWarning(LogPrefix + "Choose() was called, but the current line is not a question.");
                return;
            }

            if (visibleIndex < 0 || visibleIndex >= _visibleOptions.Count)
            {
                Debug.LogWarning(LogPrefix + $"Choice {visibleIndex} does not exist. There are {_visibleOptions.Count} choices.");
                return;
            }

            var dialog = CurrentDialog;
            var option = _visibleOptions[visibleIndex];
            if (node.pickOnce)
            {
                _pickedOptions.Add(PickKey(dialog, node, option));
            }

            ApplyAction(option.action, dialog, $"{node.identifier} (answer {visibleIndex + 1})");
            Follow(dialog, option.link);
        }

        public void Stop()
        {
            if (IsActive)
            {
                Finish(GrimoireDialogEndReason.Stopped);
            }
        }

        private void Follow(GrimoireDialogAsset from, GrimoireDialogLink link)
        {
            if (!TryResolve(from, link, out var dialog, out var identifier, out var reason))
            {
                Finish(reason);
                return;
            }

            Run(dialog, identifier);
        }

        private bool Run(GrimoireDialogAsset dialog, string identifier)
        {
            for (var step = 0; step < MaxAutomaticSteps; step++)
            {
                var node = dialog.FindNode(identifier);
                if (node == null)
                {
                    Debug.LogWarning(LogPrefix + $"Node '{identifier}' was not found in '{dialog.DisplayName}'. Re-import the dialog from Grimoire Connect.");
                    Finish(GrimoireDialogEndReason.MissingTarget);
                    return false;
                }

                GrimoireDialogLink next;
                switch (node.type)
                {
                    case GrimoireDialogNodeType.Line:
                    case GrimoireDialogNodeType.Question:
                        // A variable branch is never spoken. Older imports can label it as a
                        // line; still resolve it when it carries branches.
                        if (HasBranches(node))
                        {
                            next = ResolveCondition(dialog, node);
                            break;
                        }

                        Show(dialog, node);
                        return true;

                    case GrimoireDialogNodeType.End:
                        if (!string.IsNullOrEmpty(node.text))
                        {
                            Show(dialog, node);
                            return true;
                        }

                        Finish(GrimoireDialogEndReason.Completed);
                        return false;

                    case GrimoireDialogNodeType.Setter:
                        ApplyAction(node.setter, dialog, node.identifier);
                        next = node.next;
                        break;

                    case GrimoireDialogNodeType.Condition:
                        next = ResolveCondition(dialog, node);
                        break;

                    default:
                        next = node.next;
                        break;
                }

                if (!TryResolve(dialog, next, out dialog, out identifier, out var reason))
                {
                    Finish(reason);
                    return false;
                }
            }

            Debug.LogWarning(LogPrefix + "Stopped after too many automatic steps. The dialog probably loops through condition or setter nodes without a line in between.");
            Finish(GrimoireDialogEndReason.MissingTarget);
            return false;
        }

        private bool TryResolve(
            GrimoireDialogAsset from,
            GrimoireDialogLink link,
            out GrimoireDialogAsset dialog,
            out string identifier,
            out GrimoireDialogEndReason failReason)
        {
            dialog = from;
            identifier = null;
            failReason = GrimoireDialogEndReason.Completed;

            if (link == null || link.IsEmpty)
            {
                return false;
            }

            if (!link.IsExternal)
            {
                identifier = link.nextNode;
                return true;
            }

            var target = from.FindLinkedDialog(link.nextDialogId);
            if (target == null)
            {
                Debug.LogWarning(LogPrefix + $"'{from.DisplayName}' jumps to dialog '{link.nextDialogId}', which is not imported. Update the dialog in Grimoire Connect to import it.");
                failReason = GrimoireDialogEndReason.MissingTarget;
                return false;
            }

            dialog = target;
            identifier = string.IsNullOrEmpty(link.nextDialogNode) ? target.StartingNode : link.nextDialogNode;
            if (string.IsNullOrEmpty(identifier) && target.Nodes.Count > 0)
            {
                identifier = target.Nodes[0]?.identifier;
            }

            return true;
        }

        private void Show(GrimoireDialogAsset dialog, GrimoireDialogNode node)
        {
            CurrentDialog = dialog;
            CurrentNode = node;

            _visibleOptions.Clear();
            if (node.type == GrimoireDialogNodeType.Question && node.options != null)
            {
                foreach (var option in node.options)
                {
                    if (option == null)
                    {
                        continue;
                    }

                    if (option.visibleWhen != null && option.visibleWhen.IsSet && !Evaluate(option.visibleWhen, dialog))
                    {
                        continue;
                    }

                    if (node.pickOnce && !option.alwaysAvailable && _pickedOptions.Contains(PickKey(dialog, node, option)))
                    {
                        continue;
                    }

                    _visibleOptions.Add(option);
                }
            }

            NodeEntered?.Invoke(node);
        }

        private void Finish(GrimoireDialogEndReason reason)
        {
            CurrentNode = null;
            _visibleOptions.Clear();
            Ended?.Invoke(reason);
        }

        private static bool HasBranches(GrimoireDialogNode node)
        {
            return node.conditionBranches != null && node.conditionBranches.Count > 0;
        }

        private GrimoireDialogLink ResolveCondition(GrimoireDialogAsset dialog, GrimoireDialogNode node)
        {
            var subject = ResolveConditionSubject(dialog, node);

            if (node.conditionBranches != null && node.conditionBranches.Count > 0)
            {
                foreach (var branch in node.conditionBranches)
                {
                    if (branch == null)
                    {
                        continue;
                    }

                    var compare = CompareValue(branch, dialog);

                    if (GrimoireDialogConditions.Compare(subject, compare, branch.op))
                    {
                        if (branch.link != null && !branch.link.IsEmpty)
                        {
                            return branch.link;
                        }

                        WarnOnce("nolink:" + dialog.DialogId + "/" + node.identifier,
                            $"Variable branch '{DescribeSubject(node)}' in '{dialog.DisplayName}' matched " +
                            $"{branch.op} {DescribeValue(compare)}, but that branch is not connected. " +
                            "Connect it in Grimoire, then click Update on the dialog.");
                        return node.next != null && !node.next.IsEmpty ? node.next : null;
                    }
                }

                WarnOnce("nomatch:" + dialog.DialogId + "/" + node.identifier,
                    $"Condition '{DescribeSubject(node)}' in '{dialog.DisplayName}' had value {DescribeValue(subject)}, " +
                    "and none of its branches matched, so the dialog ended. Add a fallback branch in Grimoire or check the value.");
                return null;
            }

            return GrimoireDialogConditions.ToBool(subject) ? node.whenTrue : node.whenFalse;
        }

        private object ResolveConditionSubject(GrimoireDialogAsset dialog, GrimoireDialogNode node)
        {
            var declared = dialog.FindVariable(node.conditionVariable);
            var checkName = node.conditionCheckVariable;
            var checkRef = node.conditionCheckRef;
            if (declared != null &&
                (declared.type == "range" || declared.type == "range-selector") &&
                ((checkRef != null && checkRef.IsSet) || !string.IsNullOrEmpty(checkName)))
            {
                return checkRef != null && checkRef.IsSet
                    ? ResolveRef(checkRef, dialog)
                    : _variables.Get(dialog, checkName);
            }

            if (node.conditionSubjectRef != null && node.conditionSubjectRef.IsSet)
            {
                return ResolveRef(node.conditionSubjectRef, dialog);
            }

            var name = !string.IsNullOrEmpty(node.conditionVariable) ? node.conditionVariable : checkName;
            return _variables.Get(dialog, name);
        }

        private object CompareValue(GrimoireDialogConditionBranch branch, GrimoireDialogAsset dialog)
        {
            object compare = null;
            if (branch.valueRef != null && branch.valueRef.IsSet)
            {
                compare = ResolveRef(branch.valueRef, dialog);
            }

            if (compare == null && branch.value != null && branch.value.HasValue)
            {
                compare = branch.value.ToObject();
            }

            return compare;
        }

        private bool Evaluate(GrimoireDialogCondition condition, GrimoireDialogAsset dialog)
        {
            var subject = condition.subjectRef != null && condition.subjectRef.IsSet
                ? ResolveRef(condition.subjectRef, dialog)
                : _variables.Get(dialog, condition.variable);
            var compare = condition.valueRef != null && condition.valueRef.IsSet
                ? ResolveRef(condition.valueRef, dialog)
                : condition.value?.ToObject();
            return GrimoireDialogConditions.Compare(subject, compare, condition.op);
        }

        private void ApplyAction(GrimoireDialogVariableAction action, GrimoireDialogAsset dialog, string where)
        {
            if (action == null || !action.IsSet)
            {
                return;
            }

            var incoming = action.valueRef != null && action.valueRef.IsSet
                ? ResolveRef(action.valueRef, dialog)
                : action.value?.ToObject();

            var target = action.targetRef;
            if (target != null && target.IsSet && target.source != GrimoireDialogValueRef.SourceDialogVariable)
            {
                if (target.source == GrimoireDialogValueRef.SourceLiteral)
                {
                    return;
                }

                var current = ResolveRef(target, dialog);
                var updated = GrimoireDialogConditions.ComputeNext(current, incoming, action.op);

                if (WriteExternalValue != null)
                {
                    WriteExternalValue(target, updated);
                    if (target.source == GrimoireDialogValueRef.SourceObjectField)
                    {
                        _objectFieldOverrides[ObjectFieldKey(target)] = updated;
                    }

                    return;
                }

                if (target.source != GrimoireDialogValueRef.SourceObjectField)
                {
                    WarnOnce("write:" + target.source + ":" + target.typeId + target.elementId,
                        $"A setter writes to a Grimoire {Describe(target)}. Assign GrimoireDialogPlayer.WriteExternalValue to handle it.");
                    return;
                }

                if (GrimoireDialogObjectResolver.TryWrite(target, updated, $"Dialog '{dialog.DisplayName}' / {where}"))
                {
                    _objectFieldOverrides.Remove(ObjectFieldKey(target));
                    return;
                }

                // No Object Link in the scene: remember the value for this run so later
                // conditions see the change even when the export is missing too.
                _objectFieldOverrides[ObjectFieldKey(target)] = updated;
                if (!GrimoireDialogExportBridge.TryWriteObjectField(target, updated))
                {
                    WarnOnce("write:object_field:" + ObjectFieldKey(target),
                        $"A setter writes to {GrimoireDialogObjectResolver.Describe(target)}, but no Object Link for it is in the scene. " +
                        $"The change only lasts for this dialog run. {AddLinkHint(target)}");
                }

                return;
            }

            var name = target != null && target.IsSet ? target.variableName : action.variable;
            var value = GrimoireDialogConditions.ComputeNext(_variables.Get(dialog, name), incoming, action.op);
            _variables.Set(dialog, name, value, action.enforceRange);
        }

        private object ResolveRef(GrimoireDialogValueRef reference, GrimoireDialogAsset dialog)
        {
            switch (reference.source)
            {
                case GrimoireDialogValueRef.SourceLiteral:
                    return reference.literal?.ToObject();
                case GrimoireDialogValueRef.SourceDialogVariable:
                    return _variables.Get(dialog, reference.variableName);
            }

            if (reference.source == GrimoireDialogValueRef.SourceObjectField &&
                _objectFieldOverrides.TryGetValue(ObjectFieldKey(reference), out var overridden))
            {
                return overridden;
            }

            if (ReadExternalValue != null)
            {
                var provided = ReadExternalValue(reference);
                if (provided != null)
                {
                    return provided;
                }
            }

            if (reference.source == GrimoireDialogValueRef.SourceObjectField)
            {
                return ResolveObjectField(reference);
            }

            if (reference.authoredValue != null && reference.authoredValue.HasValue)
            {
                return reference.authoredValue.ToObject();
            }

            if (ReadExternalValue == null)
            {
                WarnOnce("read:" + reference.source + ":" + reference.typeId + reference.elementId,
                    $"A condition reads a Grimoire {Describe(reference)}. Assign GrimoireDialogPlayer.ReadExternalValue to handle it. It counts as empty for now.");
            }

            return null;
        }

        // Scene Object Link first, then the export's ObjectRuntime, then the library value baked in at import.
        private object ResolveObjectField(GrimoireDialogValueRef reference)
        {
            var warnKey = "read:object_field:" + ObjectFieldKey(reference);
            var linkFound = GrimoireDialogObjectResolver.TryFindLink(reference, out _, out var snapshot);
            if (linkFound)
            {
                if (GrimoireDialogObjectResolver.TryFindField(snapshot, reference, out var field))
                {
                    return GrimoireDialogObjectResolver.ToValue(field);
                }

                WarnOnce(warnKey,
                    $"The Object Link for {GrimoireDialogObjectResolver.Describe(reference)} is in the scene, but its snapshot has no such field. " +
                    "Select the Object Link and click Refresh so the snapshot includes the field. The dialog uses the library value for now.");
            }

            if (GrimoireDialogExportBridge.TryReadObjectField(reference, out var fromExport))
            {
                return fromExport;
            }

            if (reference.authoredValue != null && reference.authoredValue.HasValue)
            {
                if (!linkFound)
                {
                    WarnOnce(warnKey,
                        $"No Object Link for {GrimoireDialogObjectResolver.Describe(reference)} is in the scene, so the dialog uses the value from the Grimoire library " +
                        $"({reference.authoredValue}). {AddLinkHint(reference)}");
                }

                return reference.authoredValue.ToObject();
            }

            WarnOnce(warnKey,
                $"A condition reads {GrimoireDialogObjectResolver.Describe(reference)}, but no value is available. {AddLinkHint(reference)} It counts as empty for now.");
            return null;
        }

        private static string ObjectFieldKey(GrimoireDialogValueRef reference)
        {
            return (reference.objectId ?? "") + "\n" + (reference.fieldId ?? "");
        }

        private static string AddLinkHint(GrimoireDialogValueRef reference)
        {
            var key = !string.IsNullOrEmpty(reference.objectKey) ? reference.objectKey
                : !string.IsNullOrEmpty(reference.objectName) ? reference.objectName
                : reference.objectId;
            return $"Add an Object Link for '{key}' to the scene (select the dialog asset > Required Grimoire objects > Add to scene), " +
                   "or assign GrimoireDialogPlayer.ReadExternalValue / WriteExternalValue.";
        }

        private static string DescribeSubject(GrimoireDialogNode node)
        {
            var subject = node.conditionSubjectRef;
            if (subject != null && subject.IsSet)
            {
                return !string.IsNullOrEmpty(subject.label) ? subject.label : subject.variableName;
            }

            return string.IsNullOrEmpty(node.conditionVariable) ? node.identifier : node.conditionVariable;
        }

        private static string DescribeValue(object value)
        {
            return value == null ? "empty" : $"'{GrimoireDialogConditions.AsString(value)}'";
        }

        private void WarnOnce(string key, string message)
        {
            if (_warnedOnce.Add(key))
            {
                Debug.LogWarning(LogPrefix + message);
            }
        }

        private static string Describe(GrimoireDialogValueRef reference)
        {
            var kind = reference.source == GrimoireDialogValueRef.SourceObjectField ? "object field" : "type element";
            return string.IsNullOrEmpty(reference.label) ? kind : $"{kind} ('{reference.label}')";
        }

        private static string PickKey(GrimoireDialogAsset dialog, GrimoireDialogNode node, GrimoireDialogOption option)
        {
            var optionKey = string.IsNullOrEmpty(option.id) ? option.text : option.id;
            return dialog.DialogId + "/" + node.identifier + "/" + optionKey;
        }
    }
}
