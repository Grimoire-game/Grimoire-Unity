using System;
using System.Collections.Generic;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Read the dialog that is playing from any script, without a reference to
    /// the player:
    ///
    /// <code>
    /// if (GrimoireDialog.IsPlaying)
    /// {
    ///     Debug.Log($"{GrimoireDialog.Speaker}: {GrimoireDialog.Text}");
    ///     foreach (var choice in GrimoireDialog.Choices) { ... }
    /// }
    ///
    /// GrimoireDialog.LineShown += line => portrait.Show(line.SpeakerId);
    /// </code>
    ///
    /// The properties read <see cref="GrimoireDialogPlayer.Main"/>. The events fire
    /// for every player in the scene.
    /// </summary>
    public static class GrimoireDialog
    {
        private static readonly IReadOnlyList<DialogChoice> NoChoices = Array.Empty<DialogChoice>();

        /// <summary>A dialog started. Its first line follows in <see cref="LineShown"/>.</summary>
        public static event Action<GrimoireDialogPlayer, GrimoireDialogAsset> Started;

        /// <summary>A line or question is on screen. Check <see cref="DialogLine.IsChoice"/> to tell them apart.</summary>
        public static event Action<DialogLine> LineShown;

        /// <summary>A dialog ended, with the reason.</summary>
        public static event Action<GrimoireDialogPlayer, GrimoireDialogEndReason> Ended;

        /// <summary>The dialog player in the scene, or null.</summary>
        public static GrimoireDialogPlayer Player => GrimoireDialogPlayer.Main;

        public static bool IsPlaying => Player != null && Player.IsPlaying;

        /// <summary>Everything about the node on screen, or null when nothing is playing.</summary>
        public static DialogLine Current => Player != null ? Player.CurrentLine : null;

        /// <summary>The dialog playing right now (a linked dialog once the story jumps there), or null.</summary>
        public static GrimoireDialogAsset CurrentDialog => Player != null ? Player.CurrentDialog : null;

        /// <summary>Speaker display name of the current line. Empty when there is none.</summary>
        public static string Speaker => Current?.Speaker ?? "";

        /// <summary>Raw Grimoire speaker value (object id, type element id, or free text).</summary>
        public static string SpeakerId => Current?.SpeakerId ?? "";

        /// <summary>Translated text of the current line. Empty when nothing is playing.</summary>
        public static string Text => Current?.Text ?? "";

        /// <summary>Answers on the current question. Empty for a normal line.</summary>
        public static IReadOnlyList<DialogChoice> Choices => Current?.Choices ?? NoChoices;

        /// <summary>Node identifier of the current line. Empty when nothing is playing.</summary>
        public static string NodeId => Current?.NodeId ?? "";

        /// <summary>True when the current line is a question waiting for Choose(index).</summary>
        public static bool IsChoice => Current != null && Current.IsChoice;

        /// <summary>The current line, if a dialog is playing.</summary>
        public static bool TryGetCurrent(out DialogLine line)
        {
            line = Current;
            return line != null;
        }

        internal static void RaiseStarted(GrimoireDialogPlayer player, GrimoireDialogAsset dialog)
        {
            Started?.Invoke(player, dialog);
        }

        internal static void RaiseLineShown(DialogLine line)
        {
            LineShown?.Invoke(line);
        }

        internal static void RaiseEnded(GrimoireDialogPlayer player, GrimoireDialogEndReason reason)
        {
            Ended?.Invoke(player, reason);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Started = null;
            LineShown = null;
            Ended = null;
        }
    }
}
