using System;
using System.Collections.Generic;

namespace Grimoire.PluginV2
{
    public enum GrimoireDialogEndReason
    {
        /// <summary>The dialog reached an end node or a node with nowhere to go.</summary>
        Completed,

        /// <summary>Stop() was called.</summary>
        Stopped,

        /// <summary>A link pointed at a node or dialog that is not in the imported data.</summary>
        MissingTarget,
    }

    /// <summary>
    /// What the player should see right now: who speaks, what they say, and the
    /// answers to pick from (if any). Already translated for the active language.
    /// </summary>
    public sealed class DialogLine
    {
        private static readonly IReadOnlyList<DialogChoice> NoChoices = Array.Empty<DialogChoice>();

        public DialogLine(
            GrimoireDialogAsset dialog,
            string nodeId,
            string speaker,
            string speakerId,
            string text,
            string voiceUrl,
            IReadOnlyList<DialogChoice> choices,
            bool isLast)
        {
            Dialog = dialog;
            NodeId = nodeId ?? "";
            Speaker = speaker ?? "";
            SpeakerId = speakerId ?? "";
            Text = text ?? "";
            VoiceUrl = voiceUrl ?? "";
            Choices = choices ?? NoChoices;
            IsLast = isLast;
        }

        /// <summary>The dialog this line belongs to (changes after a jump to another dialog).</summary>
        public GrimoireDialogAsset Dialog { get; }

        /// <summary>The node identifier in Grimoire, handy for logging and save games.</summary>
        public string NodeId { get; }

        /// <summary>Display name of the speaker. Empty when the line has no speaker.</summary>
        public string Speaker { get; }

        /// <summary>Raw Grimoire speaker value (object id, type element id, or free text).</summary>
        public string SpeakerId { get; }

        public string Text { get; }

        /// <summary>Voice-over URL from Grimoire for the active language, if any.</summary>
        public string VoiceUrl { get; }

        /// <summary>Answers to show. Empty for a normal line.</summary>
        public IReadOnlyList<DialogChoice> Choices { get; }

        /// <summary>True when the player must pick an answer with Choose(index).</summary>
        public bool IsChoice => Choices.Count > 0;

        /// <summary>True when this line has no follow-up node, so Next() ends the dialog.</summary>
        public bool IsLast { get; }

        public bool HasSpeaker => Speaker.Length > 0;
    }

    /// <summary>One answer the player can pick. Pass <see cref="Index"/> to Choose().</summary>
    public sealed class DialogChoice
    {
        public DialogChoice(int index, string text, string optionId)
        {
            Index = index;
            Text = text ?? "";
            OptionId = optionId ?? "";
        }

        public int Index { get; }
        public string Text { get; }
        public string OptionId { get; }
    }
}
