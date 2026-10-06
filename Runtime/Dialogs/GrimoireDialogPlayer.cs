using System;
using System.Collections.Generic;
using Grimoire.PluginV2.Internal;
using UnityEngine;
using UnityEngine.Events;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Plays Grimoire dialogs. One player in the scene plays every dialog; pass
    /// the dialog asset in from your own code:
    ///
    /// <code>
    /// public GrimoireDialogAsset bedDialog;          // drag in from Assets/GrimoireDialogs
    ///
    /// GrimoireDialogPlayer.Main.StartDialog(bedDialog);
    /// GrimoireDialogPlayer.Main.Next();              // continue after a normal line
    /// GrimoireDialogPlayer.Main.Choose(0);           // pick an answer on a question
    /// GrimoireDialogPlayer.Main.Stop();              // end early
    /// </code>
    ///
    /// Listen to <see cref="OnLine"/>, <see cref="OnChoices"/>, and
    /// <see cref="OnDialogEnded"/> to drive your own UI, or use the
    /// <see cref="GrimoireDialogView"/> that Grimoire Connect sets up for you.
    /// </summary>
    [AddComponentMenu("Grimoire/Grimoire Dialog Player")]
    public class GrimoireDialogPlayer : MonoBehaviour
    {
        [Tooltip("Language code, e.g. 'de'. Leave empty to use the Connect language in the editor and the export language in builds.")]
        public string language = "";

        [Tooltip("Keep variable values after a dialog ends. Off = every run starts from the values authored in Grimoire.")]
        public bool keepVariablesBetweenRuns;

        [Header("Events")]
        [Tooltip("A normal line is shown. Call Next() to continue.")]
        public UnityEvent<DialogLine> OnLine = new UnityEvent<DialogLine>();

        [Tooltip("A question is shown. Call Choose(index) with one of line.Choices.")]
        public UnityEvent<DialogLine> OnChoices = new UnityEvent<DialogLine>();

        public UnityEvent OnDialogStarted = new UnityEvent();
        public UnityEvent OnDialogEnded = new UnityEvent();

        private GrimoireDialogVariables _variables;
        private GrimoireDialogRunner _runner;
        private bool _switching;

        /// <summary>The dialog player in the scene (the last one enabled).</summary>
        public static GrimoireDialogPlayer Main { get; private set; }

        /// <summary>True while a dialog is running.</summary>
        public bool IsPlaying => _runner != null && _runner.IsActive;

        /// <summary>The dialog playing right now (a linked dialog once the story jumps there), or null.</summary>
        public GrimoireDialogAsset CurrentDialog => IsPlaying ? _runner.CurrentDialog : null;

        /// <summary>The asset passed to the last <see cref="StartDialog"/> call.</summary>
        public GrimoireDialogAsset LastStartedDialog { get; private set; }

        /// <summary>The line on screen right now, or null when nothing is playing.</summary>
        public DialogLine CurrentLine { get; private set; }

        /// <summary>Why the last dialog ended.</summary>
        public GrimoireDialogEndReason LastEndReason { get; private set; }

        /// <summary>
        /// Optional: supply values for conditions that read a Grimoire type element
        /// or object field (for example from your save game). Return null if unknown;
        /// object fields then come from the imported export's ObjectRuntime.
        /// </summary>
        public Func<GrimoireDialogValueRef, object> ReadExternalValue
        {
            get => Runner.ReadExternalValue;
            set => Runner.ReadExternalValue = value;
        }

        /// <summary>
        /// Optional: apply setters that write to a Grimoire type element or object field.
        /// Without it, object fields are written to the imported export's ObjectRuntime.
        /// </summary>
        public Action<GrimoireDialogValueRef, object> WriteExternalValue
        {
            get => Runner.WriteExternalValue;
            set => Runner.WriteExternalValue = value;
        }

        internal GrimoireDialogRunner Runner
        {
            get
            {
                EnsureRunner();
                return _runner;
            }
        }

        private void OnEnable()
        {
            Main = this;
        }

        private void OnDisable()
        {
            if (Main == this)
            {
                Main = null;
            }
        }

        /// <summary>
        /// Play <paramref name="dialog"/> from its starting node. A dialog that is
        /// already playing is stopped first.
        /// </summary>
        public void StartDialog(GrimoireDialogAsset dialog)
        {
            if (dialog == null)
            {
                Debug.LogWarning("[Grimoire Dialog] StartDialog was called without a dialog. Pass an asset from " +
                                 "Assets/GrimoireDialogs (import dialogs in Grimoire Connect > Dialogs).", this);
                return;
            }

            EnsureRunner();
            if (IsPlaying)
            {
                var interrupted = LastStartedDialog;
                _switching = true;
                _runner.Stop();
                _switching = false;
                if (!keepVariablesBetweenRuns && interrupted != null && interrupted != dialog)
                {
                    _variables.Reset(interrupted);
                }
            }

            LastStartedDialog = dialog;
            CurrentLine = null;
            OnDialogStarted.Invoke();
            _runner.Start(dialog);
        }

        /// <summary>Continue after a normal line.</summary>
        public void Next()
        {
            if (IsPlaying)
            {
                _runner.Advance();
            }
        }

        /// <summary>Pick an answer. Use the index from <see cref="DialogLine.Choices"/>.</summary>
        public void Choose(int choiceIndex)
        {
            if (IsPlaying)
            {
                _runner.Choose(choiceIndex);
            }
        }

        /// <summary>End the dialog now.</summary>
        public void Stop()
        {
            if (IsPlaying)
            {
                _runner.Stop();
            }
        }

        /// <summary>Set a variable of the dialog that is playing, e.g. SetVariable("hasKey", true).</summary>
        public void SetVariable(string variableName, object value)
        {
            SetVariable(CurrentDialogOrLast(), variableName, value);
        }

        /// <summary>Set a variable of <paramref name="dialog"/>, e.g. before starting it.</summary>
        public void SetVariable(GrimoireDialogAsset dialog, string variableName, object value)
        {
            if (dialog != null)
            {
                Runner.Variables.Set(dialog, variableName, value);
            }
        }

        /// <summary>Read a variable of the dialog that is playing, e.g. GetVariable&lt;bool&gt;("hasKey").</summary>
        public T GetVariable<T>(string variableName)
        {
            return GetVariable<T>(CurrentDialogOrLast(), variableName);
        }

        /// <summary>Read a variable of <paramref name="dialog"/>.</summary>
        public T GetVariable<T>(GrimoireDialogAsset dialog, string variableName)
        {
            var value = dialog != null ? Runner.Variables.Get(dialog, variableName) : null;
            return ConvertValue<T>(value);
        }

        /// <summary>Show the current line again, e.g. after changing <see cref="language"/>.</summary>
        public void Refresh()
        {
            if (IsPlaying)
            {
                PublishCurrentNode();
            }
        }

        private GrimoireDialogAsset CurrentDialogOrLast()
        {
            return CurrentDialog != null ? CurrentDialog : LastStartedDialog;
        }

        private void EnsureRunner()
        {
            if (_runner != null)
            {
                return;
            }

            _variables = new GrimoireDialogVariables();
            _runner = new GrimoireDialogRunner(_variables);
            _runner.NodeEntered += _ => PublishCurrentNode();
            _runner.Ended += HandleEnded;
        }

        private void PublishCurrentNode()
        {
            var node = _runner.CurrentNode;
            if (node == null)
            {
                return;
            }

            CurrentLine = BuildLine(_runner.CurrentDialog, node, _runner.VisibleOptions);
            if (CurrentLine.IsChoice)
            {
                OnChoices.Invoke(CurrentLine);
            }
            else
            {
                OnLine.Invoke(CurrentLine);
            }
        }

        private void HandleEnded(GrimoireDialogEndReason reason)
        {
            LastEndReason = reason;
            CurrentLine = null;

            // Reset on end rather than on start, so SetVariable(dialog, ...) before StartDialog still applies.
            if (!keepVariablesBetweenRuns && !_switching)
            {
                _variables.Reset();
            }

            OnDialogEnded.Invoke();
        }

        private DialogLine BuildLine(GrimoireDialogAsset current, GrimoireDialogNode node, IReadOnlyList<GrimoireDialogOption> options)
        {
            var activeLanguage = GrimoireDialogLocalization.ResolveLanguage(language);

            var choices = new List<DialogChoice>(options.Count);
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                choices.Add(new DialogChoice(
                    i,
                    GrimoireDialogLocalization.Text(option.text, option.translations, activeLanguage),
                    option.id));
            }

            var isLast = node.type == GrimoireDialogNodeType.End || (choices.Count == 0 && node.next.IsEmpty);
            return new DialogLine(
                current,
                node.identifier,
                node.speakerName,
                node.speakerId,
                GrimoireDialogLocalization.Text(node.text, node.translations, activeLanguage),
                GrimoireDialogLocalization.Voice(node.voiceUrl, node.translations, activeLanguage),
                choices,
                isLast);
        }

        private static T ConvertValue<T>(object value)
        {
            if (value == null)
            {
                return default;
            }

            if (value is T typed)
            {
                return typed;
            }

            var type = typeof(T);
            if (type == typeof(bool))
            {
                return (T)(object)GrimoireDialogConditions.ToBool(value);
            }

            if (type == typeof(string))
            {
                return (T)(object)GrimoireDialogConditions.AsString(value);
            }

            if (type == typeof(int))
            {
                return (T)(object)(int)Math.Round(GrimoireDialogConditions.ToNumber(value));
            }

            if (type == typeof(float))
            {
                return (T)(object)(float)GrimoireDialogConditions.ToNumber(value);
            }

            if (type == typeof(double))
            {
                return (T)(object)GrimoireDialogConditions.ToNumber(value);
            }

            try
            {
                return (T)Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return default;
            }
        }
    }
}
