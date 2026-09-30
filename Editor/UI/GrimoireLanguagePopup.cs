using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Dropdown of the selected game's supported languages, shared by the Scene
    /// tab, the Text Link inspector, and Connect settings so every place offers
    /// the same choices.
    ///
    /// Falls back to a plain text field while the game's languages are unknown
    /// (not signed in, still loading, or the fetch failed) so a locale can still
    /// be typed rather than being locked out.
    /// </summary>
    public static class GrimoireLanguagePopup
    {
        /// <summary>Draw with a prefix label. Returns the chosen language code.</summary>
        public static string Draw(GUIContent label, string current, string emptyOptionLabel)
        {
            GrimoireGameLanguages.EnsureLoaded();
            var code = current?.Trim() ?? "";

            if (!GrimoireGameLanguages.HasLanguages)
            {
                return EditorGUILayout.TextField(label, code)?.Trim() ?? "";
            }

            BuildOptions(code, emptyOptionLabel, out var codes, out var labels, out var index);
            return codes[EditorGUILayout.Popup(label, index, labels)];
        }

        /// <summary>Draw without a prefix label, for custom row layouts.</summary>
        public static string Draw(string current, string emptyOptionLabel, params GUILayoutOption[] options)
        {
            GrimoireGameLanguages.EnsureLoaded();
            var code = current?.Trim() ?? "";

            if (!GrimoireGameLanguages.HasLanguages)
            {
                return EditorGUILayout.TextField(code, options)?.Trim() ?? "";
            }

            BuildOptions(code, emptyOptionLabel, out var codes, out var labels, out var index);
            return codes[EditorGUILayout.Popup(index, labels, options)];
        }

        /// <summary>
        /// Why the dropdown is unavailable, or null when languages are ready.
        /// </summary>
        public static string DescribeUnavailable()
        {
            if (GrimoireGameLanguages.HasLanguages)
            {
                return null;
            }

            if (!GrimoireSettings.IsConfigured)
            {
                return "Sign in and choose a workspace to pick from the game's languages.";
            }

            if (GrimoireGameLanguages.IsLoading)
            {
                return "Loading the game's languages…";
            }

            if (!string.IsNullOrEmpty(GrimoireGameLanguages.Error))
            {
                return GrimoireGameLanguages.Error;
            }

            return "This game has no languages configured yet. Add them in Grimoire under " +
                   "Settings > Game Management, or type a code here.";
        }

        /// <summary>
        /// Options are the no-translation choice, then the game's translated
        /// languages. The source language is left out because it has no
        /// translation rows — the first option already resolves to it. A code
        /// that is set but no longer configured is kept as its own option so
        /// selecting another language stays a deliberate act.
        /// </summary>
        private static void BuildOptions(
            string current,
            string emptyOptionLabel,
            out string[] codes,
            out GUIContent[] labels,
            out int index)
        {
            var codeList = new List<string> { GrimoireGameLanguages.SourceLanguage };
            var labelList = new List<GUIContent> { new GUIContent(emptyOptionLabel) };

            foreach (var code in GrimoireGameLanguages.Codes)
            {
                if (GrimoireGameLanguages.IsDefault(code))
                {
                    continue;
                }

                codeList.Add(code);
                labelList.Add(new GUIContent(GrimoireGameLanguages.DisplayName(code)));
            }

            if (current.Length > 0 && !GrimoireGameLanguages.Supports(current))
            {
                codeList.Add(current);
                labelList.Add(new GUIContent($"{current}  ·  not a game language"));
            }

            codes = codeList.ToArray();
            labels = labelList.ToArray();

            index = Array.FindIndex(codes, c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                index = 0;
            }
        }
    }
}
