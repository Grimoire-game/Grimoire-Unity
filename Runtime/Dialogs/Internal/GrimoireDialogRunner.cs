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

        /// <summary>Reads type-element and object-field references. Optional; object fields fall back to ObjectRuntime.</summary>
        public Func<GrimoireDialogValueRef, object> ReadExternalValue;

        /// <summary>Writes type-element and object-field references. Optional; object fields fall back to ObjectRuntime.</summary>
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

            ApplyAction(option.action, dialog);
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
                        ApplyAction(node.setter, dialog);
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

        private GrimoireDialogLink ResolveCondition(GrimoireDialogAsset dialog, GrimoireDialogNode node)
        {
            var subject = node.conditionSubjectRef != null && node.conditionSubjectRef.IsSet
                ? ResolveRef(node.conditionSubjectRef, dialog)
                : _variables.Get(dialog, node.conditionVariable);

            if (node.conditionBranches != null && node.conditionBranches.Count > 0)
            {
                foreach (var branch in node.conditionBranches)
                {
                    if (branch == null)
                    {
                        continue;
                    }

                    var compare = branch.valueRef != null && branch.valueRef.IsSet
                        ? ResolveRef(branch.valueRef, dialog)
                        : branch.value?.ToObject();

                    if (GrimoireDialogConditions.Compare(subject, compare, branch.op))
                    {
                        return branch.link;
                    }
                }

                WarnOnce("nomatch:" + dialog.DialogId + "/" + node.identifier,
                    $"Condition '{DescribeSubject(node)}' in '{dialog.DisplayName}' had value {DescribeValue(subject)}, " +
                    "and none of its branches matched, so the dialog ended. Add a fallback branch in Grimoire or check the value.");
                return null;
            }

            return GrimoireDialogConditions.ToBool(subject) ? node.whenTrue : node.whenFalse;
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

        private void ApplyAction(GrimoireDialogVariableAction action, GrimoireDialogAsset dialog)
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
                }
                else if (target.source != GrimoireDialogValueRef.SourceObjectField ||
                         !GrimoireDialogExportBridge.TryWriteObjectField(target, updated))
                {
                    WarnOnce("write:" + target.source + ":" + target.objectId + target.fieldId,
                        $"A setter writes to a Grimoire {Describe(target)}. {MissingExternalHint(target, "WriteExternalValue")}");
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

            if (ReadExternalValue != null)
            {
                var provided = ReadExternalValue(reference);
                if (provided != null)
                {
                    return provided;
                }
            }

            if (reference.source == GrimoireDialogValueRef.SourceObjectField &&
                GrimoireDialogExportBridge.TryReadObjectField(reference, out var fromExport))
            {
                return fromExport;
            }

            if (ReadExternalValue == null)
            {
                WarnOnce("read:" + reference.source + ":" + reference.objectId + reference.fieldId,
                    $"A condition reads a Grimoire {Describe(reference)}. {MissingExternalHint(reference, "ReadExternalValue")} It counts as empty for now.");
            }

            return null;
        }

        private static string MissingExternalHint(GrimoireDialogValueRef reference, string hook)
        {
            if (reference.source != GrimoireDialogValueRef.SourceObjectField)
            {
                return $"Assign GrimoireDialogPlayer.{hook} to handle it.";
            }

            if (!GrimoireDialogExportBridge.HasObjectRuntime)
            {
                return $"Import a Unity export (Grimoire Connect > Export) so the value comes from ObjectRuntime, or assign GrimoireDialogPlayer.{hook}.";
            }

            return $"ObjectRuntime has no value for '{GrimoireDialogExportBridge.DescribeKey(reference)}'. " +
                   $"Re-import the export and click Update on the dialog, or assign GrimoireDialogPlayer.{hook}.";
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
